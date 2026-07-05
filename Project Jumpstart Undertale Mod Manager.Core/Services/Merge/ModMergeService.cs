using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Models;
using System.Text.Json;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Textures;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;

public sealed class ModMergeService : IModMergeService
{
    private readonly Data.IDataService _data;
    private readonly TextureRepacker _repacker = new();

    public ModMergeService(Data.IDataService data) => _data = data;

    public async Task<MergeResult> ApplyAsync(
        string baseDataPath, IReadOnlyList<ModSource> mods, string outputPath)
    {
        // === PHASE 0: read every manifest ===================================
        var manifests = new Dictionary<string, ModManifest>();
        foreach (ModSource mod in mods)
        {
            if (!TryReadManifest(mod, out ModManifest manifest, out string err))
                return MergeResult.Fail(mod.Name, err);   // broken mod.json -> abort naming it
            manifests[mod.Name] = manifest;
        }

        // === PHASE 1: dependency check (hard-fail) ==========================
        var presentNames = new HashSet<string>(mods.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        foreach (ModSource mod in mods)
        {
            foreach (string dep in manifests[mod.Name].Requires.Keys)
            {
                if (!presentNames.Contains(dep))
                    return MergeResult.Fail(mod.Name,
                        $"requires '{dep}', which is not in the mod set. Add it or remove {mod.Name}.");
            }
        }

        // === PHASE 2: load base ONCE (pristine) =============================
        UndertaleData data = await _data.LoadAsync(baseDataPath);

        // === PHASE 3: gather + parse every address, build the last-wins plan =
        // assetKey ("category:name") -> the mod that ultimately owns it (later wins),
        // plus the list of files that mod contributes for that asset.
        var plan = new Dictionary<string, PlannedAsset>();
        var conflictLog = new List<ConflictLogEntry>();
        var overriddenBy = new Dictionary<string, List<string>>(); // assetKey -> earlier mods overridden

        foreach (ModSource mod in mods) // load order
        {
            foreach (string file in EnumerateModFiles(mod))
            {
                string rel = ToModRelative(mod, file);
                if (!ModAddressParser.TryParse(rel, out ModAddress addr, out string parseErr))
                    return MergeResult.Fail(mod.Name, $"bad address '{rel}': {parseErr}");

                string key = AssetKey(addr);

                if (plan.TryGetValue(key, out PlannedAsset prev) && prev.OwningMod != mod.Name)
                {
                    // Conflict: later mod (this one) wins.
                    if (!overriddenBy.TryGetValue(key, out var list))
                        overriddenBy[key] = list = new List<string>();
                    if (!list.Contains(prev.OwningMod)) list.Add(prev.OwningMod);
                }

                plan[key] = new PlannedAsset(addr, mod.Name, file);
            }
        }

        foreach (var kv in overriddenBy)
            conflictLog.Add(new ConflictLogEntry(kv.Key, plan[kv.Key].OwningMod, kv.Value));

        // === PHASE 4: resolve every planned asset against real data =========
        // Pure inspection — no mutation. A resolve failure aborts naming the mod.
        foreach (PlannedAsset pa in plan.Values)
        {
            try
            {
                ModResolver.Resolve(pa.Address, data, manifests[pa.OwningMod]);
            }
            catch (ModResolveException ex)
            {
                return MergeResult.Fail(pa.OwningMod,
                    $"cannot resolve {pa.Address.RelativePath}: {ex.Message}");
            }
        }

        // ---- from here, the plan is VALID. Only now do we mutate. ----------
        var warnings = new List<string>();

        // === PHASE 5: apply ================================================
        // 5a. Code -> CompileGroup (strings/vars/functions handled by the compiler).
        var codeAssets = plan.Values.Where(p => p.Address.Category == AssetCategory.Code).ToList();
        if (codeAssets.Count > 0)
        {
            var group = new CompileGroup(data);
            foreach (PlannedAsset pa in codeAssets)
            {
                UndertaleCode code = data.Code.ByName(pa.Address.AssetName);
                if (code is null)
                {
                    // Declared-new code: create an empty entry to compile into.
                    // (Full new-code creation is a Tier-1 concern; stub for now.)
                    warnings.Add($"[{pa.OwningMod}] new code '{pa.Address.AssetName}' creation not wired yet; skipped.");
                    continue;
                }
                group.QueueCodeReplace(code, File.ReadAllText(pa.SourceFile));
            }
            CompileResult cr = group.Compile();
            if (!cr.Successful)
                return MergeResult.Fail("(compile)", "GML compile failed: " + cr.PrintAllErrors(false));
        }

        // 5b. Tier 1 (objects/sounds/paths) -- STUBBED.
        foreach (PlannedAsset pa in plan.Values)
        {
            switch (pa.Address.Category)
            {
                case AssetCategory.Objects:
                case AssetCategory.Sounds:
                case AssetCategory.Paths:
                    warnings.Add($"[{pa.OwningMod}] Tier 1 apply for {pa.Address.Category} '{pa.Address.AssetName}' not wired yet; skipped.");
                    break;
            }
        }

        // 5c. Textures -> collect winners into a flat dir, ONE repack at the end.
        string overridesDir = Path.Combine(Path.GetTempPath(), "pjum_merge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(overridesDir);
        try
        {
            bool anyTexture = false;
            foreach (PlannedAsset pa in plan.Values)
            {
                string flat = ToFlatTextureName(pa.Address);
                if (flat is null) continue; // not a texture-backed category
                File.Copy(pa.SourceFile, Path.Combine(overridesDir, flat), overwrite: true);
                anyTexture = true;
            }

            if (anyTexture)
            {
                RepackResult rr = await _repacker.RepackAsync(data, overridesDir);
                warnings.AddRange(rr.Warnings);
            }

            // === PHASE 6: write merged file ================================
            await _data.SaveAsync(data, outputPath);
        }
        finally
        {
            TryDelete(overridesDir);
        }

        return MergeResult.Ok(conflictLog, warnings);
    }

    // -- helpers ------------------------------------------------------------

    private sealed record PlannedAsset(ModAddress Address, string OwningMod, string SourceFile);

    private static string AssetKey(ModAddress a)
    {
        // Sprites collapse frames to the sprite name so two mods editing
        // different frames of the same sprite still count as the same asset.
        string cat = a.Category.ToString().ToLowerInvariant();
        return $"{a.Game}/{a.Container}/{cat}:{a.AssetName}";
    }

    // category translation for the repacker's flat naming; null = not a texture.
    private static string ToFlatTextureName(ModAddress a)
    {
        return a.Category switch
        {
            AssetCategory.Sprites     => $"{a.AssetName}_{a.Frame}.png",
            AssetCategory.Backgrounds => $"{a.AssetName}.png",
            AssetCategory.Fonts       => $"{a.AssetName}.png",
            _ => null
        };
    }

    private static bool TryReadManifest(ModSource mod, out ModManifest manifest, out string error)
    {
        manifest = null; error = null;
        string path = Path.Combine(mod.ModDirectory, "mod.json");
        if (!File.Exists(path))
        {
            error = "mod.json not found.";
            return false;
        }
        try
        {
            manifest = JsonSerializer.Deserialize<ModManifest>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest is null) { error = "mod.json parsed to null."; return false; }
            return true;
        }
        catch (Exception ex)
        {
            error = "mod.json is not valid JSON: " + ex.Message;
            return false;
        }
    }

    private static IEnumerable<string> EnumerateModFiles(ModSource mod)
    {
        if (!Directory.Exists(mod.ModDirectory)) yield break;
        foreach (string f in Directory.EnumerateFiles(mod.ModDirectory, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(f).Equals("mod.json", StringComparison.OrdinalIgnoreCase)) continue;
            yield return f;
        }
    }

    private static string ToModRelative(ModSource mod, string fullPath)
    {
        string rel = Path.GetRelativePath(mod.ModDirectory, fullPath);
        return rel.Replace('\\', '/');
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}