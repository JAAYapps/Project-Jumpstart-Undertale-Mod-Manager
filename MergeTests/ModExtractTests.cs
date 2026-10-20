using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Data;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;
using Project_Jumpstart_Undertale_Mod_Manager.Services.ModExtract;
using Xunit;
using PjumModLib;

namespace MergeTests;

public class ModExtractTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pjum_extract_" + Guid.NewGuid().ToString("N"));

    public ModExtractTests(ITestOutputHelper output)
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

    private static string Decompile(UndertaleData data, string entry) =>
        new Underanalyzer.Decompiler.DecompileContext(new GlobalDecompileContext(data), data.Code.ByName(entry), data.ToolInfo.DecompilerSettings)
            .DecompileToString();

    private static byte[] FramePixels(UndertaleData data, string sprite)
    {
        using var worker = new TextureWorker();
        using IMagickImage<byte> image = worker.GetTextureFor(data.Sprites.ByName(sprite).Textures[0].Texture, "frame", includePadding: true);
        return image.GetPixels().ToByteArray(PixelMapping.RGBA)!;
    }

    [Fact]
    public async Task An_extracted_mod_rebuilds_the_modded_changes_and_nothing_else()
    {
        string? src = TestPaths.DataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }
        var io = new DirectDataService();

        UndertaleData data = await io.LoadAsync(src);
        string changedCode = data.Code.First(c => c?.Name?.Content is string n && n.StartsWith("gml_Object_") && n.EndsWith("_Step_0") && c.ParentEntry is null).Name.Content;
        string newCode = data.IsVersionAtLeast(2, 3) ? "gml_GlobalScript_pjum_extract_new" : "gml_Script_pjum_extract_new";
        var group = new CodeImportGroup(data);
        group.QueueAppend(changedCode, "pjum_extract_marker = 42;");
        group.QueueReplace(newCode, "global.pjum_extract_new = \"hello from the extract test\";");
        Assert.True(group.Import(false).Successful);
        UndertaleCode.CreateEmptyEntry(data, "gml_Object_obj_pjum_orphan_alarm_9");

        UndertaleGameObject obj = data.GameObjects.First(o => o?.Name?.Content is not null);
        obj.Depth += 7;

        var items = data.Sprites.Where(s => s is not null).SelectMany(s => s.Textures).Select(e => e?.Texture).Where(t => t is not null).ToList();
        bool Alone(UndertaleTexturePageItem t) => items.Count(o => o!.TexturePage == t.TexturePage
            && o.SourceX < t.SourceX + t.SourceWidth && t.SourceX < o.SourceX + o.SourceWidth
            && o.SourceY < t.SourceY + t.SourceHeight && t.SourceY < o.SourceY + o.SourceHeight) == 1;
        UndertaleSprite sprite = data.Sprites.First(s => s?.Textures.Count > 0 && s.Textures[0]?.Texture is { } t && t.SourceWidth == t.TargetWidth && t.SourceHeight == t.TargetHeight && t.SourceWidth > 1 && Alone(t));
        using (var worker = new TextureWorker())
        using (IMagickImage<byte> frame = worker.GetTextureFor(sprite.Textures[0].Texture, "frame"))
        {
            var negated = new MagickImage(frame);
            negated.Negate(Channels.RGB);
            sprite.Textures[0].Texture.ReplaceTexture(negated);
        }

        UndertalePath? path = data.Paths.FirstOrDefault(p => p?.Points.Count > 0);
        if (path is not null)
            path.Points[0].X += 3;

        string modded = Path.Combine(_root, "modded.win");
        await io.SaveAsync(data, modded);
        _out.WriteLine($"Changed {changedCode}, added {newCode}, object {obj.Name.Content}, sprite {sprite.Name.Content}, path {path?.Name.Content ?? "(none in this game)"}.");

        string mod = Path.Combine(_root, "ExtractedMod");
        ExtractResult result = await new ModExtractService(io).ExtractAsync(src, modded, mod, "Extracted test mod");
        foreach ((string category, int count) in result.WrittenByCategory) _out.WriteLine($"wrote {count} {category}");
        foreach (string note in result.NotConverted) _out.WriteLine("not converted: " + note);

        Assert.Equal(2, result.WrittenByCategory["code"]);
        Assert.Equal(1, result.WrittenByCategory["objects"]);
        Assert.Equal(1, result.WrittenByCategory["sprite frames"]);
        Assert.Equal(path is null ? 0 : 1, result.WrittenByCategory.GetValueOrDefault("paths"));
        Assert.Equal(3 + (path is null ? 0 : 1), result.WrittenByCategory.Count);
        string skipped = Assert.Single(result.NotConverted);
        Assert.Contains("gml_Object_obj_pjum_orphan_alarm_9", skipped);
        Assert.Contains("isn't used", skipped);
        JsonNode manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(mod, "mod.json")))!;
        Assert.NotNull(manifest["newAssets"]!["code"]![newCode]);

        string game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        File.Copy(src, Path.Combine(game, "data.win"));
        MergeResult merge = await new ModMergeService(io).ApplyAsync(game, new List<ModSource> { new("Extracted test mod", mod) });
        foreach (string w in merge.Warnings) _out.WriteLine("WARN: " + w);
        Assert.True(merge.Success, merge.Reason);

        UndertaleData rebuilt = await io.LoadAsync(Path.Combine(game, "data.win"));
        UndertaleData expected = await io.LoadAsync(modded);
        Assert.Equal(Decompile(expected, changedCode), Decompile(rebuilt, changedCode));
        Assert.Equal(Decompile(expected, newCode), Decompile(rebuilt, newCode));
        Assert.Equal(expected.GameObjects.ByName(obj.Name.Content).Depth, rebuilt.GameObjects.ByName(obj.Name.Content).Depth);
        Assert.Equal(FramePixels(expected, sprite.Name.Content), FramePixels(rebuilt, sprite.Name.Content));
        if (path is not null)
            Assert.Equal(expected.Paths.ByName(path.Name.Content).Points[0].X, rebuilt.Paths.ByName(path.Name.Content).Points[0].X);
    }

    private static bool Streamed(UndertaleSound s) =>
        !s.Flags.HasFlag(UndertaleSound.AudioEntryFlags.IsEmbedded) && !s.Flags.HasFlag(UndertaleSound.AudioEntryFlags.IsCompressed);

    [Fact]
    public async Task Changed_streamed_music_comes_from_the_loose_file_and_a_new_ogg_serial_is_not_a_change()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        string gameFolder = Path.GetDirectoryName(src)!;
        var io = new DirectDataService();
        UndertaleData data = await io.LoadAsync(src);
        var streamed = data.Sounds.Where(s => s is not null && Streamed(s) && File.Exists(Path.Combine(gameFolder, s.File.Content))).Take(3).ToList();
        if (streamed.Count < 3) { _out.WriteLine("Not enough streamed sounds — skipping."); return; }
        UndertaleSound changed = streamed[0], reserialed = streamed[1], donor = streamed[2];

        string moddedFolder = Path.Combine(_root, "modded");
        Directory.CreateDirectory(moddedFolder);
        File.Copy(src, Path.Combine(moddedFolder, "data.win"));
        File.Copy(Path.Combine(gameFolder, donor.File.Content), Path.Combine(moddedFolder, changed.File.Content.ToUpperInvariant()));
        byte[] music = File.ReadAllBytes(Path.Combine(gameFolder, reserialed.File.Content));
        for (int at = 0; at + 27 <= music.Length && music[at] == 'O' && music[at + 1] == 'g'; )
        {
            music[at + 14] ^= 1 << 3;
            music[at + 22] ^= 1 << 5;
            int segments = music[at + 26], body = 0;
            for (int i = 0; i < segments; i++) body += music[at + 27 + i];
            at += 27 + segments + body;
        }
        File.WriteAllBytes(Path.Combine(moddedFolder, reserialed.File.Content), music);

        string mod = Path.Combine(_root, "ExtractedMod");
        ExtractResult result = await new ModExtractService(io).ExtractAsync(src, Path.Combine(moddedFolder, "data.win"), mod, "Streamed test mod");
        foreach (string note in result.NotConverted) _out.WriteLine("not converted: " + note);

        Assert.Equal(1, result.WrittenByCategory["streamed sounds"]);
        Assert.Single(result.WrittenByCategory);

        string game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        File.Copy(src, Path.Combine(game, "data.win"));
        MergeResult merge = await new ModMergeService(io).ApplyAsync(game, new List<ModSource> { new("Streamed test mod", mod) });
        Assert.True(merge.Success, merge.Reason);

        UndertaleSound rebuilt = (await io.LoadAsync(Path.Combine(game, "data.win"))).Sounds.ByName(changed.Name.Content);
        Assert.True(Streamed(rebuilt));
        Assert.Equal(File.ReadAllBytes(Path.Combine(gameFolder, donor.File.Content)), File.ReadAllBytes(Path.Combine(game, rebuilt.File.Content)));
    }

    [Fact]
    public async Task Room_instance_changes_are_extracted_and_rebuild_the_room()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        var io = new DirectDataService();
        UndertaleData data = await io.LoadAsync(src);
        UndertaleRoom room = data.Rooms.First(r => r?.GameObjects.Count >= 3 && r.GameObjects.Take(3).All(g => g.ObjectDefinition is not null && g.CreationCode is null));
        UndertaleRoom.GameObject moved = room.GameObjects[0], removed = room.GameObjects[1];
        moved.X += 4;
        room.GameObjects.Remove(removed);
        room.GameObjects.Add(new UndertaleRoom.GameObject { InstanceID = data.GeneralInfo.LastObj++, ObjectDefinition = room.GameObjects[1].ObjectDefinition, X = 21, Y = 43, ScaleY = 3 });
        var withCode = new UndertaleRoom.GameObject { InstanceID = data.GeneralInfo.LastObj++, ObjectDefinition = room.GameObjects[1].ObjectDefinition, X = 1, Y = 1 };
        string creationName = $"gml_RoomCC_{room.Name.Content}_{withCode.InstanceID}_Create";
        var creation = new CodeImportGroup(data);
        creation.QueueReplace(creationName, "pjum_creation_marker = 7;");
        Assert.True(creation.Import(false).Successful);
        withCode.CreationCode = data.Code.ByName(creationName);
        room.GameObjects.Add(withCode);
        string modded = Path.Combine(_root, "modded.win");
        await io.SaveAsync(data, modded);
        _out.WriteLine($"Room {room.Name.Content}: moved {moved.InstanceID}, removed {removed.InstanceID}, added 2.");

        string mod = Path.Combine(_root, "RoomMod");
        ExtractResult result = await new ModExtractService(io).ExtractAsync(src, modded, mod, "Room test mod");
        foreach (string note in result.NotConverted) _out.WriteLine("not converted: " + note);

        Assert.Equal(1, result.WrittenByCategory["rooms"]);
        Assert.Equal(1, result.WrittenByCategory["code"]);
        Assert.DoesNotContain(result.NotConverted, n => n.Contains("creation code"));
        JsonNode patch = JsonNode.Parse(File.ReadAllText(Path.Combine(mod, "undertale", "data.win", "rooms", room.Name.Content + ".json")))!;
        Assert.Equal((uint)removed.InstanceID, patch["remove"]![0]!.GetValue<uint>());
        Assert.Equal(moved.X, patch["change"]![0]!["x"]!.GetValue<int>());
        Assert.Null(patch["change"]![0]!["y"]);
        Assert.Equal(2, patch["add"]!.AsArray().Count);
        Assert.Null(patch["add"]![0]!["creationCode"]);
        Assert.Equal(creationName, patch["add"]![1]!["creationCode"]!.GetValue<string>());

        string game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        File.Copy(src, Path.Combine(game, "data.win"));
        MergeResult merge = await new ModMergeService(io).ApplyAsync(game, new List<ModSource> { new("Room test mod", mod) });
        Assert.True(merge.Success, merge.Reason);

        UndertaleRoom rebuilt = (await io.LoadAsync(Path.Combine(game, "data.win"))).Rooms.ByName(room.Name.Content);
        static string Shape(UndertaleRoom.GameObject g) => $"{g.ObjectDefinition?.Name?.Content} {g.X},{g.Y} {g.ScaleX},{g.ScaleY}";
        Assert.Equal(room.GameObjects.Select(Shape), rebuilt.GameObjects.Select(Shape));
        UndertaleCode rebuiltCreation = rebuilt.GameObjects[^1].CreationCode;
        Assert.Equal(creationName, rebuiltCreation.Name.Content);
        UndertaleData rebuiltData = await io.LoadAsync(Path.Combine(game, "data.win"));
        Assert.Contains("pjum_creation_marker = 7;", Decompile(rebuiltData, creationName));
    }

    [Fact]
    public async Task An_object_stored_by_number_is_written_by_name()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        var io = new DirectDataService();
        UndertaleData data = await io.LoadAsync(src);
        const string objectName = "obj_pjum_by_number";
        var obj = new UndertaleGameObject { Name = data.Strings.MakeString(objectName) };
        data.GameObjects.Add(obj);
        int index = data.GameObjects.IndexOf(obj);
        string script = data.IsVersionAtLeast(2, 3) ? "gml_GlobalScript_pjum_by_number" : "gml_Script_pjum_by_number";
        var group = new CodeImportGroup(data);
        group.QueueReplace(script, $"pjum_stored = {objectName};\npjum_list[{index}] = 1;");
        Assert.True(group.Import(false).Successful);
        string modded = Path.Combine(_root, "modded.win");
        await io.SaveAsync(data, modded);

        string mod = Path.Combine(_root, "NumberMod");
        ExtractResult result = await new ModExtractService(io).ExtractAsync(src, modded, mod, "Number test mod");
        foreach (string note in result.NotConverted) _out.WriteLine("not converted: " + note);

        string gml = File.ReadAllText(Path.Combine(mod, "undertale", "data.win", "code", script + ".gml"));
        _out.WriteLine(gml);
        Assert.Contains($"pjum_stored = {objectName};", gml);
        Assert.Contains($"pjum_list[{index}] = 1;", gml);
        Assert.Contains(result.NotConverted, n => n.Contains($"Code '{script}' line 2") && n.Contains($"{index} may mean object '{objectName}'"));
    }

    [Fact]
    public async Task Event_code_entries_that_share_a_name_keep_their_own_code()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        var io = new DirectDataService();
        UndertaleData original = await io.LoadAsync(src);
        UndertaleData modded = await io.LoadAsync(src);
        var picks = modded.GameObjects
            .Select(o => (Object: o, Code: o.Events[(int)EventType.Create].FirstOrDefault()?.Actions.FirstOrDefault()?.CodeId))
            .Where(p => p.Code is not null && p.Code.Length > 40 && p.Code.ParentEntry is null)
            .Take(2).ToList();
        Assert.Equal(2, picks.Count);
        string Decompile(UndertaleData d, UndertaleCode c) =>
            new Underanalyzer.Decompiler.DecompileContext(new GlobalDecompileContext(d), c, d.ToolInfo.DecompilerSettings).DecompileToString();
        var expected = picks.Select(p => Decompile(modded, p.Code!)).ToList();
        Assert.NotEqual(expected[0], expected[1]);
        foreach (var p in picks)
            p.Code!.Name = modded.Strings.MakeString("pjum_shared");
        string folder = Path.GetDirectoryName(src)!;
        string mod = Path.Combine(_root, "SharedNameMod");

        ExtractResult result = ModExtractor.Extract(original, modded, folder, folder, mod, "Shared name mod");
        foreach (string note in result.NotConverted) _out.WriteLine("note: " + note);

        Assert.False(File.Exists(Path.Combine(mod, "undertale", "data.win", "code", "pjum_shared.gml")));
        Assert.Contains(result.NotConverted, n => n.Contains("shares a name") && n.Contains("'pjum_shared'"));
        Assert.DoesNotContain(result.NotConverted, n => n.Contains("was removed"));

        string game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        File.Copy(src, Path.Combine(game, "data.win"));
        MergeResult merge = await new ModMergeService(io).ApplyAsync(game, new List<ModSource> { new("Shared name mod", mod) });
        Assert.True(merge.Success, merge.Reason);

        UndertaleData after = await io.LoadAsync(Path.Combine(game, "data.win"));
        for (int i = 0; i < 2; i++)
        {
            UndertaleCode code = after.GameObjects.ByName(picks[i].Object.Name.Content).Events[(int)EventType.Create][0].Actions[0].CodeId;
            Assert.Equal(expected[i], Decompile(after, code));
        }
    }

    [Fact]
    public async Task Oddly_named_creation_code_on_an_added_instance_gets_the_standard_name()
    {
        string? src = TestPaths.UndertaleDataWin;
        if (src is null) { _out.WriteLine("Undertale not found — skipping."); return; }
        var io = new DirectDataService();
        UndertaleData original = await io.LoadAsync(src);
        UndertaleData modded = await io.LoadAsync(src);
        UndertaleRoom room = modded.Rooms.First(r => r?.GameObjects.Count >= 2 && r.GameObjects[1].ObjectDefinition is not null);
        var creation = new CodeImportGroup(modded);
        creation.QueueReplace("pjum_odd_creation", "pjum_odd_marker = 5;");
        Assert.True(creation.Import(false).Successful);
        UndertaleCode odd = modded.Code.ByName("pjum_odd_creation");
        odd.Name = modded.Strings.MakeString("gml_Object_pjum_weird_layer");
        var added = new UndertaleRoom.GameObject { InstanceID = modded.GeneralInfo.LastObj++, ObjectDefinition = room.GameObjects[1].ObjectDefinition, X = 9, Y = 9, CreationCode = odd };
        room.GameObjects.Add(added);
        string folder = Path.GetDirectoryName(src)!;
        string mod = Path.Combine(_root, "OddCreationMod");

        ExtractResult result = ModExtractor.Extract(original, modded, folder, folder, mod, "Odd creation mod");
        foreach (string note in result.NotConverted) _out.WriteLine("note: " + note);

        string standard = $"gml_RoomCC_{room.Name.Content}_{added.InstanceID}_Create";
        Assert.True(File.Exists(Path.Combine(mod, "undertale", "data.win", "code", standard + ".gml")));
        Assert.False(File.Exists(Path.Combine(mod, "undertale", "data.win", "code", "gml_Object_pjum_weird_layer.gml")));
        Assert.Contains(result.NotConverted, n => n.Contains("isn't named gml_RoomCC_") && n.Contains("'gml_Object_pjum_weird_layer'"));

        string game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        File.Copy(src, Path.Combine(game, "data.win"));
        MergeResult merge = await new ModMergeService(io).ApplyAsync(game, new List<ModSource> { new("Odd creation mod", mod) });
        Assert.True(merge.Success, merge.Reason);
        UndertaleData after = await io.LoadAsync(Path.Combine(game, "data.win"));
        UndertaleCode linked = after.Rooms.ByName(room.Name.Content).GameObjects[^1].CreationCode;
        Assert.Equal(standard, linked.Name.Content);
        Assert.Contains("pjum_odd_marker = 5;", Decompile(after, standard));
    }

    [Fact]
    public async Task A_file_for_a_different_game_is_refused()
    {
        string? src = TestPaths.DataWin;
        string? other = TestPaths.UndertaleDataWin;
        if (string.IsNullOrEmpty(src) || other is null || Path.GetFullPath(src) == Path.GetFullPath(other)) { _out.WriteLine("Need two different games — skipping."); return; }

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ModExtractService(new DirectDataService()).ExtractAsync(src, other, Path.Combine(_root, "mod"), "x"));

        Assert.Contains("is for", ex.Message);
    }
}
