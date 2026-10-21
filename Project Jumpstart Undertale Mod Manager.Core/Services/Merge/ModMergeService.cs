using System.Diagnostics.CodeAnalysis;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Models;
using System.Text.Json;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Code;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Textures;
using PjumModLib;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;

// See IModMergeService.cs for the full design contract. This file is the impl.
// DIR-MODEL: ApplyAsync mutates <gameDir>/data.win (and any audiogroupN.dat)
// in place. gameDir is a prepared, disposable copy owned by the caller.

public sealed class ModMergeService(Data.IDataService dataService) : IModMergeService
{
    private readonly TextureRepacker _repacker = new();

    public async Task<MergeResult> ApplyAsync(string gameDir, IReadOnlyList<ModSource> mods, string? gameRoot = null)
    {
        // gameDir must contain data.win directly (see contract).
        string dataWinPath = Path.Combine(gameDir, "data.win");
        if (!File.Exists(dataWinPath))
            return MergeResult.Fail("(setup)", $"data.win not found in game directory: {gameDir}");

        // === PHASE 0: read every manifest ===================================
        var manifests = new Dictionary<string, ModManifest>();
        foreach (ModSource mod in mods)
        {
            if (!TryReadManifest(mod, out ModManifest? manifest, out string? err))
                return MergeResult.Fail(mod.Name, err);   // broken mod.json -> abort naming it
            manifests[mod.Name] = manifest; // Shouldn't really happen to be null if try fails and go to Fail state.
        }

        // === PHASE 1: dependency check (hard-fail) ==========================
        var presentNames = new HashSet<string>(mods.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        var loadPosition = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < mods.Count; i++)
            loadPosition.TryAdd(mods[i].Name, i);
        var orderWarnings = new List<string>();
        foreach (ModSource mod in mods)
        {
            foreach ((string dep, string rule) in manifests[mod.Name].Requires)
            {
                if (!presentNames.Contains(dep))
                    return MergeResult.Fail(mod.Name,
                        $"requires '{dep}', which is not in the mod set. Add it or remove {mod.Name}.");

                if (!VersionRule.IsAny(rule))
                {
                    if (!VersionRule.TryParse(rule, out VersionRule? wanted))
                        return MergeResult.Fail(mod.Name,
                            $"can't read the version rule '{rule}' for '{dep}'. Use a version such as 1.0.0, optionally after >=, >, <= or <.");

                    ModSource depMod = mods[loadPosition[dep]];
                    string installed = manifests[depMod.Name].Version;
                    if (!VersionRule.TryParseVersion(installed, out int[]? have))
                        return MergeResult.Fail(mod.Name,
                            $"requires {dep} {rule.Trim()}, but {depMod.Name} has no readable version ('{installed}').");
                    if (!wanted.Allows(have))
                        return MergeResult.Fail(mod.Name,
                            $"requires {dep} {rule.Trim()}, but {installed} is enabled.");
                }

                if (loadPosition[dep] > loadPosition[mod.Name])
                    orderWarnings.Add($"[{mod.Name}] requires '{dep}', but '{dep}' loads after it, so '{dep}' wins any asset both change. Move '{dep}' earlier in the load order.");
            }
        }

        // === PHASE 2: load the game's data.win ONCE =========================
        UndertaleData data = await dataService.LoadAsync(dataWinPath);

        // === PHASE 2b: which game + chapter is this data.win? ===============
        // Read from the data itself. A mod file addressed to another game or
        // chapter is SKIPPED, not failed — one mod may cover several chapters.
        var warnings = new List<string>(orderWarnings);
        ModTarget? target = ModTarget.FromData(data);
        if (target is null)
            warnings.Add("data.win has no game name; mod paths were not checked against the game.");

        var renames = new AssetRenames(data);
        foreach (ModSource mod in mods)
            if (!renames.TryAdd(mod.Name, manifests[mod.Name].Renames, out string renameError))
                return MergeResult.Fail(mod.Name, renameError);
        renames.Apply();
        (int Sprites, int Backgrounds, int Sounds, int Objects) firstNew = (data.Sprites.Count, data.Backgrounds.Count, data.Sounds.Count, data.GameObjects.Count);
        List<string> NewAssetOrder(string category) =>
            mods.SelectMany(m => manifests[m.Name].NewAssets.TryGetValue(category, out var byName) ? byName.Keys : Enumerable.Empty<string>()).ToList();

        // === PHASE 3: gather + parse every address, build the last-wins plan =
        var plan = new Dictionary<string, PlannedAsset>();
        var conflictLog = new List<ConflictLogEntry>();
        var overriddenBy = new Dictionary<string, List<string>>();
        var codeOps = new List<PlannedAsset>();
        var roomOps = new List<PlannedAsset>();
        var gameOps = new List<PlannedAsset>();
        
        foreach (ModSource mod in mods) // load order
        {
            int accepted = 0;
            var misspelled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var otherGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sharedNotFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in EnumerateModFiles(mod, manifests[mod.Name]))
            {
                string rel = ToModRelative(mod, file);
                if (!ModAddressParser.TryParse(rel, out ModAddress? addr, out string parseErr))
                    return MergeResult.Fail(mod.Name, $"bad address '{rel}': {parseErr}");

                if (target is not null && !target.Accepts(addr))
                {
                    if (addr.InSharedFolder && addr.Game.Equals(target.Game, StringComparison.OrdinalIgnoreCase))
                        sharedNotFiles.Add($"{addr.Game}/{addr.Route[0]}/{addr.Category.ToString().ToLowerInvariant()}/");
                    else if (target.LooksMisspelled(addr))
                        misspelled.Add(string.Join("/", addr.RoutePrefix));
                    else if (!addr.Game.Equals(target.Game, StringComparison.OrdinalIgnoreCase))
                        otherGames.Add(addr.Game);
                    continue;
                }
                accepted++;
                addr = renames.Alias(addr, mod.Name);

                // Code is meant to accumulate to combine mods together in code, otherwise one mod will break or disable another programmatically.
                if (addr.Category == AssetCategory.Code)
                {
                    codeOps.Add(new PlannedAsset(addr, mod.Name, file));
                    continue;
                }

                if (addr.Category == AssetCategory.Rooms)
                {
                    roomOps.Add(new PlannedAsset(addr, mod.Name, file));
                    continue;
                }

                if (addr.Category == AssetCategory.Game)
                {
                    gameOps.Add(new PlannedAsset(addr, mod.Name, file));
                    continue;
                }

                string key = AssetKey(addr);

                if (plan.TryGetValue(key, out PlannedAsset? prev) && prev.OwningMod != mod.Name)
                {
                    if (!overriddenBy.TryGetValue(key, out var list))
                        overriddenBy[key] = list = new List<string>();
                    if (!list.Contains(prev.OwningMod)) list.Add(prev.OwningMod);
                }

                plan[key] = new PlannedAsset(addr, mod.Name, file);
            }

            foreach (string folder in sharedNotFiles)
                warnings.Add($"[{mod.Name}] folder '{folder}' was skipped: shared/ only holds files/ (copied into the game folder).");
            foreach (string folder in misspelled)
                warnings.Add($"[{mod.Name}] folder '{target!.Game}/{folder}' is not a chapter name " +
                             "(expected nothing, or chapter + a number such as chapter2); its files were skipped.");
            if (accepted > 0)
                foreach (string game in otherGames)
                    warnings.Add($"[{mod.Name}] files under '{game}/' were skipped: this game is '{target!.Game}'.");
            if (accepted == 0 && manifests[mod.Name].Renames.Count == 0)
                warnings.Add(target is null
                    ? $"[{mod.Name}] has no files; skipped."
                    : $"[{mod.Name}] has no files for {target}; skipped.");
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

        var newObjects = new HashSet<string>(plan.Where(kv => kv.Value.Address.Category == AssetCategory.Objects && resolutions[kv.Key] == ResolutionKind.Create)
                                                 .Select(kv => kv.Value.Address.AssetName));
        var newBackgrounds = new HashSet<string>(plan.Where(kv => kv.Value.Address.Category == AssetCategory.Backgrounds && resolutions[kv.Key] == ResolutionKind.Create)
                                                     .Select(kv => kv.Value.Address.AssetName));
        var newCode = new HashSet<string>(codeOps.Select(op => op.Address.AssetName));
        var roomPatches = new List<(PlannedAsset Op, RoomJson Json)>();
        foreach (PlannedAsset ro in roomOps)
        {
            if (!Path.GetExtension(ro.SourceFile).Equals(".json", StringComparison.OrdinalIgnoreCase))
                return MergeResult.Fail(ro.OwningMod, $"room file {ro.Address.RelativePath} must be a .json patch.");
            if (data.Rooms.ByName(ro.Address.AssetName) is null)
            {
                if (!manifests[ro.OwningMod].DeclaresNew("rooms", ro.Address.AssetName, out _))
                    return MergeResult.Fail(ro.OwningMod, $"room '{ro.Address.AssetName}' doesn't exist in this game and isn't declared in mod.json > newAssets.rooms.");
                if (data.IsGameMaker2())
                    return MergeResult.Fail(ro.OwningMod, $"room '{ro.Address.AssetName}' is new, but PJUM can only create rooms in GameMaker 1 games so far.");
                Rooms.RoomImporter.CreateEmptyRoom(data, ro.Address.AssetName);
            }
            try
            {
                RoomJson json = Rooms.RoomImporter.Read(ro.SourceFile);
                Rooms.RoomImporter.Validate(data, ro.Address, json, newObjects, newBackgrounds, newCode);
                roomPatches.Add((ro, json));
            }
            catch (Exception ex)
            {
                return MergeResult.Fail(ro.OwningMod, $"cannot apply room {ro.Address.RelativePath}: {ex.Message}");
            }
        }

        var gamePatches = new List<(PlannedAsset Op, GameJson Json)>();
        foreach (PlannedAsset go in gameOps)
        {
            GameJson? json;
            try
            {
                json = JsonSerializer.Deserialize<GameJson>(File.ReadAllText(go.SourceFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                return MergeResult.Fail(go.OwningMod, $"{go.Address.RelativePath} is invalid: {ex.Message}");
            }
            if (json is null || new[] { json.Name, json.DisplayName, json.FileName }.All(string.IsNullOrWhiteSpace))
                return MergeResult.Fail(go.OwningMod, $"{go.Address.RelativePath} sets none of \"name\", \"displayName\" or \"fileName\".");
            gamePatches.Add((go, json));
        }

        var spriteInfos = new Dictionary<string, (SpriteJson Json, string Mod)>();
        foreach (PlannedAsset pa in plan.Values.Where(p => p.Address.Category == AssetCategory.SpriteInfo))
        {
            try
            {
                spriteInfos[pa.Address.AssetName] = (Sprites.SpriteImporter.ReadJson(pa.SourceFile), pa.OwningMod);
            }
            catch (Exception ex)
            {
                return MergeResult.Fail(pa.OwningMod, $"cannot apply {pa.Address.RelativePath}: {ex.Message}");
            }
        }
        var spriteBoxes = spriteInfos.Where(kv => kv.Value.Json.Width is not null && kv.Value.Json.Height is not null)
                                     .ToDictionary(kv => kv.Key, kv => (kv.Value.Json.Width!.Value, kv.Value.Json.Height!.Value));
        var spriteMasks = new Dictionary<string, (List<(int Index, string File)> Files, string Mod)>();
        foreach (PlannedAsset pa in plan.Values.Where(p => p.Address.Category == AssetCategory.SpriteMasks))
        {
            ModSource owner = mods.First(m => m.Name == pa.OwningMod);
            var files = new List<(int Index, string File)>();
            foreach (string file in EnumerateModFiles(owner, manifests[owner.Name]))
            {
                if (!ModAddressParser.TryParse(ToModRelative(owner, file), out ModAddress? a, out _)) continue;
                if (target is not null && !target.Accepts(a)) continue;
                a = renames.Alias(a, owner.Name);
                if (a.Category == AssetCategory.SpriteMasks && a.AssetName == pa.Address.AssetName && a.Container == pa.Address.Container)
                    files.Add((a.Frame!.Value, file));
            }
            spriteMasks[pa.Address.AssetName] = (files, owner.Name);
        }

        string dataDir = Path.GetFullPath(gameDir);
        string sharedDir = Path.GetFullPath(gameRoot ?? gameDir);
        HashSet<string> mergeWrittenFiles = MergeWrittenFiles(data, dataDir);
        foreach (PlannedAsset pa in plan.Values)
        {
            if (pa.Address.Category != AssetCategory.Files) continue;

            string shown = FilesPath(pa.Address);
            string root = pa.Address.IsShared ? sharedDir : dataDir;
            string dest = Path.GetFullPath(Path.Combine(root, pa.Address.AssetName));
            if (mergeWrittenFiles.Contains(dest))
                return MergeResult.Fail(pa.OwningMod,
                    $"{shown} would replace {Path.GetRelativePath(root, dest).Replace('\\', '/')}, which the merge itself rewrites. Remove it from files/.");

            if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return MergeResult.Fail(pa.OwningMod, $"{shown} points outside the game folder.");

            if (ProgramExtensions.Contains(Path.GetExtension(dest)))
                warnings.Add($"[{pa.OwningMod}] {shown} is a program file; it was copied into the game folder.");
        }

        // ---- plan is VALID. Only now do we mutate. ------------------------

        // === PHASE 5a: Tier 1 (paths wired; sounds wired) ==
        // Sounds are grouped by asset first, because a sound is up to TWO files
        // (<name>.json metadata + <name>.ogg/.wav bytes) sharing one asset key.
        
        // NOTE on ordering: assets are created BEFORE code compiles, and it has to
        // stay that way. GML resolves asset names (sounds, objects, sprites) to
        // indices at compile time, so an asset the code references must already
        // exist in `data` or the name compiles as an undefined variable — the game
        // then dies at runtime with "not set before reading it".
        
        var soundBundles = new Dictionary<string, SoundBundle>();

        var objectlist = new Dictionary<string, (ModAddress address, string owningMod, string sourceFile, bool create)>();
        
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
                        return MergeResult.Fail(pa.OwningMod, $"failed applying path {pa.Address.RelativePath}: {ex.Message}");
                    }
                    break;

                case AssetCategory.Sounds:
                    // A sound is up to two files sharing one asset key; the plan
                    // dict only kept one. Record the owning mod + asset once;
                    // we re-scan the mod's folder for both files below.
                    soundBundles.TryAdd(kv.Key, new SoundBundle(pa.Address, pa.OwningMod, create));
                    break;

                case AssetCategory.Objects:
                    // An object requires that it be processed last after the texture repack.
                    // Objects that rely on sprites will fail to find newly created sprites not originally in game.
                    objectlist.TryAdd(kv.Key, (pa.Address, pa.OwningMod, pa.SourceFile, create));
                    break;
            }
        }

