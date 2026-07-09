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

// Tier 1 path importer, end to end through the DIR-MODEL dispatcher. Gated on
// PJUM_TEST_DATAWIN. Creates a new path via a declared mod, merges into a temp
// game dir, reloads <dir>/data.win, and verifies the path.
public class PathImporterTests
{
    private readonly ITestOutputHelper _out;
    public PathImporterTests(ITestOutputHelper output) => _out = output;

    private static string SourceDataWin => Environment.GetEnvironmentVariable("PJUM_TEST_DATAWIN");

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
        string dir = Path.Combine(Path.GetTempPath(), "pjum_gamedir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.Copy(src, Path.Combine(dir, "data.win"), overwrite: true);
        return dir;
    }

    [Fact]
    public async Task NewPath_declared_createsAndRoundTrips()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        string root = Path.Combine(Path.GetTempPath(), "pjum_path_" + Guid.NewGuid().ToString("N"));
        string mod = Path.Combine(root, "PathMod");
        string gameDir = PrepareGameDir(src);
        const string pathName = "path_pjum_testroute";

        try
        {
            Directory.CreateDirectory(mod);
            File.WriteAllText(Path.Combine(mod, "mod.json"),
                "{ \"name\": \"PathMod\", \"version\": \"1.0.0\", " +
                "\"newAssets\": { \"paths\": { \"" + pathName + "\": {} } } }");

            string pathsDir = Path.Combine(mod, "undertale", "data.win", "paths");
            Directory.CreateDirectory(pathsDir);
            File.WriteAllText(Path.Combine(pathsDir, pathName + ".json"),
                "{ \"smooth\": false, \"closed\": true, \"precision\": 4, " +
                "\"points\": [ {\"x\":10,\"y\":20,\"speed\":100}, {\"x\":30,\"y\":40,\"speed\":50} ] }");

            var svc = new ModMergeService(new DirectDataService());
            var mods = new List<ModSource> { new("PathMod", mod) };

            MergeResult result = await svc.ApplyAsync(gameDir, mods);
            Assert.True(result.Success, result.Reason);
            foreach (string w in result.Warnings) _out.WriteLine("WARN: " + w);

            // Reload the in-place data.win and verify.
            var reload = new DirectDataService();
            UndertaleData after = await reload.LoadAsync(Path.Combine(gameDir, "data.win"));

            UndertalePath p = after.Paths.ByName(pathName);
            Assert.NotNull(p);
            Assert.True(p.IsClosed);
            Assert.False(p.IsSmooth);
            Assert.Equal(2, p.Points.Count);
            Assert.Equal(10f, p.Points[0].X);
            Assert.Equal(20f, p.Points[0].Y);
            Assert.Equal(100f, p.Points[0].Speed);
            Assert.Equal(50f, p.Points[1].Speed);
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