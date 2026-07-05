namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;

// ---------------------------------------------------------------------------
// The 6-step merge pipeline, apply-in-order + always-repack.
//
//
// PIPELINE (per target data file, e.g. Undertale data.win or
// Deltarune chapterN_windows/data.win):
//   1. Load base data file into a live UndertaleData graph.
//   2. Apply Tier 1 (declarative: objects/events, paths, sounds) — reference ops.
//   3. Apply Tier 2 (code) — CompileGroup.QueueCodeReplace + Compile.
//   4. Collect texture overrides (sprites/backgrounds/fonts) into a flat dir.
//   5. One full repack (TextureRepacker) folding base + overrides together.
//   6. Write the merged data file back out.
//
// CONFLICTS: mods are applied in load order; an asset touched by two mods is a
// conflict keyed on asset NAME (not file-relative IDs). Detected while collecting,
// before any graph mutation, so a conflicted merge can stop clean.
// ---------------------------------------------------------------------------

/// <summary>One mod on disk, already resolved to a folder. Load order is the list order.</summary>
public sealed record ModSource(string Name, string ModDirectory);

/// <summary>Two mods writing the same named asset in the same target container.</summary>
public sealed record ModConflict(string AssetName, string FirstMod, string SecondMod);

public sealed record MergeResult(bool Success, IReadOnlyList<ModConflict> Conflicts, IReadOnlyList<string> Warnings);

public interface IModMergeService
{
    /// <summary>
    /// Applies <paramref name="mods"/> (in order) onto the base data file at
    /// <paramref name="baseDataPath"/> and writes the merged result to
    /// <paramref name="outputPath"/>. If two mods collide on an asset name the
    /// merge stops before writing and the conflicts are returned.
    /// </summary>
    Task<MergeResult> ApplyAsync(string baseDataPath, IReadOnlyList<ModSource> mods, string outputPath);
}