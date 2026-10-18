using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModLib.Util;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Data;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Objects;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Paths;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Sounds;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.ModExtract;

public class ModExtractService(IDataService dataService) : IModExtractService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ExtractResult> ExtractAsync(string originalDataFile, string moddedDataFile, string modDirectory, string modName)
    {
        UndertaleData original = await dataService.LoadAsync(originalDataFile);
        UndertaleData modded = await dataService.LoadAsync(moddedDataFile);

        ModTarget target = ModTarget.FromData(modded)
                           ?? throw new InvalidDataException("The modded data file has no game name, so PJUM can't tell which game it is for.");
        if (ModTarget.FromData(original) is ModTarget originalTarget && originalTarget != target)
            throw new InvalidDataException($"The modded data file is for {target}, but the original is for {originalTarget}.");

        string originalFolder = Path.GetDirectoryName(Path.GetFullPath(originalDataFile))!;
        string moddedFolder = Path.GetDirectoryName(Path.GetFullPath(moddedDataFile))!;
        return await Task.Run(() => new Extraction(original, modded, target, modDirectory, modName, originalFolder, moddedFolder).Run());
    }

    private sealed class Extraction(UndertaleData original, UndertaleData modded, ModTarget target, string modDirectory, string modName, string originalFolder, string moddedFolder)
    {
        private readonly string _root = target.Chapter is null
            ? Path.Combine(modDirectory, target.Game, "data.win")
            : Path.Combine(modDirectory, target.Game, target.Chapter, "data.win");
        private readonly Dictionary<string, int> _written = new();
        private readonly List<string> _notConverted = [];
        private readonly Dictionary<string, Dictionary<string, JsonObject>> _newAssets = new();

        public ExtractResult Run()
        {
            Directory.CreateDirectory(modDirectory);
            using (var originalTextures = new TextureWorker())
            using (var moddedTextures = new TextureWorker())
            {
                Code();
                Sprites(originalTextures, moddedTextures);
                Backgrounds(originalTextures, moddedTextures);
                Fonts(originalTextures, moddedTextures);
            }
            Objects();
            Paths();
            Sounds();
            Rooms();
            OtherResources();
            WriteManifest();
            return new ExtractResult(modDirectory, _written, _notConverted);
        }

        private void Count(string category) => _written[category] = _written.GetValueOrDefault(category) + 1;

        private void DeclareNew(string category, string name, JsonObject? properties = null)
        {
            if (!_newAssets.TryGetValue(category, out var byName))
                _newAssets[category] = byName = new Dictionary<string, JsonObject>();
            byName[name] = properties ?? new JsonObject();
        }

        private string FilePath(string category, string fileName)
        {
            string path = Path.Combine(_root, category, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }

        private static void Removed<T>(IEnumerable<T> originals, Func<string, bool> stillThere, string kind, List<string> notes) where T : UndertaleNamedResource
        {
            foreach (T item in originals)
                if (item?.Name?.Content is string name && !stillThere(name))
                    notes.Add($"{kind} '{name}' was removed in the modded file; PJUM mods can't remove assets.");
        }

        private void Code()
        {
            var decompileContext = new GlobalDecompileContext(modded);
            HashSet<UndertaleCode> used = UsedCode(modded);
            foreach (UndertaleCode code in modded.Code)
            {
                if (code?.Name?.Content is not string name || code.ParentEntry is not null)
                    continue;

                UndertaleCode? before = original.Code.ByName(name);
                string moddedText = Disassemble(modded, code);
                if (before is not null && Disassemble(original, before) == moddedText)
                    continue;
                if (!used.Contains(code))
                {
                    _notConverted.Add($"Code '{name}' isn't used by any object, script, room or timeline in the modded file, so it was skipped.");
                    continue;
                }

                string gml;
                try
                {
                    gml = new Underanalyzer.Decompiler.DecompileContext(decompileContext, code, modded.ToolInfo.DecompilerSettings).DecompileToString();
                }
                catch (Exception ex)
                {
                    _notConverted.Add($"Code '{name}' changed but could not be decompiled: {ex.Message}");
                    continue;
                }

                File.WriteAllText(FilePath("code", name + ".gml"), gml);
                Count("code");
                if (before is null)
                    DeclareNew("code", name);
            }
            Removed(original.Code.Where(c => c?.ParentEntry is null), n => modded.Code.ByName(n) is not null, "Code", _notConverted);
        }

        private static HashSet<UndertaleCode> UsedCode(UndertaleData data)
        {
            var used = new HashSet<UndertaleCode>();
            void Add(UndertaleCode? code)
            {
                if (code is not null)
                    used.Add(code.ParentEntry ?? code);
            }
            foreach (UndertaleGameObject obj in data.GameObjects)
                foreach (var events in obj?.Events ?? [])
                    foreach (UndertaleGameObject.Event ev in events)
                        foreach (UndertaleGameObject.EventAction action in ev.Actions)
                            Add(action.CodeId);
            foreach (UndertaleScript script in data.Scripts)
                Add(script?.Code);
            foreach (UndertaleRoom room in data.Rooms)
            {
                Add(room?.CreationCodeId);
                foreach (UndertaleRoom.GameObject instance in room?.GameObjects ?? [])
                {
                    Add(instance.CreationCode);
                    Add(instance.PreCreateCode);
                }
            }
            foreach (UndertaleGlobalInit init in data.GlobalInitScripts ?? [])
                Add(init?.Code);
            foreach (UndertaleGlobalInit end in data.GameEndScripts ?? [])
                Add(end?.Code);
            foreach (UndertaleTimeline timeline in data.Timelines ?? [])
                foreach (UndertaleTimeline.UndertaleTimelineMoment moment in timeline?.Moments ?? [])
                    foreach (UndertaleGameObject.EventAction action in moment.Event)
                        Add(action.CodeId);
            return used;
        }

        private static string Disassemble(UndertaleData data, UndertaleCode code)
        {
            try
            {
                return code.Disassemble(data.Variables, data.CodeLocals?.ByName(code.Name.Content), true);
            }
            catch (Exception ex)
            {
                return "could not disassemble: " + ex.Message;
            }
        }

        private static byte[]? Pixels(TextureWorker worker, UndertaleTexturePageItem? item)
        {
            if (item?.TexturePage is null)
                return null;
            using IMagickImage<byte> image = worker.GetTextureFor(item, "compare");
            return [.. BitConverter.GetBytes(image.Width), .. BitConverter.GetBytes(image.Height), .. image.GetPixels().ToByteArray(PixelMapping.RGBA)!];
        }

        private static bool SamePixels(byte[]? a, byte[]? b) => a is not null && b is not null && a.AsSpan().SequenceEqual(b);

        private void Sprites(TextureWorker originalTextures, TextureWorker moddedTextures)
        {
            foreach (UndertaleSprite sprite in modded.Sprites)
            {
                if (sprite?.Name?.Content is not string name)
                    continue;
                UndertaleSprite? before = original.Sprites.ByName(name);

                for (int i = 0; i < sprite.Textures.Count; i++)
                {
                    UndertaleTexturePageItem? item = sprite.Textures[i]?.Texture;
                    if (item is null)
                        continue;
                    UndertaleTexturePageItem? beforeItem = before is not null && i < before.Textures.Count ? before.Textures[i]?.Texture : null;
                    if (beforeItem is not null && SamePixels(Pixels(originalTextures, beforeItem), Pixels(moddedTextures, item)))
                        continue;
                    moddedTextures.ExportAsPNG(item, FilePath("sprites", Path.Combine(name, i + ".png")));
                    Count("sprite frames");
                }

                if (before is null)
                {
                    DeclareNew("sprites", name, new JsonObject { ["frames"] = sprite.Textures.Count });
                    if (sprite.OriginX != 0 || sprite.OriginY != 0)
                        _notConverted.Add($"New sprite '{name}' has origin ({sprite.OriginX}, {sprite.OriginY}); PJUM creates new sprites with origin (0, 0).");
                    continue;
                }
                if (sprite.Textures.Count < before.Textures.Count)
                    _notConverted.Add($"Sprite '{name}' has fewer frames ({sprite.Textures.Count} instead of {before.Textures.Count}); PJUM mods can't remove frames.");
                if (sprite.OriginX != before.OriginX || sprite.OriginY != before.OriginY || sprite.Width != before.Width || sprite.Height != before.Height
                    || sprite.MarginLeft != before.MarginLeft || sprite.MarginRight != before.MarginRight || sprite.MarginTop != before.MarginTop || sprite.MarginBottom != before.MarginBottom)
                    _notConverted.Add($"Sprite '{name}' has changed size, origin or collision box; PJUM mods only carry the frame images.");
            }
            Removed(original.Sprites, n => modded.Sprites.ByName(n) is not null, "Sprite", _notConverted);
        }

        private void Backgrounds(TextureWorker originalTextures, TextureWorker moddedTextures)
        {
            foreach (UndertaleBackground background in modded.Backgrounds)
            {
                if (background?.Name?.Content is not string name || background.Texture is null)
                    continue;
                UndertaleBackground? before = original.Backgrounds.ByName(name);
                if (before is null)
                {
                    _notConverted.Add($"Background '{name}' is new; PJUM can't create backgrounds yet.");
                    continue;
                }
                if (SamePixels(Pixels(originalTextures, before.Texture), Pixels(moddedTextures, background.Texture)))
                    continue;
                moddedTextures.ExportAsPNG(background.Texture, FilePath("backgrounds", name + ".png"));
                Count("backgrounds");
            }
        }

        private void Fonts(TextureWorker originalTextures, TextureWorker moddedTextures)
        {
            foreach (UndertaleFont font in modded.Fonts)
            {
                if (font?.Name?.Content is not string name || font.Texture is null)
                    continue;
                UndertaleFont? before = original.Fonts.ByName(name);
                if (before is null)
                {
                    _notConverted.Add($"Font '{name}' is new; PJUM can't create fonts yet.");
                    continue;
                }
                if (!SamePixels(Pixels(originalTextures, before.Texture), Pixels(moddedTextures, font.Texture)))
                {
                    moddedTextures.ExportAsPNG(font.Texture, FilePath("fonts", name + ".png"));
                    Count("fonts");
                }
                bool sameGlyphs = font.Glyphs.Count == before.Glyphs.Count && font.Glyphs.Zip(before.Glyphs).All(g =>
                    g.First.Character == g.Second.Character && g.First.SourceX == g.Second.SourceX && g.First.SourceY == g.Second.SourceY
                    && g.First.SourceWidth == g.Second.SourceWidth && g.First.SourceHeight == g.Second.SourceHeight
                    && g.First.Shift == g.Second.Shift && g.First.Offset == g.Second.Offset);
                if (!sameGlyphs)
                    _notConverted.Add($"Font '{name}' has changed letters (glyphs); PJUM mods only carry the font picture.");
            }
        }

        private static string? NameOf(UndertaleNamedResource? resource) => resource?.Name?.Content;

        private static ObjectJson Describe(UndertaleData data, UndertaleGameObject obj, out List<string> eventKeys)
        {
            var json = new ObjectJson
            {
                Sprite = NameOf(obj.Sprite),
                Parent = NameOf(obj.ParentId),
                MaskSprite = NameOf(obj.TextureMaskId),
                Visible = obj.Visible,
                Solid = obj.Solid,
                Persistent = obj.Persistent,
                Depth = obj.Depth,
                Physics = new PhysicsJson
                {
                    Uses = obj.UsesPhysics,
                    Sensor = obj.IsSensor,
                    Shape = obj.CollisionShape.ToString(),
                    Density = obj.Density,
                    Restitution = obj.Restitution,
                    Group = obj.Group,
                    LinearDamping = obj.LinearDamping,
                    AngularDamping = obj.AngularDamping,
                    Friction = obj.Friction,
                    Awake = obj.Awake,
                    Kinematic = obj.Kinematic
                }
            };
            eventKeys = [];
            for (int type = 0; type < obj.Events.Count; type++)
            {
                foreach (UndertaleGameObject.Event ev in obj.Events[type])
                {
                    JsonElement subtype = (EventType)type == EventType.Collision && ev.EventSubtype < data.GameObjects.Count && data.GameObjects[(int)ev.EventSubtype]?.Name?.Content is string other
                        ? JsonSerializer.SerializeToElement(other)
                        : JsonSerializer.SerializeToElement(ev.EventSubtype);
                    string? code = ev.Actions.FirstOrDefault()?.CodeId?.Name?.Content;
                    json.Events.Add(new ObjectEventJson { Type = ((EventType)type).ToString(), Subtype = subtype, Code = code });
                    eventKeys.Add($"{(EventType)type}:{subtype.GetRawText()}");
                }
            }
            return json;
        }

        private void Objects()
        {
            foreach (UndertaleGameObject obj in modded.GameObjects)
            {
                if (obj?.Name?.Content is not string name)
                    continue;
                UndertaleGameObject? before = original.GameObjects.ByName(name);
                string text = JsonSerializer.Serialize(Describe(modded, obj, out List<string> moddedEvents), JsonOptions);

                if (before is not null)
                {
                    string beforeText = JsonSerializer.Serialize(Describe(original, before, out List<string> originalEvents), JsonOptions);
                    if (beforeText == text)
                        continue;
                    foreach (string gone in originalEvents.Except(moddedEvents))
                        _notConverted.Add($"Object '{name}' lost its {gone} event; PJUM mods can't remove events.");
                    if (before.Sprite is not null && obj.Sprite is null)
                        _notConverted.Add($"Object '{name}' no longer has a sprite; PJUM mods can't clear an object's sprite.");
                }
                else
                {
                    DeclareNew("objects", name);
                }

                File.WriteAllText(FilePath("objects", name + ".json"), text);
                Count("objects");
            }
            Removed(original.GameObjects, n => modded.GameObjects.ByName(n) is not null, "Object", _notConverted);
        }

        private void Paths()
        {
            foreach (UndertalePath path in modded.Paths)
            {
                if (path?.Name?.Content is not string name)
                    continue;
                var json = new PathJson
                {
                    Smooth = path.IsSmooth,
                    Closed = path.IsClosed,
                    Precision = path.Precision,
                    Points = path.Points.Select(p => new PathPointJson { X = p.X, Y = p.Y, Speed = p.Speed }).ToList()
                };
                string text = JsonSerializer.Serialize(json, JsonOptions);
                UndertalePath? before = original.Paths.ByName(name);
                if (before is not null)
                {
                    var beforeJson = new PathJson
                    {
                        Smooth = before.IsSmooth,
                        Closed = before.IsClosed,
                        Precision = before.Precision,
                        Points = before.Points.Select(p => new PathPointJson { X = p.X, Y = p.Y, Speed = p.Speed }).ToList()
                    };
                    if (JsonSerializer.Serialize(beforeJson, JsonOptions) == text)
                        continue;
                }
                else
                {
                    DeclareNew("paths", name);
                }
                File.WriteAllText(FilePath("paths", name + ".json"), text);
                Count("paths");
            }
        }

        private void Sounds()
        {
            int builtin = modded.GetBuiltinSoundGroupID();
            foreach (UndertaleSound sound in modded.Sounds)
            {
                if (sound?.Name?.Content is not string name)
                    continue;
                UndertaleSound? before = original.Sounds.ByName(name);
                if (IsStreamed(sound))
                {
                    StreamedSound(sound, before, name);
                    continue;
                }
                var json = new SoundJson
                {
                    Type = sound.Type?.Content,
                    Embedded = sound.Flags.HasFlag(UndertaleSound.AudioEntryFlags.IsEmbedded),
                    DecodeOnLoad = sound.Flags.HasFlag(UndertaleSound.AudioEntryFlags.IsDecompressedOnLoad),
                    Volume = sound.Volume,
                    Pitch = sound.Pitch,
                    Effects = sound.Effects
                };
                byte[]? audio = sound.AudioFile?.Data;
                bool inDataWin = audio is not null && (modded.AudioGroups is not { Count: > 0 } || sound.GroupID == builtin);

                if (before is not null)
                {
                    bool sameAudio = audio is null ? before.AudioFile is null : before.AudioFile?.Data is byte[] old && old.AsSpan().SequenceEqual(audio);
                    bool sameProperties = before.Type?.Content == json.Type && before.Flags == sound.Flags && before.Volume == sound.Volume
                                          && before.Pitch == sound.Pitch && before.Effects == sound.Effects;
                    if (sameAudio && sameProperties)
                        continue;
                }
                if (!inDataWin)
                {
                    _notConverted.Add($"Sound '{name}' changed, but its audio lives outside data.win (an audiogroup or a loose .ogg), so it wasn't converted.");
                    continue;
                }

                string extension = audio!.Length >= 4 && audio[0] == (byte)'O' && audio[1] == (byte)'g' && audio[2] == (byte)'g' && audio[3] == (byte)'S' ? ".ogg" : ".wav";
                json.Type ??= extension;
                File.WriteAllText(FilePath("sounds", name + ".json"), JsonSerializer.Serialize(json, JsonOptions));
                File.WriteAllBytes(FilePath("sounds", name + extension), audio);
                Count("sounds");
                if (before is null)
                    DeclareNew("sounds", name);
            }
        }

        private static bool IsStreamed(UndertaleSound sound) =>
            !sound.Flags.HasFlag(UndertaleSound.AudioEntryFlags.IsEmbedded) && !sound.Flags.HasFlag(UndertaleSound.AudioEntryFlags.IsCompressed);

        private void StreamedSound(UndertaleSound sound, UndertaleSound? before, string name)
        {
            string? file = sound.File?.Content;
            string? moddedFile = FindLooseFile(moddedFolder, file);
            if (moddedFile is null)
            {
                bool changed = before is null || before.File?.Content != file || before.Volume != sound.Volume || before.Pitch != sound.Pitch;
                if (changed)
                    _notConverted.Add($"Streamed sound '{name}' plays '{file}', which isn't in the modded game's folder, so it wasn't converted.");
                return;
            }

            string? originalFile = FindLooseFile(originalFolder, before?.File?.Content);
            bool sameAudio = originalFile is not null && SameAudio(File.ReadAllBytes(originalFile), File.ReadAllBytes(moddedFile));
            bool sameProperties = before is not null && IsStreamed(before) && before.Volume == sound.Volume && before.Pitch == sound.Pitch && before.Effects == sound.Effects;
            if (sameAudio && sameProperties)
                return;

            string extension = Path.GetExtension(moddedFile).ToLowerInvariant();
            if (string.IsNullOrEmpty(extension))
                extension = ".ogg";
            var json = new SoundJson { Type = extension, Embedded = false, Volume = sound.Volume, Pitch = sound.Pitch, Effects = sound.Effects };
            File.WriteAllText(FilePath("sounds", name + ".json"), JsonSerializer.Serialize(json, JsonOptions));
            File.Copy(moddedFile, FilePath("sounds", name + extension), overwrite: true);
            Count("streamed sounds");
            if (before is null)
                DeclareNew("sounds", name);
        }

        private readonly Dictionary<string, Dictionary<string, string>> _folderFiles = new();

        private string? FindLooseFile(string folder, string? file)
        {
            if (string.IsNullOrEmpty(file))
                return null;
            string exact = Path.Combine(folder, file);
            if (File.Exists(exact))
                return exact;
            if (file.IndexOfAny(['/', '\\']) >= 0 || !Directory.Exists(folder))
                return null;

            if (!_folderFiles.TryGetValue(folder, out Dictionary<string, string>? files))
            {
                files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in Directory.EnumerateFiles(folder))
                    files.TryAdd(Path.GetFileName(path), path);
                _folderFiles[folder] = files;
            }
            return files.GetValueOrDefault(file);
        }

        private static bool SameAudio(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            byte[]? x = WithoutOggSerials(a), y = WithoutOggSerials(b);
            return x is not null && y is not null ? x.AsSpan().SequenceEqual(y) : a.AsSpan().SequenceEqual(b);
        }

        private static byte[]? WithoutOggSerials(byte[] file)
        {
            byte[] copy = (byte[])file.Clone();
            int at = 0;
            while (at < copy.Length)
            {
                if (at + 27 > copy.Length || copy[at] != 'O' || copy[at + 1] != 'g' || copy[at + 2] != 'g' || copy[at + 3] != 'S')
                    return null;
                int segments = copy[at + 26];
                if (at + 27 + segments > copy.Length)
                    return null;
                int body = 0;
                for (int i = 0; i < segments; i++)
                    body += copy[at + 27 + i];
                Array.Clear(copy, at + 14, 4);
                Array.Clear(copy, at + 22, 4);
                at += 27 + segments + body;
            }
            return copy;
        }

        private static string RoomFingerprint(UndertaleRoom room)
        {
            var parts = new List<string> { $"{room.Width}x{room.Height}", $"speed {room.Speed}", $"code {NameOf(room.CreationCodeId)}", $"layers {room.Layers?.Count ?? 0}", $"tiles {room.Tiles?.Count ?? 0}" };
            foreach (var instance in room.GameObjects)
                parts.Add($"{NameOf(instance.ObjectDefinition)}@{instance.X},{instance.Y},{instance.ScaleX},{instance.ScaleY},{instance.Rotation},{NameOf(instance.CreationCode)}");
            foreach (var layer in room.Layers ?? [])
                parts.Add($"layer {layer.LayerName?.Content} {layer.LayerDepth} {layer.LayerType}");
            return string.Join("|", parts);
        }

        private void Rooms()
        {
            foreach (UndertaleRoom room in modded.Rooms)
            {
                if (room?.Name?.Content is not string name)
                    continue;
                UndertaleRoom? before = original.Rooms.ByName(name);
                if (before is null)
                    _notConverted.Add($"Room '{name}' is new; PJUM can't mod rooms yet.");
                else if (RoomFingerprint(before) != RoomFingerprint(room))
                    _notConverted.Add($"Room '{name}' changed; PJUM can't mod rooms yet.");
            }
            Removed(original.Rooms, n => modded.Rooms.ByName(n) is not null, "Room", _notConverted);
        }

        private void OtherResources()
        {
            void NewNames<T>(IList<T>? before, IList<T>? after, string kind) where T : UndertaleNamedResource
            {
                if (after is null)
                    return;
                var known = new HashSet<string>((before ?? []).Select(x => x?.Name?.Content ?? ""));
                foreach (T item in after)
                    if (item?.Name?.Content is string name && !known.Contains(name))
                        _notConverted.Add($"{kind} '{name}' is new; PJUM can't add {kind.ToLowerInvariant()}s.");
            }
            NewNames(original.Shaders, modded.Shaders, "Shader");
            NewNames(original.Timelines, modded.Timelines, "Timeline");
            NewNames(original.Extensions, modded.Extensions, "Extension");
            NewNames(original.Sequences, modded.Sequences, "Sequence");
        }

        private void WriteManifest()
        {
            var manifest = new JsonObject
            {
                ["name"] = modName,
                ["author"] = "",
                ["version"] = "1.0.0"
            };
            if (_newAssets.Count > 0)
            {
                var newAssets = new JsonObject();
                foreach ((string category, var byName) in _newAssets.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    var entries = new JsonObject();
                    foreach ((string name, JsonObject properties) in byName.OrderBy(p => p.Key, StringComparer.Ordinal))
                        entries[name] = properties;
                    newAssets[category] = entries;
                }
                manifest["newAssets"] = newAssets;
            }
            File.WriteAllText(Path.Combine(modDirectory, "mod.json"), manifest.ToJsonString(JsonOptions));
        }
    }
}
