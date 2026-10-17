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
//     "integrityHash": "sha256:...",   // written by Package mod, see note below
//     "newAssets": {
//       "sprites": { "spr_mynewthing": { "frames": 4 } },
//       "code":    { "gml_Script_scr_x": {} },
//       "objects": { "obj_custom": { "sprite": "spr_mynewthing", "parent": "obj_x" } }
//     }
//   }
//
// integrityHash: mirrors the bootloader's per-boot IntegrityHash. Package mod
// hashes every file except mod.json (the manager rewrites that one) and writes
// it here. Add Mod hashes the unzipped files again and asks before keeping a
// mod that doesn't match, so a half-downloaded mod never reaches a data.win.
// ---------------------------------------------------------------------------

public sealed class ModManifest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    // Checked when a mod zip is added. See file header.
    [JsonPropertyName("integrityHash")]
    public string? IntegrityHash { get; set; }

    // Dependencies, keyed by required mod NAME -> version constraint string
    // (e.g. ">=1.0.0"). A required mod not present in the merge set is a HARD
    // FAIL — the dependent mod can't apply correctly without it.
    [JsonPropertyName("requires")]
    public Dictionary<string, string> Requires { get; set; } = new();

    // category -> assetName -> properties. Empty/absent means "declares nothing new".
    [JsonPropertyName("newAssets")]
    public Dictionary<string, Dictionary<string, NewAssetEntry>> NewAssets { get; set; } = new();

    public string? ImageFileName { get; set; }
    
    /// <summary>True if this manifest declares (category, name) as a new asset.</summary>
    public bool DeclaresNew(string category, string name, out NewAssetEntry? entry)
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