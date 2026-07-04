
using UndertaleModLib;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Data;

public interface IDataService
{
    Task<UndertaleData> LoadAsync(string dataPath);
    Task SaveAsync(UndertaleData data, string dataPath);
}