using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using Project_Jumpstart_Undertale_Mod_Manager.Services.ModPackage;
using Xunit;

namespace MergeTests;

public class ModPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pjum_package_" + Guid.NewGuid().ToString("N"));
    private readonly ModPackageService _service = new();

    public ModPackageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string MakeMod(string name)
    {
        string mod = Path.Combine(_root, name);
        Write(Path.Combine(mod, "mod.json"), "{ \"Name\": \"Test Mod\", \"Version\": \"1.0.0\", \"IsEnabled\": true }");
        Write(Path.Combine(mod, "undertale", "data.win", "sprites", "spr_x", "0.png"), "picture");
        Write(Path.Combine(mod, "undertale", "data.win", "code", "gml_Script_x.gml"), "x = 1;");
        Write(Path.Combine(mod, "preview.png"), "preview");
        return mod;
    }

    [Fact]
    public void The_hash_ignores_mod_json_which_the_manager_rewrites()
    {
        string mod = MakeMod("a");
        string before = _service.ComputeIntegrityHash(mod);
        File.WriteAllText(Path.Combine(mod, "mod.json"), "{ \"Name\": \"Test Mod\", \"Version\": \"1.0.0\", \"IsEnabled\": false }");

        Assert.Equal(before, _service.ComputeIntegrityHash(mod));
        Assert.StartsWith("sha256:", before);
    }

    [Fact]
    public void The_hash_changes_when_a_file_is_changed_added_removed_or_renamed()
    {
        string mod = MakeMod("a");
        string original = _service.ComputeIntegrityHash(mod);
        string code = Path.Combine(mod, "undertale", "data.win", "code", "gml_Script_x.gml");

        File.WriteAllText(code, "x = 2;");
        string changed = _service.ComputeIntegrityHash(mod);
        File.WriteAllText(code, "x = 1;");
        Assert.Equal(original, _service.ComputeIntegrityHash(mod));

        Write(Path.Combine(mod, "extra.txt"), "x");
        string added = _service.ComputeIntegrityHash(mod);
        File.Delete(Path.Combine(mod, "extra.txt"));

        File.Move(code, code + ".old");
        string renamed = _service.ComputeIntegrityHash(mod);
        File.Move(code + ".old", code);

        File.Delete(Path.Combine(mod, "preview.png"));
        string removed = _service.ComputeIntegrityHash(mod);

        Assert.Equal(5, new[] { original, changed, added, renamed, removed }.Distinct().Count());
    }

    [Fact]
    public void Two_folders_with_the_same_files_have_the_same_hash()
    {
        string first = MakeMod("first");
        string second = Path.Combine(_root, "second");
        Write(Path.Combine(second, "preview.png"), "preview");
        Write(Path.Combine(second, "undertale", "data.win", "code", "gml_Script_x.gml"), "x = 1;");
        Write(Path.Combine(second, "undertale", "data.win", "sprites", "spr_x", "0.png"), "picture");
        Write(Path.Combine(second, "mod.json"), "{ \"Name\": \"Different\" }");

        Assert.Equal(_service.ComputeIntegrityHash(first), _service.ComputeIntegrityHash(second));
    }

    [Fact]
    public void A_packaged_mod_matches_after_it_is_unzipped()
    {
        string mod = MakeMod("a");
        string zip = Path.Combine(_root, "Test Mod 1.0.0.zip");

        string hash = _service.PackageMod(mod, zip);

        Assert.Equal(hash, JsonNode.Parse(File.ReadAllText(Path.Combine(mod, "mod.json")))!["integrityHash"]!.GetValue<string>());
        Assert.Equal(true, JsonNode.Parse(File.ReadAllText(Path.Combine(mod, "mod.json")))!["IsEnabled"]!.GetValue<bool>());
        string unzipped = Path.Combine(_root, "unzipped");
        ZipFile.ExtractToDirectory(zip, unzipped);
        Assert.True(File.Exists(Path.Combine(unzipped, "mod.json")));
        Assert.Equal(IntegrityResult.Match, _service.CheckIntegrity(unzipped));
    }

    [Fact]
    public void A_changed_file_after_packaging_is_a_mismatch()
    {
        string mod = MakeMod("a");
        string zip = Path.Combine(_root, "mod.zip");
        _service.PackageMod(mod, zip);
        string unzipped = Path.Combine(_root, "unzipped");
        ZipFile.ExtractToDirectory(zip, unzipped);

        File.WriteAllText(Path.Combine(unzipped, "undertale", "data.win", "sprites", "spr_x", "0.png"), "broken download");

        Assert.Equal(IntegrityResult.Mismatch, _service.CheckIntegrity(unzipped));
    }

    [Fact]
    public void A_mod_without_a_hash_is_not_checked()
    {
        Assert.Equal(IntegrityResult.NoHash, _service.CheckIntegrity(MakeMod("a")));
    }

    [Fact]
    public void Packaging_again_replaces_the_old_hash()
    {
        string mod = MakeMod("a");
        _service.PackageMod(mod, Path.Combine(_root, "one.zip"));
        File.WriteAllText(Path.Combine(mod, "preview.png"), "new preview");

        string second = _service.PackageMod(mod, Path.Combine(_root, "two.zip"));

        JsonObject manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(mod, "mod.json")))!.AsObject();
        Assert.Single(manifest, p => p.Key.Equals("integrityHash", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(second, manifest["integrityHash"]!.GetValue<string>());
        Assert.Equal(IntegrityResult.Match, _service.CheckIntegrity(mod));
    }

    [Fact]
    public void A_zip_inside_the_mod_folder_is_refused()
    {
        string mod = MakeMod("a");

        var ex = Assert.Throws<InvalidOperationException>(() => _service.PackageMod(mod, Path.Combine(mod, "mod.zip")));

        Assert.Contains("outside the mod's own folder", ex.Message);
        Assert.False(File.Exists(Path.Combine(mod, "mod.zip")));
    }
}
