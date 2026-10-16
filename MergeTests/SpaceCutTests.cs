using System;
using System.IO;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Launcher;
using Xunit;

namespace MergeTests;

public class SpaceCutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pjum_cut_" + Guid.NewGuid().ToString("N"));
    private readonly LauncherService _launcher = new();

    public SpaceCutTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "My Games", "Mod Manager", "game"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Exe => Path.Combine(_root, "My Games", "Mod Manager", "game", "DELTARUNE.exe");

    [Fact]
    public void Nothing_at_the_cuts_finds_nothing()
    {
        Assert.Empty(_launcher.FindSpaceCutFiles(Exe));
    }

    [Fact]
    public void A_file_at_the_first_cut_is_found()
    {
        string file = Path.Combine(_root, "My");
        File.WriteAllText(file, "x");

        Assert.Equal(new[] { file }, _launcher.FindSpaceCutFiles(Exe));
    }

    [Fact]
    public void A_file_at_a_later_cut_with_exe_added_is_found()
    {
        string file = Path.Combine(_root, "My Games", "Mod.exe");
        File.WriteAllText(file, "x");

        Assert.Equal(new[] { file }, _launcher.FindSpaceCutFiles(Exe));
    }

    [Fact]
    public void A_file_with_different_capitals_is_found()
    {
        string file = Path.Combine(_root, "MY");
        File.WriteAllText(file, "x");

        Assert.Equal(new[] { file }, _launcher.FindSpaceCutFiles(Exe));
    }

    [Fact]
    public void A_folder_at_a_cut_is_not_reported()
    {
        Directory.CreateDirectory(Path.Combine(_root, "My"));

        Assert.Empty(_launcher.FindSpaceCutFiles(Exe));
    }
}