        foreach (SoundBundle b in soundBundles.Values)
        {
            // Re-scan the owning mod for both files by asset name (the plan
            // collapsed them into one entry, so don't trust a single SourceFile).
            ModSource owner = mods.First(m => m.Name == b.OwningMod);
            string? jsonFile = FindSoundFile(owner, b.Address, target, renames, ".json");
            string? audioFile = FindSoundFile(owner, b.Address, target, renames, ".wav", ".ogg", ".mp3");

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
        
        // === PHASE 5b: textures -> one repack ====================
        string overridesDir = Path.Combine(Path.GetTempPath(), "pjum_merge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(overridesDir);
        try
        {
            bool anyTexture = false;
            foreach (PlannedAsset pa in plan.Values)
            {
                // Only sprites/backgrounds/fonts have a flat texture name.
                if (ToFlatTextureName(pa.Address) is null) continue;

                // The plan collapsed every frame of a sprite into ONE entry (AssetKey has
                // no frame index). Re-scan the WINNING mod for all frame files of THIS
                // asset so every frame reaches the repacker — not just the last one
                // enumerated. Mirrors how sounds re-scan for their json+audio pair.
                ModSource owner = mods.First(m => m.Name == pa.OwningMod);
                foreach (string file in EnumerateModFiles(owner, manifests[owner.Name]))
                {
                    string rel = ToModRelative(owner, file);
                    if (!ModAddressParser.TryParse(rel, out ModAddress? a, out _)) continue;
                    if (target is not null && !target.Accepts(a)) continue;   // same name in another chapter
                    a = renames.Alias(a, owner.Name);
                    if (a.Category  != pa.Address.Category)  continue;
                    if (a.AssetName != pa.Address.AssetName) continue;
                    if (a.Container != pa.Address.Container) continue;

                    string? flat = ToFlatTextureName(a);
                    if (flat is null) continue;
                    File.Copy(file, Path.Combine(overridesDir, flat), overwrite: true);
                    anyTexture = true;
                }
            }

            if (anyTexture)
            {
                RepackResult rr = await _repacker.RepackAsync(data, overridesDir, spriteBoxes, newBackgrounds);
                warnings.AddRange(rr.Warnings);
            }
        }
        finally
        {
            TryDelete(overridesDir);
        }
        
        foreach (string spriteName in spriteInfos.Keys.Union(spriteMasks.Keys))
        {
            bool hasInfo = spriteInfos.TryGetValue(spriteName, out var info);
            bool hasMasks = spriteMasks.TryGetValue(spriteName, out var masks);
            string owner = hasInfo ? info.Mod : masks.Mod;
            UndertaleSprite? sprite = data.Sprites.ByName(spriteName);
            if (sprite is null)
                return MergeResult.Fail(owner, $"sprite '{spriteName}' has a sprite.json or masks, but no frames, and isn't in this game.");
            try
            {
                Sprites.SpriteImporter.Apply(data, sprite, hasInfo ? info.Json : null, hasMasks ? masks.Files : [], warnings, owner);
            }
            catch (Exception ex)
            {
                return MergeResult.Fail(owner, $"failed applying sprite {spriteName}: {ex.Message}");
            }
        }

        // Note: Event connect to gml scripts and if a newly created object ties to connect an event that is missing (gml code script added in code section), may result in an object build failure
        
        // === PHASE 5c: objects -> CodeImportGroup ================================
        foreach (KeyValuePair<string, (ModAddress address, string owningMod, string sourceFile, bool create)> obj in objectlist)
            if (obj.Value.create)
                Objects.ObjectImporter.CreateEmpty(data, obj.Value.address.AssetName);

        foreach (KeyValuePair<string, (ModAddress address, string owningMod, string sourceFile, bool create)> obj in objectlist)
        {
            try
            {
                Objects.ObjectImporter.Apply(data, obj.Value.address, obj.Value.sourceFile, false);
            }
            catch (Exception ex)
            {
                return MergeResult.Fail(obj.Value.owningMod, $"failed applying object {obj.Value.address.RelativePath}: {ex.Message}");
            }
        }
        
        OrderNewAssets(data.Sprites, firstNew.Sprites, NewAssetOrder("sprites"));
        OrderNewAssets(data.Backgrounds, firstNew.Backgrounds, NewAssetOrder("backgrounds"));
        OrderNewAssets(data.Sounds, firstNew.Sounds, NewAssetOrder("sounds"));
        OrderNewAssets(data.GameObjects, firstNew.Objects, NewAssetOrder("objects"));

        // === PHASE 5d: code -> CodeImportGroup ================================
        if (codeOps.Count > 0)
        {
            var codeRank = NewAssetOrder("code").Select((name, i) => (name, i)).GroupBy(x => x.name).ToDictionary(x => x.Key, x => x.First().i);
            var modRank = mods.Select((m, i) => (m.Name, i)).GroupBy(x => x.Name).ToDictionary(x => x.Key, x => x.First().i);
            codeOps = codeOps.OrderBy(op => modRank.GetValueOrDefault(op.OwningMod))
                             .ThenBy(op => codeRank.TryGetValue(op.Address.AssetName, out int rank) ? rank : int.MaxValue)
                             .ToList();
            var group = new CodeImportGroup(data);
            foreach (PlannedAsset co in codeOps)
            {
                if (CheckIfFileConflict(co))
                {
                    return MergeResult.Fail(co.OwningMod,
                        $"code '{co.Address.AssetName}' has both .gml and .json; use one or the other.");
                }
                try
                {
                    if (Path.GetExtension(co.SourceFile).Equals(".json", StringComparison.OrdinalIgnoreCase))
                        CodeImporter.Apply(group, co.Address, co.SourceFile);
                    else
                        group.QueueReplace(co.Address.AssetName, File.ReadAllText(co.SourceFile));
                }
                catch (Exception ex)
                {
                    return MergeResult.Fail(co.OwningMod,
                        $"failed applying code {co.Address.RelativePath}: {ex.Message}");
                }
            }
            CompileResult cr = group.Import(false);
            if (!cr.Successful)
                return MergeResult.Fail("(compile)", "GML compile failed: " + cr.PrintAllErrors(false));
        }

        // === PHASE 5e: objects phase 2 -> CodeImportGroup at the end ================================
        foreach (KeyValuePair<string, (ModAddress address, string owningMod, string sourceFile, bool create)> obj in objectlist)
        {
            try
            {
                Objects.ObjectImporter.Apply(data, obj.Value.address, obj.Value.sourceFile, false);
            }
            catch (Exception ex)
            {
                return MergeResult.Fail(obj.Value.owningMod, $"failed applying object {obj.Value.address.RelativePath}: {ex.Message}");
            }
        }

        var rooms = new Rooms.RoomImporter(data);
        foreach ((PlannedAsset op, RoomJson json) in roomPatches)
        {
            try
            {
                rooms.Apply(op.Address, json, op.OwningMod);
            }
            catch (Exception ex)
            {
                return MergeResult.Fail(op.OwningMod, $"failed applying room {op.Address.RelativePath}: {ex.Message}");
            }
        }
        warnings.AddRange(rooms.Warnings);
        warnings.AddRange(renames.Warnings);
        conflictLog.AddRange(rooms.Conflicts);

        var gameFieldOwners = new Dictionary<string, List<string>>();
        foreach ((PlannedAsset op, GameJson json) in gamePatches)
        {
            void Set(string field, string? value, Action<UndertaleString> apply)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                apply(data.Strings.MakeString(value));
                string key = $"{op.Address.Game}/{op.Address.Container}/game:{field}";
                if (!gameFieldOwners.TryGetValue(key, out List<string>? owners))
                    gameFieldOwners[key] = owners = new List<string>();
                owners.Remove(op.OwningMod);
                owners.Add(op.OwningMod);
            }
            Set("name", json.Name, v => data.GeneralInfo.Name = v);
            Set("displayName", json.DisplayName, v => data.GeneralInfo.DisplayName = v);
            Set("fileName", json.FileName, v => data.GeneralInfo.FileName = v);
        }
        foreach ((string key, List<string> owners) in gameFieldOwners)
            if (owners.Count > 1)
                conflictLog.Add(new ConflictLogEntry(key, owners[^1], owners.Take(owners.Count - 1).ToList()));
        
        try
        {
            // === PHASE 6: write data.win back IN PLACE =====================
            await dataService.SaveAsync(data, dataWinPath);
        }
        finally
        {
            TryDelete(overridesDir);
        }
        
        foreach (PlannedAsset pa in plan.Values)
        {
            if (pa.Address.Category != AssetCategory.Files) continue;

            string rel = pa.Address.AssetName;
            if (!pa.Address.IsShared && !rel.Contains('/') && soundBundles.Values.Any(b => b.Address.AssetName.Equals(Path.GetFileNameWithoutExtension(rel), StringComparison.OrdinalIgnoreCase)))
                warnings.Add($"[{pa.OwningMod}] files/{rel} has the same name as a merged sound; the files/ copy was applied last.");

            string dest = Path.GetFullPath(Path.Combine(pa.Address.IsShared ? sharedDir : dataDir, rel));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(pa.SourceFile, dest, overwrite: true);
            }
            catch (Exception ex)
            {
                return MergeResult.Fail(pa.OwningMod, $"failed copying {FilesPath(pa.Address)}: {ex.Message}");
            }
        }

