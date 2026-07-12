namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Launcher;

public interface ILauncherService
{
    Task LaunchAndWaitAsync(string gameDir, string exeName, string dataName, Action<string> log, CancellationToken ct = default);
}