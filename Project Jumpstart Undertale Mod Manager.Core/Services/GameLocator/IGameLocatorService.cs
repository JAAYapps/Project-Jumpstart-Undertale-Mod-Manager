using System.Collections.Generic;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.GameLocator;

public interface IGameLocatorService
{
    List<string> FindGameInstallations();
    IReadOnlyList<string> MissingGames { get; }
    string AddCustomGame(string path);
    GameEntry? GetEntry(string path);
    void SaveChoices(string path, string? executable, string? dataFile);
}