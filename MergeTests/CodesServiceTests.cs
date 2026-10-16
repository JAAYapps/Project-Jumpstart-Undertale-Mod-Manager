using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Codes;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Data;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;
using Xunit;

namespace MergeTests;

public class CodesServiceTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pjum_codes_test_" + Guid.NewGuid().ToString("N"));

    public CodesServiceTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class DirectDataService : IDataService
    {
        public Task<UndertaleData> LoadAsync(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            return Task.FromResult(UndertaleIO.Read(fs));
        }
        public Task SaveAsync(UndertaleData data, string path)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            UndertaleIO.Write(fs, data);
            return Task.CompletedTask;
        }
    }

    private static string ShippedCodesFolder =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Project Jumpstart Undertale Mod Manager", "codes"));

    private static CheatCode Code(string name, string target, string entry, string gml) => new()
    {
        Name = name,
        Target = target,
        Patches = [new CheatCodePatch { Entry = entry, Operation = "append", Code = gml }]
    };

    private static string Decompile(UndertaleData data, string entry) =>
        new Underanalyzer.Decompiler.DecompileContext(new GlobalDecompileContext(data), data.Code.ByName(entry), data.ToolInfo.DecompilerSettings)
            .DecompileToString();

    [Fact]
    public void No_codes_file_means_no_codes()
    {
        Assert.Empty(new CodesService(_root).LoadCodes("NOGAME"));
    }

    [Fact]
    public void A_codes_file_is_read()
    {
        File.WriteAllText(Path.Combine(_root, "GAME.json"),
            "[ { \"name\": \"Show FPS\", \"description\": \"d\", \"target\": \"deltarune/chapter2\", " +
            "\"patches\": [ { \"entry\": \"gml_Object_obj_time_Draw_64\", \"op\": \"append\", \"code\": \"x = 1;\" } ] } ]");

        CheatCode code = Assert.Single(new CodesService(_root).LoadCodes("GAME"));

        Assert.Equal("Show FPS", code.Name);
        Assert.Equal("deltarune/chapter2", code.Target);
        Assert.Equal("gml_Object_obj_time_Draw_64", Assert.Single(code.Patches).Entry);
    }

    [Fact]
    public void A_broken_codes_file_names_the_file()
    {
        File.WriteAllText(Path.Combine(_root, "GAME.json"), "[ { \"name\": ");

        var ex = Assert.Throws<InvalidDataException>(() => new CodesService(_root).LoadCodes("GAME"));

        Assert.Contains("GAME.json", ex.Message);
    }

    [Fact]
    public void The_shipped_Deltarune_codes_file_is_read()
    {
        IReadOnlyList<CheatCode> codes = new CodesService(ShippedCodesFolder).LoadCodes("DELTARUNE");

        Assert.NotEmpty(codes);
        Assert.All(codes, c => Assert.NotEmpty(c.Patches));
    }

    [Fact]
    public void Codes_on_the_same_entry_become_one_file_in_order()
    {
        string mod = Path.Combine(_root, "mod");
        new CodesService(_root).WriteCodesMod(
        [
            Code("First", "deltarune/chapter2", "gml_Object_obj_time_Step_0", "a = 1;"),
            Code("Second", "deltarune/chapter2", "gml_Object_obj_time_Step_0", "b = 2;"),
            Code("Other chapter", "deltarune/chapter3", "gml_Object_obj_time_Step_0", "c = 3;")
        ], mod);

        Assert.Equal(CodesService.ModName, JsonDocument.Parse(File.ReadAllText(Path.Combine(mod, "mod.json"))).RootElement.GetProperty("name").GetString());
        string chapter2 = Path.Combine(mod, "deltarune", "chapter2", "data.win", "code", "gml_Object_obj_time_Step_0.json");
        var operations = JsonDocument.Parse(File.ReadAllText(chapter2)).RootElement.GetProperty("operations").EnumerateArray()
            .Select(o => o.GetProperty("code").GetString()).ToList();
        Assert.Equal(new[] { "a = 1;", "b = 2;" }, operations);
        Assert.True(File.Exists(Path.Combine(mod, "deltarune", "chapter3", "data.win", "code", "gml_Object_obj_time_Step_0.json")));
    }

    [Theory]
    [InlineData("", "gml_Object_obj_time_Step_0")]
    [InlineData("deltarune/chapter2", "")]
    [InlineData("deltarune/chapter2/sprites", "gml_Object_obj_time_Step_0")]
    public void A_code_that_does_not_make_a_mod_path_is_named(string target, string entry)
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            new CodesService(_root).WriteCodesMod([Code("Broken code", target, entry, "a = 1;")], Path.Combine(_root, "mod")));

        Assert.Contains("Broken code", ex.Message);
    }

    [Fact]
    public async Task An_enabled_code_is_merged_into_the_game()
    {
        string? src = TestPaths.DataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        UndertaleData before = await new DirectDataService().LoadAsync(src);
        string entry = before.Code.First(c => c?.Name?.Content is string n && n.StartsWith("gml_Object_") && n.EndsWith("_Step_0") && c.ParentEntry is null).Name.Content;
        string modRoot = TestPaths.ModRoot(Path.Combine(_root, "probe"), before);
        string target = Path.GetRelativePath(Path.Combine(_root, "probe"), Path.GetDirectoryName(modRoot)!).Replace('\\', '/');
        _out.WriteLine($"Appending to {entry} with target {target}.");

        string gameDir = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameDir);
        File.Copy(src, Path.Combine(gameDir, "data.win"));
        string mod = Path.Combine(_root, "codesmod");
        new CodesService(_root).WriteCodesMod([Code("Marker", target, entry, "pjum_code_marker = 7;")], mod);

        MergeResult result = await new ModMergeService(new DirectDataService())
            .ApplyAsync(gameDir, new List<ModSource> { new(CodesService.ModName, mod) });
        foreach (string w in result.Warnings) _out.WriteLine("WARN: " + w);

        Assert.True(result.Success, result.Reason);
        UndertaleData after = await new DirectDataService().LoadAsync(Path.Combine(gameDir, "data.win"));
        Assert.Contains("pjum_code_marker = 7", Decompile(after, entry));
    }

    [Fact]
    public async Task The_shipped_Deltarune_codes_compile_into_chapter_2()
    {
        string? src = TestPaths.DataWin;
        string? deltarune = src is null ? null : Path.GetDirectoryName(src);
        string? chapter2 = deltarune is null ? null : Path.Combine(deltarune, "chapter2_windows", "data.win");
        if (chapter2 is null || !File.Exists(chapter2))
        {
            _out.WriteLine("Deltarune chapter 2 not found next to the test data.win — skipping.");
            return;
        }

        IReadOnlyList<CheatCode> codes = new CodesService(ShippedCodesFolder).LoadCodes("DELTARUNE");
        string gameDir = Path.Combine(_root, "chapter2");
        Directory.CreateDirectory(gameDir);
        File.Copy(chapter2, Path.Combine(gameDir, "data.win"));
        string mod = Path.Combine(_root, "codesmod");
        new CodesService(_root).WriteCodesMod(codes.Where(c => c.Target == "deltarune/chapter2"), mod);

        MergeResult result = await new ModMergeService(new DirectDataService())
            .ApplyAsync(gameDir, new List<ModSource> { new(CodesService.ModName, mod) });
        foreach (string w in result.Warnings) _out.WriteLine("WARN: " + w);

        Assert.True(result.Success, result.Reason);
        Assert.Empty(result.Warnings);
        UndertaleData after = await new DirectDataService().LoadAsync(Path.Combine(gameDir, "data.win"));
        Assert.Contains("FPS: ", Decompile(after, "gml_Object_obj_time_Draw_64"));
        Assert.Contains("global.maxhp", Decompile(after, "gml_Object_obj_time_Step_0"));
    }
}
