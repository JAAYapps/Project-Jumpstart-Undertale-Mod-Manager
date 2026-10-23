using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Data;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;
using PjumModLib;
using Xunit;

namespace MergeTests;

public class FontTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pjum_fonts_" + Guid.NewGuid().ToString("N"));

    public FontTests(ITestOutputHelper output)
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

    private string Mod(UndertaleData probe, string name, Dictionary<string, string> fontFiles, string newAssets = "")
    {
        string mod = Path.Combine(_root, name);
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "mod.json"), "{ \"name\": \"" + name + "\", \"version\": \"1.0.0\", \"newAssets\": { " + newAssets + " } }");
        string fonts = Path.Combine(TestPaths.ModRoot(mod, probe), "fonts");
        Directory.CreateDirectory(fonts);
        foreach ((string file, string json) in fontFiles)
            File.WriteAllText(Path.Combine(fonts, file), json);
        return mod;
    }

    private static void Picture(string mod, UndertaleData probe, string font, uint width, uint height)
    {
        using var image = new MagickImage(MagickColors.White, width, height);
        image.Write(Path.Combine(TestPaths.ModRoot(mod, probe), "fonts", font + ".png"));
    }

    private static string Print(UndertaleFont f) =>
        $"{f.DisplayName?.Content} {f.EmSize} {f.EmSizeIsFloat} {f.Bold} {f.Italic} {f.RangeStart}-{f.RangeEnd} {f.Charset} {f.AntiAliasing} {f.ScaleX},{f.ScaleY} {f.Texture?.SourceWidth}x{f.Texture?.SourceHeight}\n" +
        string.Join("\n", f.Glyphs.Select(g => $"{g.Character} {g.SourceX},{g.SourceY} {g.SourceWidth}x{g.SourceHeight} {g.Shift} {g.Offset}"));

    [Fact]
    public void A_font_json_is_its_own_category()
    {
        ModAddress json = ModAddressParser.Parse("undertale/data.win/fonts/fnt_main.json");
        ModAddress png = ModAddressParser.Parse("undertale/data.win/fonts/fnt_main.png");
        Assert.Equal((AssetCategory.FontInfo, "fnt_main"), (json.Category, json.AssetName));
        Assert.Equal((AssetCategory.Fonts, "fnt_main"), (png.Category, png.AssetName));
    }

    [Fact]
    public async Task Changed_letters_and_a_new_font_extract_and_rebuild()
    {
        string? src = TestPaths.DataWin;
        if (src is null) { _out.WriteLine("No game found — skipping."); return; }
        UndertaleData original = Load(src);
        UndertaleData modded = Load(src);
        UndertaleFont changed = modded.Fonts.First(f => f?.Texture is not null && f.Glyphs.Count >= 2);
        UndertaleFont template = modded.Fonts.LastOrDefault(f => f?.Texture is not null && f != changed && f.Glyphs.Count >= 1) ?? changed;
        changed.EmSize += 2;
        changed.Bold = !changed.Bold;
        UndertaleFont.Glyph first = changed.Glyphs[0];
        changed.Glyphs[1].Shift += 3;
        changed.Glyphs[1].Offset -= 1;
        ushort extra = (ushort)(changed.Glyphs.Max(g => g.Character) + 1);
        changed.Glyphs.Add(new UndertaleFont.Glyph { Character = extra, SourceX = first.SourceX, SourceY = first.SourceY, SourceWidth = first.SourceWidth, SourceHeight = first.SourceHeight, Shift = first.Shift });
        changed.RangeEnd = Math.Max(changed.RangeEnd, extra);
        var added = new UndertaleFont
        {
            Name = modded.Strings.MakeString("fnt_pjum_new"),
            DisplayName = modded.Strings.MakeString("PJUM Test"),
            EmSize = 9,
            RangeStart = template.RangeStart,
            RangeEnd = template.RangeEnd,
            Texture = template.Texture
        };
        foreach (UndertaleFont.Glyph g in template.Glyphs)
            added.Glyphs.Add(new UndertaleFont.Glyph { Character = g.Character, SourceX = g.SourceX, SourceY = g.SourceY, SourceWidth = g.SourceWidth, SourceHeight = g.SourceHeight, Shift = g.Shift, Offset = g.Offset });
        modded.Fonts.Add(added);
        string folder = Path.GetDirectoryName(src)!;
        string mod = Path.Combine(_root, "FontMod");

        ExtractResult result = ModExtractor.Extract(original, modded, folder, folder, mod, "Font mod");
        foreach (string note in result.NotConverted) _out.WriteLine("note: " + note);
        Assert.DoesNotContain(result.NotConverted, n => n.Contains("Font '"));
        Assert.Contains("\"fnt_pjum_new\": {}", File.ReadAllText(Path.Combine(mod, "mod.json")).Replace("\n", "").Replace("  ", ""));

        string game = Game(src);
        MergeResult merge = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("Font mod", mod) });
        Assert.True(merge.Success, merge.Reason);
        foreach (string w in merge.Warnings) _out.WriteLine("WARN: " + w);

        UndertaleData after = Load(Path.Combine(game, "data.win"));
        Assert.Equal(Print(changed), Print(after.Fonts.ByName(changed.Name.Content)));
        UndertaleFont rebuilt = after.Fonts.ByName("fnt_pjum_new");
        Assert.Equal(Print(added), Print(rebuilt));
        Assert.Equal(modded.Fonts.IndexOf(added), after.Fonts.IndexOf(rebuilt));
    }

    [Fact]
    public async Task A_new_font_without_its_letters_fails()
    {
        string? src = TestPaths.DataWin;
        if (src is null) { _out.WriteLine("No game found — skipping."); return; }
        UndertaleData probe = Load(src);
        string mod = Mod(probe, "NoLettersMod", new Dictionary<string, string>(), "\"fonts\": { \"fnt_pjum_bare\": {} }");
        Picture(mod, probe, "fnt_pjum_bare", 16, 16);

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(Game(src), new List<ModSource> { new("NoLettersMod", mod) });

        Assert.False(result.Success);
        Assert.Contains("needs fonts/fnt_pjum_bare.json", result.Reason);
    }

    [Fact]
    public async Task A_letter_outside_the_picture_fails()
    {
        string? src = TestPaths.DataWin;
        if (src is null) { _out.WriteLine("No game found — skipping."); return; }
        UndertaleData probe = Load(src);
        string mod = Mod(probe, "OutsideMod", new Dictionary<string, string>
        {
            ["fnt_pjum_small.json"] = """{ "emSize": 8, "glyphs": [ { "char": 65, "x": 10, "y": 0, "width": 10, "height": 10, "shift": 10, "offset": 0 } ] }"""
        }, "\"fonts\": { \"fnt_pjum_small\": {} }");
        Picture(mod, probe, "fnt_pjum_small", 16, 16);

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(Game(src), new List<ModSource> { new("OutsideMod", mod) });

        Assert.False(result.Success);
        Assert.Contains("letter 65 ('A')", result.Reason);
        Assert.Contains("outside the 16x16 font picture", result.Reason);
    }

    [Fact]
    public async Task Letters_and_picture_from_different_mods_give_a_warning()
    {
        string? src = TestPaths.DataWin;
        if (src is null) { _out.WriteLine("No game found — skipping."); return; }
        UndertaleData probe = Load(src);
        UndertaleFont font = probe.Fonts.First(f => f?.Texture is not null && f.Glyphs.Count >= 1);
        UndertaleFont.Glyph g = font.Glyphs[0];
        string letters = $$"""{ "glyphs": [ { "char": {{g.Character}}, "x": 0, "y": 0, "width": 1, "height": 1, "shift": {{g.Shift}}, "offset": 0 } ] }""";
        string a = Mod(probe, "LettersMod", new Dictionary<string, string> { [font.Name.Content + ".json"] = letters });
        string b = Mod(probe, "PictureMod", new Dictionary<string, string>());
        Picture(b, probe, font.Name.Content, font.Texture.SourceWidth, font.Texture.SourceHeight);

        string game = Game(src);
        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("LettersMod", a), new("PictureMod", b) });
        Assert.True(result.Success, result.Reason);

        Assert.Contains(result.Warnings, w => w.Contains($"Font '{font.Name.Content}'") && w.Contains("'LettersMod'") && w.Contains("'PictureMod'"));
        Assert.Equal((ushort)1, Load(Path.Combine(game, "data.win")).Fonts.ByName(font.Name.Content).Glyphs.Single().SourceWidth);
    }
}
