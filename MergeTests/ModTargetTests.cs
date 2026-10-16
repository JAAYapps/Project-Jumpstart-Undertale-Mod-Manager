using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UndertaleModLib;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Data;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;
using Xunit;

namespace MergeTests;

// Target matching: a mod file only merges into the game + chapter its address
// names. The first group runs offline; the last test is gated on a real data.win.
public class ModTargetTests
{
    private readonly ITestOutputHelper _out;
    public ModTargetTests(ITestOutputHelper output) => _out = output;

    private static UndertaleData DataNamed(string name, string displayName)
    {
        UndertaleData data = UndertaleData.CreateNew();
        data.GeneralInfo.Name = data.Strings.MakeString(name);
        data.GeneralInfo.DisplayName = data.Strings.MakeString(displayName);
        return data;
    }

    [Theory]
    [InlineData("UNDERTALE", "UNDERTALE", "undertale", null)]
    [InlineData("DELTARUNE", "DELTARUNE", "deltarune", null)]
    [InlineData("DELTARUNE", "DELTARUNE Chapter 1", "deltarune", "chapter1")]
    [InlineData("DELTARUNE", "DELTARUNE Chapter 2", "deltarune", "chapter2")]
    [InlineData("Undertale_Yellow", "Undertale Yellow", "undertale_yellow", null)]
    [InlineData("SOMEOTHERGAME", "Some Other Game", "someothergame", null)]
    [InlineData("MYFANGAME", "MYFANGAME Demo", "myfangame", null)]
    public void FromData_reads_game_and_chapter(string name, string displayName, string game, string? chapter)
    {
        ModTarget? target = ModTarget.FromData(DataNamed(name, displayName));
        Assert.Equal(new ModTarget(game, chapter), target);
    }

    [Fact]
    public void FromData_returns_null_when_the_game_has_no_name()
    {
        Assert.Null(ModTarget.FromData(DataNamed("", "")));
    }

    [Theory]
    // target deltarune chapter2
    [InlineData("deltarune", "chapter2", "deltarune/chapter2/data.win/sprites/spr_x/0.png", true)]
    [InlineData("deltarune", "chapter2", "deltarune/chapter2/audio_sfx/sounds/snd_x.ogg", true)]
    [InlineData("deltarune", "chapter2", "deltarune/chapter1/data.win/sprites/spr_x/0.png", false)]
    [InlineData("deltarune", "chapter2", "deltarune/data.win/sprites/spr_x/0.png", false)]
    [InlineData("deltarune", "chapter2", "undertale/data.win/sprites/spr_x/0.png", false)]
    [InlineData("deltarune", "chapter2", "deltarune/shared/files/lang/x.json", true)]
    [InlineData("deltarune", "chapter2", "deltarune/shared/sprites/spr_x/0.png", false)]
    // target deltarune launcher (no chapter)
    [InlineData("deltarune", null, "deltarune/data.win/sprites/spr_x/0.png", true)]
    [InlineData("deltarune", null, "deltarune/chapter2/data.win/sprites/spr_x/0.png", false)]
    [InlineData("deltarune", null, "deltarune/shared/files/lang/x.json", true)]
    [InlineData("deltarune", null, "deltarune/shared/sprites/spr_x/0.png", false)]
    [InlineData("deltarune", null, "deltarune/shared/sounds/snd_x.ogg", false)]
    // target undertale
    [InlineData("undertale", null, "undertale/data.win/sprites/spr_x/0.png", true)]
    [InlineData("undertale", null, "deltarune/chapter2/data.win/sprites/spr_x/0.png", false)]
    [InlineData("undertale", null, "undertale_yellow/data.win/sprites/spr_x/0.png", false)]
    [InlineData("undertale", null, "deltarune/shared/files/lang/x.json", false)]
    [InlineData("undertale", null, "undertale/shared/sprites/spr_x/0.png", false)]
    [InlineData("undertale_yellow", null, "undertale_yellow/data.win/sprites/spr_x/0.png", true)]
    [InlineData("undertale_yellow", null, "undertale/data.win/sprites/spr_x/0.png", false)]
    public void Accepts_only_the_same_game_and_chapter(string game, string? chapter, string path, bool expected)
    {
        var target = new ModTarget(game, chapter);
        Assert.Equal(expected, target.Accepts(ModAddressParser.Parse(path)));
    }

