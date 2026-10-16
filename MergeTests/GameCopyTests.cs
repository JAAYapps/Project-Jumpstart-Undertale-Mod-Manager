using System;
using System.IO;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Launcher;
using Xunit;

namespace MergeTests;

public class GameCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pjum_copy_" + Guid.NewGuid().ToString("N"));
    private readonly LauncherService _launcher = new();

    private string Game => Path.Combine(_root, "game");
    private string Copy => Path.Combine(_root, "copy");

    public GameCopyTests()
    {
        Write(Path.Combine(Game, "DELTARUNE.exe"), "exe");
        Write(Path.Combine(Game, "data.win"), "launcher");
        Write(Path.Combine(Game, "chapter2_windows", "data.win"), "chapter two");
        Write(Path.Combine(Game, "mods", "load_order.json"), "[]");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void First_refresh_copies_every_file_except_the_mods_folder()
    {
        GameCopyResult result = _launcher.RefreshGameCopy(Game, Copy);

        Assert.Equal(3, result.FilesCopied);
        Assert.Equal("chapter two", File.ReadAllText(Path.Combine(Copy, "chapter2_windows", "data.win")));
        Assert.False(Directory.Exists(Path.Combine(Copy, "mods")));
    }

    [Fact]
    public void Second_refresh_with_nothing_changed_copies_nothing()
    {
        _launcher.RefreshGameCopy(Game, Copy);

        GameCopyResult result = _launcher.RefreshGameCopy(Game, Copy);

        Assert.Equal(new GameCopyResult(0, 0, 0), result);
    }

    [Fact]
    public void A_file_the_merge_rewrote_is_copied_back()
    {
        _launcher.RefreshGameCopy(Game, Copy);
        File.WriteAllText(Path.Combine(Copy, "chapter2_windows", "data.win"), "merged chapter two");

        GameCopyResult result = _launcher.RefreshGameCopy(Game, Copy);

        Assert.Equal(1, result.FilesCopied);
        Assert.Equal("chapter two", File.ReadAllText(Path.Combine(Copy, "chapter2_windows", "data.win")));
    }

    [Fact]
    public void A_rewritten_file_with_the_same_size_is_copied_back()
    {
        _launcher.RefreshGameCopy(Game, Copy);
        string merged = Path.Combine(Copy, "data.win");
        File.WriteAllText(merged, "LAUNCHER");
        File.SetLastWriteTimeUtc(merged, DateTime.UtcNow.AddMinutes(5));

        GameCopyResult result = _launcher.RefreshGameCopy(Game, Copy);

        Assert.Equal(1, result.FilesCopied);
        Assert.Equal("launcher", File.ReadAllText(merged));
    }

    [Fact]
    public void Files_and_folders_the_game_never_had_are_removed()
    {
        _launcher.RefreshGameCopy(Game, Copy);
        Write(Path.Combine(Copy, "steam_appid.txt"), "1671210");
        Write(Path.Combine(Copy, "lang", "mod_text.json"), "{}");
        Write(Path.Combine(Copy, "mods", "left_by_a_mod.txt"), "x");

        GameCopyResult result = _launcher.RefreshGameCopy(Game, Copy);

        Assert.Equal(3, result.FilesRemoved);
        Assert.False(File.Exists(Path.Combine(Copy, "steam_appid.txt")));
        Assert.False(Directory.Exists(Path.Combine(Copy, "lang")));
        Assert.False(Directory.Exists(Path.Combine(Copy, "mods")));
        Assert.True(File.Exists(Path.Combine(Copy, "chapter2_windows", "data.win")));
    }

    [Fact]
    public void A_game_update_is_picked_up()
    {
        _launcher.RefreshGameCopy(Game, Copy);
        File.WriteAllText(Path.Combine(Game, "chapter2_windows", "data.win"), "chapter two, patched");
        Write(Path.Combine(Game, "chapter3_windows", "data.win"), "chapter three");

        GameCopyResult result = _launcher.RefreshGameCopy(Game, Copy);

        Assert.Equal(2, result.FilesCopied);
        Assert.Equal("chapter two, patched", File.ReadAllText(Path.Combine(Copy, "chapter2_windows", "data.win")));
        Assert.Equal("chapter three", File.ReadAllText(Path.Combine(Copy, "chapter3_windows", "data.win")));
    }
}
