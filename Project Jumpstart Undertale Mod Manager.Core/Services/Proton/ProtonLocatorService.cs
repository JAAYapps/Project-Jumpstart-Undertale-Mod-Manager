using Gameloop.Vdf;
using Gameloop.Vdf.Linq;
using Project_Jumpstart_Undertale_Mod_Manager.Services.SteamScanner;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Proton;

public class ProtonLocatorService(ISteamScanner steamScanner) : IProtonLocatorService
{
    public string? SteamRoot => steamScanner.GetSteamRootPath();

    public IReadOnlyList<ProtonInstall> FindProtonInstalls()
    {
        var installs = new List<ProtonInstall>();
        string? root = SteamRoot;
        if (root is null) return installs;

        foreach (string library in Libraries(root))
        {
            string common = Path.Combine(library, "steamapps", "common");
            if (!Directory.Exists(common)) continue;
            foreach (string dir in Directory.GetDirectories(common, "Proton*").OrderBy(d => d))
                AddInstall(installs, dir);
        }

        string tools = Path.Combine(root, "compatibilitytools.d");
        if (Directory.Exists(tools))
        {
            foreach (string dir in Directory.GetDirectories(tools).OrderBy(d => d))
                AddInstall(installs, dir);
        }
        return installs;
    }

    public string? SteamAppIdFor(string gameDirectory) => FindManifest(gameDirectory)?.AppId;

    public string? CompatDataFor(string gameDirectory)
    {
        var manifest = FindManifest(gameDirectory);
        return manifest is null ? null : Path.Combine(manifest.Value.SteamApps, "compatdata", manifest.Value.AppId);
    }

    public ProtonInstall? SteamChoiceFor(string appId)
    {
        string? root = SteamRoot;
        if (root is null) return null;

        string config = Path.Combine(root, "config", "config.vdf");
        Dictionary<string, string> mapping;
        try
        {
            mapping = ReadCompatToolMapping(File.ReadAllText(config));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        string? toolName = mapping.GetValueOrDefault(appId) is { Length: > 0 } forApp ? forApp : mapping.GetValueOrDefault("0");
        if (string.IsNullOrEmpty(toolName)) return null;

        var installs = FindProtonInstalls();
        string? folder = toolName switch
        {
            "proton_experimental" => "Proton - Experimental",
            "proton_hotfix" => "Proton Hotfix",
            _ => null
        };
        if (folder is not null)
            return installs.FirstOrDefault(i => i.Name == folder);

        if (toolName.StartsWith("proton_") && int.TryParse(toolName["proton_".Length..], out int major))
            return installs.FirstOrDefault(i => i.Name == $"Proton {major}.0" || i.Name == $"Proton {major}");

        return installs.FirstOrDefault(i => i.Name == toolName);
    }

    private static void AddInstall(List<ProtonInstall> installs, string dir)
    {
        if (!File.Exists(Path.Combine(dir, "proton"))) return;
        string name = Path.GetFileName(dir);
        if (installs.Any(i => i.Name == name)) return;
        installs.Add(new ProtonInstall(name, dir));
    }

    private IEnumerable<string> Libraries(string root)
    {
        var libraries = new List<string> { root };
        try
        {
            foreach (string library in steamScanner.ResolveLibraryPaths(root))
            {
                if (!libraries.Contains(library))
                    libraries.Add(library);
            }
        }
        catch (Exception)
        {
        }
        return libraries;
    }

    private (string SteamApps, string AppId)? FindManifest(string gameDirectory)
    {
        string? root = SteamRoot;
        if (root is null) return null;

        string game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
        foreach (string library in Libraries(root))
        {
            string steamApps = Path.Combine(library, "steamapps");
            string common = Path.Combine(steamApps, "common") + Path.DirectorySeparatorChar;
            if (!game.StartsWith(common, StringComparison.Ordinal)) continue;

            string installDir = game[common.Length..].Split(Path.DirectorySeparatorChar)[0];
            foreach (string manifest in Directory.GetFiles(steamApps, "appmanifest_*.acf"))
            {
                try
                {
                    VProperty acf = VdfConvert.Deserialize(File.ReadAllText(manifest));
                    if (acf.Value["installdir"]?.ToString() == installDir)
                        return (steamApps, acf.Value["appid"]?.ToString() ?? Path.GetFileNameWithoutExtension(manifest)["appmanifest_".Length..]);
                }
                catch (Exception)
                {
                }
            }
        }
        return null;
    }

    private static Dictionary<string, string> ReadCompatToolMapping(string vdf)
    {
        var mapping = new Dictionary<string, string>();
        int start = vdf.IndexOf("\"CompatToolMapping\"", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return mapping;

        List<string> tokens = Tokens(vdf, start + "\"CompatToolMapping\"".Length);
        int depth = 0;
        string? app = null;
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (token == "{")
            {
                depth++;
            }
            else if (token == "}")
            {
                depth--;
                if (depth == 0) break;
                if (depth == 1) app = null;
            }
            else if (depth == 1 && i + 1 < tokens.Count && tokens[i + 1] == "{")
            {
                app = token;
            }
            else if (depth == 2 && app is not null && token.Equals("name", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
            {
                mapping[app] = tokens[i + 1];
                i++;
            }
            else if (depth == 2 && i + 1 < tokens.Count && tokens[i + 1] != "{" && tokens[i + 1] != "}")
            {
                i++;
            }
        }
        return mapping;
    }

    private static List<string> Tokens(string text, int from)
    {
        var tokens = new List<string>();
        int depth = 0;
        for (int i = from; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{' || c == '}')
            {
                tokens.Add(c.ToString());
                depth += c == '{' ? 1 : -1;
                if (depth == 0 && c == '}') break;
            }
            else if (c == '"')
            {
                int end = i + 1;
                var value = new System.Text.StringBuilder();
                while (end < text.Length && text[end] != '"')
                {
                    if (text[end] == '\\' && end + 1 < text.Length) end++;
                    value.Append(text[end]);
                    end++;
                }
                tokens.Add(value.ToString());
                i = end;
            }
        }
        return tokens;
    }
}
