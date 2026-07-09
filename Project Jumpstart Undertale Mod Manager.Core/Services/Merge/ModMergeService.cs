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

// See IModMergeService.cs for the full design contract. This file is the impl.
// DIR-MODEL: ApplyAsync mutates <gameDir>/data.win (and any audiogroupN.dat)
// in place. gameDir is a prepared, disposable copy owned by the caller.

public sealed class ModMergeService : IModMergeService
{
    private readonly Data.IDataService _data;
    private readonly TextureRepacker _repacker = new();

    public ModMergeService(Data.IDataService data) => _data = data;

    public async Task<MergeResult> ApplyAsync(string gameDir, IReadOnlyList<ModSource> mods)
    {
        // gameDir must contain data.win directly (see contract).
        string dataWinPath = Path.Combine(gameDir, "data.win");
        if (!File.Exists(dataWinPath))
            return MergeResult.Fail("(setup)", $"data.win not found in game directory: {gameDir}");

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

        // === PHASE 2: load the game's data.win ONCE =========================
        UndertaleData data = await _data.LoadAsync(dataWinPath);

        // === PHASE 3: gather + parse every address, build the last-wins plan =
        var plan = new Dictionary<string, PlannedAsset>();
        var conflictLog = new List<ConflictLogEntry>();
        var overriddenBy = new Dictionary<string, List<string>>();

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
        var resolutions = new Dictionary<string, ResolutionKind>();
        foreach (var kv in plan)
        {
            try
            {
                ResolvedAsset r = ModResolver.Resolve(kv.Value.Address, data, manifests[kv.Value.OwningMod]);
                resolutions[kv.Key] = r.Kind;
            }
            catch (ModResolveException ex)
            {
                return MergeResult.Fail(kv.Value.OwningMod,
                    $"cannot resolve {kv.Value.Address.RelativePath}: {ex.Message}");
            }
        }

        // ---- plan is VALID. Only now do we mutate. ------------------------
        var warnings = new List<string>();

        // === PHASE 5a: code -> CompileGroup ================================
        var codeAssets = plan.Values.Where(p => p.Address.Category == AssetCategory.Code).ToList();
        if (codeAssets.Count > 0)
        {
            var group = new CompileGroup(data);
            foreach (PlannedAsset pa in codeAssets)
            {
                UndertaleCode code = data.Code.ByName(pa.Address.AssetName);
                if (code is null)
                {
                    warnings.Add($"[{pa.OwningMod}] new code '{pa.Address.AssetName}' creation not wired yet; skipped.");
                    continue;
                }
                group.QueueCodeReplace(code, File.ReadAllText(pa.SourceFile));
            }
            CompileResult cr = group.Compile();
            if (!cr.Successful)
                return MergeResult.Fail("(compile)", "GML compile failed: " + cr.PrintAllErrors(false));
        }

        // === PHASE 5b: Tier 1 (paths wired; sounds wired; objects stubbed) ==
        // Sounds are grouped by asset first, because a sound is up to TWO files
        // (<name>.json metadata + <name>.ogg/.wav bytes) sharing one asset key.
        var soundBundles = new Dictionary<string, SoundBundle>();

        foreach (var kv in plan)
        {
            PlannedAsset pa = kv.Value;
            bool create = resolutions[kv.Key] == ResolutionKind.Create;

            switch (pa.Address.Category)
            {
                case AssetCategory.Paths:
                    try { Paths.PathImporter.Apply(data, pa.Address, pa.SourceFile, create); }
                    catch (Exception ex)
                    {
                        return MergeResult.Fail(pa.OwningMod,
                            $"failed applying path {pa.Address.RelativePath}: {ex.Message}");
                    }
                    break;

                case AssetCategory.Sounds:
                    // A sound is up to two files sharing one asset key; the plan
                    // dict only kept one. Record the owning mod + asset once;
                    // we re-scan the mod's folder for both files below.
                    soundBundles.TryAdd(kv.Key, new SoundBundle(pa.Address, pa.OwningMod, create));
                    break;

                case AssetCategory.Objects:
                    try
                    {
                        Objects.ObjectImporter.Apply(data, pa.Address, pa.SourceFile, create);
                    }
                    catch (Exception ex)
                    {
                        return MergeResult.Fail(pa.OwningMod, $"failed applying object {pa.Address.RelativePath}: {ex.Message}");
                    }
                    break;
                // NOTE on ordering: EventHandlerFor may CREATE an empty code entry
                // (gml_Object_<name>_<evt>). If a mod ALSO ships that code file, the code/
                // importer (PHASE 5a) compiles into it. PHASE 5a currently runs BEFORE 5b, so
                // on a fresh object the code entry won't exist yet when 5a runs -> the code
                // gets skipped with a "new code creation not wired yet" warning. For v1 that's
                // acceptable (object + its code in one mod, code compiles on a REPLACE of an
                // existing object). Fully supporting "new object + new event code in one pass"
                // means running object wiring before code compile, or a second compile pass.
                // Flag for later; not tonight.
            }
        }

        foreach (SoundBundle b in soundBundles.Values)
        {
            // Re-scan the owning mod for both files by asset name (the plan
            // collapsed them into one entry, so don't trust a single SourceFile).
            ModSource owner = mods.First(m => m.Name == b.OwningMod);
            string jsonFile = FindSoundFile(owner, b.Address, ".json");
            string audioFile = FindSoundFile(owner, b.Address, ".wav", ".ogg", ".mp3");

            if (jsonFile is null)
            {
                warnings.Add($"[{b.OwningMod}] sound '{b.Address.AssetName}' has no .json metadata; skipped.");
                continue;
            }
            try
            {
                Sounds.SoundImporter.Apply(data, gameDir, b.Address, jsonFile, audioFile, b.Create);
            }
            catch (Exception ex)
            {
                return MergeResult.Fail(b.OwningMod,
                    $"failed applying sound {b.Address.RelativePath}: {ex.Message}");
            }
        }

        // === PHASE 5c: textures -> one repack at the end ====================
        string overridesDir = Path.Combine(Path.GetTempPath(), "pjum_merge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(overridesDir);
        try
        {
            bool anyTexture = false;
            foreach (PlannedAsset pa in plan.Values)
            {
                string flat = ToFlatTextureName(pa.Address);
                if (flat is null) continue;
                File.Copy(pa.SourceFile, Path.Combine(overridesDir, flat), overwrite: true);
                anyTexture = true;
            }

            if (anyTexture)
            {
                RepackResult rr = await _repacker.RepackAsync(data, overridesDir);
                warnings.AddRange(rr.Warnings);
            }

            // === PHASE 6: write data.win back IN PLACE =====================
            await _data.SaveAsync(data, dataWinPath);
        }
        finally
        {
            TryDelete(overridesDir);
        }

        return MergeResult.Ok(conflictLog, warnings);
    }

    // -- helpers ------------------------------------------------------------

    private sealed record PlannedAsset(ModAddress Address, string OwningMod, string SourceFile);

    // Find <assetName>.<ext> for a sound within the owning mod, matching the
    // asset's addressed location (same route/container). Returns null if absent.
    private static string FindSoundFile(ModSource mod, ModAddress addr, params string[] exts)
    {
        foreach (string file in EnumerateModFiles(mod))
        {
            string rel = Path.GetRelativePath(mod.ModDirectory, file).Replace('\\', '/');
            if (!ModAddressParser.TryParse(rel, out ModAddress a, out _)) continue;
            if (a.Category != AssetCategory.Sounds) continue;
            if (a.AssetName != addr.AssetName) continue;
            if (a.Container != addr.Container) continue;   // same audiogroup/container
            string ext = Path.GetExtension(file).ToLowerInvariant();
            foreach (string want in exts)
                if (ext == want) return file;
        }
        return null;
    }
    
    // Pairs a sound's json + audio files (same asset key) for one Apply call.
    private sealed class SoundBundle
    {
        public ModAddress Address { get; }
        public string OwningMod { get; }
        public bool Create { get; }
        public string JsonFile { get; set; }
        public string AudioFile { get; set; }
        public SoundBundle(ModAddress addr, string owningMod, bool create)
        {
            Address = addr; OwningMod = owningMod; Create = create;
        }
    }

    private static string AssetKey(ModAddress a)
    {
        string cat = a.Category.ToString().ToLowerInvariant();
        return $"{a.Game}/{a.Container}/{cat}:{a.AssetName}";
    }

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