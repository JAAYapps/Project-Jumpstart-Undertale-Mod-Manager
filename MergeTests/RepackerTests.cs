using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Textures;
using Xunit;

namespace MergeTests;

// ---------------------------------------------------------------------------
// Integration tests for TextureRepacker. These load a REAL data.win, so they
// are gated on an environment variable and skip silently if it isn't set:
//
//   PJUM_TEST_DATAWIN = full path to a copy of Undertale (or Deltarune) data.win
//
// IMPORTANT: point it at a COPY. The tests never write over the source (they
// write to a temp output), but a copy is the safe habit.
//
//   Linux/bash:
//     export PJUM_TEST_DATAWIN="/path/to/data.win"
//     dotnet test MergeTests
//
// The output file for the visual test is printed to the test log; open THAT
// file in your fork to eyeball the result.
// ---------------------------------------------------------------------------
public class RepackerTests
{
    private readonly ITestOutputHelper _out;
    public RepackerTests(ITestOutputHelper output) => _out = output;

    private static string SourceDataWin => TestPaths.DataWin;

    private static UndertaleData Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        // If your fork requires the warning-handler overload, use:
        //   return UndertaleIO.Read(fs, (warning, _) => throw new Exception(warning));
        return UndertaleIO.Read(fs);
    }

    private static void Save(UndertaleData data, string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        UndertaleIO.Write(fs, data);
    }

    // -----------------------------------------------------------------------
    // TEST 1 — the corruption check that matters most.
    // Full-repack an UNCHANGED game (empty overrides), write it, reload it, and
    // assert the structure survived. If a no-op repack round-trips clean, the
    // machinery (export -> clear -> pack -> rebuild -> rewire) is sound.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Repack_NoChange_RoundTripsWithoutCorruption()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src))
        {
            _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping.");
            return;
        }

        UndertaleData before = Load(src);
        int spritesBefore = before.Sprites.Count;
        int backgroundsBefore = before.Backgrounds.Count;
        int fontsBefore = before.Fonts.Count;

        // Per-sprite frame counts, to prove no frames got dropped or duplicated.
        var frameCountsBefore = before.Sprites
            .Where(s => s?.Name?.Content is not null)
            .ToDictionary(s => s.Name.Content, s => s.Textures.Count);

        string emptyOverrides = Path.Combine(Path.GetTempPath(), "pjum_empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyOverrides);
        string outPath = Path.Combine(Path.GetTempPath(), "pjum_noop_" + Guid.NewGuid().ToString("N") + ".win");

        try
        {
            var repacker = new TextureRepacker();
            RepackResult result = await repacker.RepackAsync(before, emptyOverrides);

            _out.WriteLine($"Repack produced {result.AtlasCount} atlas page(s), {result.PageItemCount} page item(s).");
            foreach (string w in result.Warnings)
                _out.WriteLine("WARN: " + w);

            Assert.True(result.PageItemCount > 0, "Repack produced zero texture page items.");

            Save(before, outPath);

            // Reload the written file — this is the real corruption gate. If the
            // graph we rebuilt was inconsistent, Read throws here.
            UndertaleData after = Load(outPath);

            Assert.Equal(spritesBefore, after.Sprites.Count);
            Assert.Equal(backgroundsBefore, after.Backgrounds.Count);
            Assert.Equal(fontsBefore, after.Fonts.Count);
            Assert.True(after.EmbeddedTextures.Count > 0, "No embedded textures after repack.");
            Assert.True(after.TexturePageItems.Count > 0, "No texture page items after repack.");

            // Frame counts preserved for every sprite.
            foreach (UndertaleSprite s in after.Sprites)
            {
                if (s?.Name?.Content is not string name) continue;
                if (frameCountsBefore.TryGetValue(name, out int expected))
                    Assert.Equal(expected, s.Textures.Count);
            }
        }
        finally
        {
            TryDelete(emptyOverrides);
            TryDeleteFile(outPath);
        }
    }

    // -----------------------------------------------------------------------
    // TEST 2 — the one you eyeball.
    // Export frame 0 of the first textured sprite, NEGATE it (obvious color
    // flip), feed it back as an override, repack, and write to a file you open
    // in the fork. The assert proves it loads; your eyes prove it's correct.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Repack_SingleSpriteSwap_ProducesLoadableFile()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src))
        {
            _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping.");
            return;
        }

        UndertaleData data = Load(src);

        // Pick the first sprite that actually has a frame-0 texture.
        UndertaleSprite target = data.Sprites.FirstOrDefault(
            s => s?.Textures.Count > 0 && s.Textures[0]?.Texture is not null);
        Assert.NotNull(target);
        string spriteName = target.Name.Content;
        _out.WriteLine($"Modifying sprite: {spriteName} (frame 0)");

        string overrides = Path.Combine(Path.GetTempPath(), "pjum_over_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(overrides);
        string outPath = Path.Combine(Path.GetTempPath(), $"pjum_swap_{spriteName}.win");

        try
        {
            // Export the real frame so dimensions are guaranteed valid...
            string framePath = Path.Combine(overrides, $"{spriteName}_0.png");
            using (var worker = new TextureWorker())
                worker.ExportAsPNG(target.Textures[0].Texture, framePath);

            // ...then flip its colors so the change is unmistakable in the GUI.
            using (var img = new MagickImage(framePath))
            {
                img.Negate();
                img.Write(framePath);
            }

            var repacker = new TextureRepacker();
            RepackResult result = await repacker.RepackAsync(data, overrides);
            foreach (string w in result.Warnings)
                _out.WriteLine("WARN: " + w);

            Save(data, outPath);

            // Loads clean?
            UndertaleData reloaded = Load(outPath);
            Assert.NotNull(reloaded.Sprites.ByName(spriteName));

            _out.WriteLine("OPEN THIS IN YOUR FORK TO EYEBALL THE NEGATED SPRITE:");
            _out.WriteLine(outPath);
            // NOTE: not deleting outPath — you need it for the visual check.
        }
        finally
        {
            TryDelete(overrides);
        }
    }

    [Fact]
    public async Task Repack_PictureBiggerThanPage_KeepsItsOriginalPage()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src))
        {
            _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping.");
            return;
        }

        UndertaleData data = Load(src);
        UndertaleSprite sprite = data.Sprites
            .Where(s => s?.Textures.Count > 0 && s.Textures[0]?.Texture is { } t
                        && t.SourceWidth == t.TargetWidth && t.SourceHeight == t.TargetHeight)
            .MaxBy(s => Math.Max(s.Textures[0].Texture.BoundingWidth, s.Textures[0].Texture.BoundingHeight));
        Assert.NotNull(sprite);
        UndertaleTexturePageItem original = sprite.Textures[0].Texture;
        UndertaleEmbeddedTexture originalPage = original.TexturePage;

        string emptyOverrides = Path.Combine(Path.GetTempPath(), "pjum_big_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyOverrides);
        string probe = Path.Combine(emptyOverrides, "probe.png");
        string outPath = Path.Combine(Path.GetTempPath(), "pjum_big_" + Guid.NewGuid().ToString("N") + ".win");

        try
        {
            using (var worker = new TextureWorker())
                worker.ExportAsPNG(original, probe);
            (int width, int height) = TextureWorker.GetImageSizeFromFile(probe);
            File.Delete(probe);
            int pageSize = Math.Max(width, height) - 1;
            _out.WriteLine($"Sprite {sprite.Name.Content} frame 0 is {width}x{height}; repacking with {pageSize} pages.");

            RepackResult result = await new TextureRepacker(atlasSize: pageSize).RepackAsync(data, emptyOverrides);
            foreach (string w in result.Warnings)
                _out.WriteLine("WARN: " + w);

            Assert.Same(original, sprite.Textures[0].Texture);
            Assert.Contains(original, data.TexturePageItems);
            Assert.Contains(originalPage, data.EmbeddedTextures);
            Assert.Empty(FindOrphanPageItemRefs(data));
            Assert.Contains(result.Warnings, w => w.Contains($"'{sprite.Name.Content}_0'") && w.Contains("Skipped"));
            Assert.Contains(result.Warnings, w => w.Contains($"Sprite '{sprite.Name.Content}' frame 0") && w.Contains("original texture page"));

            Save(data, outPath);
            UndertaleData after = Load(outPath);
            UndertaleTexturePageItem reloaded = after.Sprites.ByName(sprite.Name.Content).Textures[0].Texture;
            Assert.Equal(original.SourceWidth, reloaded.SourceWidth);
            Assert.Equal(original.SourceHeight, reloaded.SourceHeight);
            Assert.Equal(original.BoundingWidth, reloaded.BoundingWidth);
            Assert.Equal(original.BoundingHeight, reloaded.BoundingHeight);
        }
        finally
        {
            TryDelete(emptyOverrides);
            TryDeleteFile(outPath);
        }
    }

    [Fact]
    public async Task Repack_PictureDrawnLargerThanStored_KeepsItsStoredSizeAndPixels()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src))
        {
            _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping.");
            return;
        }

        UndertaleData data = Load(src);
        UndertaleSprite sprite = data.Sprites.First(s =>
            s?.Textures.Count > 0 && s.Textures[0]?.Texture is { } t &&
            t.SourceWidth == t.TargetWidth && t.SourceHeight == t.TargetHeight && t.SourceWidth is > 1 and < 200 && t.SourceHeight is > 1 and < 200);
        UndertaleTexturePageItem item = sprite.Textures[0].Texture;
        item.TargetX = 0;
        item.TargetY = 0;
        item.TargetWidth = item.BoundingWidth = (ushort)(item.SourceWidth * 2);
        item.TargetHeight = item.BoundingHeight = (ushort)(item.SourceHeight * 2);
        (ushort width, ushort height) stored = (item.SourceWidth, item.SourceHeight);
        byte[] storedPixels = StoredPixels(item);
        _out.WriteLine($"Sprite {sprite.Name.Content} frame 0: stored {stored.width}x{stored.height}, now drawn {item.TargetWidth}x{item.TargetHeight}.");

        string emptyOverrides = Path.Combine(Path.GetTempPath(), "pjum_larger_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyOverrides);
        string outPath = Path.Combine(Path.GetTempPath(), "pjum_larger_" + Guid.NewGuid().ToString("N") + ".win");

        try
        {
            RepackResult result = await new TextureRepacker().RepackAsync(data, emptyOverrides);
            foreach (string w in result.Warnings)
                _out.WriteLine("WARN: " + w);

            Save(data, outPath);
            UndertaleTexturePageItem after = Load(outPath).Sprites.ByName(sprite.Name.Content).Textures[0].Texture;
            Assert.Equal(stored.width, after.SourceWidth);
            Assert.Equal(stored.height, after.SourceHeight);
            Assert.Equal(stored.width * 2, after.TargetWidth);
            Assert.Equal(stored.height * 2, after.TargetHeight);
            Assert.Equal(storedPixels, StoredPixels(after));
        }
        finally
        {
            TryDelete(emptyOverrides);
            TryDeleteFile(outPath);
        }
    }

    [Fact]
    public async Task Repack_FullFrameOverride_ReplacesTheOriginalTrimBox()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src))
        {
            _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping.");
            return;
        }

        UndertaleData data = Load(src);
        UndertaleSprite sprite = data.Sprites.First(s =>
            s?.Textures.Count > 0 && s.Textures[0]?.Texture is { } t &&
            t.SourceWidth == t.TargetWidth && t.SourceHeight == t.TargetHeight &&
            (t.TargetX > 0 || t.TargetY > 0) && t.BoundingWidth is > 2 and < 200 && t.BoundingHeight is > 2 and < 200);
        UndertaleTexturePageItem item = sprite.Textures[0].Texture;
        (ushort width, ushort height) box = (item.BoundingWidth, item.BoundingHeight);
        _out.WriteLine($"Sprite {sprite.Name.Content} frame 0: drawn at ({item.TargetX},{item.TargetY}) {item.TargetWidth}x{item.TargetHeight} in a {box.width}x{box.height} frame.");

        string overrides = Path.Combine(Path.GetTempPath(), "pjum_full_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(overrides);
        string outPath = Path.Combine(Path.GetTempPath(), "pjum_full_" + Guid.NewGuid().ToString("N") + ".win");
        try
        {
            using var picture = new MagickImage(MagickColors.Red, box.width, box.height);
            picture.Write(Path.Combine(overrides, $"{sprite.Name.Content}_0.png"), MagickFormat.Png32);

            RepackResult result = await new TextureRepacker().RepackAsync(data, overrides);
            foreach (string w in result.Warnings)
                _out.WriteLine("WARN: " + w);

            Save(data, outPath);
            UndertaleTexturePageItem after = Load(outPath).Sprites.ByName(sprite.Name.Content).Textures[0].Texture;
            Assert.Equal((0, 0), ((int)after.TargetX, (int)after.TargetY));
            Assert.Equal(box, (after.TargetWidth, after.TargetHeight));
            Assert.Equal(box, (after.BoundingWidth, after.BoundingHeight));
            Assert.Equal(box, (after.SourceWidth, after.SourceHeight));
        }
        finally
        {
            TryDelete(overrides);
            TryDeleteFile(outPath);
        }
    }

    [Fact]
    public void Export_PictureDrawnAtAnotherShape_ComesOutAtItsDrawnSize()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src))
        {
            _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping.");
            return;
        }

        UndertaleData data = Load(src);
        UndertaleTexturePageItem item = data.Sprites.Where(s => s?.Textures.Count > 0).Select(s => s.Textures[0]?.Texture).First(t =>
            t is not null && t.SourceWidth == t.TargetWidth && t.SourceHeight == t.TargetHeight && t.SourceWidth is > 8 and < 200 && t.SourceHeight is > 8 and < 200)!;
        item.TargetWidth = (ushort)(item.SourceWidth - 3);
        item.TargetHeight = (ushort)(item.SourceHeight + 5);
        item.BoundingWidth = (ushort)Math.Max(item.BoundingWidth, item.TargetX + item.TargetWidth);
        item.BoundingHeight = (ushort)Math.Max(item.BoundingHeight, item.TargetY + item.TargetHeight);

        using var worker = new TextureWorker();
        using IMagickImage<byte> image = worker.GetTextureFor(item, "frame");

        Assert.Equal((uint)item.TargetWidth, image.Width);
        Assert.Equal((uint)item.TargetHeight, image.Height);
    }

    private static byte[] StoredPixels(UndertaleTexturePageItem item)
    {
        using var worker = new TextureWorker();
        MagickImage page = worker.GetEmbeddedTexture(item.TexturePage);
        using IMagickImage<byte> stored = page.CloneArea(item.SourceX, item.SourceY, item.SourceWidth, item.SourceHeight);
        return stored.GetPixels().ToByteArray(PixelMapping.RGBA)!;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
    
    // Add this method (and the helper below it) INTO your existing
    // RepackerTests class in MergeTests/RepackerTests.cs.
    // It reuses the Load(...) / SourceDataWin members already there.

    // -----------------------------------------------------------------------
    // RACE CATCHER.
    // The orphan (0xDEADC0DE UndertaleTexturePageItem) is intermittent: the same
    // input passes some runs and corrupts others. A single green run proves
    // nothing. So repack N times, and after EACH repack — before any save —
    // scan every asset that references a texture page item and confirm that
    // page item is actually in Data.TexturePageItems. Any reference that points
    // at an item NOT in the list is an orphan: it gets a pointer slot on write
    // but no data, which is exactly what ModLib reports as 0xDEADC0DE on reload.
    //
    // This detector finds the orphan directly, on the run it happens, and names
    // the owning asset — instead of waiting for a cryptic failure on reload.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Repack_RepeatedRuns_ProduceNoOrphanPageItems()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src))
        {
            _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping.");
            return;
        }

        bool soak = Environment.GetEnvironmentVariable("PJUM_SOAK") == "1";
        int iterations = soak ? 50 : 5;
        var failures = new System.Collections.Generic.List<string>();

        for (int i = 0; i < iterations; i++)
        {
            // Fresh load every iteration — the race is in the repack, and we
            // don't want state carrying over between runs.
            UndertaleData data = Load(src);

            string emptyOverrides = Path.Combine(Path.GetTempPath(), $"pjum_race_{i}_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(emptyOverrides);

            try
            {
                var repacker = new TextureRepacker();
                await repacker.RepackAsync(data, emptyOverrides);

                var orphans = FindOrphanPageItemRefs(data);
                if (orphans.Count > 0)
                {
                    failures.Add($"iteration {i}: {orphans.Count} orphan(s) -> " + string.Join("; ", orphans.Take(8)));
                    _out.WriteLine($"iteration {i}: ORPHANS: {string.Join("; ", orphans)}");
                }
                else
                {
                    _out.WriteLine($"iteration {i}: clean ({data.TexturePageItems.Count} page items)");
                }
            }
            finally
            {
                TryDelete(emptyOverrides);
            }
        }

        Assert.True(
            failures.Count == 0,
            $"Orphaned texture page items in {failures.Count}/{iterations} runs:\n" + string.Join("\n", failures));
    }

    // Returns a description of every texture-page-item reference held by an
    // asset that is NOT present in data.TexturePageItems (i.e. would serialize
    // as a dangling pointer). Empty list == clean graph.
    private static System.Collections.Generic.List<string> FindOrphanPageItemRefs(UndertaleData data)
    {
        // Reference identity, not equality: an item is "in the list" only if the
        // SAME object instance is in TexturePageItems.
        var live = new System.Collections.Generic.HashSet<UndertaleTexturePageItem>(
            data.TexturePageItems.Where(p => p is not null),
            ReferenceEqualityComparer.Instance);

        var orphans = new System.Collections.Generic.List<string>();

        void Check(UndertaleTexturePageItem item, string owner)
        {
            if (item is not null && !live.Contains(item))
                orphans.Add(owner);
        }

        foreach (UndertaleSprite s in data.Sprites)
        {
            if (s is null) continue;
            for (int f = 0; f < s.Textures.Count; f++)
                Check(s.Textures[f]?.Texture, $"sprite {s.Name?.Content}[{f}]");
        }

        foreach (UndertaleBackground b in data.Backgrounds)
            if (b is not null) Check(b.Texture, $"background {b.Name?.Content}");

        foreach (UndertaleFont fnt in data.Fonts)
        {
            if (fnt is null) continue;
            Check(fnt.Texture, $"font {fnt.Name?.Content}");
            // Fonts also carry per-glyph page references in some versions; if
            // your fork exposes fnt.Glyphs with a .Texture, uncomment to cover:
            // if (fnt.Glyphs is not null)
            //     foreach (var g in fnt.Glyphs)
            //         Check(g?.Texture, $"font-glyph {fnt.Name?.Content}");
        }

        return orphans;
    }
}