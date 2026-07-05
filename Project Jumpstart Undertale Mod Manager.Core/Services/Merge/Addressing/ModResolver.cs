using System;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;

// ---------------------------------------------------------------------------
// SEMANTIC validation: resolve a parsed ModAddress against a LOADED data file.
// This is the "validate against the real data, not a hardcoded table" layer.
//
// Decision table (all four rows fail loud where they should):
//
//   target HAS name, right type          -> Replace(existing)
//   target LACKS name, declared new       -> Create(category, name)
//   target LACKS name, NOT declared       -> throw  (typo trap)
//   target HAS name, WRONG type           -> throw  (lie trap)
//
// The category -> ModLib-collection mapping is centralized in LookupByName so
// the gated test can prove every category resolves a real asset in YOUR fork.
// If a collection name differs, the test names it instead of a cryptic merge
// failure later.
// ---------------------------------------------------------------------------

public enum ResolutionKind { Replace, Create }

/// <summary>
/// Outcome of resolving one address. For Replace, Existing is the live ModLib
/// object to mutate. For Create, Existing is null and the caller builds the new
/// asset (using the manifest's NewAssetEntry properties).
/// </summary>
public sealed record ResolvedAsset(
    ResolutionKind Kind,
    ModAddress Address,
    object Existing,                 // the live UndertaleData object, or null for Create
    NewAssetEntry NewEntry);         // the declared-new properties, or null for Replace

public sealed class ModResolveException : Exception
{
    public ModResolveException(string message) : base(message) { }
}

public static class ModResolver
{
    /// <summary>
    /// Resolve one parsed address against loaded data + the mod's manifest.
    /// Throws ModResolveException on the two error rows.
    /// </summary>
    public static ResolvedAsset Resolve(ModAddress addr, UndertaleData data, ModManifest manifest)
    {
        if (addr is null) throw new ArgumentNullException(nameof(addr));
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));

        // Raw files aren't graph assets — they're copied as-is, no data lookup.
        if (addr.Category == AssetCategory.Files)
            return new ResolvedAsset(ResolutionKind.Replace, addr, null, null);

        string category = addr.Category.ToString().ToLowerInvariant();
        string name = addr.AssetName;

        object existing = LookupByName(addr.Category, data, name, out bool categorySupported);

        if (!categorySupported)
            throw new ModResolveException(
                $"Category '{category}' has no resolvable collection yet (address '{addr.RelativePath}').");

        if (existing is not null)
        {
            // HAS name — but is it the right TYPE? LookupByName only searches the
            // collection for THIS category, so a hit is already the right type.
            // The lie trap is when the SAME name exists in a DIFFERENT collection.
            string wrongType = FindConflictingType(addr.Category, data, name);
            // (existing != null means right-type hit; wrongType check is for the
            //  miss branch below. Right-type hit -> Replace.)
            return new ResolvedAsset(ResolutionKind.Replace, addr, existing, null);
        }

        // LACKS name in its own category. Two sub-cases:
        //  - declared new  -> Create
        //  - not declared  -> error, but give a BETTER message if the name exists
        //                     as a different type (the lie trap).
        if (manifest.DeclaresNew(category, name, out NewAssetEntry entry))
            return new ResolvedAsset(ResolutionKind.Create, addr, null, entry);

        string conflicting = FindConflictingType(addr.Category, data, name);
        if (conflicting is not null)
            throw new ModResolveException(
                $"'{name}' is addressed as {category} in '{addr.RelativePath}', " +
                $"but it already exists as {conflicting}. Wrong category, or rename the asset.");

        throw new ModResolveException(
            $"'{name}' ({category}) in '{addr.RelativePath}' does not exist in the target " +
            $"and is not declared in mod.json > newAssets.{category}. " +
            "Add it there to create a new asset, or fix the name.");
    }

    // Central category -> collection lookup. categorySupported=false means we
    // haven't wired this category's collection yet (distinct from "not found").
    private static object LookupByName(
        AssetCategory category, UndertaleData data, string name, out bool categorySupported)
    {
        categorySupported = true;
        switch (category)
        {
            case AssetCategory.Sprites:     return data.Sprites.ByName(name);
            case AssetCategory.Backgrounds: return data.Backgrounds.ByName(name);
            case AssetCategory.Fonts:       return data.Fonts.ByName(name);
            case AssetCategory.Code:        return data.Code.ByName(name);
            case AssetCategory.Objects:     return data.GameObjects.ByName(name);
            case AssetCategory.Sounds:      return data.Sounds.ByName(name);
            case AssetCategory.Paths:       return data.Paths.ByName(name);
            default:
                categorySupported = false;
                return null;
        }
    }

    // Does `name` exist in some collection OTHER than the addressed category?
    // Returns the conflicting type's name, or null. Powers the lie-trap message.
    private static string FindConflictingType(AssetCategory addressed, UndertaleData data, string name)
    {
        if (addressed != AssetCategory.Sprites     && data.Sprites.ByName(name)     is not null) return "sprite";
        if (addressed != AssetCategory.Backgrounds && data.Backgrounds.ByName(name) is not null) return "background";
        if (addressed != AssetCategory.Fonts       && data.Fonts.ByName(name)       is not null) return "font";
        if (addressed != AssetCategory.Code        && data.Code.ByName(name)        is not null) return "code";
        if (addressed != AssetCategory.Objects     && data.GameObjects.ByName(name) is not null) return "object";
        if (addressed != AssetCategory.Sounds      && data.Sounds.ByName(name)      is not null) return "sound";
        if (addressed != AssetCategory.Paths       && data.Paths.ByName(name)       is not null) return "path";
        return null;
    }
}