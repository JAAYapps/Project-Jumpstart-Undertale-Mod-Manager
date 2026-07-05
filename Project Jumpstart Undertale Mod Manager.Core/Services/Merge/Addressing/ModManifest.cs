using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;

// ---------------------------------------------------------------------------
// mod.json shape. Keyed-entry grammar, modelled on the SEOSBL bootloader
// config: identity is the JSON KEY, properties hang off the entry.
//
// The folder tree remains the manifest for WHAT a mod touches. mod.json's only
// structural job is `newAssets`: the deliberate, written-down subset of assets
// the mod is allowed to CREATE (assets not present in the target game). An
// undeclared missing asset is an error — that's the typo trap. This is NOT a
// copy of the folder tree; it's the small set of intentional additions.
//
//   {
//     "name": "Sonic Lyoko Tale",
//     "author": "Joshua",
//     "version": "1.0.0",
//     "integrityHash": null,            // RESERVED — see note below, not yet enforced
//     "newAssets": {
//       "sprites": { "spr_mynewthing": { "frames": 4 } },
//       "code":    { "gml_Script_scr_x": {} },
//       "objects": { "obj_custom": { "sprite": "spr_mynewthing", "parent": "obj_x" } }
//     }
//   }
//
// integrityHash: reserved slot, mirrors the bootloader's per-boot IntegrityHash.
// When enforced later, the manager hashes the mod payload and rejects a
// tampered/half-downloaded mod before it touches a data.win. Present now so the
// idea isn't lost; the resolver ignores it for v1.
// ---------------------------------------------------------------------------

public sealed class ModManifest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    // RESERVED. Not verified in v1. See file header.
    [JsonPropertyName("integrityHash")]
    public string? IntegrityHash { get; set; }

    // category -> assetName -> properties. Empty/absent means "declares nothing new".
    [JsonPropertyName("newAssets")]
    public Dictionary<string, Dictionary<string, NewAssetEntry>> NewAssets { get; set; } = new();

    /// <summary>True if this manifest declares (category, name) as a new asset.</summary>
    public bool DeclaresNew(string category, string name, out NewAssetEntry entry)
    {
        entry = null;
        return NewAssets.TryGetValue(category, out var byName)
               && byName.TryGetValue(name, out entry);
    }
}

/// <summary>
/// Properties for a declared-new asset. All optional; which ones matter depends
/// on the category (a new sprite uses Frames; a new object uses Sprite/Parent).
/// Unknown JSON fields are ignored, so categories can grow their own props later.
/// </summary>
public sealed class NewAssetEntry
{
    [JsonPropertyName("frames")]
    public int? Frames { get; set; }        // sprites

    [JsonPropertyName("sprite")]
    public string? Sprite { get; set; }     // objects: sprite reference by name

    [JsonPropertyName("parent")]
    public string? Parent { get; set; }     // objects: parent reference by name
}