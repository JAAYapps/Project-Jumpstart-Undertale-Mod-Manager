using PjumModLib;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.ModExtract;

public interface IModExtractService
{
    Task<ExtractResult> ExtractAsync(string originalDataFile, string moddedDataFile, string modDirectory, string modName);
}
