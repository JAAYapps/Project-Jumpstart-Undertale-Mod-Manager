using UndertaleModLib;
using UndertaleModLib.Models;
using PjumModLib;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;

public sealed class AssetRenames(UndertaleData data)
{
    private readonly Dictionary<(string Category, string Old), (string New, string Mod)> _byOld = new();
    private readonly Dictionary<(string Category, string New), (string Old, string Mod)> _byNew = new();

    public List<string> Warnings { get; } = new();

    public static IEnumerable<UndertaleNamedResource>? Assets(UndertaleData data, string category) => category switch
    {
        "sprites" => data.Sprites,
        "backgrounds" => data.Backgrounds,
        "fonts" => data.Fonts,
        "objects" => data.GameObjects,
        "sounds" => data.Sounds,
        "paths" => data.Paths,
        "rooms" => data.Rooms,
        _ => null
    };

    public bool TryAdd(string modName, Dictionary<string, Dictionary<string, string>> renames, out string error)
    {
        error = "";
        foreach ((string category, Dictionary<string, string> byOld) in renames)
        {
            IEnumerable<UndertaleNamedResource>? assets = Assets(data, category);
            if (assets is null)
            {
                error = $"mod.json renames '{category}', but PJUM can only rename sprites, backgrounds, fonts, objects, sounds, paths and rooms.";
                return false;
            }

            foreach ((string oldName, string newName) in byOld)
            {
                if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName) || oldName == newName)
                    continue;
                if (_byOld.TryGetValue((category, oldName), out var earlier))
                {
                    if (earlier.New == newName)
                        continue;
                    error = $"renames {category} '{oldName}' to '{newName}', but '{earlier.Mod}' renames it to '{earlier.New}'.";
                    return false;
                }
                if (_byNew.TryGetValue((category, newName), out var taken))
                {
                    error = $"renames {category} '{oldName}' to '{newName}', but '{taken.Mod}' already gives that name to '{taken.Old}'.";
                    return false;
                }
                if (!assets.Any(a => a?.Name?.Content == oldName))
                {
                    error = $"renames {category} '{oldName}' to '{newName}', but there is no '{oldName}' in this game.";
                    return false;
                }
                if (assets.Any(a => a?.Name?.Content == newName))
                {
                    error = $"renames {category} '{oldName}' to '{newName}', but this game already has a '{newName}'.";
                    return false;
                }
                _byOld[(category, oldName)] = (newName, modName);
                _byNew[(category, newName)] = (oldName, modName);
            }
        }
        return true;
    }

    public void Apply()
    {
        foreach (((string category, string oldName), (string newName, _)) in _byOld)
        {
            UndertaleNamedResource asset = Assets(data, category)!.First(a => a?.Name?.Content == oldName);
            asset.Name = data.Strings.MakeString(newName);
            if (category != "objects")
                continue;

            string oldPrefix = $"gml_Object_{oldName}_", newPrefix = $"gml_Object_{newName}_";
            foreach (UndertaleCode code in data.Code)
                if (code?.Name?.Content is string codeName && codeName.StartsWith(oldPrefix, StringComparison.Ordinal))
                    code.Name = data.Strings.MakeString(newPrefix + codeName[oldPrefix.Length..]);
            foreach (UndertaleCodeLocals locals in data.CodeLocals ?? [])
                if (locals?.Name?.Content is string localsName && localsName.StartsWith(oldPrefix, StringComparison.Ordinal))
                    locals.Name = data.Strings.MakeString(newPrefix + localsName[oldPrefix.Length..]);
        }
    }

    public ModAddress Alias(ModAddress address, string modName)
    {
        string category = address.Category.ToString().ToLowerInvariant();
        string? newName = null;
        string? by = null;
        if (_byOld.TryGetValue((category, address.AssetName), out var renamed))
            (newName, by) = renamed;
        else if (address.Category == AssetCategory.Code)
            foreach (((string c, string oldName), (string objectNew, string mod)) in _byOld)
                if (c == "objects" && address.AssetName.StartsWith($"gml_Object_{oldName}_", StringComparison.Ordinal))
                {
                    newName = $"gml_Object_{objectNew}_" + address.AssetName[$"gml_Object_{oldName}_".Length..];
                    by = mod;
                    break;
                }

        if (newName is null)
            return address;
        if (by != modName)
        {
            string warning = $"[{modName}] uses the old name '{address.AssetName}'; '{by}' renamed it to '{newName}', so it was applied to that.";
            if (!Warnings.Contains(warning))
                Warnings.Add(warning);
        }
        return address with { AssetName = newName };
    }
}
