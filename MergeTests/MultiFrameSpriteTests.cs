using System;
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

// Regression test for the multi-frame texture collapse: AssetKey has no frame
// index, so all frames of a sprite share one plan entry. Phase 5c must re-scan
// the owning mod for EVERY frame, not just the last one enumerated. This test
// paints each frame a DISTINCT color and asserts frame i comes back as color i
// — catching both the collapse (one frame changed) and any frame mis-mapping.
// Gated on PJUM_TEST_DATAWIN; skips silently if unset.
public class MultiFrameSpriteTests
{
    private readonly ITestOutputHelper _out;
    public MultiFrameSpriteTests(ITestOutputHelper output) => _out = output;

    private static string SourceDataWin => Environment.GetEnvironmentVariable("PJUM_TEST_DATAWIN");

    // Opaque, far-apart colors — distinct under a 12% fuzz.
    private static readonly MagickColor[] Palette =
    {
        MagickColors.Red, MagickColors.Lime, MagickColors.Blue, MagickColors.Yellow,
        MagickColors.Magenta, MagickColors.Cyan, MagickColors.White, MagickColors.Orange,
    };

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

    private static string PrepareGameDir(string src)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pjum_gamedir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.Copy(src, Path.Combine(dir, "data.win"), overwrite: true);
        return dir;
    }

    // First sprite whose frame count is 2..Palette.Length, with real textures.
    private static string FirstMultiFrameSprite(UndertaleData d)
    {
        foreach (var s in d.Sprites)
        {
            if (s?.Name?.Content is not string name) continue;
            int n = s.Textures.Count;
            if (n < 2 || n > Palette.Length) continue;
            if (s.Textures.All(t => t?.Texture is not null)) return name;
        }
        return null;
    }

    private static MagickColor Dominant(string pngPath)
    {
        using var img = new MagickImage(pngPath);
        var top = img.Histogram().OrderByDescending(kv => kv.Value).First().Key;
        return new MagickColor(top);
    }

    [Fact]
    public async Task EveryFrame_ofMultiFrameSprite_isReplaced()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        var probe = new DirectDataService();
        UndertaleData baseData = await probe.LoadAsync(src);
        string sprite = FirstMultiFrameSprite(baseData);
        Assert.NotNull(sprite);   // need a multi-frame sprite to test the collapse

        int frameCount = baseData.Sprites.ByName(sprite).Textures.Count;
        _out.WriteLine($"testing sprite '{sprite}' with {frameCount} frames");

        string root    = Path.Combine(Path.GetTempPath(), "pjum_mf_" + Guid.NewGuid().ToString("N"));
        string mod     = Path.Combine(root, "MultiFrameMod");
        string gameDir = PrepareGameDir(src);

        try
        {
            // Write one solid, distinct-colored frame per index.
            string frameDir = Path.Combine(mod, "undertale", "data.win", "sprites", sprite);
            Directory.CreateDirectory(frameDir);
            File.WriteAllText(Path.Combine(mod, "mod.json"),
                "{ \"name\": \"MultiFrameMod\", \"version\": \"1.0.0\" }");

            using (var worker = new TextureWorker())
            {
                var baseSprite = baseData.Sprites.ByName(sprite);
                for (int i = 0; i < frameCount; i++)
                {
                    string fp = Path.Combine(frameDir, $"{i}.png");
                    worker.ExportAsPNG(baseSprite.Textures[i].Texture, fp);   // for correct dimensions
                    using var img   = new MagickImage(fp);
                    using var solid = new MagickImage(Palette[i], img.Width, img.Height);
                    solid.Write(fp);
                }
            }

            var svc = new ModMergeService(new DirectDataService());
            MergeResult result = await svc.ApplyAsync(gameDir,
                new[] { new ModSource("MultiFrameMod", mod) });
            Assert.True(result.Success, result.Reason);

            // Reload the merged data.win and check EACH frame is ITS color.
            UndertaleData merged = await probe.LoadAsync(Path.Combine(gameDir, "data.win"));
            var mergedSprite = merged.Sprites.ByName(sprite);
            Assert.NotNull(mergedSprite);
            Assert.Equal(frameCount, mergedSprite.Textures.Count);

            string scratch = Path.Combine(root, "verify");
            Directory.CreateDirectory(scratch);
            using var vw = new TextureWorker();

            for (int i = 0; i < frameCount; i++)
            {
                string fp = Path.Combine(scratch, $"out_{i}.png");
                vw.ExportAsPNG(mergedSprite.Textures[i].Texture, fp);
                var got = Dominant(fp);
                Assert.True(got.FuzzyEquals(Palette[i], new Percentage(12)),
                    $"frame {i} of '{sprite}' should be {Palette[i]} but was {got} " +
                    $"— frame collapse or mis-mapping (only one frame reached the repack).");
            }

            _out.WriteLine($"all {frameCount} frames replaced with their distinct colors");
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