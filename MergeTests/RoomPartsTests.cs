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

public class RoomPartsTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pjum_roomparts_" + Guid.NewGuid().ToString("N"));

    public RoomPartsTests(ITestOutputHelper output)
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

    private string Mod(UndertaleData probe, string name, string room, string json, string newAssets = "")
    {
        string mod = Path.Combine(_root, name);
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "mod.json"), "{ \"name\": \"" + name + "\", \"version\": \"1.0.0\", \"newAssets\": { " + newAssets + " } }");
        string rooms = Path.Combine(TestPaths.ModRoot(mod, probe), "rooms");
        Directory.CreateDirectory(rooms);
        File.WriteAllText(Path.Combine(rooms, room + ".json"), json);
        return mod;
    }

    private static UndertaleRoom PickRoom(UndertaleData data) =>
        data.Rooms.First(r => r?.Tiles.Count >= 3 && r.Backgrounds.Count >= 2 && r.Tiles.All(t => t.BackgroundDefinition is not null));

    [Fact]
    public async Task A_room_patch_changes_settings_a_background_slot_and_tiles()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        UndertaleRoom room = PickRoom(probe);
        UndertaleRoom.Tile changed = room.Tiles[0], removed = room.Tiles[1];
        string bg = room.Tiles[2].BackgroundDefinition.Name.Content;
        uint nextTile = probe.GeneralInfo.LastTile;
        _out.WriteLine($"Room {room.Name.Content}: change tile {changed.InstanceID}, remove {removed.InstanceID}, add a {bg} tile; next tile id {nextTile}.");

        string mod = Mod(probe, "PartsMod", room.Name.Content,
            "{ \"settings\": { \"height\": " + (room.Height + 20) + " }, " +
            "\"backgrounds\": [ { \"slot\": 1, \"enabled\": true, \"background\": \"" + bg + "\", \"x\": 4, \"y\": 5, \"tileX\": true } ], " +
            "\"tiles\": { \"add\": [ { \"background\": \"" + bg + "\", \"x\": 11, \"y\": 22, \"sourceX\": 0, \"sourceY\": 0, \"width\": 20, \"height\": 20, \"depth\": 999 } ], " +
            "\"change\": [ { \"id\": " + changed.InstanceID + ", \"sourceX\": " + (changed.SourceX + 20) + " } ], \"remove\": [ " + removed.InstanceID + " ] } }");
        string game = Game(src);

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("PartsMod", mod) });
        foreach (string w in result.Warnings) _out.WriteLine("WARN: " + w);
        Assert.True(result.Success, result.Reason);

        UndertaleData after = Load(Path.Combine(game, "data.win"));
        UndertaleRoom r = after.Rooms.ByName(room.Name.Content);
        Assert.Equal(room.Height + 20, r.Height);
        UndertaleRoom.Background slot = r.Backgrounds[1];
        Assert.Equal((true, bg, 4, 5, true, false), (slot.Enabled, slot.BackgroundDefinition.Name.Content, slot.X, slot.Y, slot.TiledHorizontally, slot.TiledVertically));
        UndertaleRoom.Tile c = r.Tiles.Single(t => t.InstanceID == changed.InstanceID);
        Assert.Equal((changed.SourceX + 20, changed.SourceY, changed.X, changed.Y), (c.SourceX, c.SourceY, c.X, c.Y));
        Assert.DoesNotContain(r.Tiles, t => t.InstanceID == removed.InstanceID);
        UndertaleRoom.Tile added = r.Tiles.Single(t => t.InstanceID == nextTile);
        Assert.Equal((bg, 11, 22, 20u, 20u, 999), (added.BackgroundDefinition.Name.Content, added.X, added.Y, added.Width, added.Height, added.TileDepth));
        Assert.Equal(nextTile + 1, after.GeneralInfo.LastTile);
        Assert.Equal(room.Tiles.Count, r.Tiles.Count);
    }

    [Fact]
    public async Task A_new_background_is_created_and_a_tile_and_a_slot_can_use_it()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        UndertaleRoom room = PickRoom(probe);
        const string newBg = "pjum_new_background";
        string mod = Mod(probe, "NewBgMod", room.Name.Content,
            "{ \"backgrounds\": [ { \"slot\": 1, \"enabled\": true, \"background\": \"" + newBg + "\" } ], " +
            "\"tiles\": { \"add\": [ { \"background\": \"" + newBg + "\", \"x\": 0, \"y\": 0, \"sourceX\": 10, \"sourceY\": 10, \"width\": 20, \"height\": 20 } ] } }",
            "\"backgrounds\": { \"" + newBg + "\": {} }");
        string bgDir = Path.Combine(TestPaths.ModRoot(mod, probe), "backgrounds");
        Directory.CreateDirectory(bgDir);
        using (var image = new MagickImage(MagickColors.Orange, 64, 48))
            image.Write(Path.Combine(bgDir, newBg + ".png"), MagickFormat.Png32);
        string game = Game(src);

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("NewBgMod", mod) });
        foreach (string w in result.Warnings) _out.WriteLine("WARN: " + w);
        Assert.True(result.Success, result.Reason);
        Assert.DoesNotContain(result.Warnings, w => w.Contains(newBg));

        UndertaleData after = Load(Path.Combine(game, "data.win"));
        UndertaleBackground created = after.Backgrounds.ByName(newBg);
        Assert.NotNull(created);
        Assert.Equal((64, 48), ((int)created.Texture.BoundingWidth, (int)created.Texture.BoundingHeight));
        using var worker = new TextureWorker();
        using IMagickImage<byte> pic = worker.GetTextureFor(created.Texture, newBg, includePadding: true);
        Assert.Equal(MagickColors.Orange.ToByteArray(), pic.GetPixels().GetPixel(10, 10).ToArray());
        UndertaleRoom r = after.Rooms.ByName(room.Name.Content);
        Assert.Same(created, r.Backgrounds[1].BackgroundDefinition);
        Assert.Contains(r.Tiles, t => t.BackgroundDefinition == created);
    }

    [Theory]
    [InlineData("{ \"tiles\": { \"change\": [ { \"id\": 4000000000, \"x\": 1 } ] } }", "has no tile 4000000000")]
    [InlineData("{ \"tiles\": { \"add\": [ { \"background\": \"bg_pjum_nope\" } ] } }", "uses background 'bg_pjum_nope', which doesn't exist")]
    [InlineData("{ \"backgrounds\": [ { \"slot\": 99, \"enabled\": true } ] }", "not 99")]
    public async Task A_bad_room_part_fails_before_anything_is_written(string json, string expected)
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        string mod = Mod(probe, "BadPartMod", PickRoom(probe).Name.Content, json);
        string game = Game(src);
        byte[] before = File.ReadAllBytes(Path.Combine(game, "data.win"));

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("BadPartMod", mod) });

        Assert.False(result.Success);
        Assert.Contains(expected, result.Reason);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(game, "data.win")));
    }

    [Fact]
    public async Task Room_parts_and_a_new_background_extract_and_rebuild_the_room()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData original = Load(src);
        UndertaleData modded = Load(src);
        UndertaleRoom room = PickRoom(modded);
        UndertaleBackground template = room.Tiles[2].BackgroundDefinition;
        var newBg = new UndertaleBackground { Name = modded.Strings.MakeString("pjum_extracted_background"), Texture = template.Texture };
        modded.Backgrounds.Add(newBg);
        room.Height += 10;
        room.Backgrounds[1].Enabled = true;
        room.Backgrounds[1].BackgroundDefinition = newBg;
        room.Backgrounds[1].X = 7;
        room.Tiles[0].SourceX += 20;
        room.Tiles.RemoveAt(1);
        room.Tiles.Add(new UndertaleRoom.Tile { InstanceID = modded.GeneralInfo.LastTile++, BackgroundDefinition = newBg, X = 3, Y = 4, Width = 20, Height = 20, TileDepth = 500 });
        string folder = Path.GetDirectoryName(src)!;
        string mod = Path.Combine(_root, "ExtractedParts");

        ExtractResult result = ModExtractor.Extract(original, modded, folder, folder, mod, "Extracted parts");
        foreach (string note in result.NotConverted) _out.WriteLine("note: " + note);

        Assert.DoesNotContain(result.NotConverted, n => n.Contains(room.Name.Content) || n.Contains("pjum_extracted_background"));
        Assert.Equal(1, result.WrittenByCategory["rooms"]);
        Assert.Equal(1, result.WrittenByCategory["backgrounds"]);

        string game = Game(src);
        MergeResult merge = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("Extracted parts", mod) });
        foreach (string w in merge.Warnings) _out.WriteLine("WARN: " + w);
        Assert.True(merge.Success, merge.Reason);

        UndertaleRoom r = Load(Path.Combine(game, "data.win")).Rooms.ByName(room.Name.Content);
        static string Tile(UndertaleRoom.Tile t) => $"{t.BackgroundDefinition?.Name?.Content} {t.X},{t.Y} {t.SourceX},{t.SourceY} {t.Width}x{t.Height} {t.TileDepth} {t.ScaleX},{t.ScaleY} {t.Color}";
        static string Slot(UndertaleRoom.Background b) => $"{b.Enabled} {b.BackgroundDefinition?.Name?.Content} {b.X},{b.Y} {b.TiledHorizontally},{b.TiledVertically} {b.SpeedX},{b.SpeedY} {b.Foreground} {b.Stretch}";
        Assert.Equal(room.Height, r.Height);
        Assert.Equal(room.Backgrounds.Select(Slot), r.Backgrounds.Select(Slot));
        Assert.Equal(room.Tiles.Select(Tile), r.Tiles.Select(Tile));
    }
}
