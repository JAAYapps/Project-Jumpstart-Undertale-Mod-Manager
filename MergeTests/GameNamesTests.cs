using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UndertaleModLib;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Data;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;
using PjumModLib;
using Xunit;

namespace MergeTests;

public class GameNamesTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pjum_gamenames_" + Guid.NewGuid().ToString("N"));

    public GameNamesTests(ITestOutputHelper output)
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

    private static UndertaleData Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return UndertaleIO.Read(fs);
    }

    private string Game(string src)
    {
        string game = Path.Combine(_root, "game_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(game);
        File.Copy(src, Path.Combine(game, "data.win"));
        return game;
    }

    private string Mod(UndertaleData probe, string name, string gameJson)
    {
        string mod = Path.Combine(_root, name);
        string root = TestPaths.ModRoot(mod, probe);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(mod, "mod.json"), "{ \"name\": \"" + name + "\", \"version\": \"1.0.0\" }");
        File.WriteAllText(Path.Combine(root, "game.json"), gameJson);
        return mod;
    }

    [Fact]
    public async Task Game_json_renames_the_game_and_the_later_mod_wins_a_name_both_set()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        string first = Mod(probe, "FirstMod", "{ \"name\": \"PJUM_FIRST\", \"fileName\": \"PJUM_FILE\" }");
        string second = Mod(probe, "SecondMod", "{ \"name\": \"PJUM_SECOND\", \"displayName\": \"PJUM Second\" }");
        string game = Game(src);

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game,
            new List<ModSource> { new("FirstMod", first), new("SecondMod", second) });
        Assert.True(result.Success, result.Reason);

        UndertaleData after = Load(Path.Combine(game, "data.win"));
        Assert.Equal(("PJUM_SECOND", "PJUM Second", "PJUM_FILE"),
            (after.GeneralInfo.Name.Content, after.GeneralInfo.DisplayName.Content, after.GeneralInfo.FileName.Content));
        ConflictLogEntry conflict = Assert.Single(result.Conflicts, c => c.AssetKey.EndsWith("game:name"));
        Assert.Equal("SecondMod", conflict.WinningMod);
        Assert.Equal(new[] { "FirstMod" }, conflict.OverriddenMods);
        Assert.DoesNotContain(result.Conflicts, c => c.AssetKey.EndsWith("game:fileName") || c.AssetKey.EndsWith("game:displayName"));
    }

    [Fact]
    public async Task An_empty_game_json_fails_before_anything_is_written()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        string mod = Mod(Load(src), "EmptyGameMod", "{ }");
        string game = Game(src);
        byte[] before = File.ReadAllBytes(Path.Combine(game, "data.win"));

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("EmptyGameMod", mod) });

        Assert.False(result.Success);
        Assert.Contains("sets none of", result.Reason);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(game, "data.win")));
    }

    [Fact]
    public async Task A_renamed_fan_game_extracts_as_a_mod_of_its_original_and_merges_back_with_its_name()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData original = Load(src);
        UndertaleData fanGame = Load(src);
        fanGame.GeneralInfo.Name = fanGame.Strings.MakeString("PJUM_SWAP");
        fanGame.GeneralInfo.DisplayName = fanGame.Strings.MakeString("PJUM SWAP");
        string folder = Path.GetDirectoryName(src)!;
        string mod = Path.Combine(_root, "FanGameMod");

        ExtractResult result = ModExtractor.Extract(original, fanGame, folder, folder, mod, "Fan game mod");
        foreach (string note in result.NotConverted) _out.WriteLine("note: " + note);

        Assert.Equal(1, result.WrittenByCategory["game names"]);
        string json = File.ReadAllText(Path.Combine(mod, "undertale", "data.win", "game.json"));
        Assert.Contains("\"name\": \"PJUM_SWAP\"", json);
        Assert.Contains("\"displayName\": \"PJUM SWAP\"", json);
        Assert.DoesNotContain("fileName", json);
        Assert.Contains(result.NotConverted, n => n.Contains("renames the game from 'UNDERTALE' to 'PJUM_SWAP'") && n.Contains($"{original.GameObjects.Count} of {original.GameObjects.Count} objects"));

        string game = Game(src);
        MergeResult merge = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("Fan game mod", mod) });
        Assert.True(merge.Success, merge.Reason);
        UndertaleData after = Load(Path.Combine(game, "data.win"));
        Assert.Equal(("PJUM_SWAP", "PJUM SWAP", "UNDERTALE"), (after.GeneralInfo.Name.Content, after.GeneralInfo.DisplayName.Content, after.GeneralInfo.FileName.Content));
    }

    [Fact]
    public void A_file_from_another_GameMaker_version_is_refused_even_with_the_same_name()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData original = Load(src);
        UndertaleData other = Load(src);
        other.GeneralInfo.Build += 1;
        string folder = Path.GetDirectoryName(src)!;

        var ex = Assert.Throws<InvalidDataException>(() => ModExtractor.Extract(original, other, folder, folder, Path.Combine(_root, "mod"), "x"));

        Assert.Contains("same GameMaker", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "mod")));
    }
}
