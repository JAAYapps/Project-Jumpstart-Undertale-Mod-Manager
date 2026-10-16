namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Launcher;

public sealed record LaunchOptions(
    string Runner,
    string? ProtonDirectory = null,
    string? CompatDataDirectory = null,
    string? SteamClientDirectory = null,
    string? SteamAppId = null);
