using System.Collections.Generic;
using System.IO;
using System;
using UndertaleModLib;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;
using Xunit;

namespace MergeTests;

// Semantic-resolver tests. These need a REAL data.win, so they are gated on
// PJUM_TEST_DATAWIN and skip silently if unset. They prove all four rows of the
// decision table against actual loaded data — AND prove every category's
// ByName lookup works in your fork (if a collection name is wrong, the
// "existing asset resolves to Replace" assert fails and names it).
public class ModResolverTests
{
    private readonly ITestOutputHelper _out;
    public ModResolverTests(ITestOutputHelper output) => _out = output;

    private static string SourceDataWin => Environment.GetEnvironmentVariable("PJUM_TEST_DATAWIN");

    private static UndertaleData Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return UndertaleIO.Read(fs);
    }

    private static ModManifest EmptyManifest() => new();

    // Find a real asset name of a given category to test against, so the test
    // isn't tied to a specific game's contents.
    private static string FirstSpriteName(UndertaleData d)
    {
        foreach (var s in d.Sprites)
            if (s?.Name?.Content is string n) return n;
        return null;
    }

    [Fact]
    public void ExistingSprite_resolves_to_Replace()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        UndertaleData data = Load(src);
        string real = FirstSpriteName(data);
        Assert.NotNull(real);

        var addr = ModAddressParser.Parse($"undertale/data.win/sprites/{real}/0.png");
        var result = ModResolver.Resolve(addr, data, EmptyManifest());

        Assert.Equal(ResolutionKind.Replace, result.Kind);
        Assert.NotNull(result.Existing);
    }

    [Fact]
    public void MissingUndeclared_throws_typo_trap()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        UndertaleData data = Load(src);
        var addr = ModAddressParser.Parse("undertale/data.win/sprites/spr_zzz_definitely_not_real/0.png");

        var ex = Assert.Throws<ModResolveException>(
            () => ModResolver.Resolve(addr, data, EmptyManifest()));
        Assert.Contains("newAssets", ex.Message);
    }

    [Fact]
    public void MissingButDeclared_resolves_to_Create()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        UndertaleData data = Load(src);
        var manifest = new ModManifest();
        manifest.NewAssets["sprites"] = new Dictionary<string, NewAssetEntry>
        {
            ["spr_mynewthing"] = new NewAssetEntry { Frames = 4 }
        };

        var addr = ModAddressParser.Parse("undertale/data.win/sprites/spr_mynewthing/0.png");
        var result = ModResolver.Resolve(addr, data, manifest);

        Assert.Equal(ResolutionKind.Create, result.Kind);
        Assert.Null(result.Existing);
        Assert.Equal(4, result.NewEntry.Frames);
    }

    [Fact]
    public void WrongType_throws_lie_trap()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        UndertaleData data = Load(src);

        // Find a name that exists as a sprite, then address it as a background.
        string spriteName = FirstSpriteName(data);
        Assert.NotNull(spriteName);

        var addr = ModAddressParser.Parse($"undertale/data.win/backgrounds/{spriteName}.png");
        var ex = Assert.Throws<ModResolveException>(
            () => ModResolver.Resolve(addr, data, EmptyManifest()));
        Assert.Contains("already exists as sprite", ex.Message);
    }

    [Fact]
    public void EveryCategory_lookup_is_wired()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        UndertaleData data = Load(src);

        // A bogus name in each category should hit the "not found" path, NOT the
        // "category has no collection" path. If any category isn't wired, its
        // resolve throws the "no resolvable collection" message and this fails.
        foreach (var cat in new[] { "sprites", "backgrounds", "fonts", "code", "objects", "sounds", "paths" })
        {
            string path = cat == "sprites"
                ? $"undertale/data.win/{cat}/zzz_nope/0.png"
                : $"undertale/data.win/{cat}/zzz_nope.bin";

            var addr = ModAddressParser.Parse(path);
            var ex = Assert.Throws<ModResolveException>(
                () => ModResolver.Resolve(addr, data, EmptyManifest()));
            Assert.DoesNotContain("no resolvable collection", ex.Message);
        }
    }
}