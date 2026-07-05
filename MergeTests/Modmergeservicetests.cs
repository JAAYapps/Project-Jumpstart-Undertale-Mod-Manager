using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Data;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;
using Xunit;
using Xunit.Abstractions;

namespace MergeTests;

// End-to-end dispatcher tests. Gated on PJUM_TEST_DATAWIN. Builds tiny synthetic
// mod folders on disk, runs the multi-mod merge, and checks the plan behaves:
// last-wins on conflict, hard-fail on missing dependency, clean abort (no file
// written) on failure.
public class ModMergeServiceTests
{
    private readonly ITestOutputHelper _out;
    public ModMergeServiceTests(ITestOutputHelper output) => _out = output;

    private static string SourceDataWin => Environment.GetEnvironmentVariable("PJUM_TEST_DATAWIN");

    // Minimal IDataService stand-in so the test doesn't depend on the app's DI.
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

    // Write a mod that overrides frame 0 of `sprite`, tinted a solid color.
    private static void WriteSpriteMod(string dir, string modName, string sprite,
        UndertaleData data, MagickColor tint)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mod.json"),
            $"{{ \"name\": \"{modName}\", \"version\": \"1.0.0\" }}");

        string frameDir = Path.Combine(dir, "undertale", "data.win", "sprites", sprite);
        Directory.CreateDirectory(frameDir);

        // Export the real frame for correct dimensions, then flood it with tint.
        string framePath = Path.Combine(frameDir, "0.png");
        using (var worker = new TextureWorker())
            worker.ExportAsPNG(FindSprite(data, sprite).Textures[0].Texture, framePath);
        using (var img = new MagickImage(framePath))
        {
            using var solid = new MagickImage(tint, img.Width, img.Height);
            solid.Write(framePath);
        }
    }

    private static UndertaleSprite FindSprite(UndertaleData d, string name) => d.Sprites.ByName(name);

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
        string outPath = Path.Combine(root, "merged.win");

        try
        {
            WriteSpriteMod(modA, "ModA", sprite, baseData, MagickColors.Red);
            WriteSpriteMod(modB, "ModB", sprite, baseData, MagickColors.Blue);

            var svc = new ModMergeService(new DirectDataService());
            var mods = new List<ModSource>
            {
                new("ModA", modA),   // load order: A first...
                new("ModB", modB),   // ...B last -> B should win
            };

            MergeResult result = await svc.ApplyAsync(src, mods, outPath);

            Assert.True(result.Success, result.Reason);
            Assert.True(File.Exists(outPath), "merged file was not written");

            // Conflict logged, ModB recorded as winner.
            Assert.Contains(result.Conflicts, c => c.WinningMod == "ModB");
            _out.WriteLine("conflict winner: " + string.Join(", ", result.Conflicts.Select(c => $"{c.AssetKey} -> {c.WinningMod}")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task MissingDependency_hardFails_namingTheMod()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        string root = Path.Combine(Path.GetTempPath(), "pjum_dep_" + Guid.NewGuid().ToString("N"));
        string modC = Path.Combine(root, "ModC");
        string outPath = Path.Combine(root, "merged.win");

        try
        {
            Directory.CreateDirectory(modC);
            // ModC requires "SonicBase", which is NOT in the set.
            File.WriteAllText(Path.Combine(modC, "mod.json"),
                "{ \"name\": \"ModC\", \"version\": \"1.0.0\", \"requires\": { \"SonicBase\": \">=1.0.0\" } }");

            var svc = new ModMergeService(new DirectDataService());
            var mods = new List<ModSource> { new("ModC", modC) };

            MergeResult result = await svc.ApplyAsync(src, mods, outPath);

            Assert.False(result.Success);
            Assert.Equal("ModC", result.FailedMod);
            Assert.Contains("SonicBase", result.Reason);
            Assert.False(File.Exists(outPath), "no file should be written on a failed merge");
            _out.WriteLine("hard-fail reason: " + result.Reason);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}