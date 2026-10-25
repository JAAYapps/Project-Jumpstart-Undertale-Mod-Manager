using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Project;
using PjumModLib;
using Xunit;

namespace MergeTests;

public class ProjectConverterTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pjum_projectconvert_" + Guid.NewGuid().ToString("N"));

    public ProjectConverterTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static UndertaleData Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return UndertaleIO.Read(fs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_modded_file_becomes_a_project_that_rebuilds_it_with_the_same_numbers(int chapter)
    {
        string? src = chapter == 0 ? TestPaths.UndertaleDataWin : TestPaths.DeltaruneChapterDataWin(chapter);
        if (src is null) { _out.WriteLine("Game not found — skipping."); return; }
        UndertaleData original = Load(src);
        UndertaleData modded = Load(src);
        UndertaleSprite renamed = modded.Sprites[5];
        string oldName = renamed.Name.Content;
        renamed.Name = modded.Strings.MakeString("spr_pjum_renamed");
        UndertaleSprite template = modded.Sprites.First(s => s?.Textures.Count > 0 && s != renamed);
        UndertaleSprite NewSprite(string name)
        {
            var sprite = new UndertaleSprite { Name = modded.Strings.MakeString(name), Width = template.Width, Height = template.Height };
            sprite.Textures.Add(new UndertaleSprite.TextureEntry { Texture = template.Textures[0].Texture });
            modded.Sprites.Add(sprite);
            return sprite;
        }
        NewSprite("spr_pjum_b");
        UndertaleSprite a = NewSprite("spr_pjum_a");
        modded.GameObjects.Add(new UndertaleGameObject { Name = modded.Strings.MakeString("obj_pjum_new"), Sprite = a });
        UndertaleRoom changedRoom = modded.Rooms.First(r => r?.GameObjects.Count > 0);
        changedRoom.GameObjects[0].X += 4;
        UndertaleRoom newRoom = RoomLayers.EmptyRoom(modded, text => modded.Strings.MakeString(text));
        newRoom.Name = modded.Strings.MakeString("room_pjum_new");
        modded.Rooms.Add(newRoom);
        modded.GeneralInfo.RoomOrder.Add(new(newRoom));

        string project = Path.Combine(_root, "project");
        ProjectConvertResult result = ProjectConverter.Convert(original, src, modded, src, project, "Converted");
        _out.WriteLine(string.Join(", ", result.WrittenByFolder.Select(p => $"{p.Value} {p.Key}")));
        foreach (string note in result.Notes) _out.WriteLine("note: " + note);
        Assert.Empty(result.Notes);
        Assert.Equal((1, 4, 1), (result.WrittenByFolder["renames"], result.WrittenByFolder["new assets in order"], result.WrittenByFolder["room order"]));
        Assert.All(Directory.GetFiles(Path.Combine(project, "code")), file => Assert.Contains("spr_pjum_renamed", File.ReadAllText(file)));
        Assert.All(Directory.GetFiles(Path.Combine(project, "objects")).Where(f => !f.EndsWith("obj_pjum_new.json")), file => Assert.Contains("spr_pjum_renamed", File.ReadAllText(file)));

        string game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        File.Copy(src, Path.Combine(game, "data.win"));
        ProjectContext.CreateWithDataFilePaths(Path.Combine(game, "data.win"), Path.Combine(game, "data.win"), Path.Combine(project, "project.json")).Import();

        UndertaleData after = Load(Path.Combine(game, "data.win"));
        Assert.Equal(modded.Sprites.Select(s => s?.Name?.Content), after.Sprites.Select(s => s?.Name?.Content));
        Assert.Equal(modded.GameObjects.Select(o => o?.Name?.Content), after.GameObjects.Select(o => o?.Name?.Content));
        Assert.Equal(modded.Rooms.Select(r => r?.Name?.Content), after.Rooms.Select(r => r?.Name?.Content));
        Assert.Equal(modded.GeneralInfo.RoomOrder.Select(r => r.Resource?.Name?.Content), after.GeneralInfo.RoomOrder.Select(r => r.Resource?.Name?.Content));
        Assert.Null(after.Sprites.ByName(oldName));
        Assert.Equal("spr_pjum_a", after.GameObjects.ByName("obj_pjum_new").Sprite?.Name?.Content);
        Assert.Equal(changedRoom.GameObjects[0].X, after.Rooms.ByName(changedRoom.Name.Content).GameObjects[0].X);
    }
}
