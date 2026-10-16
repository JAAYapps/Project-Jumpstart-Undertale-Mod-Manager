using System.Text.Json;
using Gameloop.Vdf;
using Gameloop.Vdf.Linq;
using Microsoft.Win32;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.GameLocator;

public class GameLocatorService : IGameLocatorService
{
    // Exact Steam AppIDs for Undertale and Deltarune
    private readonly HashSet<string> _targetAppIds = new() { "391540", "1671210" };

    private static string GamesFile => Path.Combine(AppContext.BaseDirectory, "games.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public IReadOnlyList<string> MissingGames { get; private set; } = [];

    public List<string> FindGameInstallations()
    {
        var foundPaths = new List<string>();
        var missing = new List<string>();

        foreach (GameEntry entry in LoadEntries())
        {
            if (!entry.Added) continue;
            if (Directory.Exists(entry.Path))
                AddUnique(foundPaths, entry.Path);
            else
                missing.Add(entry.Path);
        }
        MissingGames = missing;
        
        string? steamRoot = GetSteamRootPath();

        if (string.IsNullOrEmpty(steamRoot)) return foundPaths;

        var libraryPaths = ResolveLibraryPaths(steamRoot);

        foreach (var library in libraryPaths)
        {
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
                            AddUnique(foundPaths, NormalizePath(absolutePath));
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
    
    public string AddCustomGame(string path)
    {
        var entries = LoadEntries();
        GameEntry? entry = FindEntry(entries, path);
        if (entry is null)
        {
            entry = new GameEntry { Path = NormalizePath(path) };
            entries.Add(entry);
        }
        entry.Added = true;
        SaveEntries(entries);
        return entry.Path;
    }

    public GameEntry? GetEntry(string path) => FindEntry(LoadEntries(), path);

    public void SaveChoices(string path, string? executable, string? dataFile, string? runner, string? proton)
    {
        var entries = LoadEntries();
        GameEntry? entry = FindEntry(entries, path);
        if (entry is null)
        {
            entry = new GameEntry { Path = NormalizePath(path) };
            entries.Add(entry);
        }
        entry.Executable = string.IsNullOrEmpty(executable) ? null : executable;
        entry.DataFile = string.IsNullOrEmpty(dataFile) ? null : dataFile;
        entry.Runner = string.IsNullOrEmpty(runner) ? null : runner;
        entry.Proton = string.IsNullOrEmpty(proton) ? null : proton;
        SaveEntries(entries);
    }

    public void SaveEnabledCodes(string path, IEnumerable<string> codeNames)
    {
        var entries = LoadEntries();
        GameEntry? entry = FindEntry(entries, path);
        if (entry is null)
        {
            entry = new GameEntry { Path = NormalizePath(path) };
            entries.Add(entry);
        }
        entry.EnabledCodes = codeNames.ToList();
        SaveEntries(entries);
    }

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static GameEntry? FindEntry(List<GameEntry> entries, string path)
    {
        string normalized = NormalizePath(path);
        return entries.FirstOrDefault(e => string.Equals(e.Path, normalized, PathComparison));
    }

    private static void AddUnique(List<string> paths, string path)
    {
        if (!paths.Any(p => string.Equals(p, path, PathComparison)))
            paths.Add(path);
    }

    private static List<GameEntry> LoadEntries()
    {
        if (File.Exists(GamesFile))
        {
            try
            {
                return JsonSerializer.Deserialize<List<GameEntry>>(File.ReadAllText(GamesFile)) ?? new List<GameEntry>();
            }
            catch (JsonException)
            {
                File.Move(GamesFile, GamesFile + ".unreadable", overwrite: true);
                return new List<GameEntry>();
            }
        }
        return new List<GameEntry>();
    }

    private static void SaveEntries(List<GameEntry> entries)
    {
        File.WriteAllText(GamesFile, JsonSerializer.Serialize(entries, JsonOptions));
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