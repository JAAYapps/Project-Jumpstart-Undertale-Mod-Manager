using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UndertaleModLib;
using UndertaleModLib.Models;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Data;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Sounds;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;
using Xunit;

namespace MergeTests;

// ===========================================================================
// RED-FIRST. These are written to FAIL until the sound importer + dir-model
// dispatcher exist. Do NOT "fix" them by weakening asserts — make the code
// satisfy them, one at a time.
//
//   Tests 1,2,3,5  -> run against Undertale (PJUM_TEST_DATAWIN).
//                     First-fail: SoundImporter.Apply throws NotImplemented.
//   Test 4         -> runs against a Deltarune chapter dir
//                     (PJUM_TEST_DELTARUNE_CHAPTER = /.../DELTARUNE/chapter2_windows).
//                     First-fail: COMPILE — ApplyAsync(gameDir, mods) / the
//                     dir-model dispatcher doesn't exist yet. That compile error
//                     is the spec telling us to reshape the dispatcher.
// ===========================================================================
public class SoundImporterTests
{
    private readonly ITestOutputHelper _out;
    public SoundImporterTests(ITestOutputHelper output) => _out = output;

    private static string SourceDataWin => TestPaths.DataWin;
    private static string DeltaruneChapterDir => TestPaths.DeltaruneChapter;

