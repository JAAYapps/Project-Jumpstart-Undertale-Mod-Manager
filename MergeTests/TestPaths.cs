using System;
using System.Collections.Generic;
using System.IO;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;
using Project_Jumpstart_Undertale_Mod_Manager.Services.SteamScanner;
using UndertaleModLib;

namespace MergeTests;

// Resolves real game files for integration tests WITHOUT a hardcoded path.
// Order: explicit env var -> Steam auto-discovery -> null (test skips).
public static class TestPaths
{
    // A data.win to exercise. Prefers Deltarune (TGIN/EMBI coverage), then Undertale.
    public static string? DataWin =>
        FromEnv("PJUM_TEST_DATAWIN")
        ?? Discover("DELTARUNE", "data.win")
        ?? Discover("Undertale", "data.win");

    public static string? UndertaleDataWin => Discover("Undertale", "data.win");

    // A Deltarune chapter dir (its own data.win + audiogroupN.dat + loose .ogg).
    public static string? DeltaruneChapter
    {
        get
        {
            string? env = FromEnv("PJUM_TEST_DELTARUNE_CHAPTER");
            if (env is not null) return env;
            string? chapterWin = Discover("DELTARUNE", Path.Combine("chapter1_windows", "data.win"));
            return chapterWin is null ? null : Path.GetDirectoryName(chapterWin);
        }
    }

    // The folder a test mod's assets go under so they are addressed to the
    // data they will be merged into: <modDir>/<game>/[<chapter>/]<container>.
    // The merge skips files addressed to any other game or chapter.
    public static string ModRoot(string modDir, UndertaleData data, string container = "data.win")
    {
        ModTarget target = ModTarget.FromData(data) ?? new ModTarget("undertale", null);
        return target.Chapter is null
            ? Path.Combine(modDir, target.Game, container)
            : Path.Combine(modDir, target.Game, target.Chapter, container);
    }

    public static string ModRoot(string modDir, string dataWinPath, string container = "data.win")
    {
        using var fs = new FileStream(dataWinPath, FileMode.Open, FileAccess.Read);
        return ModRoot(modDir, UndertaleIO.Read(fs), container);
    }

    private static string? FromEnv(string key)
    {
        string? v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    // Steam libraries, parsed once. Primary root + every libraryfolders.vdf entry.
    private static readonly Lazy<List<string>> Libraries = new(() =>
    {
        var scanner = new CrossPlatformSteamScanner();
        string? root = scanner.GetSteamRootPath();
        var libs = new List<string>();
        if (root is null) return libs;
        libs.Add(root);                                 // primary library
        libs.AddRange(scanner.ResolveLibraryPaths(root)); // extra drives
        return libs;
    });

    private static string? Discover(string gameFolder, string relative)
    {
        foreach (string lib in Libraries.Value)
        {
            string candidate = Path.Combine(lib, "steamapps", "common", gameFolder, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}