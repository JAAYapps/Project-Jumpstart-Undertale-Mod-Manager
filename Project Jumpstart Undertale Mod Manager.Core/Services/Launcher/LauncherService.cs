using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Launcher;

public class LauncherService : ILauncherService
{
    public void LaunchGame(string gamePath, string exeName, string dataName)
    {
        string exePath = Path.Combine(gamePath, exeName);
        string dataPath = Path.Combine(gamePath, dataName);

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = gamePath,
            UseShellExecute = false
        };

        if (OperatingSystem.IsLinux() && exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "wine";
            startInfo.ArgumentList.Add(exePath);
        }
        else
        {
            startInfo.FileName = exePath;
        }

        startInfo.ArgumentList.Add("-game");
        startInfo.ArgumentList.Add(dataPath);

        try
        {
            Process.Start(startInfo);
        }
        catch (Win32Exception)
        {
            string batPath = Path.Combine(gamePath, "Play_Modded.bat");
            string batContent = $"\"{exeName}\" -game \"{dataName}\"";
            File.WriteAllText(batPath, batContent);
            
            Console.WriteLine("Wine not found. Created Play_Modded.bat for Steam/Proton use.");
        }
    }
}