using System;
using System.Collections.Generic;
using System.IO;
using Gameloop.Vdf;
using Gameloop.Vdf.Linq;
using Microsoft.Win32;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.SteamScanner;

public class CrossPlatformSteamScanner : ISteamScanner
{
    public string? GetSteamRootPath()
    {
        if (OperatingSystem.IsWindows()) //
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Wow6432Node\Valve\Steam") 
                            ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            return key?.GetValue("InstallPath") as string;
        }
        else if (OperatingSystem.IsLinux())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            
            // Check Native Linux Steam Location first
            string nativePath = Path.Combine(home, ".local", "share", "Steam");
            if (Directory.Exists(nativePath)) return nativePath;

            // Fallback to Flatpak Sandboxed Steam Location
            string flatpakPath = Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
            if (Directory.Exists(flatpakPath)) return flatpakPath;
        }

        return null;
    }

    public List<string> ResolveLibraryPaths(string steamRoot)
    {
        var paths = new List<string>();
        string vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");

        if (!File.Exists(vdfPath)) return paths;

        // Gameloop.Vdf works identically on Linux and Windows
        string vdfContent = File.ReadAllText(vdfPath);
        VProperty root = VdfConvert.Deserialize(vdfContent);

        foreach (VProperty folder in root.Value.Children<VProperty>())
        {
            var pathToken = folder.Value["path"];
            if (pathToken != null)
            {
                paths.Add(pathToken.ToString());
            }
        }
        return paths;
    }
}