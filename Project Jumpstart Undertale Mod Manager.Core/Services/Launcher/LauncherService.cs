using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Launcher;

public class LauncherService : ILauncherService
{
    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Folder not found: {path}");

        // Shell execute hands the folder to the desktop's file manager
        // (Explorer on Windows, xdg-open on Linux, open on macOS).
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public async Task LaunchAndWaitAsync(string gameDir, string exeName, string dataName, Action<string> log, LaunchOptions? options = null, CancellationToken ct = default)
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
        string runner = (options?.Runner ?? "Automatic").ToLowerInvariant();
        if (!OperatingSystem.IsLinux())
            runner = "native";
        else if (runner == "automatic")
            runner = isWindowsExe ? "wine" : "native";
        bool usingWine = runner == "wine";
        bool usingProton = runner == "proton";
        string protonScript = Path.Combine(options?.ProtonDirectory ?? "", "proton");

        if (usingProton)
        {
            if (options?.ProtonDirectory is null || !File.Exists(protonScript))
                throw new InvalidOperationException("Proton was chosen for this game, but no Proton install was found. Pick one under Manager Config, or install Proton through Steam.");
            if (string.IsNullOrEmpty(options.CompatDataDirectory))
                throw new InvalidOperationException("Proton was chosen for this game, but there is no Wine folder to run it in.");

            Directory.CreateDirectory(options.CompatDataDirectory);
            if (options.SteamAppId is not null)
                File.WriteAllText(Path.Combine(gameDir, "steam_appid.txt"), options.SteamAppId);

            psi.FileName = protonScript;
            psi.ArgumentList.Add("runinprefix");
            psi.ArgumentList.Add(exePath);
            psi.Environment["STEAM_COMPAT_DATA_PATH"] = options.CompatDataDirectory;
            psi.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = options.SteamClientDirectory ?? "";
            log($"Using {Path.GetFileName(options.ProtonDirectory)} with Wine folder {options.CompatDataDirectory}");
        }
        else if (usingWine)
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
            string hint = usingProton
                ? $"Check that '{protonScript}' exists and is executable."
                : usingWine
                    ? "Is wine installed and on PATH?"
                    : $"Check that '{exePath}' exists and is executable.";
            throw new InvalidOperationException(
                $"Could not start '{psi.FileName}'. {hint}", ex);
        }

        if (usingProton)
        {
            await proc.WaitForExitAsync(ct);
            string wineserver = Path.Combine(options!.ProtonDirectory!, "files", "bin", "wineserver");
            string prefix = Path.Combine(options.CompatDataDirectory!, "pfx");
            if (File.Exists(wineserver) && Directory.Exists(prefix))
            {
                var wait = new ProcessStartInfo(wineserver) { UseShellExecute = false };
                wait.ArgumentList.Add("-w");
                wait.Environment["WINEPREFIX"] = prefix;
                using Process? waiting = Process.Start(wait);
                if (waiting is not null)
                    await waiting.WaitForExitAsync(ct);
            }
            log($"'{exeName}' exited...");
            return;
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