    private static UndertaleData Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return UndertaleIO.Read(fs);
    }

    // A tiny valid WAV (44-byte header + a few silent samples) so embed tests
    // have real bytes without shipping a binary fixture.
    private static byte[] TinyWav()
    {
        // 8000 Hz, 8-bit, mono, 8 silent samples — a valid minimal WAV.
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        const int sampleRate = 8000, bits = 8, channels = 1, dataLen = 8;
        int byteRate = sampleRate * channels * bits / 8;
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataLen);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);                       // fmt chunk size
        w.Write((short)1);                 // PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)(channels * bits / 8)); // block align
        w.Write((short)bits);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(dataLen);
        w.Write(new byte[dataLen]);        // silence
        w.Flush();
        return ms.ToArray();
    }

    private static ModAddress ParseSound(string game, string container, string name, string ext)
        => ModAddressParser.Parse($"{game}/{container}/sounds/{name}.{ext}");

    // ---- TEST 1: WAV embeds into data.EmbeddedAudio ----------------------
    [Fact]
    public void EmbeddedWav_addsEmbeddedAudio_andWiresSound()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        UndertaleData data = Load(src);
        int audioBefore = data.EmbeddedAudio.Count;

        string dir = TempDir();
        try
        {
            string name = "snd_pjum_wav";
            string json = Path.Combine(dir, name + ".json");
            string wav = Path.Combine(dir, name + ".wav");
            File.WriteAllText(json, "{ \"type\": \".wav\", \"embedded\": true, \"volume\": 1.0, \"pitch\": 1.0 }");
            File.WriteAllBytes(wav, TinyWav());

            var addr = ParseSound("undertale", "data.win", name, "wav");
            SoundImporter.Apply(data, dir, addr, json, wav, create: true);

            UndertaleSound s = data.Sounds.ByName(name);
            Assert.NotNull(s);
            Assert.True(s.Flags.HasFlag(UndertaleSound.AudioEntryFlags.IsEmbedded));
            Assert.NotNull(s.AudioFile);                       // wired to an embedded entry
            Assert.Equal(audioBefore + 1, data.EmbeddedAudio.Count);
            Assert.Equal(data.GetBuiltinSoundGroupID(), s.GroupID);
        }
        finally { TryDelete(dir); }
    }

    // ---- TEST 2: OGG embed + decode-on-load flags ------------------------
    [Fact]
    public void EmbeddedOgg_decodeOnLoad_setsCompressedFlags()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        UndertaleData data = Load(src);
        string dir = TempDir();
        try
        {
            string name = "snd_pjum_ogg";
            string json = Path.Combine(dir, name + ".json");
            string ogg = Path.Combine(dir, name + ".ogg");
            File.WriteAllText(json,
                "{ \"type\": \".ogg\", \"embedded\": true, \"compressed\": true, \"decodeOnLoad\": true }");
            File.WriteAllBytes(ogg, new byte[] { (byte)'O', (byte)'g', (byte)'g', (byte)'S', 0, 0, 0, 0 });

            var addr = ParseSound("undertale", "data.win", name, "ogg");
            SoundImporter.Apply(data, dir, addr, json, ogg, create: true);

            UndertaleSound s = data.Sounds.ByName(name);
            Assert.NotNull(s);
            Assert.True(s.Flags.HasFlag(UndertaleSound.AudioEntryFlags.IsEmbedded));
            Assert.True(s.Flags.HasFlag(UndertaleSound.AudioEntryFlags.IsCompressed));
        }
        finally { TryDelete(dir); }
    }

    // ---- TEST 3: external streamed OGG — no embedded entry ---------------
    [Fact]
    public void ExternalOgg_streamed_addsNoEmbeddedAudio()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        UndertaleData data = Load(src);
        int audioBefore = data.EmbeddedAudio.Count;

        string dir = TempDir();
        try
        {
            string name = "snd_pjum_ext";
            string json = Path.Combine(dir, name + ".json");
            string ogg = Path.Combine(dir, name + ".ogg");
            File.WriteAllText(json, "{ \"type\": \".ogg\", \"embedded\": false }");
            File.WriteAllBytes(ogg, new byte[] { (byte)'O', (byte)'g', (byte)'g', (byte)'S', 0, 0, 0, 0 });

            var addr = ParseSound("undertale", "data.win", name, "ogg");
            SoundImporter.Apply(data, dir, addr, json, ogg, create: true);

            UndertaleSound s = data.Sounds.ByName(name);
            Assert.NotNull(s);
            Assert.False(s.Flags.HasFlag(UndertaleSound.AudioEntryFlags.IsEmbedded));
            Assert.Equal(audioBefore, data.EmbeddedAudio.Count);   // nothing embedded
            // The loose .ogg should have been dropped into the game dir to stream from.
            Assert.True(File.Exists(Path.Combine(dir, name + ".ogg")));
        }
        finally { TryDelete(dir); }
    }

    // REPLACE the GroupedSound test in SoundImporterTests.cs with this version.
    // It targets a real non-default audiogroup (audio_sfx) via the address
    // container, derives which audiogroupN.dat should change from the data itself
    // (so it's correct no matter the group's index), and asserts THAT .dat grew
    // while the source stays untouched.
    //
    // Uses Assert.Skip (xUnit v3) so a missing env var reads as skipped, not passed.
 
    [Fact]
    public async Task GroupedSound_writesIntoAudiogroupDat_inGameDir_notSource()
    {
        string chapter = DeltaruneChapterDir;
        Assert.SkipWhen(string.IsNullOrEmpty(chapter), "PJUM_TEST_DELTARUNE_CHAPTER not set.");
 
        // Prepare a disposable game dir copy — merge mutates in place.
        string gameDir = TempDir();
        CopyDir(chapter, gameDir);
 
        // Discover a real non-default audiogroup and its index from the data.
        UndertaleData probe = Load(Path.Combine(gameDir, "data.win"));
        int builtin = probe.GetBuiltinSoundGroupID();
        _out.WriteLine($"builtin group id = {builtin}"); 
        int groupIndex = -1;
        string groupName = null;
        for (int i = 0; i < probe.AudioGroups.Count; i++)
        {
            if (i == builtin) continue;
            if (probe.AudioGroups[i]?.Name?.Content is string n)
            {
                groupIndex = i; groupName = n; break;
            }
        }
        Assert.SkipWhen(groupIndex < 0, "No non-default audiogroup in this chapter to test against.");
        _out.WriteLine($"targeting group '{groupName}' (index {groupIndex}) -> audiogroup{groupIndex}.dat");
 
        string datName = $"audiogroup{groupIndex}.dat";
        string datInGameDir = Path.Combine(gameDir, datName);
        Assert.True(File.Exists(datInGameDir), $"expected {datName} in the chapter copy");
        long datBefore = new FileInfo(datInGameDir).Length;
        long sourceDatBefore = new FileInfo(Path.Combine(chapter, datName)).Length;
 
        // Mod adds a grouped sound addressed under the group's name as container.
        string mod = Path.Combine(TempDir(), "GrpMod");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "mod.json"),
            "{ \"name\": \"GrpMod\", \"version\": \"1.0.0\", " +
            "\"newAssets\": { \"sounds\": { \"snd_pjum_grouped\": {} } } }");
        string sdir = Path.Combine(mod, "deltarune", "chapter2", groupName, "sounds");
        Directory.CreateDirectory(sdir);
        File.WriteAllText(Path.Combine(sdir, "snd_pjum_grouped.json"),
            "{ \"type\": \".wav\", \"embedded\": true }");
        string wavPath = Path.Combine(sdir, "snd_pjum_grouped.wav");
        File.WriteAllBytes(wavPath, TinyWav());
        _out.WriteLine($"WAV written: {wavPath} exists={File.Exists(wavPath)} size={new FileInfo(wavPath).Length}");

        // Also parse its address the way the dispatcher will:
        string relWav = Path.GetRelativePath(mod, wavPath).Replace('\\','/');
        bool ok = ModAddressParser.TryParse(relWav, out var wavAddr, out var wavErr);
        _out.WriteLine($"WAV rel='{relWav}' parses={ok} err='{wavErr}' " +
                       (ok ? $"cat={wavAddr.Category} name={wavAddr.AssetName} container={wavAddr.Container}" : ""));
 
        var svc = new ModMergeService(new DirectDataService());
        MergeResult result = await svc.ApplyAsync(gameDir, new List<ModSource> { new("GrpMod", mod) });
        Assert.True(result.Success, result.Reason);
        foreach (string w in result.Warnings) _out.WriteLine("WARN: " + w);
 
        // The .dat inside the game dir grew...
        Assert.NotEqual(datBefore, new FileInfo(datInGameDir).Length);
        // ...and the ORIGINAL chapter's .dat did NOT.
        Assert.Equal(sourceDatBefore, new FileInfo(Path.Combine(chapter, datName)).Length);
 
        // And reloading data.win, the sound exists in the right group.
        UndertaleData after = Load(Path.Combine(gameDir, "data.win"));
        UndertaleSound s = after.Sounds.ByName("snd_pjum_grouped");
        Assert.NotNull(s);
        Assert.Equal(groupIndex, s.GroupID);
 
        TryDelete(gameDir);
        TryDelete(Path.GetDirectoryName(mod)!);
    }

    // ---- TEST 5: metadata-only replace leaves audio untouched ------------
    [Fact]
    public void MetadataOnly_replace_changesPropsButNotAudio()
    {
        string src = SourceDataWin;
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("PJUM_TEST_DATAWIN not set — skipping."); return; }

        UndertaleData data = Load(src);

        // Find an existing sound to replace metadata on.
        UndertaleSound existing = null;
        foreach (var s in data.Sounds)
            if (s?.Name?.Content is not null && s.AudioFile is not null) { existing = s; break; }
        Assert.NotNull(existing);
        string name = existing.Name.Content;
        UndertaleEmbeddedAudio audioBefore = existing.AudioFile;

        string dir = TempDir();
        try
        {
            string json = Path.Combine(dir, name + ".json");
            File.WriteAllText(json, "{ \"type\": \".wav\", \"embedded\": true, \"volume\": 0.5, \"pitch\": 1.0 }");

            var addr = ParseSound("undertale", "data.win", name, "json");
            SoundImporter.Apply(data, dir, addr, json, audioFile: null, create: false);

            UndertaleSound s = data.Sounds.ByName(name);
            Assert.Equal(0.5f, s.Volume);                 // metadata changed
            Assert.Same(audioBefore, s.AudioFile);        // audio reference untouched
        }
        finally { TryDelete(dir); }
    }

    // ---- helpers ----------------------------------------------------------

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

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "pjum_snd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(from, f);
            string dest = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}