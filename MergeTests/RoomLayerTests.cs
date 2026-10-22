using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UndertaleModLib;
using UndertaleModLib.Models;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Data;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;
using PjumModLib;
using Xunit;

namespace MergeTests;

public class RoomLayerTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pjum_roomlayers_" + Guid.NewGuid().ToString("N"));

    public RoomLayerTests(ITestOutputHelper output)
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

    private static UndertaleRoom.Layer AddLayer(UndertaleData data, UndertaleRoom room, string name, UndertaleRoom.LayerType type, int depth, UndertaleRoom.Layer.LayerData? layerData)
    {
        var layer = new UndertaleRoom.Layer
        {
            LayerName = data.Strings.MakeString(name),
            LayerId = data.Rooms.SelectMany(r => r.Layers).Max(l => l.LayerId) + 1,
            LayerType = type,
            LayerDepth = depth,
            EffectEnabled = true,
            Data = layerData
        };
        int at = 0;
        while (at < room.Layers.Count && room.Layers[at].LayerDepth <= depth)
            at++;
        room.Layers.Insert(at, layer);
        layer.ParentRoom = room;
        return layer;
    }

    private static UndertaleRoom.Layer.LayerAssetsData NewAssets(UndertaleData data)
    {
        UndertaleRoom.Layer.LayerAssetsData shape = data.Rooms.SelectMany(r => r.Layers).First(l => l.AssetsData is not null).AssetsData;
        return new UndertaleRoom.Layer.LayerAssetsData
        {
            LegacyTiles = new(),
            Sprites = new(),
            Sequences = shape.Sequences is null ? null : new(),
            NineSlices = shape.NineSlices is null ? null : new(),
            ParticleSystems = shape.ParticleSystems is null ? null : new(),
            TextItems = shape.TextItems is null ? null : new()
        };
    }

    private static string Print(UndertaleRoom room)
    {
        static string N(UndertaleNamedResource? r) => r?.Name?.Content ?? "-";
        var lines = new List<string> { $"room {room.Width}x{room.Height} {room.Flags} {room.Caption?.Content ?? "(null)"} slots {room.Backgrounds.Count}" };
        lines.AddRange(room.Views.Select(v => $"view {v.Enabled} {v.ViewX},{v.ViewY},{v.ViewWidth},{v.ViewHeight} {v.PortX},{v.PortY},{v.PortWidth},{v.PortHeight}"));
        lines.AddRange(room.GameObjects.Select(g => $"instance {N(g.ObjectDefinition)} {g.X},{g.Y}"));
        foreach (UndertaleRoom.Layer layer in room.Layers)
        {
            lines.Add($"layer {layer.LayerName.Content} {layer.LayerType} {layer.LayerDepth} {layer.IsVisible} {layer.XOffset},{layer.YOffset} {layer.HSpeed},{layer.VSpeed} {layer.EffectEnabled}");
            switch (layer.Data)
            {
                case UndertaleRoom.Layer.LayerInstancesData instances:
                    lines.AddRange(instances.Instances.Select(g => $"  instance {N(g.ObjectDefinition)} {g.X},{g.Y}"));
                    break;
                case UndertaleRoom.Layer.LayerTilesData tiles:
                    lines.Add($"  grid {N(tiles.Background)} {tiles.TilesX}x{tiles.TilesY} {tiles.TileData.Length}");
                    lines.AddRange(tiles.TileData.Select(row => "  " + string.Join(",", row)));
                    break;
                case UndertaleRoom.Layer.LayerBackgroundData bg:
                    lines.Add($"  background {N(bg.Sprite)} {bg.Visible} {bg.Foreground} {bg.TiledHorizontally},{bg.TiledVertically} {bg.Stretch} {bg.Color} {bg.FirstFrame} {bg.AnimationSpeed} {bg.AnimationSpeedType}");
                    break;
                case UndertaleRoom.Layer.LayerAssetsData assets:
                    lines.AddRange(assets.LegacyTiles.Select(t => $"  tile {t.spriteMode} {N(t.ObjectDefinition)} {t.X},{t.Y} {t.SourceX},{t.SourceY} {t.Width}x{t.Height} {t.TileDepth} {t.ScaleX},{t.ScaleY} {t.Color}"));
                    lines.AddRange(assets.Sprites.Select(s => $"  sprite {s.Name.Content} {N(s.Sprite)} {s.X},{s.Y} {s.ScaleX},{s.ScaleY} {s.Color} {s.AnimationSpeed} {s.AnimationSpeedType} {s.FrameIndex} {s.Rotation}"));
                    break;
            }
        }
        return string.Join("\n", lines);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Layer_changes_extract_and_rebuild_the_rooms(int chapter)
    {
        string? src = TestPaths.DeltaruneChapterDataWin(chapter);
        if (src is null) { _out.WriteLine($"Deltarune chapter {chapter} not found — skipping."); return; }
        UndertaleData original = Load(src);
        UndertaleData modded = Load(src);
        var changedRooms = new HashSet<string>();

        UndertaleRoom tilesRoom = modded.Rooms.First(r => r.Layers.Any(l => l.TilesData is { TilesX: >= 2, Background: not null }));
        UndertaleRoom.Layer gridLayer = tilesRoom.Layers.First(l => l.TilesData is { TilesX: >= 2, Background: not null });
        gridLayer.TilesData.TileData[0][0] ^= 1u << 28;
        gridLayer.TilesData.TileData[0][1] = 0;
        gridLayer.LayerDepth += 1;
        gridLayer.XOffset = 3;
        changedRooms.Add(tilesRoom.Name.Content);

        UndertaleRoom.Layer? resized = modded.Rooms.SelectMany(r => r.Layers).FirstOrDefault(l => l.TilesData is { TilesY: >= 1 } && l != gridLayer);
        if (resized is not null)
        {
            resized.TilesData.TilesX += 1;
            resized.TilesData.TileData[0][^1] = 3;
            changedRooms.Add(modded.Rooms.First(r => r.Layers.Contains(resized)).Name.Content);
        }

        UndertaleSprite someSprite = modded.Sprites.First(s => s?.Name?.Content is not null && s.Textures.Count > 0);
        UndertaleRoom bgRoom = modded.Rooms.First(r => r.Layers.Any(l => l.BackgroundData?.Sprite is not null));
        UndertaleRoom.Layer.LayerBackgroundData bgData = bgRoom.Layers.First(l => l.BackgroundData?.Sprite is not null).BackgroundData;
        bgData.Sprite = someSprite;
        bgData.Color ^= 255;
        bgData.TiledHorizontally = !bgData.TiledHorizontally;
        changedRooms.Add(bgRoom.Name.Content);

        UndertaleRoom assetRoom = modded.Rooms.First(r => r.Layers.Any(l => l.AssetsData?.LegacyTiles.Count >= 3));
        UndertaleRoom.Layer.LayerAssetsData assets = assetRoom.Layers.First(l => l.AssetsData?.LegacyTiles.Count >= 3).AssetsData;
        assets.LegacyTiles[0].X += 4;
        UndertaleSprite tileSprite = assets.LegacyTiles[2].SpriteDefinition;
        assets.LegacyTiles.RemoveAt(1);
        assets.LegacyTiles.Insert(1, new UndertaleRoom.Tile { spriteMode = true, SpriteDefinition = tileSprite, InstanceID = modded.GeneralInfo.LastTile++, X = 8, Y = 9, Width = 20, Height = 20, TileDepth = 100 });
        if (assets.Sprites.Count >= 2)
        {
            assets.Sprites[0].X += 5;
            assets.Sprites.RemoveAt(1);
        }
        assets.Sprites.Insert(0, new UndertaleRoom.SpriteInstance { Name = modded.Strings.MakeString("pjum_sprite_first"), Sprite = someSprite, X = 1, Y = 2 });
        assets.Sprites.Add(new UndertaleRoom.SpriteInstance { Name = modded.Strings.MakeString("pjum_sprite_last"), Sprite = someSprite, X = 3, Y = 4, ScaleX = 2, Rotation = 90 });
        changedRooms.Add(assetRoom.Name.Content);

        UndertaleRoom removeRoom = modded.Rooms.First(r => r.Layers.Count(l => l.InstancesData is not null) >= 2 && r.Layers.Any(l => l.InstancesData?.Instances.Count > 0));
        UndertaleRoom.Layer removed = removeRoom.Layers.First(l => l.InstancesData?.Instances.Count > 0);
        foreach (UndertaleRoom.GameObject instance in removed.InstancesData.Instances)
            removeRoom.GameObjects.Remove(instance);
        removeRoom.Layers.Remove(removed);
        changedRooms.Add(removeRoom.Name.Content);

        UndertaleGameObject obj = modded.GameObjects.First(o => o?.Name?.Content is not null);
        UndertaleRoom.Layer newInstances = AddLayer(modded, tilesRoom, "Instances_pjum", UndertaleRoom.LayerType.Instances, -50, new UndertaleRoom.Layer.LayerInstancesData());
        var added = new UndertaleRoom.GameObject { InstanceID = modded.GeneralInfo.LastObj++, ObjectDefinition = obj, X = 40, Y = 50 };
        tilesRoom.GameObjects.Add(added);
        newInstances.InstancesData.Instances.Add(added);
        AddLayer(modded, tilesRoom, "Tiles_pjum", UndertaleRoom.LayerType.Tiles, 900,
            new UndertaleRoom.Layer.LayerTilesData { Background = gridLayer.TilesData.Background, TilesX = 3, TilesY = 2, TileData = [[1, 2, 3], [4, (1u << 29) | 5, 6]] });
        AddLayer(modded, tilesRoom, "Background_pjum", UndertaleRoom.LayerType.Background, 1200,
            new UndertaleRoom.Layer.LayerBackgroundData { Sprite = someSprite, Stretch = true, Color = 4278190335 });
        UndertaleRoom.Layer.LayerAssetsData newAssets = NewAssets(modded);
        newAssets.LegacyTiles.Add(new UndertaleRoom.Tile { spriteMode = true, SpriteDefinition = tileSprite, InstanceID = modded.GeneralInfo.LastTile++, X = 16, Y = 32, Width = 20, Height = 20 });
        newAssets.Sprites.Add(new UndertaleRoom.SpriteInstance { Name = modded.Strings.MakeString("pjum_sprite_new_layer"), Sprite = someSprite, X = 7, Y = 7 });
        AddLayer(modded, tilesRoom, "Assets_pjum", UndertaleRoom.LayerType.Assets, 300, newAssets);
        AddLayer(modded, tilesRoom, "Path_pjum", UndertaleRoom.LayerType.Path, 0, null);

        UndertaleRoom newRoom = RoomLayers.EmptyRoom(modded, text => modded.Strings.MakeString(text));
        newRoom.Name = modded.Strings.MakeString("room_pjum_layered");
        newRoom.Width = 800;
        newRoom.Flags |= UndertaleRoom.RoomEntryFlags.ClearViewBackground;
        newRoom.Views[0].ViewWidth = 320;
        modded.Rooms.Add(newRoom);
        modded.GeneralInfo.RoomOrder.Add(new(newRoom));
        UndertaleRoom.Layer roomInstances = AddLayer(modded, newRoom, "Instances", UndertaleRoom.LayerType.Instances, 0, new UndertaleRoom.Layer.LayerInstancesData());
        var inNewRoom = new UndertaleRoom.GameObject { InstanceID = modded.GeneralInfo.LastObj++, ObjectDefinition = obj, X = 11, Y = 12 };
        newRoom.GameObjects.Add(inNewRoom);
        roomInstances.InstancesData.Instances.Add(inNewRoom);
        AddLayer(modded, newRoom, "Tiles", UndertaleRoom.LayerType.Tiles, 100,
            new UndertaleRoom.Layer.LayerTilesData { Background = gridLayer.TilesData.Background, TilesX = 2, TilesY = 2, TileData = [[7, 8], [9, 10]] });
        changedRooms.Add(newRoom.Name.Content);

        string folder = Path.GetDirectoryName(src)!;
        string mod = Path.Combine(_root, "LayerMod");
        ExtractResult result = ModExtractor.Extract(original, modded, folder, folder, mod, "Layer mod");
        foreach (string note in result.NotConverted) _out.WriteLine("note: " + note);
        Assert.DoesNotContain(result.NotConverted, n => changedRooms.Any(r => n.Contains($"'{r}'")));

        string game = Game(src);
        MergeResult merge = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("Layer mod", mod) });
        Assert.True(merge.Success, merge.Reason);
        foreach (string w in merge.Warnings) _out.WriteLine("WARN: " + w);

        UndertaleData after = Load(Path.Combine(game, "data.win"));
        Assert.Equal(modded.Rooms.Count, after.Rooms.Count);
        foreach (UndertaleRoom room in modded.Rooms)
            Assert.Equal(Print(room), Print(after.Rooms.ByName(room.Name.Content)));
        Assert.Same(after.Rooms.ByName("room_pjum_layered"), after.GeneralInfo.RoomOrder[^1].Resource);
        var ids = after.Rooms.SelectMany(r => r.Layers).Select(l => l.LayerId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        uint oldMax = original.Rooms.SelectMany(r => r.Layers).Max(l => l.LayerId);
        Assert.All(after.Rooms.ByName(tilesRoom.Name.Content).Layers.Where(l => l.LayerName.Content.EndsWith("_pjum")), l => Assert.True(l.LayerId > oldMax));
        var tileIds = after.Rooms.SelectMany(r => r.Layers).Where(l => l.AssetsData is not null).SelectMany(l => l.AssetsData.LegacyTiles).Select(t => t.InstanceID).ToList();
        Assert.Equal(tileIds.Count, tileIds.Distinct().Count());
    }

    private static (UndertaleRoom Room, UndertaleRoom.Layer Grid) GridRoom(UndertaleData data)
    {
        UndertaleRoom room = data.Rooms.First(r => r.Layers.Any(l => l.TilesData is { TilesX: >= 2, Background: not null }));
        return (room, room.Layers.First(l => l.TilesData is { TilesX: >= 2, Background: not null }));
    }

    [Fact]
    public async Task Two_mods_change_cells_of_one_grid_and_the_later_one_wins_the_shared_cell()
    {
        string? chapter = TestPaths.DeltaruneChapter;
        if (chapter is null) { _out.WriteLine("Deltarune chapter not found — skipping."); return; }
        string src = Path.Combine(chapter, "data.win");
        UndertaleData probe = Load(src);
        (UndertaleRoom room, UndertaleRoom.Layer grid) = GridRoom(probe);
        string layer = grid.LayerName.Content;
        string a = Mod(probe, "ModA", room.Name.Content, $$"""{ "layers": [ { "name": "{{layer}}", "grid": { "cells": [ [0, 0, 7], [1, 0, 8] ] } } ] }""");
        string b = Mod(probe, "ModB", room.Name.Content, $$"""{ "layers": [ { "name": "{{layer}}", "grid": { "cells": [ [1, 0, 9] ] } } ] }""");

        string game = Game(src);
        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("ModA", a), new("ModB", b) });
        Assert.True(result.Success, result.Reason);

        UndertaleRoom.Layer merged = Load(Path.Combine(game, "data.win")).Rooms.ByName(room.Name.Content).Layers.First(l => l.LayerName.Content == layer);
        Assert.Equal((7u, 9u), (merged.TilesData.TileData[0][0], merged.TilesData.TileData[0][1]));
        ConflictLogEntry conflict = Assert.Single(result.Conflicts, c => c.AssetKey.Contains(layer));
        Assert.EndsWith(".cell 1,0", conflict.AssetKey);
        Assert.Equal("ModB", conflict.WinningMod);
        Assert.Equal(new[] { "ModA" }, conflict.OverriddenMods);
    }

    [Fact]
    public async Task A_layer_one_mod_removes_is_skipped_with_a_warning_when_another_changes_it()
    {
        string? chapter = TestPaths.DeltaruneChapter;
        if (chapter is null) { _out.WriteLine("Deltarune chapter not found — skipping."); return; }
        string src = Path.Combine(chapter, "data.win");
        UndertaleData probe = Load(src);
        (UndertaleRoom room, UndertaleRoom.Layer grid) = GridRoom(probe);
        string layer = grid.LayerName.Content;
        string a = Mod(probe, "ModA", room.Name.Content, $$"""{ "removeLayers": [ "{{layer}}" ] }""");
        string b = Mod(probe, "ModB", room.Name.Content, $$"""{ "layers": [ { "name": "{{layer}}", "depth": 5 } ] }""");

        string game = Game(src);
        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("ModA", a), new("ModB", b) });
        Assert.True(result.Success, result.Reason);

        Assert.Null(Load(Path.Combine(game, "data.win")).Rooms.ByName(room.Name.Content).Layers.FirstOrDefault(l => l.LayerName.Content == layer));
        Assert.Contains(result.Warnings, w => w.Contains("[ModB]") && w.Contains($"layer '{layer}'") && w.Contains("'ModA' removed it"));
    }

    [Theory]
    [InlineData("""{ "layers": [ { "name": "pjum_missing", "depth": 1 } ] }""", "has no layer named 'pjum_missing'")]
    [InlineData("""{ "layers": [ { "name": "%GRID%", "new": true, "type": "Tiles" } ] }""", "already has a layer with that name")]
    [InlineData("""{ "layers": [ { "name": "pjum_effect", "new": true, "type": "Effect" } ] }""", "PJUM can create Instances, Tiles, Background, Assets and Path layers")]
    [InlineData("""{ "layers": [ { "name": "%GRID%", "background": { "visible": false } } ] }""", "can't have \"background\"")]
    [InlineData("""{ "layers": [ { "name": "%GRID%", "grid": { "cells": [ [100000, 0, 1] ] } } ] }""", "\"grid.cells\"")]
    [InlineData("""{ "layers": [ { "name": "%GRID%", "grid": { "rows": [ [1, 2], [3] ] } } ] }""", "same number of cells")]
    [InlineData("""{ "layers": [ { "name": "pjum_bg", "new": true, "type": "Background", "background": { "sprite": "spr_pjum_missing" } } ] }""", "uses sprite 'spr_pjum_missing'")]
    [InlineData("""{ "removeLayers": [ "pjum_missing" ] }""", "\"removeLayers\"")]
    [InlineData("""{ "add": [ { "object": "%OBJECT%", "layer": "pjum_nolayer" } ] }""", "has no instance layer named 'pjum_nolayer'")]
    public async Task A_bad_layer_patch_fails_before_anything_is_written(string json, string expected)
    {
        string? chapter = TestPaths.DeltaruneChapter;
        if (chapter is null) { _out.WriteLine("Deltarune chapter not found — skipping."); return; }
        string src = Path.Combine(chapter, "data.win");
        UndertaleData probe = Load(src);
        (UndertaleRoom room, UndertaleRoom.Layer grid) = GridRoom(probe);
        json = json.Replace("%GRID%", grid.LayerName.Content).Replace("%OBJECT%", probe.GameObjects.First(o => o?.Name?.Content is not null).Name.Content);
        string mod = Mod(probe, "BadLayerMod", room.Name.Content, json);

        string game = Game(src);
        byte[] before = File.ReadAllBytes(Path.Combine(game, "data.win"));
        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("BadLayerMod", mod) });

        Assert.False(result.Success);
        Assert.Contains(expected, result.Reason);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(game, "data.win")));
    }
}
