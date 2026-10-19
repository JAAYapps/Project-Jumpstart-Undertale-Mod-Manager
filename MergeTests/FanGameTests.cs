using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UndertaleModLib;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Data;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;
using Xunit;
using PjumModLib;

namespace MergeTests;

public class FanGameTests
{
    private readonly ITestOutputHelper _out;
    public FanGameTests(ITestOutputHelper output) => _out = output;

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

    [Fact]
    public async Task A_fan_game_merges_its_own_files_and_skips_other_games_with_a_warning()
    {
        string? src = TestPaths.DataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        var io = new DirectDataService();
        string root = Path.Combine(Path.GetTempPath(), "pjum_fangame_" + Guid.NewGuid().ToString("N"));
        string gameDir = Path.Combine(root, "game");
        string mod = Path.Combine(root, "FanMod");

        try
        {
            Directory.CreateDirectory(gameDir);
            UndertaleData data = await io.LoadAsync(src);
            data.GeneralInfo.Name = data.Strings.MakeString("PJUM_FanGame");
            data.GeneralInfo.DisplayName = data.Strings.MakeString("PJUM Fan Game");
            await io.SaveAsync(data, Path.Combine(gameDir, "data.win"));

            ModTarget? target = ModTarget.FromData(await io.LoadAsync(Path.Combine(gameDir, "data.win")));
            Assert.Equal(new ModTarget("pjum_fangame", null), target);

            Directory.CreateDirectory(mod);
            File.WriteAllText(Path.Combine(mod, "mod.json"),
                "{ \"name\": \"FanMod\", \"version\": \"1.0.0\", " +
                "\"newAssets\": { \"paths\": { \"path_pjum_fan\": {} } } }");
            WritePath(Path.Combine(mod, "pjum_fangame", "data.win"), "path_pjum_fan");
            WritePath(Path.Combine(mod, "undertale", "data.win"), "path_pjum_undertale");
            WritePath(Path.Combine(mod, "deltarune", "chapter2", "data.win"), "path_pjum_deltarune");

            MergeResult result = await new ModMergeService(io).ApplyAsync(gameDir, new List<ModSource> { new("FanMod", mod) });
            foreach (string w in result.Warnings) _out.WriteLine("WARN: " + w);

            Assert.True(result.Success, result.Reason);
            UndertaleData after = await io.LoadAsync(Path.Combine(gameDir, "data.win"));
            Assert.NotNull(after.Paths.ByName("path_pjum_fan"));
            Assert.Null(after.Paths.ByName("path_pjum_undertale"));
            Assert.Null(after.Paths.ByName("path_pjum_deltarune"));
            Assert.Equal(2, result.Warnings.Count);
            Assert.Contains(result.Warnings, w => w.Contains("'undertale/'") && w.Contains("pjum_fangame"));
            Assert.Contains(result.Warnings, w => w.Contains("'deltarune/'") && w.Contains("pjum_fangame"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
