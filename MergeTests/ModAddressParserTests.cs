using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;
using Xunit;

namespace MergeTests;

// Pure structural-grammar tests. No data.win, no soak gate — run every
// `dotnet test` in milliseconds and pin the variable-depth address contract.
public class ModAddressParserTests
{
    // ---- valid shapes parse correctly ------------------------------------

    [Fact]
    public void Undertale_sprite_frame_parses_all_fields()
    {
        var a = ModAddressParser.Parse("undertale/data.win/sprites/spr_susie/0.png");
        Assert.Equal("undertale", a.Game);
        Assert.Equal(new[] { "data.win" }, a.Route);
        Assert.Equal("data.win", a.Container);
        Assert.Equal(AssetCategory.Sprites, a.Category);
        Assert.Equal("spr_susie", a.AssetName);
        Assert.Equal(0, a.Frame);
    }

    [Fact]
    public void Deltarune_chapter_audiogroup_parses_multi_segment_route()
    {
        var a = ModAddressParser.Parse("deltarune/chapter2/audiogroup1/sounds/snd_x.ogg");
        Assert.Equal("deltarune", a.Game);
        Assert.Equal(new[] { "chapter2", "audiogroup1" }, a.Route);
        Assert.Equal("audiogroup1", a.Container);           // last route segment
        Assert.Equal(new[] { "chapter2" }, a.RoutePrefix);  // everything before container
        Assert.Equal(AssetCategory.Sounds, a.Category);
        Assert.Equal("snd_x", a.AssetName);
        Assert.Null(a.Frame);
    }

    [Fact]
    public void Deltarune_root_code_parses()
    {
        var a = ModAddressParser.Parse("deltarune/root/data.win/code/gml_Script_scr_x.gml");
        Assert.Equal(new[] { "root", "data.win" }, a.Route);
        Assert.Equal(AssetCategory.Code, a.Category);
        Assert.Equal("gml_Script_scr_x", a.AssetName);
    }

    [Fact]
    public void Background_and_font_are_single_files()
    {
        var bg = ModAddressParser.Parse("undertale/data.win/backgrounds/bg_ruinsbg.png");
        Assert.Equal(AssetCategory.Backgrounds, bg.Category);
        Assert.Equal("bg_ruinsbg", bg.AssetName);

        var fnt = ModAddressParser.Parse("undertale/data.win/fonts/fnt_main.png");
        Assert.Equal(AssetCategory.Fonts, fnt.Category);
        Assert.Equal("fnt_main", fnt.AssetName);
    }

    [Fact]
    public void Raw_files_keep_their_nested_tail()
    {
        var a = ModAddressParser.Parse("deltarune/shared/files/mus/newsong.ogg");
        Assert.Equal("deltarune", a.Game);
        Assert.Equal(new[] { "shared" }, a.Route);
        Assert.Equal(AssetCategory.Files, a.Category);
        Assert.True(a.IsRawFile);
        Assert.Equal("mus/newsong.ogg", a.AssetName);
    }

    [Fact]
    public void Backslashes_and_leading_slash_are_normalized()
    {
        var a = ModAddressParser.Parse("/undertale\\data.win\\sprites\\spr_x\\3.png");
        Assert.Equal("spr_x", a.AssetName);
        Assert.Equal(3, a.Frame);
    }

    // ---- malformed shapes fail loudly ------------------------------------

    [Theory]
    [InlineData("")]                                                // empty
    [InlineData("undertale/data.win/sprites")]                      // too few segments
    [InlineData("mario64/data.win/sprites/spr_x/0.png")]            // unknown game
    [InlineData("undertale/data.win/sprts/spr_x/0.png")]            // no known category anywhere
    [InlineData("undertale/data.win/sprites/spr_x/notaframe.png")]  // non-integer frame
    [InlineData("undertale/sprites/spr_x/0.png")]                   // missing route (category right after game)
    [InlineData("undertale/data.win/code/a/b.gml")]                 // single-file category, extra segment
    [InlineData("undertale/data.win/sprites/spr_x.png")]            // sprite missing frame file
    public void Malformed_paths_throw(string bad)
    {
        Assert.Throws<ModAddressFormatException>(() => ModAddressParser.Parse(bad));
    }

    [Fact]
    public void TryParse_reports_reason_without_throwing()
    {
        bool ok = ModAddressParser.TryParse("undertale/data.win/sprts/x/0.png", out _, out string error);
        Assert.False(ok);
        Assert.Contains("category", error);
    }
}