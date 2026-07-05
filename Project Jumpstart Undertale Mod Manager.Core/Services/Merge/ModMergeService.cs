using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Models;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Textures;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;



public sealed class ModMergeService : IModMergeService
{
    // IDataService is your existing Core service (load/save UndertaleData).
    // Kept as a dependency so this class never calls UndertaleIO directly and
    // stays consistent with the rest of the Core layer.
    private readonly Data.IDataService _data;
    private readonly TextureRepacker _repacker;

    public ModMergeService(Data.IDataService data)
    {
        _data = data;
        _repacker = new TextureRepacker(); // or inject an ITextureRepacker later
    }

    public async Task<MergeResult> ApplyAsync(string baseDataPath, IReadOnlyList<ModSource> mods, string outputPath)
    {
        var warnings = new List<string>();

        // --- pre-flight: conflict detection (name-keyed, before any mutation) ---
        IReadOnlyList<ModConflict> conflicts = DetectConflicts(mods);
        if (conflicts.Count > 0)
            return new MergeResult(Success: false, conflicts, warnings);

        // --- 1. load base graph ---
        UndertaleData data = await _data.LoadAsync(baseDataPath);

        // --- 2. Tier 1: declarative reference assets ---
        foreach (ModSource mod in mods)
            ApplyTier1(data, mod, warnings);

        // --- 3. Tier 2: code (compiler handles strings/vars/functions) ---
        foreach (ModSource mod in mods)
            ApplyTier2Code(data, mod, warnings);

        // --- 4. collect texture overrides into one flat dir ---
        string overridesDir = CollectTextureOverrides(mods, warnings);

        try
        {
            // --- 5. one full repack (base + overrides) ---
            if (Directory.EnumerateFiles(overridesDir, "*.png", SearchOption.AllDirectories).Any())
            {
                RepackResult repack = await _repacker.RepackAsync(data, overridesDir);
                warnings.AddRange(repack.Warnings);
            }

            // --- 6. write merged data file ---
            await _data.SaveAsync(data, outputPath);
        }
        finally
        {
            TryDelete(overridesDir);
        }

        return new MergeResult(Success: true, Array.Empty<ModConflict>(), warnings);
    }

    // -- conflict detection (name-keyed ownership ledger) -------------------

    private static IReadOnlyList<ModConflict> DetectConflicts(IReadOnlyList<ModSource> mods)
    {
        var owner = new Dictionary<string, string>(); // assetName -> first mod
        var conflicts = new List<ModConflict>();

        foreach (ModSource mod in mods)
            foreach (string asset in AssetsTouchedBy(mod))
            {
                if (owner.TryGetValue(asset, out string first))
                    conflicts.Add(new ModConflict(asset, first, mod.Name));
                else
                    owner[asset] = mod.Name;
            }

        return conflicts;
    }

    /// <summary>
    /// The declarative manifest IS the folder tree: every asset file's name is
    /// the asset it touches. A directory walk of the mod folder yields the set.
    /// </summary>
    private static IEnumerable<string> AssetsTouchedBy(ModSource mod)
    {
        if (!Directory.Exists(mod.ModDirectory))
            yield break;

        foreach (string file in Directory.EnumerateFiles(mod.ModDirectory, "*", SearchOption.AllDirectories))
        {
            // Asset identity = file name without extension, sprite frames
            // collapsed to their sprite name (spr_susie_0 -> spr_susie) so two
            // mods editing different frames of the same sprite still conflict.
            string name = Path.GetFileNameWithoutExtension(file);
            int lastUnderscore = name.LastIndexOf('_');
            if (lastUnderscore > 0 && int.TryParse(name[(lastUnderscore + 1)..], out _))
                name = name[..lastUnderscore];
            yield return name;
        }
    }

    // -- Tier 1: declarative reference assets -------------------------------

    private static void ApplyTier1(UndertaleData data, ModSource mod, List<string> warnings)
    {
        // TODO: for each declarative asset in the mod (objects/events, paths,
        // sounds), resolve targets by NAME and wire references, e.g.:
        //   var obj = data.GameObjects.ByName("obj_x") ?? create+add;
        //   obj.Events[(int)evType] ... Actions[0].CodeId = data.Code.ByName(codeName);
        //   var path = data.Paths.ByName("pth_x") ?? create+add; set Points/IsClosed;
        //   var snd  = data.Sounds.ByName("snd_x"); set flags; wire AudioFile.
        // Grouped sounds (GroupID != 0) route to the chapter's audiogroupN.dat,
        // NOT data.win — handle that when you wire sounds.
        //
        // Left as a stub on purpose: every call here is a clean one-liner traced
        // from the editor files, so build them one asset class at a time.
    }

    // -- Tier 2: code via the compiler --------------------------------------

    private static void ApplyTier2Code(UndertaleData data, ModSource mod, List<string> warnings)
    {
        // Mod code files live under, e.g., <mod>/<game>/<container>/code/*.gml,
        // each named for its target code entry (gml_Script_x / gml_Object_x_Event).
        string codeDir = FindCodeDir(mod.ModDirectory);
        if (codeDir is null) return;

        var group = new CompileGroup(data);

        foreach (string gmlFile in Directory.EnumerateFiles(codeDir, "*.gml", SearchOption.AllDirectories))
        {
            string codeName = Path.GetFileNameWithoutExtension(gmlFile);
            string source = File.ReadAllText(gmlFile);

            UndertaleCode code = data.Code.ByName(codeName);
            if (code is null)
            {
                warnings.Add($"Code entry '{codeName}' not found in target; creating new entries isn't wired yet. Skipped.");
                continue;
            }

            group.QueueCodeReplace(code, source);
        }

        CompileResult result = group.Compile();
        if (!result.Successful)
            warnings.Add("GML compile errors: " + result.PrintAllErrors(false));
    }

    // -- Tier 3 prep: collect texture overrides into a flat dir -------------

    private static string CollectTextureOverrides(IReadOnlyList<ModSource> mods, List<string> warnings)
    {
        string flatDir = Path.Combine(Path.GetTempPath(), "pjumpstart_overrides_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(flatDir);

        // TODO: translate each mod's folder layout into the flat naming the
        // repacker consumes:
        //   sprites/spr_susie/0.png  ->  spr_susie_0.png
        //   backgrounds/bg_x.png     ->  bg_x.png
        //   fonts/fnt_x.png          ->  fnt_x.png
        // Later mods overwrite earlier ones here (load-order wins); real
        // cross-mod conflicts were already caught in DetectConflicts.

        return flatDir;
    }

    // -- helpers ------------------------------------------------------------

    private static string FindCodeDir(string modDir)
    {
        // Placeholder resolver; replace with your container-addressed layout
        // (<mod>/<game>/<chapter-or-root>/<container>/code).
        string candidate = Path.Combine(modDir, "code");
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}