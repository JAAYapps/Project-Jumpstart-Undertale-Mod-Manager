namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Launcher;

public interface ILauncherService
{
    Task LaunchAndWaitAsync(string gameDir, string exeName, string dataName, Action<string> log, LaunchOptions? options = null, CancellationToken ct = default);

    IReadOnlyList<string> FindSpaceCutFiles(string exePath);

    GameCopyResult RefreshGameCopy(string gameDir, string copyDir);

    /// <summary>Show a folder in the system file manager.</summary>
    void OpenFolder(string path);
}