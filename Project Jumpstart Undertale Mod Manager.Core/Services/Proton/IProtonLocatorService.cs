using System.Collections.Generic;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Proton;

public interface IProtonLocatorService
{
    string? SteamRoot { get; }
    IReadOnlyList<ProtonInstall> FindProtonInstalls();
    string? SteamAppIdFor(string gameDirectory);
    string? CompatDataFor(string gameDirectory);
    ProtonInstall? SteamChoiceFor(string appId);
}
