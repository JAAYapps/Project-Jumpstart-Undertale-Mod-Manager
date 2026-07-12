using System.Text.Json;
using System.Text.Json.Serialization;
using UndertaleModLib;
using UndertaleModLib.Models;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Sounds;

// ---------------------------------------------------------------------------
// TIER 1: sounds. Ported faithfully from ImportSingleSound.csx + verified
// against UndertaleSound.cs. Key facts from source:
//
//   * A sound's group is its INDEX in Data.AudioGroups. The bytes for a
//     non-default group live in audiogroup{GroupID}.dat (line 426 of the
//     script) unless the group carries a custom Path.Content (line 420).
//   * Builtin group (GetBuiltinSoundGroupID()) => bytes embedded in data.win.
//   * needAGRP (external group) => bytes added to BOTH Data.EmbeddedAudio AND
//     the .dat's EmbeddedAudio; the sound's AudioFile ref is null, AudioID is
//     the index within the .dat, AudioGroup points at the group.
//
// OPTION 2 grouping: the address CONTAINER names the audiogroup.
//   deltarune/chapter2/audio_sfx/sounds/snd_x.json  -> group "audio_sfx"
//   undertale/data.win/sounds/snd_x.json            -> container "data.win"
//                                                      -> builtin group (embed)
// The container is matched against Data.AudioGroups[i].Name.Content; its index
// is the GroupID. A container that isn't a real group name => builtin.
// ---------------------------------------------------------------------------

public sealed class SoundJson
{
    [JsonPropertyName("type")]         public string Type { get; set; }
    [JsonPropertyName("embedded")]     public bool Embedded { get; set; } = true;
    [JsonPropertyName("decodeOnLoad")] public bool DecodeOnLoad { get; set; }
    [JsonPropertyName("volume")]       public float Volume { get; set; } = 1f;
    [JsonPropertyName("pitch")]        public float Pitch { get; set; } = 1f;
    [JsonPropertyName("effects")]      public uint Effects { get; set; }
}

