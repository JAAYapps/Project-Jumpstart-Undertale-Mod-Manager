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
//   * DIR-MODEL. Merge mutates a prepared game directory IN PLACE. The caller
//     (launcher) makes gameDir a disposable temp copy at <managerRoot>/tempgame,
//     runs the runner with --game, and deletes it on close. The pristine game
//     install is never touched; the temp copy IS the rollback.
//   * RETRY is the caller's job: re-prepare a fresh temp copy and call
//     ApplyAsync again with the failing mod removed.
//
// This class never prompts. The UI reads MergeResult.FailedMod/Reason and asks
// "retry without {FailedMod}?", then re-calls with the reduced list.
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
    /// Merge <paramref name="mods"/> (in load order — later wins) INTO a prepared
    /// game directory, mutating it in place. <paramref name="gameDir"/> must
    /// contain data.win directly (Undertale: the game dir; Deltarune: a chapter
    /// dir), plus any audiogroupN.dat / loose .ogg the game ships. The caller
    /// (launcher) owns making this a disposable temp copy and running --game.
    ///
    /// Validates the whole plan first; on any failure returns Success=false
    /// naming the failing mod and does NOT mutate. On success, data.win (and any
    /// touched audiogroup .dat) are rewritten in place inside gameDir.
    /// </summary>
    Task<MergeResult> ApplyAsync(string gameDir, IReadOnlyList<ModSource> mods);
}