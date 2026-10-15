using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UndertaleModLib;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Data;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;
using Xunit;

namespace MergeTests;

public class FilesCategoryTests
{
    private readonly ITestOutputHelper _out;
    public FilesCategoryTests(ITestOutputHelper output) => _out = output;

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

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pjum_files_" + Guid.NewGuid().ToString("N"));
        public string GameDir => Path.Combine(Root, "game");
        public Sandbox(string dataWin)
        {
            Directory.CreateDirectory(GameDir);
            File.Copy(dataWin, Path.Combine(GameDir, "data.win"));
        }
        public string Mod(string name, string manifestExtra = "")
        {
            string dir = Path.Combine(Root, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "mod.json"),
                $"{{ \"name\": \"{name}\", \"version\": \"1.0.0\"{manifestExtra} }}");
            return dir;
        }
        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task Files_are_copied_beside_data_win_keeping_their_folders()
    {
        string? src = TestPaths.DataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        using var box = new Sandbox(src);
        string mod = box.Mod("FilesMod");
        string files = Path.Combine(TestPaths.ModRoot(mod, src), "files");
        Write(Path.Combine(files, "pjum_root.txt"), "root file");
        Write(Path.Combine(files, "lang", "pjum_nested.json"), "{ \"nested\": true }");

        MergeResult result = await new ModMergeService(new DirectDataService())
            .ApplyAsync(box.GameDir, new List<ModSource> { new("FilesMod", mod) });
        foreach (string w in result.Warnings) _out.WriteLine("WARN: " + w);

        Assert.True(result.Success, result.Reason);
        Assert.Equal("root file", File.ReadAllText(Path.Combine(box.GameDir, "pjum_root.txt")));
        Assert.Equal("{ \"nested\": true }", File.ReadAllText(Path.Combine(box.GameDir, "lang", "pjum_nested.json")));
        Assert.False(Directory.Exists(Path.Combine(box.GameDir, "files")));
        Assert.False(Directory.Exists(Path.Combine(box.GameDir, "data.win", "files")));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Later_mod_wins_and_the_overlap_is_logged()
    {
        string? src = TestPaths.DataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        using var box = new Sandbox(src);
        string first = box.Mod("FirstMod");
        string second = box.Mod("SecondMod");
        Write(Path.Combine(TestPaths.ModRoot(first, src), "files", "pjum_same.txt"), "from first");
        Write(Path.Combine(TestPaths.ModRoot(second, src), "files", "pjum_same.txt"), "from second");

        MergeResult result = await new ModMergeService(new DirectDataService())
            .ApplyAsync(box.GameDir, new List<ModSource> { new("FirstMod", first), new("SecondMod", second) });

        Assert.True(result.Success, result.Reason);
        Assert.Equal("from second", File.ReadAllText(Path.Combine(box.GameDir, "pjum_same.txt")));
        ConflictLogEntry conflict = Assert.Single(result.Conflicts);
        Assert.Contains("files:pjum_same.txt", conflict.AssetKey);
        Assert.Equal("SecondMod", conflict.WinningMod);
        Assert.Equal(new[] { "FirstMod" }, conflict.OverriddenMods);
    }

    [Theory]
    [InlineData("data.win")]
    [InlineData("DATA.WIN")]
    public async Task A_files_entry_that_replaces_data_win_fails_and_changes_nothing(string name)
    {
        string? src = TestPaths.DataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        using var box = new Sandbox(src);
        string mod = box.Mod("BadMod");
        string files = Path.Combine(TestPaths.ModRoot(mod, src), "files");
        Write(Path.Combine(files, name), "not a game");
        Write(Path.Combine(files, "pjum_should_not_copy.txt"), "x");
        string before = Hash(Path.Combine(box.GameDir, "data.win"));

        MergeResult result = await new ModMergeService(new DirectDataService())
            .ApplyAsync(box.GameDir, new List<ModSource> { new("BadMod", mod) });

        Assert.False(result.Success);
        Assert.Equal("BadMod", result.FailedMod);
        Assert.Contains("rewrites", result.Reason);
        Assert.Equal(before, Hash(Path.Combine(box.GameDir, "data.win")));
        Assert.False(File.Exists(Path.Combine(box.GameDir, "pjum_should_not_copy.txt")));
    }

    [Fact]
    public async Task A_files_entry_that_replaces_an_audiogroup_fails()
    {
        string? src = TestPaths.DataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        using var box = new Sandbox(src);
        string mod = box.Mod("BadMod");
        Write(Path.Combine(TestPaths.ModRoot(mod, src), "files", "audiogroup0.dat"), "x");

        MergeResult result = await new ModMergeService(new DirectDataService())
            .ApplyAsync(box.GameDir, new List<ModSource> { new("BadMod", mod) });

        UndertaleData data = await new DirectDataService().LoadAsync(src);
        if (data.AudioGroups is not { Count: > 0 })
        {
            _out.WriteLine("This data.win has no audio groups; audiogroup0.dat is an ordinary file for it.");
            Assert.True(result.Success, result.Reason);
            return;
        }
        Assert.False(result.Success);
        Assert.Equal("BadMod", result.FailedMod);
    }

    [Fact]
    public async Task Files_for_another_chapter_are_skipped()
    {
        string? src = TestPaths.DataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        using var box = new Sandbox(src);
        string mod = box.Mod("ChapterMod");
        string game = Path.GetFileName(Path.GetDirectoryName(TestPaths.ModRoot(mod, src))!);
        string gameFolder = game.StartsWith("chapter") ? Path.GetDirectoryName(Path.GetDirectoryName(TestPaths.ModRoot(mod, src))!)! : Path.GetDirectoryName(TestPaths.ModRoot(mod, src))!;
        Write(Path.Combine(TestPaths.ModRoot(mod, src), "files", "pjum_here.txt"), "here");
        Write(Path.Combine(gameFolder, "chapter99", "data.win", "files", "pjum_elsewhere.txt"), "elsewhere");

        MergeResult result = await new ModMergeService(new DirectDataService())
            .ApplyAsync(box.GameDir, new List<ModSource> { new("ChapterMod", mod) });

        Assert.True(result.Success, result.Reason);
        Assert.True(File.Exists(Path.Combine(box.GameDir, "pjum_here.txt")));
        Assert.False(File.Exists(Path.Combine(box.GameDir, "pjum_elsewhere.txt")));
    }

    [Fact]
    public async Task A_program_file_is_copied_with_a_warning()
    {
        string? src = TestPaths.DataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        using var box = new Sandbox(src);
        string mod = box.Mod("ToolMod");
        Write(Path.Combine(TestPaths.ModRoot(mod, src), "files", "pjum_tool.dll"), "not really a dll");

        MergeResult result = await new ModMergeService(new DirectDataService())
            .ApplyAsync(box.GameDir, new List<ModSource> { new("ToolMod", mod) });

        Assert.True(result.Success, result.Reason);
        Assert.True(File.Exists(Path.Combine(box.GameDir, "pjum_tool.dll")));
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("ToolMod", warning);
        Assert.Contains("program file", warning);
    }

    [Fact]
    public async Task A_files_entry_named_like_a_merged_streamed_sound_wins_with_a_warning()
    {
        string? src = TestPaths.DataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        using var box = new Sandbox(src);
        string mod = box.Mod("SoundMod", ", \"newAssets\": { \"sounds\": { \"snd_pjum_stream\": {} } }");
        string root = TestPaths.ModRoot(mod, src);
        Write(Path.Combine(root, "sounds", "snd_pjum_stream.json"), "{ \"type\": \".ogg\", \"embedded\": false }");
        Write(Path.Combine(root, "sounds", "snd_pjum_stream.ogg"), "sound bytes");
        Write(Path.Combine(root, "files", "snd_pjum_stream.ogg"), "files bytes");

        MergeResult result = await new ModMergeService(new DirectDataService())
            .ApplyAsync(box.GameDir, new List<ModSource> { new("SoundMod", mod) });
        foreach (string w in result.Warnings) _out.WriteLine("WARN: " + w);

        Assert.True(result.Success, result.Reason);
        Assert.Equal("files bytes", File.ReadAllText(Path.Combine(box.GameDir, "snd_pjum_stream.ogg")));
        Assert.Contains(result.Warnings, w => w.Contains("same name as a merged sound"));
        UndertaleData after = await new DirectDataService().LoadAsync(Path.Combine(box.GameDir, "data.win"));
        Assert.NotNull(after.Sounds.ByName("snd_pjum_stream"));
    }
}