public static class SoundImporter
{
    public static void Apply(
        UndertaleData data, string gameDir, ModAddress addr,
        string jsonFile, string audioFile, bool create)
    {
        SoundJson json = ReadJson(jsonFile);
        string name = addr.AssetName;

        bool usesAGRP = data.AudioGroups is { Count: > 0 };
        int builtin = data.GetBuiltinSoundGroupID();

        // --- resolve group from the address container (Option 2) ---
        int audioGroupID = ResolveGroupIDFromContainer(data, addr, builtin);
        bool needAGRP = usesAGRP && audioGroupID != builtin;

        string type = json.Type ?? Path.GetExtension(audioFile ?? "") ?? "";
        bool isOgg = type.Equals(".ogg", StringComparison.OrdinalIgnoreCase);
        bool embedSound = !isOgg || json.Embedded;   // WAV always embeds
        
        var existing = create ? null : data.Sounds.ByName(name);
        if (!create && existing is null)
            throw new InvalidOperationException($"Sound '{name}' expected to exist (Replace) but was not found.");

        // Metadata-only replace: no bytes supplied -> touch only properties,
        // leave audio references intact. (This is the third branch in the source
        // where replaceSoundPropertiesCheck is false.)
        bool metadataOnly = audioFile is null;

        int audioId = -1;
        UndertaleEmbeddedAudio? finalAudioReference = null;

        if (audioFile is not null)
        {
            // --- create embedded audio entry if required ---
            UndertaleEmbeddedAudio? soundData = null;
            if (embedSound)   // (embedSound && !needAGRP) || needAGRP  == embedSound here
            {
                soundData = new UndertaleEmbeddedAudio { Data = File.ReadAllBytes(audioFile) };
                data.EmbeddedAudio.Add(soundData);
                if (existing?.AudioFile is not null && !needAGRP)
                    data.EmbeddedAudio.Remove(existing.AudioFile);
                audioId = data.EmbeddedAudio.Count - 1;
            }

            // --- external audiogroup .dat, if needed ---
            if (needAGRP)
            {
                string relName = $"audiogroup{audioGroupID}.dat";
                if (audioGroupID < data.AudioGroups.Count
                    && data.AudioGroups[audioGroupID] is UndertaleAudioGroup { Path.Content: string custom }
                    && !string.IsNullOrEmpty(custom))
                {
                    relName = custom;
                }

                string datPath = Path.Combine(gameDir, relName);
                if (!File.Exists(datPath))
                    throw new FileNotFoundException(
                        $"Grouped sound '{name}' targets '{relName}', not found in the game directory.", datPath);

                UndertaleData groupDat;
                using (var read = new FileStream(datPath, FileMode.Open, FileAccess.Read))
                    groupDat = UndertaleIO.Read(read);

                groupDat.EmbeddedAudio.Add(soundData);
                audioId = groupDat.EmbeddedAudio.Count - 1;

                using (var write = new FileStream(datPath, FileMode.Create, FileAccess.Write))
                    UndertaleIO.Write(write, groupDat);
            }
            else if (!embedSound)
            {
                // External streamed OGG: no embedded entry; loose .ogg into gameDir.
                string dest = Path.Combine(gameDir, name + (Path.GetExtension(audioFile) ?? ".ogg"));
                if (Path.GetFullPath(audioFile) != Path.GetFullPath(dest))
                    File.Copy(audioFile, dest, overwrite: true);
                audioId = -1;
            }

            // --- final embedded-audio reference (source lines 472-484) ---
            if (!embedSound)            finalAudioReference = null;
            else if (embedSound && !needAGRP) finalAudioReference = data.EmbeddedAudio[audioId];
            else                        finalAudioReference = null;  // embed && needAGRP
        }

        // --- flags (source lines 445-470) ---
        var flags = UndertaleSound.AudioEntryFlags.Regular;
        if (isOgg && embedSound && json.DecodeOnLoad)
            flags = UndertaleSound.AudioEntryFlags.IsEmbedded | UndertaleSound.AudioEntryFlags.IsCompressed | UndertaleSound.AudioEntryFlags.Regular;
        else if (isOgg && embedSound && !json.DecodeOnLoad)
            flags = UndertaleSound.AudioEntryFlags.IsCompressed | UndertaleSound.AudioEntryFlags.Regular;
        else if (!isOgg)
            flags = UndertaleSound.AudioEntryFlags.IsEmbedded | UndertaleSound.AudioEntryFlags.Regular;
        else // isOgg && !embedSound
            flags = UndertaleSound.AudioEntryFlags.Regular;

        // --- final audio group reference (source lines 487-495) ---
        UndertaleAudioGroup? finalGroupReference = !usesAGRP ? null : (needAGRP ? data.AudioGroups[audioGroupID] : data.AudioGroups[builtin]);

        int finalGroupID = needAGRP ? audioGroupID : builtin;
        string typeStr = isOgg ? ".ogg" : ".wav";
        string fileStr = name + typeStr;

        // --- create or update the sound asset ---
        UndertaleSound sound = existing;
        if (create)
        {
            sound = new UndertaleSound { Name = data.Strings.MakeString(name) };
            data.Sounds.Add(sound);
        }

        if (metadataOnly && !create)
        {
            // Properties only; audio untouched (source's third branch).
            sound.Flags = flags;
            sound.Type = data.Strings.MakeString(typeStr);
            sound.File = data.Strings.MakeString(fileStr);
            sound.Effects = json.Effects;
            sound.Volume = json.Volume;
            sound.Pitch = json.Pitch;
            sound.GroupID = finalGroupID;
            sound.AudioGroup = finalGroupReference;
            // AudioFile / AudioID intentionally left as-is.
            return;
        }

        sound.Flags = flags;
        sound.Type = data.Strings.MakeString(typeStr);
        sound.File = data.Strings.MakeString(fileStr);
        sound.Effects = json.Effects;
        sound.Volume = json.Volume;
        sound.Pitch = json.Pitch;
        sound.AudioID = audioId;
        sound.AudioFile = finalAudioReference;
        sound.AudioGroup = finalGroupReference;
        sound.GroupID = finalGroupID;
    }

    // Container names the group; its index in Data.AudioGroups is the GroupID.
    // Container "data.win" (or any non-group name) => builtin (embed in data.win).
    private static int ResolveGroupIDFromContainer(UndertaleData data, ModAddress addr, int builtin)
    {
        string container = addr.Container;
        if (string.IsNullOrEmpty(container) || data.AudioGroups is null || data.AudioGroups.Count == 0)
            return builtin;

        for (int i = 0; i < data.AudioGroups.Count; i++)
        {
            if (data.AudioGroups[i]?.Name?.Content == container)
                return i;
        }
        return builtin;
    }

    private static SoundJson ReadJson(string file)
    {
        try
        {
            SoundJson json = JsonSerializer.Deserialize<SoundJson>(
                File.ReadAllText(file),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (json is null)
                throw new InvalidOperationException($"Sound JSON '{file}' parsed to null.");
            return json;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Sound JSON '{file}' is invalid: {ex.Message}", ex);
        }
    }
}