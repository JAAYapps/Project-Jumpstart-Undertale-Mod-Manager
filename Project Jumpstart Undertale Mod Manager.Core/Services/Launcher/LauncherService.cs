using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Launcher;

public class LauncherService : ILauncherService
{
    public async Task LaunchAndWaitAsync(string gameDir, string exeName, string dataName, Action<string> log, CancellationToken ct = default)
    {
        log($"Launching '{exeName}'...");
        string exePath  = Path.Combine(gameDir, exeName);
        string dataPath = Path.Combine(gameDir, dataName);

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = gameDir,
            UseShellExecute  = false,
        };

        bool isWindowsExe = exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        bool usingWine = OperatingSystem.IsLinux() && isWindowsExe;
        
        if (usingWine)
        {
            psi.FileName = "wine";               // needs wine on PATH
            psi.ArgumentList.Add(exePath);
        }
        else
        {
            // native ELF runner (or a native-platform exe): make sure it's runnable
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(exePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            psi.FileName = exePath;
        }

        psi.ArgumentList.Add("-game");
        psi.ArgumentList.Add(dataPath);

        Process proc;
        try
        {
            proc = Process.Start(psi)
                   ?? throw new InvalidOperationException($"Process.Start returned no handle for '{psi.FileName}'.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            string hint = usingWine
                ? "Is wine installed and on PATH?"
                : $"Check that '{exePath}' exists and is executable.";
            throw new InvalidOperationException(
                $"Could not start '{psi.FileName}'. {hint}", ex);
        }

        var procName = proc.ProcessName; 
        
        await Task.Run(() =>
        {
            int timeout = 5;
            while (timeout > 0)
            {
                var isRunning = Process.GetProcessesByName(exeName).Length > 0 || Process.GetProcessesByName(procName).Length > 0;
                timeout--;
                if (isRunning)
                    timeout = 5;
                Thread.Sleep(2000);
            }
        });
        log($"'{exeName}' exited...");
    }
}