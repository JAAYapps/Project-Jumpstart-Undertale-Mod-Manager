using System.Collections.Generic;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.GameLocator;

public interface IGameLocatorService
{
    List<string> FindGameInstallations();
    void AddCustomGame(string path);
}