    [Theory]
    [InlineData("deltarune/chapter1/data.win/paths/p.json", false)]   // a real chapter, just not this one
    [InlineData("deltarune/chapter12/data.win/paths/p.json", false)]
    [InlineData("deltarune/data.win/paths/p.json", false)]            // launcher
    [InlineData("undertale/data.win/paths/p.json", false)]            // other game
    [InlineData("deltarune/chaptr2/data.win/paths/p.json", true)]
    [InlineData("deltarune/chapter/data.win/paths/p.json", true)]
    [InlineData("deltarune/chapter 2/data.win/paths/p.json", true)]
    [InlineData("deltarune/chapter2/extra/data.win/paths/p.json", true)]
    public void LooksMisspelled_flags_folders_that_are_not_chapter_names(string path, bool expected)
    {
        var target = new ModTarget("deltarune", "chapter2");
        Assert.Equal(expected, target.LooksMisspelled(ModAddressParser.Parse(path)));
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

    private const string PathJson =
        "{ \"smooth\": false, \"closed\": true, \"precision\": 4, \"points\": [ {\"x\":10,\"y\":20,\"speed\":100} ] }";

    private static void WritePath(string containerDir, string name)
    {
        string dir = Path.Combine(containerDir, "paths");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".json"), PathJson);
    }

    // One mod with a file for this target, one for another chapter, and one in
    // a misspelled folder; plus a second mod for the other game entirely.
    // Only the first file may merge. The skipped paths are NOT declared in
    // newAssets, so if they were not skipped the merge would fail to resolve them.
    [Fact]
    public async Task Merge_applies_only_files_addressed_to_the_target()
    {
        string? src = TestPaths.DataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        var io = new DirectDataService();
        ModTarget? target = ModTarget.FromData(await io.LoadAsync(src));
        Assert.NotNull(target);
        _out.WriteLine($"target = {target}");

        string root = Path.Combine(Path.GetTempPath(), "pjum_target_" + Guid.NewGuid().ToString("N"));
        string gameDir = Path.Combine(root, "game");
        string mod = Path.Combine(root, "TargetMod");
        string otherGameMod = Path.Combine(root, "OtherGameMod");
        string otherGame = target.Game == "undertale" ? "deltarune" : "undertale";

        try
        {
            Directory.CreateDirectory(gameDir);
            File.Copy(src, Path.Combine(gameDir, "data.win"));

            Directory.CreateDirectory(mod);
            File.WriteAllText(Path.Combine(mod, "mod.json"),
                "{ \"name\": \"TargetMod\", \"version\": \"1.0.0\", " +
                "\"newAssets\": { \"paths\": { \"path_pjum_here\": {} } } }");
            string here = target.Chapter is null
                ? Path.Combine(mod, target.Game, "data.win")
                : Path.Combine(mod, target.Game, target.Chapter, "data.win");
            WritePath(here, "path_pjum_here");
            WritePath(Path.Combine(mod, target.Game, "chapter99", "data.win"), "path_pjum_otherchapter");
            WritePath(Path.Combine(mod, target.Game, "chaptr2", "data.win"), "path_pjum_typo");

            Directory.CreateDirectory(otherGameMod);
            File.WriteAllText(Path.Combine(otherGameMod, "mod.json"),
                "{ \"name\": \"OtherGameMod\", \"version\": \"1.0.0\" }");
            WritePath(Path.Combine(otherGameMod, otherGame, "data.win"), "path_pjum_othergame");

            var svc = new ModMergeService(io);
            MergeResult result = await svc.ApplyAsync(gameDir,
                new List<ModSource> { new("TargetMod", mod), new("OtherGameMod", otherGameMod) });
            foreach (string w in result.Warnings) _out.WriteLine("WARN: " + w);
            Assert.True(result.Success, result.Reason);

            UndertaleData after = await io.LoadAsync(Path.Combine(gameDir, "data.win"));
            Assert.NotNull(after.Paths.ByName("path_pjum_here"));
            Assert.Null(after.Paths.ByName("path_pjum_otherchapter"));
            Assert.Null(after.Paths.ByName("path_pjum_typo"));
            Assert.Null(after.Paths.ByName("path_pjum_othergame"));

            Assert.Equal(2, result.Warnings.Count);
            Assert.Contains(result.Warnings, w => w.Contains("TargetMod") && w.Contains("chaptr2"));
            Assert.Contains(result.Warnings, w => w.Contains("OtherGameMod") && w.Contains("has no files for"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
