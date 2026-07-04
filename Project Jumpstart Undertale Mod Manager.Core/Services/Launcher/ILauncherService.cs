namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Launcher;

public interface ILauncherService
{
    public void LaunchGame(string gamepath, string selectedExecutable, string selectedDataFile);
}