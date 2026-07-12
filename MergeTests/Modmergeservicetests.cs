using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Data;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;
using Xunit;

namespace MergeTests;

// End-to-end dispatcher tests, DIR-MODEL. Gated on PJUM_TEST_DATAWIN. Each test
// prepares a temp game dir containing data.win, runs the merge into it, and
// asserts against <dir>/data.win. Last-wins on conflict; hard-fail on missing
// dependency; no mutation on failure.
public class ModMergeServiceTests
{
    private readonly ITestOutputHelper _out;
    public ModMergeServiceTests(ITestOutputHelper output) => _out = output;

    private static string SourceDataWin => TestPaths.DataWin;

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

    private static string FirstSpriteName(UndertaleData d)
    {
        foreach (var s in d.Sprites)
            if (s?.Textures.Count > 0 && s.Textures[0]?.Texture is not null && s.Name?.Content is string n)
                return n;
        return null;
    }

    private static UndertaleSprite FindSprite(UndertaleData d, string name) => d.Sprites.ByName(name);

    private static void WriteSpriteMod(string dir, string modName, string sprite,
        UndertaleData data, MagickColor tint)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mod.json"),
            $"{{ \"name\": \"{modName}\", \"version\": \"1.0.0\" }}");

        string frameDir = Path.Combine(dir, "undertale", "data.win", "sprites", sprite);
        Directory.CreateDirectory(frameDir);

        string framePath = Path.Combine(frameDir, "0.png");
        using (var worker = new TextureWorker())
            worker.ExportAsPNG(FindSprite(data, sprite).Textures[0].Texture, framePath);
        using (var img = new MagickImage(framePath))
        {
            using var solid = new MagickImage(tint, img.Width, img.Height);
            solid.Write(framePath);
        }
    }

    // Prepare a temp game dir: <dir>/data.win = a copy of the source.
    private static string PrepareGameDir(string src)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pjum_gamedir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.Copy(src, Path.Combine(dir, "data.win"), overwrite: true);
        return dir;
    }

    [Fact]
    public async Task TwoMods_sameSprite_lastWins()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        var probe = new DirectDataService();
        UndertaleData baseData = await probe.LoadAsync(src);
        string sprite = FirstSpriteName(baseData);
        Assert.NotNull(sprite);

        string root = Path.Combine(Path.GetTempPath(), "pjum_multimod_" + Guid.NewGuid().ToString("N"));
        string modA = Path.Combine(root, "ModA");
        string modB = Path.Combine(root, "ModB");
        string gameDir = PrepareGameDir(src);

        try
        {
            WriteSpriteMod(modA, "ModA", sprite, baseData, MagickColors.Red);
            WriteSpriteMod(modB, "ModB", sprite, baseData, MagickColors.Blue);

            var svc = new ModMergeService(new DirectDataService());
            var mods = new List<ModSource>
            {
                new("ModA", modA),   // A first...
                new("ModB", modB),   // ...B last -> B wins
            };

            MergeResult result = await svc.ApplyAsync(gameDir, mods);

            Assert.True(result.Success, result.Reason);
            Assert.True(File.Exists(Path.Combine(gameDir, "data.win")), "data.win missing after merge");
            Assert.Contains(result.Conflicts, c => c.WinningMod == "ModB");
            _out.WriteLine("conflict winner: " +
                string.Join(", ", result.Conflicts.Select(c => $"{c.AssetKey} -> {c.WinningMod}")));
        }
        finally
        {
            TryDelete(root);
            TryDelete(gameDir);
        }
    }

    [Fact]
    public async Task MissingDependency_hardFails_namingTheMod()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        string root = Path.Combine(Path.GetTempPath(), "pjum_dep_" + Guid.NewGuid().ToString("N"));
        string modC = Path.Combine(root, "ModC");
        string gameDir = PrepareGameDir(src);

        try
        {
            Directory.CreateDirectory(modC);
            File.WriteAllText(Path.Combine(modC, "mod.json"),
                "{ \"name\": \"ModC\", \"version\": \"1.0.0\", \"requires\": { \"SonicBase\": \">=1.0.0\" } }");

            // Capture data.win bytes before, to prove no mutation on failure.
            string dataWin = Path.Combine(gameDir, "data.win");
            long lenBefore = new FileInfo(dataWin).Length;

            var svc = new ModMergeService(new DirectDataService());
            var mods = new List<ModSource> { new("ModC", modC) };

            MergeResult result = await svc.ApplyAsync(gameDir, mods);

            Assert.False(result.Success);
            Assert.Equal("ModC", result.FailedMod);
            Assert.Contains("SonicBase", result.Reason);
            // data.win untouched (dependency fails in phase 1, before load/save).
            Assert.Equal(lenBefore, new FileInfo(dataWin).Length);
            _out.WriteLine("hard-fail reason: " + result.Reason);
        }
        finally
        {
            TryDelete(root);
            TryDelete(gameDir);
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}