        return MergeResult.Ok(conflictLog, warnings);
    }

    // -- helpers ------------------------------------------------------------

    private sealed record PlannedAsset(ModAddress Address, string OwningMod, string SourceFile);

    private static void OrderNewAssets<T>(IList<T> assets, int firstNew, List<string> wanted) where T : UndertaleNamedResource
    {
        if (firstNew >= assets.Count || wanted.Count == 0)
            return;
        var rank = new Dictionary<string, int>();
        foreach (string name in wanted)
            rank.TryAdd(name, rank.Count);
        List<T> ordered = assets.Skip(firstNew)
                                .OrderBy(a => a?.Name?.Content is string name && rank.TryGetValue(name, out int r) ? r : int.MaxValue)
                                .ToList();
        for (int i = 0; i < ordered.Count; i++)
            assets[firstNew + i] = ordered[i];
    }

    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".so", ".dylib" };

    private static HashSet<string> MergeWrittenFiles(UndertaleData data, string dataDir)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(dataDir, "data.win") };
        if (data.AudioGroups is null) return names;

        for (int i = 0; i < data.AudioGroups.Count; i++)
        {
            names.Add(Path.Combine(dataDir, $"audiogroup{i}.dat"));
            string? custom = data.AudioGroups[i]?.Path?.Content;
            if (!string.IsNullOrEmpty(custom))
                names.Add(Path.GetFullPath(Path.Combine(dataDir, custom.Replace('\\', '/'))));
        }
        return names;
    }

    private static string FilesPath(ModAddress a) => (a.IsShared ? "shared/files/" : "files/") + a.AssetName;

    // Find <assetName>.<ext> for a sound within the owning mod, matching the
    // asset's addressed location (same route/container). Returns null if absent.
    private static string? FindSoundFile(ModSource mod, ModAddress addr, ModTarget? target, AssetRenames renames, params string[] exts)
    {
        foreach (string file in EnumerateModFiles(mod))
        {
            string rel = Path.GetRelativePath(mod.ModDirectory, file).Replace('\\', '/');
            if (!ModAddressParser.TryParse(rel, out ModAddress? a, out _)) continue;
            if (target is not null && !target.Accepts(a)) continue;   // same name in another chapter
            a = renames.Alias(a, mod.Name);
            if (a.Category != AssetCategory.Sounds) continue;
            if (a.AssetName != addr.AssetName) continue;
            if (a.Container != addr.Container) continue;   // same audiogroup/container
            string ext = Path.GetExtension(file).ToLowerInvariant();
            foreach (string want in exts)
                if (ext == want) return file;
        }
        return null;
    }
    
    private static bool CheckIfFileConflict(PlannedAsset co)
    {
        if (co.SourceFile.Contains(".json"))
        {
            if (File.Exists(co.SourceFile.Replace(".json", ".gml"))) return true;
        }
        else if (co.SourceFile.Contains(".gml"))
        {
            if (File.Exists(co.SourceFile.Replace(".gml", ".json"))) return true;
        }
        return false;
    }
    
    // Pairs a sound's json + audio files (same asset key) for one Apply call.
    private sealed class SoundBundle(ModAddress addr, string owningMod, bool create)
    {
        public ModAddress Address { get; } = addr;
        public string OwningMod { get; } = owningMod;
        public bool Create { get; } = create;
    }

    private static string AssetKey(ModAddress a)
    {
        string cat = a.Category.ToString().ToLowerInvariant();
        return $"{a.Game}/{a.Container}/{cat}:{a.AssetName}";
    }

    private static string? ToFlatTextureName(ModAddress a)
    {
        return a.Category switch
        {
            AssetCategory.Sprites     => $"{a.AssetName}_{a.Frame}.png",
            AssetCategory.Backgrounds => $"{a.AssetName}.png",
            AssetCategory.Fonts       => $"{a.AssetName}.png",
            _ => null
        };
    }

    private static bool TryReadManifest(ModSource mod, [NotNullWhen(true)] out ModManifest? manifest, out string? error)
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
    
    private static IEnumerable<string> EnumerateModFiles(ModSource mod, ModManifest? manifest = null)
    {
        if (!Directory.Exists(mod.ModDirectory)) yield break;

        // Manager metadata that lives in the mod folder but is NOT a game asset.
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mod.json" };
        if (!string.IsNullOrEmpty(manifest?.ImageFileName))
            skip.Add(manifest.ImageFileName);

        foreach (string f in Directory.EnumerateFiles(mod.ModDirectory, "*", SearchOption.AllDirectories))
        {
            // Only skip metadata sitting at the mod ROOT — a game asset legitimately
            // named splash.png deeper in the tree must still merge.
            string rel = Path.GetRelativePath(mod.ModDirectory, f);
            bool atRoot = !rel.Contains(Path.DirectorySeparatorChar) && !rel.Contains('/');
            if (atRoot && skip.Contains(Path.GetFileName(f))) continue;

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
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch { /* ignored, don't care if folder deletion fails, only just a make things neat */ }
    }
}