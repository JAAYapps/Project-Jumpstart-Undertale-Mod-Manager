using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UndertaleModLib;
using UndertaleModLib.Models;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Data;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;
using Xunit;

namespace MergeTests;

// Tier 1 object importer, end to end through the dir-model dispatcher. Gated on
// PJUM_TEST_DATAWIN. RED-FIRST: written before confirming the importer greens,
// so the first run should fail on the assertions (or NotImplemented if the
// stub is still in the dispatcher). Green it next session.
public class ObjectImporterTests
{
    private readonly ITestOutputHelper _out;
    public ObjectImporterTests(ITestOutputHelper output) => _out = output;

    private static string SourceDataWin => TestPaths.DataWin;

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

    private static string PrepareGameDir(string src)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pjum_objdir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.Copy(src, Path.Combine(dir, "data.win"), overwrite: true);
        return dir;
    }

    private static UndertaleData Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return UndertaleIO.Read(fs);
    }

    // Find an existing sprite name to reference, so the object's sprite resolves.
    private static string FirstSpriteName(UndertaleData d)
    {
        foreach (var s in d.Sprites)
            if (s?.Name?.Content is string n) return n;
        return null;
    }

    // CREATE a new object with properties + an event, prove it round-trips.
    [Fact]
    public async Task NewObject_declared_createsWithPropertiesAndEvent()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        UndertaleData probe = Load(src);
        string sprite = FirstSpriteName(probe);
        Assert.NotNull(sprite);

        string root = Path.Combine(Path.GetTempPath(), "pjum_obj_" + Guid.NewGuid().ToString("N"));
        string mod = Path.Combine(root, "ObjMod");
        string gameDir = PrepareGameDir(src);
        const string objName = "obj_pjum_test";

        try
        {
            Directory.CreateDirectory(mod);
            File.WriteAllText(Path.Combine(mod, "mod.json"),
                "{ \"name\": \"ObjMod\", \"version\": \"1.0.0\", " +
                "\"newAssets\": { \"objects\": { \"" + objName + "\": {} } } }");

            string objDir = Path.Combine(mod, "undertale", "data.win", "objects");
            Directory.CreateDirectory(objDir);
            File.WriteAllText(Path.Combine(objDir, objName + ".json"),
                "{ \"sprite\": \"" + sprite + "\", \"visible\": true, \"solid\": true, " +
                "\"depth\": -5, \"persistent\": false, " +
                "\"physics\": { \"uses\": true, \"shape\": \"Box\", \"density\": 0.75, \"friction\": 0.3 }, " +
                "\"events\": [ { \"type\": \"Create\" }, { \"type\": \"Step\", \"subtype\": 0 } ] }");

            var svc = new ModMergeService(new DirectDataService());
            MergeResult result = await svc.ApplyAsync(gameDir, new List<ModSource> { new("ObjMod", mod) });
            Assert.True(result.Success, result.Reason);
            foreach (string w in result.Warnings) _out.WriteLine("WARN: " + w);

            // Reload and verify.
            UndertaleData after = Load(Path.Combine(gameDir, "data.win"));
            UndertaleGameObject o = after.GameObjects.ByName(objName);
            Assert.NotNull(o);
            Assert.Equal(sprite, o.Sprite?.Name?.Content);
            Assert.True(o.Visible);
            Assert.True(o.Solid);
            Assert.Equal(-5, o.Depth);
            Assert.True(o.UsesPhysics);
            Assert.Equal(CollisionShapeFlags.Box, o.CollisionShape);
            Assert.Equal(0.75f, o.Density);
            Assert.Equal(0.3f, o.Friction);

            // Events wired: Create (type 0) and Step (type 3) sublists non-empty.
            Assert.NotEmpty(o.Events[(int)EventType.Create]);
            Assert.NotEmpty(o.Events[(int)EventType.Step]);
        }
        finally
        {
            TryDelete(root);
            TryDelete(gameDir);
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}