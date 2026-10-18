namespace Project_Jumpstart_Undertale_Mod_Manager.Services.ModExtract;

public sealed record ExtractResult(
    string ModDirectory,
    IReadOnlyDictionary<string, int> WrittenByCategory,
    IReadOnlyList<string> NotConverted);

public interface IModExtractService
{
    Task<ExtractResult> ExtractAsync(string originalDataFile, string moddedDataFile, string modDirectory, string modName);
}
