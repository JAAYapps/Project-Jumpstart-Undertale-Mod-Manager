using UndertaleModLib;
using PjumModLib;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Data;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.ModExtract;

public class ModExtractService(IDataService dataService) : IModExtractService
{
    public async Task<ExtractResult> ExtractAsync(string originalDataFile, string moddedDataFile, string modDirectory, string modName)
    {
        UndertaleData original = await dataService.LoadAsync(originalDataFile);
        UndertaleData modded = await dataService.LoadAsync(moddedDataFile);
        string originalFolder = Path.GetDirectoryName(Path.GetFullPath(originalDataFile))!;
        string moddedFolder = Path.GetDirectoryName(Path.GetFullPath(moddedDataFile))!;
        return await Task.Run(() => ModExtractor.Extract(original, modded, originalFolder, moddedFolder, modDirectory, modName));
    }
}
