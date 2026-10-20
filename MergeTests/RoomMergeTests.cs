using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UndertaleModLib;
using UndertaleModLib.Models;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Data;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;
using Xunit;

namespace MergeTests;

public class RoomMergeTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pjum_rooms_" + Guid.NewGuid().ToString("N"));

    public RoomMergeTests(ITestOutputHelper output)
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

    private string Mod(UndertaleData probe, string name, string room, string json, string extraNewAssets = "")
    {
        string mod = Path.Combine(_root, name);
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "mod.json"),
            "{ \"name\": \"" + name + "\", \"version\": \"1.0.0\", \"newAssets\": { " + extraNewAssets + " } }");
        string rooms = Path.Combine(TestPaths.ModRoot(mod, probe), "rooms");
        Directory.CreateDirectory(rooms);
        File.WriteAllText(Path.Combine(rooms, room + ".json"), json);
        return mod;
    }

    private static UndertaleRoom PickRoom(UndertaleData data) =>
        data.Rooms.First(r => r?.GameObjects.Count >= 3 && r.GameObjects.Take(3).All(g => g.ObjectDefinition is not null));

    [Fact]
    public async Task A_room_patch_adds_changes_and_removes_instances()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        UndertaleRoom room = PickRoom(probe);
        UndertaleRoom.GameObject moved = room.GameObjects[0], removed = room.GameObjects[1];
        string objectName = room.GameObjects[2].ObjectDefinition.Name.Content;
        uint nextId = probe.GeneralInfo.LastObj;
        _out.WriteLine($"Room {room.Name.Content}: move {moved.InstanceID}, remove {removed.InstanceID}, add {objectName}; next id {nextId}.");

        string mod = Mod(probe, "RoomMod", room.Name.Content,
            "{ \"add\": [ { \"object\": \"" + objectName + "\", \"x\": 12, \"y\": 34, \"scaleX\": 2 } ], " +
            "\"change\": [ { \"id\": " + moved.InstanceID + ", \"y\": " + (moved.Y + 5) + " } ], " +
            "\"remove\": [ " + removed.InstanceID + " ] }");
        string game = Game(src);

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("RoomMod", mod) });
        foreach (string w in result.Warnings) _out.WriteLine("WARN: " + w);
        Assert.True(result.Success, result.Reason);

        UndertaleData after = Load(Path.Combine(game, "data.win"));
        UndertaleRoom r = after.Rooms.ByName(room.Name.Content);
        Assert.Equal(room.GameObjects.Count, r.GameObjects.Count);
        Assert.Equal(moved.Y + 5, r.GameObjects.Single(g => g.InstanceID == moved.InstanceID).Y);
        Assert.Equal(moved.X, r.GameObjects.Single(g => g.InstanceID == moved.InstanceID).X);
        Assert.DoesNotContain(r.GameObjects, g => g.InstanceID == removed.InstanceID);
        UndertaleRoom.GameObject added = r.GameObjects[^1];
        Assert.Equal(nextId, added.InstanceID);
        Assert.Equal((objectName, 12, 34, 2f, 1f), (added.ObjectDefinition.Name.Content, added.X, added.Y, added.ScaleX, added.ScaleY));
        Assert.Equal(nextId + 1, after.GeneralInfo.LastObj);
    }

    [Fact]
    public async Task Two_mods_both_add_to_a_room_and_the_later_change_wins()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        UndertaleRoom room = PickRoom(probe);
        uint first = room.GameObjects[0].InstanceID, second = room.GameObjects[1].InstanceID;
        string objectName = room.GameObjects[2].ObjectDefinition.Name.Content;
        uint nextId = probe.GeneralInfo.LastObj;

        string modA = Mod(probe, "ModA", room.Name.Content,
            "{ \"add\": [ { \"object\": \"" + objectName + "\", \"x\": 1, \"y\": 1 } ], " +
            "\"change\": [ { \"id\": " + first + ", \"x\": 100 } ], \"remove\": [ " + second + " ] }");
        string modB = Mod(probe, "ModB", room.Name.Content,
            "{ \"add\": [ { \"object\": \"" + objectName + "\", \"x\": 2, \"y\": 2 } ], " +
            "\"change\": [ { \"id\": " + first + ", \"x\": 200, \"y\": 7 }, { \"id\": " + second + ", \"y\": 9 } ] }");
        string game = Game(src);

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game,
            new List<ModSource> { new("ModA", modA), new("ModB", modB) });
        foreach (string w in result.Warnings) _out.WriteLine("WARN: " + w);
        Assert.True(result.Success, result.Reason);

        UndertaleRoom r = Load(Path.Combine(game, "data.win")).Rooms.ByName(room.Name.Content);
        UndertaleRoom.GameObject changed = r.GameObjects.Single(g => g.InstanceID == first);
        Assert.Equal((200, 7), (changed.X, changed.Y));
        Assert.Equal(new[] { (nextId, 1), (nextId + 1, 2) }, r.GameObjects.Where(g => g.InstanceID >= nextId).Select(g => (g.InstanceID, g.X)));
        ConflictLogEntry conflict = Assert.Single(result.Conflicts, c => c.AssetKey.EndsWith($"#{first}.x"));
        Assert.Equal("ModB", conflict.WinningMod);
        Assert.Equal(new[] { "ModA" }, conflict.OverriddenMods);
        Assert.Contains(result.Warnings, w => w.Contains("[ModB]") && w.Contains($"instance {second}") && w.Contains("'ModA' removed it"));
    }

    [Fact]
    public async Task A_change_to_an_instance_the_room_does_not_have_fails_before_anything_is_written()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        UndertaleRoom room = PickRoom(probe);
        string mod = Mod(probe, "BadRoomMod", room.Name.Content, "{ \"change\": [ { \"id\": 4000000000, \"x\": 1 } ] }");
        string game = Game(src);
        byte[] before = File.ReadAllBytes(Path.Combine(game, "data.win"));

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("BadRoomMod", mod) });

        Assert.False(result.Success);
        Assert.Equal("BadRoomMod", result.FailedMod);
        Assert.Contains("has no instance 4000000000", result.Reason);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(game, "data.win")));
    }

    [Fact]
    public async Task An_added_instance_can_use_an_object_the_same_mod_creates()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        UndertaleRoom room = PickRoom(probe);
        const string newObject = "obj_pjum_room_test";
        string mod = Mod(probe, "NewObjectRoomMod", room.Name.Content,
            "{ \"add\": [ { \"object\": \"" + newObject + "\", \"x\": 5, \"y\": 6 } ] }",
            "\"objects\": { \"" + newObject + "\": {} }");
        string objects = Path.Combine(TestPaths.ModRoot(mod, probe), "objects");
        Directory.CreateDirectory(objects);
        File.WriteAllText(Path.Combine(objects, newObject + ".json"), "{ \"visible\": true }");
        string game = Game(src);

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("NewObjectRoomMod", mod) });
        Assert.True(result.Success, result.Reason);

        UndertaleRoom.GameObject added = Load(Path.Combine(game, "data.win")).Rooms.ByName(room.Name.Content).GameObjects[^1];
        Assert.Equal(newObject, added.ObjectDefinition.Name.Content);
    }

    [Fact]
    public async Task An_added_instance_runs_creation_code_from_the_mods_code_folder()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        UndertaleRoom room = PickRoom(probe);
        string objectName = room.GameObjects[2].ObjectDefinition.Name.Content;
        const string codeName = "gml_RoomCC_pjum_added_Create";
        string mod = Mod(probe, "CreationCodeMod", room.Name.Content,
            "{ \"add\": [ { \"object\": \"" + objectName + "\", \"x\": 1, \"y\": 2, \"creationCode\": \"" + codeName + "\" } ], " +
            "\"change\": [ { \"id\": " + room.GameObjects[0].InstanceID + ", \"creationCode\": \"\" } ] }");
        string codeDir = Path.Combine(TestPaths.ModRoot(mod, probe), "code");
        Directory.CreateDirectory(codeDir);
        File.WriteAllText(Path.Combine(codeDir, codeName + ".gml"), "pjum_added_marker = 3;\n");
        string game = Game(src);

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("CreationCodeMod", mod) });
        foreach (string w in result.Warnings) _out.WriteLine("WARN: " + w);
        Assert.True(result.Success, result.Reason);

        UndertaleData after = Load(Path.Combine(game, "data.win"));
        UndertaleRoom r = after.Rooms.ByName(room.Name.Content);
        UndertaleCode code = r.GameObjects[^1].CreationCode;
        Assert.Equal(codeName, code?.Name?.Content);
        string text = new Underanalyzer.Decompiler.DecompileContext(new UndertaleModLib.Decompiler.GlobalDecompileContext(after), code, after.ToolInfo.DecompilerSettings).DecompileToString();
        Assert.Contains("pjum_added_marker = 3;", text);
        Assert.Null(r.GameObjects.Single(g => g.InstanceID == room.GameObjects[0].InstanceID).CreationCode);
    }

    [Fact]
    public async Task Creation_code_that_no_mod_provides_fails_before_anything_is_written()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        UndertaleData probe = Load(src);
        UndertaleRoom room = PickRoom(probe);
        string mod = Mod(probe, "MissingCodeMod", room.Name.Content,
            "{ \"add\": [ { \"object\": \"" + room.GameObjects[2].ObjectDefinition.Name.Content + "\", \"creationCode\": \"gml_RoomCC_pjum_nowhere_Create\" } ] }");
        string game = Game(src);
        byte[] before = File.ReadAllBytes(Path.Combine(game, "data.win"));

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("MissingCodeMod", mod) });

        Assert.False(result.Success);
        Assert.Contains("uses code 'gml_RoomCC_pjum_nowhere_Create', which doesn't exist", result.Reason);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(game, "data.win")));
    }

    [Fact]
    public async Task In_a_layered_game_an_added_instance_goes_into_the_layer_with_its_depth()
    {
        string? chapter = TestPaths.DeltaruneChapter;
        if (chapter is null) { _out.WriteLine("Deltarune chapter not found — skipping."); return; }
        string src = Path.Combine(chapter, "data.win");
        UndertaleData probe = Load(src);
        UndertaleRoom room = probe.Rooms.First(r => r?.Layers?.Count(l => l.LayerType == UndertaleRoom.LayerType.Instances) >= 2
            && r.GameObjects.Any(g => g.ObjectDefinition is not null && r.Layers.Any(l => l.LayerType == UndertaleRoom.LayerType.Instances && l.LayerDepth == g.ObjectDefinition.Depth)
                                      && r.Layers.First(l => l.LayerType == UndertaleRoom.LayerType.Instances).LayerDepth != g.ObjectDefinition.Depth));
        UndertaleGameObject obj = room.GameObjects.Select(g => g.ObjectDefinition).First(o => o is not null
            && room.Layers.Any(l => l.LayerType == UndertaleRoom.LayerType.Instances && l.LayerDepth == o.Depth)
            && room.Layers.First(l => l.LayerType == UndertaleRoom.LayerType.Instances).LayerDepth != o.Depth);
        string expectedLayer = room.Layers.First(l => l.LayerType == UndertaleRoom.LayerType.Instances && l.LayerDepth == obj.Depth).LayerName.Content;
        _out.WriteLine($"Room {room.Name.Content}: adding {obj.Name.Content} (depth {obj.Depth}), expecting layer {expectedLayer}.");

        string mod = Mod(probe, "LayerRoomMod", room.Name.Content, "{ \"add\": [ { \"object\": \"" + obj.Name.Content + "\", \"x\": 3, \"y\": 4 } ] }");
        string game = Game(src);

        MergeResult result = await new ModMergeService(new DirectDataService()).ApplyAsync(game, new List<ModSource> { new("LayerRoomMod", mod) });
        foreach (string w in result.Warnings) _out.WriteLine("WARN: " + w);
        Assert.True(result.Success, result.Reason);

        UndertaleRoom r = Load(Path.Combine(game, "data.win")).Rooms.ByName(room.Name.Content);
        UndertaleRoom.GameObject added = r.GameObjects[^1];
        Assert.Equal(obj.Name.Content, added.ObjectDefinition.Name.Content);
        UndertaleRoom.Layer holder = Assert.Single(r.Layers, l => l.LayerType == UndertaleRoom.LayerType.Instances && l.InstancesData.Instances.Any(i => i.InstanceID == added.InstanceID));
        Assert.Equal(expectedLayer, holder.LayerName.Content);
    }
}
