namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;

public interface IModMergeService
{
    // Merges modDataPath onto baseDataPath, writes the result to outputPath.
    // Returns the conflicts found (empty list = clean merge).
    Task<MergeResult> ApplyAsync(
        string baseDataPath,
        IReadOnlyList<ModSource> mods,   // in load order
        string outputPath);
}

public sealed record ModSource(string Name, string ModDirectory);
public sealed record ModConflict(string AssetName, string FirstMod, string SecondMod);
public sealed record MergeResult(bool Success, IReadOnlyList<ModConflict> Conflicts);