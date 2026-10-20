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
using PjumModLib;
using Xunit;

namespace MergeTests;

public class RenameTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pjum_renames_" + Guid.NewGuid().ToString("N"));

    public RenameTests(ITestOutputHelper output)
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

    private string Mod(string name, string renames)
    {
        string mod = Path.Combine(_root, name);
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "mod.json"), "{ \"name\": \"" + name + "\", \"version\": \"1.0.0\", \"renames\": " + renames + " }");
        return mod;
    }

    private static UndertaleSprite PickSprite(UndertaleData data) =>
        data.Sprites.First(s => s?.Textures.Count == 1 && s.Textures[0]?.Texture is { } t && t.BoundingWidth is > 2 and < 100 && t.BoundingHeight is > 2 and < 100);

    private static UndertaleGameObject PickObject(UndertaleData data) =>
        data.GameObjects.First(o => o?.Name?.Content is string n && data.Code.Count(c => c?.Name?.Content?.StartsWith($"gml_Object_{n}_") == true) >= 2);

    [Fact]
    public async Task A_renamed_sprite_and_object_keep_their_numbers_and_the_mod_replaces_them_by_the_new_name()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        UndertaleSprite sprite = PickSprite(probe);
        UndertaleGameObject obj = PickObject(probe);
        string oldSprite = sprite.Name.Content, oldObject = obj.Name.Content;
        int spriteIndex = probe.Sprites.IndexOf(sprite), objectIndex = probe.GameObjects.IndexOf(obj);
        var oldCode = probe.Code.Where(c => c?.Name?.Content?.StartsWith($"gml_Object_{oldObject}_") == true).Select(c => c.Name.Content).ToList();
        _out.WriteLine($"Renaming sprite {oldSprite} (#{spriteIndex}) and object {oldObject} (#{objectIndex}, {oldCode.Count} code entries).");

        string mod = Mod("RenameMod", "{ \"sprites\": { \"" + oldSprite + "\": \"spr_pjum_renamed\" }, \"objects\": { \"" + oldObject + "\": \"obj_pjum_renamed\" } }");
        string frameDir = Path.Combine(TestPaths.ModRoot(mod, probe), "sprites", "spr_pjum_renamed");
        Directory.CreateDirectory(frameDir);
        UndertaleTexturePageItem item = sprite.Textures[0].Texture;
        using (var red = new MagickImage(MagickColors.Red, item.BoundingWidth, item.BoundingHeight))
            red.Write(Path.Combine(frameDir, "0.png"), MagickFormat.Png32);
        string game = Game(src);

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("RenameMod", mod) });
        foreach (string w in result.Warnings) _out.WriteLine("WARN: " + w);
        Assert.True(result.Success, result.Reason);

        UndertaleData after = Load(Path.Combine(game, "data.win"));
        Assert.Equal("spr_pjum_renamed", after.Sprites[spriteIndex].Name.Content);
        Assert.Null(after.Sprites.ByName(oldSprite));
        Assert.Equal("obj_pjum_renamed", after.GameObjects[objectIndex].Name.Content);
        Assert.Null(after.GameObjects.ByName(oldObject));
        foreach (string code in oldCode)
        {
            Assert.Null(after.Code.ByName(code));
            Assert.NotNull(after.Code.ByName("gml_Object_obj_pjum_renamed_" + code[$"gml_Object_{oldObject}_".Length..]));
        }
        using var worker = new TextureWorker();
        using IMagickImage<byte> frame = worker.GetTextureFor(after.Sprites[spriteIndex].Textures[0].Texture, "frame", includePadding: true);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, frame.GetPixels().GetPixel(0, 0).ToArray());
    }

    [Fact]
    public async Task Two_mods_renaming_one_sprite_to_different_names_fail_naming_both()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        string oldSprite = PickSprite(Load(src)).Name.Content;
        string first = Mod("FirstRename", "{ \"sprites\": { \"" + oldSprite + "\": \"spr_pjum_first\" } }");
        string second = Mod("SecondRename", "{ \"sprites\": { \"" + oldSprite + "\": \"spr_pjum_second\" } }");

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(Game(src),
            new List<ModSource> { new("FirstRename", first), new("SecondRename", second) });

        Assert.False(result.Success);
        Assert.Equal("SecondRename", result.FailedMod);
        Assert.Contains("'FirstRename' renames it to 'spr_pjum_first'", result.Reason);
    }

    [Fact]
    public async Task A_later_mod_using_the_old_name_is_applied_to_the_renamed_sprite_with_a_warning()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        UndertaleSprite sprite = PickSprite(probe);
        string oldSprite = sprite.Name.Content;
        int spriteIndex = probe.Sprites.IndexOf(sprite);
        string renamer = Mod("Renamer", "{ \"sprites\": { \"" + oldSprite + "\": \"spr_pjum_renamed\" } }");
        string older = Mod("OldNameMod", "{ }");
        string frameDir = Path.Combine(TestPaths.ModRoot(older, probe), "sprites", oldSprite);
        Directory.CreateDirectory(frameDir);
        UndertaleTexturePageItem item = sprite.Textures[0].Texture;
        using (var blue = new MagickImage(MagickColors.Blue, item.BoundingWidth, item.BoundingHeight))
            blue.Write(Path.Combine(frameDir, "0.png"), MagickFormat.Png32);
        string game = Game(src);

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game,
            new List<ModSource> { new("Renamer", renamer), new("OldNameMod", older) });
        foreach (string w in result.Warnings) _out.WriteLine("WARN: " + w);
        Assert.True(result.Success, result.Reason);

        Assert.Contains(result.Warnings, w => w.Contains("[OldNameMod] uses the old name") && w.Contains("spr_pjum_renamed"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("[Renamer] has no files"));
        UndertaleData after = Load(Path.Combine(game, "data.win"));
        using var worker = new TextureWorker();
        using IMagickImage<byte> frame = worker.GetTextureFor(after.Sprites[spriteIndex].Textures[0].Texture, "frame", includePadding: true);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, frame.GetPixels().GetPixel(0, 0).ToArray());
    }

    [Fact]
    public void Two_swapped_objects_are_noted_and_not_treated_as_renames()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData original = Load(src);
        UndertaleData modded = Load(src);
        const int a = 20, b = 21;
        string first = modded.GameObjects[a].Name.Content, second = modded.GameObjects[b].Name.Content;
        (modded.GameObjects[a], modded.GameObjects[b]) = (modded.GameObjects[b], modded.GameObjects[a]);
        string folder = Path.GetDirectoryName(src)!;
        string mod = Path.Combine(_root, "SwapMod");

        ExtractResult result = ModExtractor.Extract(original, modded, folder, folder, mod, "Swap mod");
        foreach (string note in result.NotConverted) _out.WriteLine("note: " + note);

        Assert.Contains(result.NotConverted, n => n.Contains("2 objects changed number") && n.Contains($"'{first}' ({a} -> {b})") && n.Contains($"'{second}' ({b} -> {a})"));
        Assert.DoesNotContain("renames", File.ReadAllText(Path.Combine(mod, "mod.json")));
    }

    [Fact]
    public async Task A_renamed_sprite_is_extracted_as_a_rename_and_rebuilds_the_modded_game()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData original = Load(src);
        UndertaleData modded = Load(src);
        UndertaleSprite sprite = PickSprite(modded);
        string oldSprite = sprite.Name.Content;
        int spriteIndex = modded.Sprites.IndexOf(sprite);
        sprite.Name = modded.Strings.MakeString("spr_pjum_extracted_rename");
        using (var worker = new TextureWorker())
        using (IMagickImage<byte> image = worker.GetTextureFor(sprite.Textures[0].Texture, "frame"))
        {
            var negated = new MagickImage(image);
            negated.Negate(Channels.RGB);
            sprite.Textures[0].Texture.ReplaceTexture(negated);
        }
        string folder = Path.GetDirectoryName(src)!;
        string mod = Path.Combine(_root, "ExtractedRename");

        ExtractResult result = ModExtractor.Extract(original, modded, folder, folder, mod, "Extracted rename");
        foreach (string note in result.NotConverted) _out.WriteLine("note: " + note);

        Assert.DoesNotContain(result.NotConverted, n => n.Contains(oldSprite));
        string manifest = File.ReadAllText(Path.Combine(mod, "mod.json"));
        Assert.Contains($"\"{oldSprite}\": \"spr_pjum_extracted_rename\"", manifest);
        Assert.DoesNotContain("spr_pjum_extracted_rename\": {", manifest);
        Assert.True(File.Exists(Path.Combine(mod, "undertale", "data.win", "sprites", "spr_pjum_extracted_rename", "0.png")));

        string game = Game(src);
        MergeResult merge = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("Extracted rename", mod) });
        Assert.True(merge.Success, merge.Reason);
        UndertaleData after = Load(Path.Combine(game, "data.win"));
        Assert.Equal("spr_pjum_extracted_rename", after.Sprites[spriteIndex].Name.Content);
        using var a = new TextureWorker();
        using var b = new TextureWorker();
        using IMagickImage<byte> want = a.GetTextureFor(sprite.Textures[0].Texture, "w", includePadding: true);
        using IMagickImage<byte> got = b.GetTextureFor(after.Sprites[spriteIndex].Textures[0].Texture, "g", includePadding: true);
        Assert.Equal(want.GetPixels().ToByteArray(PixelMapping.RGBA), got.GetPixels().ToByteArray(PixelMapping.RGBA));
    }
}
