using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Data;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;
using PjumModLib;
using Xunit;

namespace MergeTests;

public class SpriteInfoTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pjum_spriteinfo_" + Guid.NewGuid().ToString("N"));

    public SpriteInfoTests(ITestOutputHelper output)
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

    private string SpriteFolder(UndertaleData probe, string modName, string sprite)
    {
        string mod = Path.Combine(_root, modName);
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "mod.json"), "{ \"name\": \"" + modName + "\", \"version\": \"1.0.0\" }");
        string folder = Path.Combine(TestPaths.ModRoot(mod, probe), "sprites", sprite);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Solid(string path, MagickColor color, uint width, uint height)
    {
        using var image = new MagickImage(color, width, height);
        image.Write(path, MagickFormat.Png32);
    }

    private static UndertaleSprite PickSprite(UndertaleData data, int frames = 1) =>
        data.Sprites.First(s => s?.Textures.Count == frames && s.CollisionMasks.Count == 1 && s.Textures.All(t => t?.Texture is { } i && i.BoundingWidth is > 4 and < 100 && i.BoundingHeight is > 4 and < 100));

    [Fact]
    public async Task Sprite_json_resizes_a_sprite_and_its_full_frame_and_mask_follow_the_new_size()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        UndertaleSprite sprite = PickSprite(probe);
        string name = sprite.Name.Content;
        uint width = sprite.Width + 6, height = sprite.Height + 4;
        _out.WriteLine($"Sprite {name}: {sprite.Width}x{sprite.Height} -> {width}x{height}.");
        string folder = SpriteFolder(probe, "ResizeMod", name);
        Solid(Path.Combine(folder, "0.png"), MagickColors.Red, width, height);
        using (var mask = new MagickImage(MagickColors.Black, width, height))
        {
            mask.Draw(new ImageMagick.Drawing.DrawableFillColor(MagickColors.White), new ImageMagick.Drawing.DrawableRectangle(0, 0, width / 2 - 1, height - 1));
            mask.Write(Path.Combine(folder, "0.mask.png"), MagickFormat.Png32);
        }
        File.WriteAllText(Path.Combine(folder, "sprite.json"),
            $"{{ \"width\": {width}, \"height\": {height}, \"originX\": 3, \"margins\": {{ \"left\": 0, \"top\": 0, \"right\": {width - 1}, \"bottom\": {height - 1} }}, \"collisionKind\": \"Precise\" }}");
        byte[] expectedMask = TextureWorker.ReadMaskData(Path.Combine(folder, "0.mask.png"), (int)width, (int)height);
        string game = Game(src);

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("ResizeMod", Path.Combine(_root, "ResizeMod")) });
        foreach (string w in result.Warnings) _out.WriteLine("WARN: " + w);
        Assert.True(result.Success, result.Reason);

        UndertaleSprite after = Load(Path.Combine(game, "data.win")).Sprites.ByName(name);
        Assert.Equal((width, height, 3, UndertaleSprite.SepMaskType.Precise), (after.Width, after.Height, after.OriginX, after.SepMasks));
        Assert.Equal((0, 0, (int)width - 1, (int)height - 1), (after.MarginLeft, after.MarginTop, after.MarginRight, after.MarginBottom));
        UndertaleTexturePageItem frame = after.Textures[0].Texture;
        Assert.Equal((0, 0, (int)width, (int)height, (int)width, (int)height),
            ((int)frame.TargetX, (int)frame.TargetY, (int)frame.TargetWidth, (int)frame.TargetHeight, (int)frame.BoundingWidth, (int)frame.BoundingHeight));
        UndertaleSprite.MaskEntry m = Assert.Single(after.CollisionMasks);
        Assert.Equal(expectedMask, m.Data);
    }

    [Fact]
    public async Task A_mask_of_the_wrong_size_fails_and_nothing_is_written()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        UndertaleSprite sprite = PickSprite(probe);
        string folder = SpriteFolder(probe, "BadMaskMod", sprite.Name.Content);
        Solid(Path.Combine(folder, "0.mask.png"), MagickColors.White, sprite.Width + 1, sprite.Height);
        string game = Game(src);
        byte[] before = File.ReadAllBytes(Path.Combine(game, "data.win"));

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("BadMaskMod", Path.Combine(_root, "BadMaskMod")) });

        Assert.False(result.Success);
        Assert.Equal("BadMaskMod", result.FailedMod);
        Assert.Contains("is not the proper size", result.Reason);
        Assert.Contains($"width: {sprite.Width} px, height: {sprite.Height} px", result.Reason);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(game, "data.win")));
    }

    [Fact]
    public async Task A_resize_without_mask_files_rebuilds_the_masks_from_the_frames_with_a_warning()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        UndertaleSprite sprite = PickSprite(probe);
        string name = sprite.Name.Content;
        uint width = sprite.Width + 8, height = sprite.Height + 2;
        string folder = SpriteFolder(probe, "NoMaskMod", name);
        Solid(Path.Combine(folder, "0.png"), MagickColors.Red, width, height);
        File.WriteAllText(Path.Combine(folder, "sprite.json"), $"{{ \"width\": {width}, \"height\": {height} }}");
        string game = Game(src);

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("NoMaskMod", Path.Combine(_root, "NoMaskMod")) });
        Assert.True(result.Success, result.Reason);

        Assert.Contains(result.Warnings, w => w.Contains($"sprite '{name}' changed size but has no mask files"));
        UndertaleSprite after = Load(Path.Combine(game, "data.win")).Sprites.ByName(name);
        UndertaleSprite.MaskEntry m = Assert.Single(after.CollisionMasks);
        Assert.Equal(((int)width, (int)height), (m.Width, m.Height));
        int stride = ((int)width + 7) / 8;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                Assert.True((m.Data[y * stride + x / 8] & (1 << (7 - x % 8))) != 0, $"mask bit {x},{y} should be set (the frame is solid red)");
    }

    [Fact]
    public async Task A_frame_png_of_another_size_becomes_a_full_frame_of_that_size()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        UndertaleSprite sprite = PickSprite(probe);
        string name = sprite.Name.Content;
        UndertaleTexturePageItem item = sprite.Textures[0].Texture;
        uint width = (uint)item.BoundingWidth + 7, height = (uint)item.BoundingHeight + 3;
        string folder = SpriteFolder(probe, "BiggerFrameMod", name);
        Solid(Path.Combine(folder, "0.png"), MagickColors.Red, width, height);
        string game = Game(src);

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("BiggerFrameMod", Path.Combine(_root, "BiggerFrameMod")) });
        Assert.True(result.Success, result.Reason);

        UndertaleSprite after = Load(Path.Combine(game, "data.win")).Sprites.ByName(name);
        UndertaleTexturePageItem frame = after.Textures[0].Texture;
        Assert.Equal((0, 0, (int)width, (int)height, (int)width, (int)height),
            ((int)frame.TargetX, (int)frame.TargetY, (int)frame.TargetWidth, (int)frame.TargetHeight, (int)frame.BoundingWidth, (int)frame.BoundingHeight));
        Assert.Equal((sprite.Width, sprite.Height), (after.Width, after.Height));
    }

    [Fact]
    public async Task Sprite_json_masks_zero_removes_the_collision_masks()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        UndertaleSprite sprite = PickSprite(probe);
        string folder = SpriteFolder(probe, "NoMasksMod", sprite.Name.Content);
        File.WriteAllText(Path.Combine(folder, "sprite.json"), "{ \"masks\": 0 }");
        string game = Game(src);

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("NoMasksMod", Path.Combine(_root, "NoMasksMod")) });
        Assert.True(result.Success, result.Reason);

        Assert.Empty(Load(Path.Combine(game, "data.win")).Sprites.ByName(sprite.Name.Content).CollisionMasks);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("mask"));
    }

    [Fact]
    public async Task Sprite_json_frames_drops_the_extra_frames()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        UndertaleSprite sprite = probe.Sprites.First(s => s?.Textures.Count >= 3 && s.CollisionMasks.Count == 1);
        string folder = SpriteFolder(probe, "FewerFramesMod", sprite.Name.Content);
        File.WriteAllText(Path.Combine(folder, "sprite.json"), "{ \"frames\": 1 }");
        string game = Game(src);

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("FewerFramesMod", Path.Combine(_root, "FewerFramesMod")) });
        Assert.True(result.Success, result.Reason);

        UndertaleSprite after = Load(Path.Combine(game, "data.win")).Sprites.ByName(sprite.Name.Content);
        Assert.Single(after.Textures);
    }

    [Fact]
    public async Task A_resized_sprite_extracts_with_sprite_json_and_masks_and_rebuilds_exactly()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData original = Load(src);
        UndertaleData modded = Load(src);
        UndertaleSprite sprite = PickSprite(modded);
        string name = sprite.Name.Content;
        uint width = sprite.Width + 5, height = sprite.Height + 3;
        UndertaleTexturePageItem item = sprite.Textures[0].Texture;
        using (var green = new MagickImage(MagickColors.Lime, width, height))
            item.ReplaceTexture(green);
        item.TargetX = item.TargetY = 0;
        item.BoundingWidth = (ushort)width;
        item.BoundingHeight = (ushort)height;
        sprite.Width = width;
        sprite.Height = height;
        sprite.OriginY = 2;
        sprite.CollisionMasks.Clear();
        UndertaleSprite.MaskEntry newMask = sprite.NewMaskEntry(modded);
        int maskStride = (newMask.Width + 7) / 8;
        for (int i = 0; i < newMask.Data.Length; i += 3)
            newMask.Data[i] = 0b10100101;
        for (int y = 0; y < newMask.Height; y++)
            for (int x = newMask.Width; x < maskStride * 8; x++)
                newMask.Data[y * maskStride + x / 8] &= (byte)~(1 << (7 - x % 8));
        sprite.CollisionMasks.Add(newMask);
        string folder = Path.GetDirectoryName(src)!;
        string mod = Path.Combine(_root, "ExtractedResize");

        ExtractResult result = ModExtractor.Extract(original, modded, folder, folder, mod, "Extracted resize");
        foreach (string note in result.NotConverted) _out.WriteLine("note: " + note);

        Assert.DoesNotContain(result.NotConverted, n => n.Contains(name));
        string spriteDir = Path.Combine(mod, "undertale", "data.win", "sprites", name);
        JsonNode json = JsonNode.Parse(File.ReadAllText(Path.Combine(spriteDir, "sprite.json")))!;
        Assert.Equal((width, height, 2), (json["width"]!.GetValue<uint>(), json["height"]!.GetValue<uint>(), json["originY"]!.GetValue<int>()));
        Assert.Null(json["originX"]);
        Assert.True(File.Exists(Path.Combine(spriteDir, "0.png")));
        Assert.True(File.Exists(Path.Combine(spriteDir, "0.mask.png")));

        string game = Game(src);
        MergeResult merge = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("Extracted resize", mod) });
        Assert.True(merge.Success, merge.Reason);
        UndertaleSprite after = Load(Path.Combine(game, "data.win")).Sprites.ByName(name);
        Assert.Equal((width, height, 2), (after.Width, after.Height, after.OriginY));
        Assert.Equal(newMask.Data, Assert.Single(after.CollisionMasks).Data);
        using var a = new TextureWorker();
        using var b = new TextureWorker();
        using IMagickImage<byte> want = a.GetTextureFor(item, "w", includePadding: true);
        using IMagickImage<byte> got = b.GetTextureFor(after.Textures[0].Texture, "g", includePadding: true);
        Assert.Equal(want.GetPixels().ToByteArray(PixelMapping.RGBA), got.GetPixels().ToByteArray(PixelMapping.RGBA));
    }
}
