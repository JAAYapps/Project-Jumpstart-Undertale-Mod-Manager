

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UndertaleModLib;
using UndertaleModLib.Models;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Paths;

// ---------------------------------------------------------------------------
// TIER 1: paths. Pure declarative data — no compiler, no texture packing, no
// cross-references. A path is Smooth/Closed/Precision + a list of X/Y/Speed
// points, exactly as the tool's Path editor shows.
//
// JSON (lands at <route>/paths/<name>.json):
//   { "smooth": false, "closed": false, "precision": 4,
//     "points": [ { "x": 500, "y": 400, "speed": 100 }, ... ] }
//
// VERIFY notes: field names taken from the Path editor panel —
//   UndertalePath.IsSmooth, .IsClosed, .Precision, .Points (X/Y/Speed).
// If your fork names any of these differently, the gated test will fail on the
// exact property.
// ---------------------------------------------------------------------------

public sealed class PathJson
{
    [JsonPropertyName("smooth")]   public bool Smooth { get; set; }
    [JsonPropertyName("closed")]   public bool Closed { get; set; }
    [JsonPropertyName("precision")] public uint Precision { get; set; } = 4;
    [JsonPropertyName("points")]   public List<PathPointJson> Points { get; set; } = new();
}

public sealed class PathPointJson
{
    [JsonPropertyName("x")]     public float X { get; set; }
    [JsonPropertyName("y")]     public float Y { get; set; }
    [JsonPropertyName("speed")] public float Speed { get; set; } = 100f;
}

public static class PathImporter
{
    /// <summary>
    /// Apply one path asset. `create` = build a new UndertalePath and add it;
    /// otherwise mutate the existing one in place. The resolver has already
    /// decided which (Replace vs Create) — this just executes it.
    /// </summary>
    public static void Apply(UndertaleData data, ModAddress addr, string sourceFile, bool create)
    {
        PathJson json = ReadJson(sourceFile);

        UndertalePath path;
        if (create)
        {
            path = new UndertalePath { Name = data.Strings.MakeString(addr.AssetName) };
            data.Paths.Add(path);
        }
        else
        {
            path = data.Paths.ByName(addr.AssetName)
                   ?? throw new InvalidOperationException(
                       $"Path '{addr.AssetName}' expected to exist (Replace) but was not found.");
        }

        path.IsSmooth = json.Smooth;
        path.IsClosed = json.Closed;
        path.Precision = json.Precision;

        // Replace the whole point list — a path override is a full redefinition.
        path.Points.Clear();
        foreach (PathPointJson p in json.Points)
        {
            path.Points.Add(new UndertalePath.PathPoint
            {
                X = p.X,
                Y = p.Y,
                Speed = p.Speed
            });
        }
    }

    private static PathJson ReadJson(string file)
    {
        try
        {
            PathJson json = JsonSerializer.Deserialize<PathJson>(
                File.ReadAllText(file),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (json is null)
                throw new InvalidOperationException($"Path JSON '{file}' parsed to null.");
            return json;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Path JSON '{file}' is invalid: {ex.Message}", ex);
        }
    }
}