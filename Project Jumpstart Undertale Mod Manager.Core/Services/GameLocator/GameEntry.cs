namespace Project_Jumpstart_Undertale_Mod_Manager.Services.GameLocator;

public sealed class GameEntry
{
    public string Path { get; set; } = "";
    public bool Added { get; set; }
    public string? Executable { get; set; }
    public string? DataFile { get; set; }
    public string? Runner { get; set; }
    public string? Proton { get; set; }
}
