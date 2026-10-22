using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Compiler;
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
    [InlineData("{ \"settings\": { \"creationCode\": \"gml_pjum_missing\" } }", "uses creation code 'gml_pjum_missing', which doesn't exist")]
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
    public async Task Added_tiles_and_instances_in_the_middle_extract_and_rebuild_in_the_same_order()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData original = Load(src);
        UndertaleData modded = Load(src);
        UndertaleRoom room = modded.Rooms.First(r => r?.Tiles.Count >= 4 && r.GameObjects.Count >= 4 && r.Tiles.All(t => t.BackgroundDefinition is not null) && r.GameObjects.All(g => g.ObjectDefinition is not null));
        UndertaleBackground bg = room.Tiles[0].BackgroundDefinition;
        room.Tiles.Insert(0, new UndertaleRoom.Tile { InstanceID = modded.GeneralInfo.LastTile++, BackgroundDefinition = bg, X = 1, Width = 20, Height = 20, TileDepth = 7 });
        room.Tiles.Insert(3, new UndertaleRoom.Tile { InstanceID = modded.GeneralInfo.LastTile++, BackgroundDefinition = bg, X = 2, Width = 20, Height = 20, TileDepth = 7 });
        room.Tiles.Insert(4, new UndertaleRoom.Tile { InstanceID = modded.GeneralInfo.LastTile++, BackgroundDefinition = bg, X = 3, Width = 20, Height = 20, TileDepth = 7 });
        room.Tiles.Add(new UndertaleRoom.Tile { InstanceID = modded.GeneralInfo.LastTile++, BackgroundDefinition = bg, X = 4, Width = 20, Height = 20, TileDepth = 7 });
        UndertaleGameObject obj = room.GameObjects[0].ObjectDefinition;
        room.GameObjects.Insert(2, new UndertaleRoom.GameObject { InstanceID = modded.GeneralInfo.LastObj++, ObjectDefinition = obj, X = 11 });
        room.GameObjects.Insert(0, new UndertaleRoom.GameObject { InstanceID = modded.GeneralInfo.LastObj++, ObjectDefinition = obj, X = 12 });
        string folder = Path.GetDirectoryName(src)!;
        string mod = Path.Combine(_root, "OrderMod");

        ExtractResult result = ModExtractor.Extract(original, modded, folder, folder, mod, "Order mod");
        foreach (string note in result.NotConverted) _out.WriteLine("note: " + note);

        string game = Game(src);
        MergeResult merge = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("Order mod", mod) });
        foreach (string w in merge.Warnings) _out.WriteLine("WARN: " + w);
        Assert.True(merge.Success, merge.Reason);

        UndertaleRoom r = Load(Path.Combine(game, "data.win")).Rooms.ByName(room.Name.Content);
        static string Tile(UndertaleRoom.Tile t) => $"{t.BackgroundDefinition?.Name?.Content} {t.X},{t.Y} {t.SourceX},{t.SourceY} {t.Width}x{t.Height} {t.TileDepth}";
        static string Inst(UndertaleRoom.GameObject g) => $"{g.ObjectDefinition?.Name?.Content} {g.X},{g.Y}";
        Assert.Equal(room.Tiles.Select(Tile), r.Tiles.Select(Tile));
        Assert.Equal(room.GameObjects.Select(Inst), r.GameObjects.Select(Inst));
    }

    [Fact]
    public async Task A_new_room_is_created_at_the_end_filled_in_and_code_can_go_to_it()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        string objectName = probe.GameObjects.First(o => o?.Name?.Content is not null).Name.Content;
        int roomsBefore = probe.Rooms.Count;
        const string room = "room_pjum_new";
        string mod = Mod(probe, "NewRoomMod", room,
            "{ \"settings\": { \"width\": 400, \"height\": 300, \"persistent\": true }, " +
            "\"views\": [ { \"slot\": 0, \"enabled\": true, \"viewX\": 0, \"viewY\": 0, \"viewWidth\": 320, \"viewHeight\": 240, \"portX\": 0, \"portY\": 0, \"portWidth\": 640, \"portHeight\": 480, \"borderX\": 32, \"borderY\": 32, \"speedX\": -1, \"speedY\": -1 } ], " +
            "\"add\": [ { \"object\": \"" + objectName + "\", \"x\": 10, \"y\": 20 }, { \"object\": \"" + objectName + "\", \"x\": 30, \"y\": 40, \"creationCode\": \"gml_RoomCC_room_pjum_new_1_Create\" } ] }",
            "\"rooms\": { \"" + room + "\": {} }, \"code\": { \"gml_Script_pjum_go_new\": {} }");
        string codeDir = Path.Combine(TestPaths.ModRoot(mod, probe), "code");
        Directory.CreateDirectory(codeDir);
        File.WriteAllText(Path.Combine(codeDir, "gml_RoomCC_room_pjum_new_1_Create.gml"), "pjum_new_room_marker = 1;\n");
        File.WriteAllText(Path.Combine(codeDir, "gml_Script_pjum_go_new.gml"), "room_goto(room_pjum_new);\n");
        string game = Game(src);

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("NewRoomMod", mod) });
        foreach (string w in result.Warnings) _out.WriteLine("WARN: " + w);
        Assert.True(result.Success, result.Reason);

        UndertaleData after = Load(Path.Combine(game, "data.win"));
        Assert.Equal(roomsBefore + 1, after.Rooms.Count);
        UndertaleRoom r = after.Rooms[^1];
        Assert.Equal(room, r.Name.Content);
        Assert.Same(r, after.GeneralInfo.RoomOrder[^1].Resource);
        Assert.Equal((400u, 300u, true), (r.Width, r.Height, r.Persistent));
        Assert.Equal((true, 320, 640), (r.Views[0].Enabled, r.Views[0].ViewWidth, r.Views[0].PortWidth));
        Assert.Equal(new[] { (10, 20), (30, 40) }, r.GameObjects.Select(g => (g.X, g.Y)));
        Assert.Equal("gml_RoomCC_room_pjum_new_1_Create", r.GameObjects[1].CreationCode?.Name?.Content);
        UndertaleCode go = after.Code.ByName("gml_Script_pjum_go_new");
        Assert.Contains(go.Instructions, i => i.Kind == UndertaleInstruction.Opcode.PushI && i.ValueShort == after.Rooms.Count - 1
                                            || i.Kind == UndertaleInstruction.Opcode.Push && i.ValueInt == after.Rooms.Count - 1);
    }

    [Fact]
    public async Task A_room_that_is_neither_in_the_game_nor_declared_new_fails()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        string mod = Mod(probe, "UndeclaredRoomMod", "room_pjum_undeclared", "{ }");

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(Game(src), new List<ModSource> { new("UndeclaredRoomMod", mod) });

        Assert.False(result.Success);
        Assert.Contains("isn't declared in mod.json > newAssets.rooms", result.Reason);
    }

    [Fact]
    public async Task A_new_room_in_a_layered_game_gets_GameMaker_2_room_settings()
    {
        string? chapter = TestPaths.DeltaruneChapter;
        if (chapter is null) { _out.WriteLine("Deltarune chapter not found — skipping."); return; }
        string src = Path.Combine(chapter, "data.win");
        UndertaleData probe = Load(src);
        string mod = Mod(probe, "LayeredRoomMod", "room_pjum_layered", "{ \"layers\": [ { \"name\": \"Instances\", \"new\": true, \"type\": \"Instances\" } ] }", "\"rooms\": { \"room_pjum_layered\": {} }");

        string game = Game(src);
        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("LayeredRoomMod", mod) });
        Assert.True(result.Success, result.Reason);

        UndertaleData after = Load(Path.Combine(game, "data.win"));
        UndertaleRoom r = after.Rooms.ByName("room_pjum_layered");
        Assert.Same(r, after.GeneralInfo.RoomOrder[^1].Resource);
        Assert.True(r.Flags.HasFlag(UndertaleRoom.RoomEntryFlags.IsGMS2) && r.Flags.HasFlag(UndertaleRoom.RoomEntryFlags.IsGMS2_3));
        Assert.Null(r.Caption);
        Assert.Empty(r.Backgrounds);
        UndertaleRoom.Layer layer = Assert.Single(r.Layers);
        Assert.Equal(("Instances", UndertaleRoom.LayerType.Instances), (layer.LayerName.Content, layer.LayerType));
        Assert.Equal(probe.Rooms.SelectMany(x => x.Layers).Max(l => l.LayerId) + 1, layer.LayerId);
    }

    [Fact]
    public async Task A_new_room_extracts_and_rebuilds()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData original = Load(src);
        UndertaleData modded = Load(src);
        var room = new UndertaleRoom { Name = modded.Strings.MakeString("room_pjum_extracted"), Caption = modded.Strings.MakeString(""), Width = 500, Speed = 60 };
        UndertaleGameObject obj = modded.GameObjects.First(o => o?.Name?.Content is not null);
        room.GameObjects.Add(new UndertaleRoom.GameObject { InstanceID = modded.GeneralInfo.LastObj++, ObjectDefinition = obj, X = 5, Y = 6 });
        room.GameObjects.Add(new UndertaleRoom.GameObject { InstanceID = modded.GeneralInfo.LastObj++, ObjectDefinition = obj, X = 7, Y = 8 });
        room.Views[1].Enabled = true;
        room.Views[1].ViewX = 99;
        modded.Rooms.Add(room);
        modded.GeneralInfo.RoomOrder.Add(new(room));
        string folder = Path.GetDirectoryName(src)!;
        string mod = Path.Combine(_root, "ExtractedNewRoom");

        ExtractResult result = ModExtractor.Extract(original, modded, folder, folder, mod, "Extracted new room");
        foreach (string note in result.NotConverted) _out.WriteLine("note: " + note);

        Assert.DoesNotContain(result.NotConverted, n => n.Contains("room_pjum_extracted"));
        Assert.Contains("\"room_pjum_extracted\": {}", File.ReadAllText(Path.Combine(mod, "mod.json")).Replace("\n", "").Replace("  ", ""));

        string game = Game(src);
        MergeResult merge = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("Extracted new room", mod) });
        Assert.True(merge.Success, merge.Reason);

        UndertaleData after = Load(Path.Combine(game, "data.win"));
        UndertaleRoom r = after.Rooms.ByName("room_pjum_extracted");
        Assert.Equal(after.Rooms.Count - 1, after.Rooms.IndexOf(r));
        Assert.Equal((500u, room.Height, 60u), (r.Width, r.Height, r.Speed));
        static string View(UndertaleRoom.View v) => $"{v.Enabled} {v.ViewX},{v.ViewY},{v.ViewWidth},{v.ViewHeight} {v.PortX},{v.PortY},{v.PortWidth},{v.PortHeight}";
        Assert.Equal(room.Views.Select(View), r.Views.Select(View));
        Assert.Equal(room.GameObjects.Select(g => (g.ObjectDefinition.Name.Content, g.X, g.Y)), r.GameObjects.Select(g => (g.ObjectDefinition.Name.Content, g.X, g.Y)));
    }

    [Fact]
    public async Task Room_creation_code_and_physics_extract_and_rebuild()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData original = Load(src);
        UndertaleData modded = Load(src);
        UndertaleRoom gainsCode = modded.Rooms.First(r => r?.CreationCodeId is null);
        UndertaleRoom losesCode = modded.Rooms.First(r => r?.CreationCodeId is not null);
        UndertaleRoom physics = modded.Rooms.First(r => r is not null && r != gainsCode && r != losesCode);
        string codeName = $"gml_Room_{gainsCode.Name.Content}_Create";
        var group = new CodeImportGroup(modded);
        group.QueueReplace(codeName, "pjum_room_marker = 3;");
        Assert.True(group.Import(false).Successful);
        gainsCode.CreationCodeId = modded.Code.ByName(codeName);
        losesCode.CreationCodeId = null;
        physics.World = true;
        physics.GravityY = 5;
        physics.Right = 640;
        string folder = Path.GetDirectoryName(src)!;
        string mod = Path.Combine(_root, "ExtractedRoomCode");

        ExtractResult result = ModExtractor.Extract(original, modded, folder, folder, mod, "Extracted room code");
        foreach (string note in result.NotConverted) _out.WriteLine("note: " + note);
        Assert.DoesNotContain(result.NotConverted, n => new[] { gainsCode, losesCode, physics }.Any(r => n.Contains($"'{r.Name.Content}'")));

        string game = Game(src);
        MergeResult merge = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("Extracted room code", mod) });
        Assert.True(merge.Success, merge.Reason);

        UndertaleData after = Load(Path.Combine(game, "data.win"));
        Assert.Equal(codeName, after.Rooms.ByName(gainsCode.Name.Content).CreationCodeId?.Name?.Content);
        Assert.Null(after.Rooms.ByName(losesCode.Name.Content).CreationCodeId);
        UndertaleRoom p = after.Rooms.ByName(physics.Name.Content);
        Assert.Equal((true, 5f, 640u, physics.Bottom, physics.GravityX, physics.MetersPerPixel), (p.World, p.GravityY, p.Right, p.Bottom, p.GravityX, p.MetersPerPixel));
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
