using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;

// ---------------------------------------------------------------------------
// STRUCTURAL validation only. Pure path-split into a typed address; knows
// nothing about whether the target exists (that's the semantic resolver).
//
// Grammar is VARIABLE-DEPTH. The category keyword is the pivot:
//
//   <game> / <route...> / <category> / <asset...>
//            ^^^^^^^^^^^  1+ segments, meaning is game-specific
//
//   undertale/data.win/sprites/spr_susie/0.png
//     game=undertale  route=[data.win]              category=sprites  asset=spr_susie frame=0
//
//   deltarune/chapter2/audiogroup1/sounds/snd_x.ogg
//     game=deltarune  route=[chapter2,audiogroup1]  category=sounds   asset=snd_x
//
// Undertale collapses slot+container into one route segment; Deltarune spells
// out chapter + container. The parser doesn't care how many — it finds the
// category and splits there. Interpreting the route (which segment is the
// chapter, which is the container) belongs to the semantic layer, per the
// validate-against-loaded-data design.
// ---------------------------------------------------------------------------

public enum AssetCategory
{
    Sprites,
    Backgrounds,
    Fonts,
    Code,
    Objects,
    Sounds,
    Paths,
    Files   // raw copy-as-is (shared/ music, lang json, etc.)
}

/// <summary>
/// A structurally-parsed mod asset address. Everything comes from the path;
/// nothing here has been checked against a real data file.
/// </summary>
public sealed record ModAddress(
    string Game,                        // "undertale" | "deltarune"
    IReadOnlyList<string> Route,        // ["data.win"] | ["chapter2","audiogroup1"] — game-specific meaning
    AssetCategory Category,
    string AssetName,                   // "spr_susie", "gml_Script_scr_x", "bg_ruinsbg"
    int? Frame,                         // sprite frame index, else null
    string RelativePath)                // original path, for messages + copy operations
{
    public bool IsRawFile => Category == AssetCategory.Files;

    // Convenience: by our convention the container is the LAST route segment
    // (data.win / audiogroup1 / files); the chapter (if any) is everything
    // before it. The semantic resolver may interpret differently, but these
    // cover the current layout.
    public string Container => Route[Route.Count - 1];
    public IReadOnlyList<string> RoutePrefix => Route.Take(Route.Count - 1).ToList();
}

/// <summary>Thrown when a path is not a well-formed mod address.</summary>
public sealed class ModAddressFormatException : Exception
{
    public ModAddressFormatException(string message) : base(message) { }
}

public static class ModAddressParser
{
    private static readonly string[] KnownGames = { "undertale", "deltarune" };

    public static ModAddress Parse(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ModAddressFormatException("Empty path.");

        string norm = relativePath.Replace('\\', '/').TrimStart('.', '/');
        string[] parts = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Minimum: game / <route:1+> / category / <asset:1+>  == at least 4.
        if (parts.Length < 4)
            throw new ModAddressFormatException(
                $"Too few path segments in '{relativePath}'. " +
                "Expected <game>/<route...>/<category>/<asset...>.");

        string game = parts[0].ToLowerInvariant();
        if (!KnownGames.Contains(game))
            throw new ModAddressFormatException(
                $"Unknown game '{parts[0]}' in '{relativePath}'. Known: {string.Join(", ", KnownGames)}.");

        // Find the category: first segment after the game that is a known
        // category keyword. Between game and it is the route; after it, the asset.
        int categoryIndex = -1;
        AssetCategory category = default;
        for (int i = 1; i < parts.Length; i++)
        {
            if (TryParseCategory(parts[i], out category))
            {
                categoryIndex = i;
                break;
            }
        }

        if (categoryIndex == -1)
            throw new ModAddressFormatException(
                $"No known category segment found in '{relativePath}'. " +
                $"Expected one of: {KnownCategoryList()}.");

        // Route is everything between game and category — at least one segment.
        if (categoryIndex == 1)
            throw new ModAddressFormatException(
                $"Missing route (container) segment in '{relativePath}'. " +
                "Expected <game>/<route...>/<category>/<asset...>.");

        string[] route = parts[1..categoryIndex];
        string[] assetSegments = parts[(categoryIndex + 1)..];

        if (assetSegments.Length == 0)
            throw new ModAddressFormatException(
                $"Missing asset after category in '{relativePath}'.");

        return category == AssetCategory.Sprites
            ? ParseSprite(game, route, assetSegments, relativePath)
            : ParseSingleFile(game, route, category, assetSegments, relativePath);
    }

    public static bool TryParse(string relativePath, out ModAddress address, out string error)
    {
        try
        {
            address = Parse(relativePath);
            error = null;
            return true;
        }
        catch (ModAddressFormatException ex)
        {
            address = null;
            error = ex.Message;
            return false;
        }
    }

    // Sprites: <spriteName>/<frame>.png — exactly two asset segments.
    private static ModAddress ParseSprite(
        string game, string[] route, string[] assetSegments, string original)
    {
        if (assetSegments.Length != 2)
            throw new ModAddressFormatException(
                $"Sprite path '{original}' must be sprites/<spriteName>/<frame>.png (one name segment, one frame file).");

        string spriteName = assetSegments[0];
        string frameStem = Path.GetFileNameWithoutExtension(assetSegments[1]);
        if (!int.TryParse(frameStem, out int frame) || frame < 0)
            throw new ModAddressFormatException(
                $"Sprite frame must be a non-negative integer file name (got '{assetSegments[1]}') in '{original}'.");

        return new ModAddress(game, route, AssetCategory.Sprites, spriteName, frame, original);
    }

    private static ModAddress ParseSingleFile(
        string game, string[] route, AssetCategory category,
        string[] assetSegments, string original)
    {
        // Raw files may nest; keep the tail so the copy step recreates structure.
        if (category == AssetCategory.Files)
        {
            string rel = string.Join("/", assetSegments);
            return new ModAddress(game, route, category, rel, null, original);
        }

        if (assetSegments.Length != 1)
            throw new ModAddressFormatException(
                $"{category} path '{original}' must be a single file: {category.ToString().ToLowerInvariant()}/<name>.<ext>.");

        string name = Path.GetFileNameWithoutExtension(assetSegments[0]);
        if (string.IsNullOrEmpty(name))
            throw new ModAddressFormatException($"Missing asset name in '{original}'.");

        return new ModAddress(game, route, category, name, null, original);
    }

    private static bool TryParseCategory(string raw, out AssetCategory category)
    {
        switch (raw.ToLowerInvariant())
        {
            case "sprites": category = AssetCategory.Sprites; return true;
            case "backgrounds": category = AssetCategory.Backgrounds; return true;
            case "fonts": category = AssetCategory.Fonts; return true;
            case "code": category = AssetCategory.Code; return true;
            case "objects": category = AssetCategory.Objects; return true;
            case "sounds": category = AssetCategory.Sounds; return true;
            case "paths": category = AssetCategory.Paths; return true;
            case "files": category = AssetCategory.Files; return true;
            default: category = default; return false;
        }
    }

    private static string KnownCategoryList() =>
        string.Join(", ", Enum.GetNames(typeof(AssetCategory)).Select(s => s.ToLowerInvariant()));
}