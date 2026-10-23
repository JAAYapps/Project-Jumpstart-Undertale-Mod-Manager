using System.Text.Json;
using UndertaleModLib;
using UndertaleModLib.Models;
using PjumModLib;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Fonts;

public static class FontImporter
{
    public static FontJson ReadJson(string file)
    {
        FontJson json;
        try
        {
            json = JsonSerializer.Deserialize<FontJson>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidOperationException($"Font JSON '{file}' parsed to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Font JSON '{file}' is invalid: {ex.Message}", ex);
        }
        if (json.Glyphs is List<FontGlyphJson> glyphs)
        {
            if (glyphs.GroupBy(g => g.Char).FirstOrDefault(g => g.Count() > 1) is { } twice)
                throw new InvalidOperationException($"Font JSON '{file}' lists letter {twice.Key} ({Letter(twice.Key)}) {twice.Count()} times; each letter can only be listed once.");
            foreach (FontGlyphJson glyph in glyphs)
                if (glyph.Kerning?.FirstOrDefault(k => k is null || k.Length != 2) is not null)
                    throw new InvalidOperationException($"Font JSON '{file}': letter {glyph.Char} ({Letter(glyph.Char)}) has a \"kerning\" entry that isn't [letter, amount].");
        }
        return json;
    }

    public static void Apply(UndertaleData data, UndertaleFont font, FontJson json)
    {
        if (json.DisplayName is string displayName) font.DisplayName = data.Strings.MakeString(displayName);
        if (json.EmSize is float emSize) font.EmSize = emSize;
        if (json.EmSizeIsFloat is bool emSizeIsFloat) font.EmSizeIsFloat = emSizeIsFloat;
        if (json.Bold is bool bold) font.Bold = bold;
        if (json.Italic is bool italic) font.Italic = italic;
        if (json.RangeStart is ushort rangeStart) font.RangeStart = rangeStart;
        if (json.RangeEnd is uint rangeEnd) font.RangeEnd = rangeEnd;
        if (json.Charset is byte charset) font.Charset = charset;
        if (json.AntiAliasing is byte antiAliasing) font.AntiAliasing = antiAliasing;
        if (json.ScaleX is float scaleX) font.ScaleX = scaleX;
        if (json.ScaleY is float scaleY) font.ScaleY = scaleY;
        if (json.AscenderOffset is int ascenderOffset) font.AscenderOffset = ascenderOffset;
        if (json.Ascender is uint ascender) font.Ascender = ascender;
        if (json.SdfSpread is uint sdfSpread) font.SDFSpread = sdfSpread;
        if (json.LineHeight is uint lineHeight) font.LineHeight = lineHeight;
        if (json.Glyphs is not List<FontGlyphJson> glyphs)
            return;

        int width = font.Texture?.SourceWidth ?? 0, height = font.Texture?.SourceHeight ?? 0;
        foreach (FontGlyphJson glyph in glyphs)
            if (glyph.X + glyph.Width > width || glyph.Y + glyph.Height > height)
                throw new InvalidOperationException($"Font '{font.Name.Content}' letter {glyph.Char} ({Letter(glyph.Char)}) is at {glyph.X},{glyph.Y} and {glyph.Width}x{glyph.Height} big, which goes outside the {width}x{height} font picture.");
        font.Glyphs.Clear();
        foreach (FontGlyphJson glyph in glyphs)
        {
            var g = new UndertaleFont.Glyph
            {
                Character = glyph.Char,
                SourceX = glyph.X,
                SourceY = glyph.Y,
                SourceWidth = glyph.Width,
                SourceHeight = glyph.Height,
                Shift = glyph.Shift,
                Offset = glyph.Offset
            };
            foreach (short[] kerning in glyph.Kerning ?? [])
                g.Kerning.Add(new UndertaleFont.Glyph.GlyphKerning { Character = kerning[0], ShiftModifier = kerning[1] });
            font.Glyphs.Add(g);
        }
    }

    private static string Letter(ushort c) => c is >= 33 and < 127 ? $"'{(char)c}'" : $"code {c}";
}
