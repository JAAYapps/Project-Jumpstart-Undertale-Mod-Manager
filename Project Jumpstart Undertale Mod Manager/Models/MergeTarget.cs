using System.IO;

namespace Project_Jumpstart_Undertale_Mod_Manager.Models;

// A single mergeable unit: a folder that directly contains a data.win.
// Undertale exposes one (the game root). Deltarune exposes one per chapter.
public sealed class MergeTarget
{
    public string DisplayName { get; init; } = string.Empty;   // "Undertale", "Chapter 1"
    public string DataDirectory { get; init; } = string.Empty; // folder holding data.win
    public string DataFileName { get; init; } = "data.win";    // the file inside it

    public string DataFilePath => Path.Combine(DataDirectory, DataFileName);
}