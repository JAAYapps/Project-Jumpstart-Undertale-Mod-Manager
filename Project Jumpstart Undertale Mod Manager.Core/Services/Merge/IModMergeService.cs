namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;

// ---------------------------------------------------------------------------
// Multi-mod dispatcher. Design (locked with Joshua):
//
//   * LAST-WINS on conflict. Two mods touching spr_susie is NOT an error —
//     later mod in load order wins. Logged, not failed.
//   * DEPENDENCIES hard-fail. A mod's mod.json "requires" another mod that
//     isn't in the set -> that mod cannot apply -> abort the run naming it.
//   * VALIDATE-THEN-APPLY. The whole plan (parse + requires + resolve every
//     address) is checked BEFORE any mutation. If it can't succeed, we abort
//     having touched nothing and return WHICH mod failed and WHY.
//   * RETRY is the caller's job: on failure, reload the pristine base and call
//     ApplyAsync again with the failing mod removed. Every run — including
//     retries — starts from the untouched base. The base file on disk IS the
//     rollback; we never half-mutate.
//
// This class never prompts. The UI reads MergeResult.FailedMod/Reason and asks
// "retry without {FailedMod}?", then re-calls with the reduced list.
//
// Tier 1 asset application (objects/sounds/paths) is STUBBED here — code and
// textures are wired; the one-liners come next.
// ---------------------------------------------------------------------------

public sealed record ModSource(string Name, string ModDirectory);

/// <summary>One mod-vs-mod overlap. Not a failure — the record of who won.</summary>
public sealed record ConflictLogEntry(string AssetKey, string WinningMod, IReadOnlyList<string> OverriddenMods);

public sealed record MergeResult(
    bool Success,
    string FailedMod,                          // null on success
    string Reason,                             // null on success
    IReadOnlyList<ConflictLogEntry> Conflicts, // last-wins record (informational)
    IReadOnlyList<string> Warnings)
{
    public static MergeResult Ok(IReadOnlyList<ConflictLogEntry> conflicts, IReadOnlyList<string> warnings)
        => new(true, null, null, conflicts, warnings);

    public static MergeResult Fail(string mod, string reason)
        => new(false, mod, reason, Array.Empty<ConflictLogEntry>(), Array.Empty<string>());
}

public interface IModMergeService
{
    /// <summary>
    /// Merge <paramref name="mods"/> (in load order — later wins) onto the base
    /// data file, writing to <paramref name="outputPath"/>. Validates the whole
    /// plan first; on any failure returns Success=false naming the failing mod
    /// and does NOT write. On success, writes the merged file.
    /// </summary>
    Task<MergeResult> ApplyAsync(string baseDataPath, IReadOnlyList<ModSource> mods, string outputPath);
}