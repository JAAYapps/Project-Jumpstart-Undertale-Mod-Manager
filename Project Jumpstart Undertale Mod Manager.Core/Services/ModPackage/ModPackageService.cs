using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.ModPackage;

public class ModPackageService : IModPackageService
{
    private const string ManifestName = "mod.json";
    private const string HashKey = "integrityHash";
    private const string HashPrefix = "sha256:";

    public string ComputeIntegrityHash(string modDirectory)
    {
        var files = Directory.EnumerateFiles(modDirectory, "*", SearchOption.AllDirectories)
            .Select(f => (full: f, rel: Path.GetRelativePath(modDirectory, f).Replace('\\', '/')))
            .Where(f => !f.rel.Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.rel, StringComparer.Ordinal);

        using var total = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (full, rel) in files)
        {
            byte[] content;
            using (var stream = File.OpenRead(full))
                content = SHA256.HashData(stream);
            total.AppendData(Encoding.UTF8.GetBytes(rel + "\0" + Convert.ToHexStringLower(content) + "\n"));
        }
        return HashPrefix + Convert.ToHexStringLower(total.GetHashAndReset());
    }

    public string PackageMod(string modDirectory, string zipPath)
    {
        string modFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modDirectory));
        string zipFull = Path.GetFullPath(zipPath);
        if (zipFull.StartsWith(modFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Save the zip outside the mod's own folder, or it would end up inside itself.");

        string manifestPath = Path.Combine(modFull, ManifestName);
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"{modFull} has no mod.json, so it isn't a mod folder.");

        string hash = ComputeIntegrityHash(modFull);
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject
                              ?? throw new InvalidDataException($"{manifestPath} is not a JSON object.");
        foreach (string key in manifest.Select(p => p.Key).Where(k => k.Equals(HashKey, StringComparison.OrdinalIgnoreCase)).ToList())
            manifest.Remove(key);
        manifest[HashKey] = hash;
        File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        string staging = zipFull + ".partial";
        if (File.Exists(staging))
            File.Delete(staging);
        ZipFile.CreateFromDirectory(modFull, staging, CompressionLevel.Optimal, includeBaseDirectory: false);
        File.Move(staging, zipFull, overwrite: true);
        return hash;
    }

    public IntegrityResult CheckIntegrity(string modDirectory)
    {
        string manifestPath = Path.Combine(modDirectory, ManifestName);
        if (!File.Exists(manifestPath))
            return IntegrityResult.NoHash;

        JsonObject? manifest;
        try
        {
            manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
        }
        catch (JsonException)
        {
            return IntegrityResult.NoHash;
        }

        JsonNode? value = manifest?.FirstOrDefault(p => p.Key.Equals(HashKey, StringComparison.OrdinalIgnoreCase)).Value;
        string? expected = value is JsonValue text && text.TryGetValue(out string? s) ? s : null;
        if (string.IsNullOrWhiteSpace(expected))
            return IntegrityResult.NoHash;

        return ComputeIntegrityHash(modDirectory).Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase)
            ? IntegrityResult.Match
            : IntegrityResult.Mismatch;
    }
}
