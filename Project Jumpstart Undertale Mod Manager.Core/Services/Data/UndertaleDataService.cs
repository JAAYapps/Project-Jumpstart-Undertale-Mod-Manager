using UndertaleModLib;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Data;

public class UndertaleDataService
{
    public async Task<UndertaleData> LoadAsync(string dataPath)
    {
        await using var fs = new FileStream(dataPath, FileMode.Open, FileAccess.Read);
        return UndertaleIO.Read(fs);
    }

    public async Task SaveAsync(UndertaleData data, string dataPath)
    {
        await using var fs = new FileStream(dataPath, FileMode.Create, FileAccess.Write);
        UndertaleIO.Write(fs, data);
    }
}