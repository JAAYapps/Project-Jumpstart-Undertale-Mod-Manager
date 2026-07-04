using System.Text.Json;
using Gameloop.Vdf;
using Gameloop.Vdf.Linq;
using Microsoft.Win32;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.GameLocator;

public class GameLocatorService : IGameLocatorService
{
    // Exact Steam AppIDs for Undertale and Deltarune
    private readonly HashSet<string> _targetAppIds = new() { "391540", "1671210" };

    private const string CustomGamesFile = "custom_games.json";
    
    public List<string> FindGameInstallations()
    {
        var foundPaths = new List<string>();
        
        if (File.Exists(CustomGamesFile))
        {
            try
            {
                string json = File.ReadAllText(CustomGamesFile);
                var customGames = JsonSerializer.Deserialize<List<string>>(json);
                if (customGames != null)
                {
                    foundPaths.AddRange(customGames);
                }
            }
            catch
            {
                // File is empty or corrupt, ignore and continue
            }
        }
        
        string? steamRoot = GetSteamRootPath();

        if (string.IsNullOrEmpty(steamRoot)) return foundPaths;

        var libraryPaths = ResolveLibraryPaths(steamRoot);

        foreach (var library in libraryPaths)
        {
            Console.WriteLine($"Found {library} installations");
            string steamappsPath = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamappsPath)) continue;

            // Scan the manifest files directly to see if the target games are installed
            foreach (var appId in _targetAppIds)
            {
                string manifestPath = Path.Combine(steamappsPath, $"appmanifest_{appId}.acf");
                if (!File.Exists(manifestPath)) continue;

                try
                {
                    string acfContent = File.ReadAllText(manifestPath);
                    VProperty manifest = VdfConvert.Deserialize(acfContent);
                    string? installDir = manifest.Value["installdir"]?.ToString();

                    if (!string.IsNullOrEmpty(installDir))
                    {
                        string absolutePath = Path.Combine(steamappsPath, "common", installDir);
                        if (Directory.Exists(absolutePath))
                        {
                            foundPaths.Add(absolutePath);
                        }
                    }
                }
                catch
                {
                    // Fail silently on corrupt or locked manifest configurations
                    continue;
                }
            }
        }

        return foundPaths;
    }
    
    public void AddCustomGame(string path)
    {
        var customGames = new List<string>();

        if (File.Exists(CustomGamesFile))
        {
            try
            {
                string json = File.ReadAllText(CustomGamesFile);
                customGames = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch { }
        }

        if (!customGames.Contains(path))
        {
            customGames.Add(path);
            
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(CustomGamesFile, JsonSerializer.Serialize(customGames, options));
        }
    }

    private string? GetSteamRootPath()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Wow6432Node\Valve\Steam") 
                            ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            return key?.GetValue("InstallPath") as string;
        }
        
        if (OperatingSystem.IsLinux())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            
            // 1. Native/Package Manager Steam Location
            string nativePath = Path.Combine(home, ".local", "share", "Steam");
            if (Directory.Exists(nativePath)) return nativePath;

            // 2. Flatpak Sandboxed Steam Location
            string flatpakPath = Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
            if (Directory.Exists(flatpakPath)) return flatpakPath;
        }

        return null;
    }

    private List<string> ResolveLibraryPaths(string steamRoot)
    {
        var paths = new List<string> { steamRoot }; // Root installation is always a library
        string vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");

        if (!File.Exists(vdfPath)) return paths;

        try
        {
            string vdfContent = File.ReadAllText(vdfPath);
            VProperty root = VdfConvert.Deserialize(vdfContent);

            foreach (VProperty folder in root.Value.Children<VProperty>())
            {
                var pathToken = folder.Value["path"];
                if (pathToken != null)
                {
                    string pathStr = pathToken.ToString();
                    if (!paths.Contains(pathStr))
                    {
                        paths.Add(pathStr);
                    }
                }
            }
        }
        catch
        {
            // Fallback to basic list if libraryfolders.vdf fails to parse
        }

        return paths;
    }
}