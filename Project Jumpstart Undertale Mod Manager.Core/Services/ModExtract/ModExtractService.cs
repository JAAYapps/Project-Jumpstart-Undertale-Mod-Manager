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
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Rooms;
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
            var movedObjects = new Dictionary<int, string>();
            for (int i = 0; i < modded.GameObjects.Count; i++)
                if (modded.GameObjects[i]?.Name?.Content is string objectName && original.GameObjects.ElementAtOrDefault(i)?.Name?.Content != objectName)
                    movedObjects[i] = objectName;
            HashSet<UndertaleCode> addedInstanceCode = AddedInstances().SelectMany(a => new[] { a.Instance.CreationCode, a.Instance.PreCreateCode })
                                                                        .OfType<UndertaleCode>().ToHashSet();
            foreach (UndertaleCode code in modded.Code)
            {
                if (code?.Name?.Content is not string name || code.ParentEntry is not null)
                    continue;

                UndertaleCode? before = original.Code.ByName(name);
                string moddedText = Disassemble(modded, code);
                if (before is not null && Disassemble(original, before) == moddedText)
                    continue;
                if (before is null && addedInstanceCode.Contains(code))
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

                gml = NameObjectNumbers(name, gml, movedObjects);
                File.WriteAllText(FilePath("code", name + ".gml"), gml);
                Count("code");
                if (before is null)
                    DeclareNew("code", name);
            }
            Removed(original.Code.Where(c => c?.ParentEntry is null), n => modded.Code.ByName(n) is not null, "Code", _notConverted);
        }

        private string NameObjectNumbers(string codeName, string gml, Dictionary<int, string> movedObjects)
        {
            if (movedObjects.Count == 0)
                return gml;
            string[] lines = gml.Split('\n');
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n];
                string trimmed = line.TrimEnd();
                int equals = trimmed.IndexOf(" = ", StringComparison.Ordinal);
                if (trimmed.EndsWith(';') && equals > 0)
                {
                    string left = trimmed[..equals].Trim();
                    string right = trimmed[(equals + 3)..^1];
                    if (left.Length > 0 && left.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '.') && !char.IsDigit(left[0])
                        && right.Length > 0 && right.All(char.IsDigit) && int.TryParse(right, out int value) && movedObjects.TryGetValue(value, out string? objectName))
                    {
                        lines[n] = trimmed[..(equals + 3)] + objectName + ";" + line[trimmed.Length..];
                        continue;
                    }
                }

                foreach (int value in NumbersOutsideStrings(line))
                    if (movedObjects.TryGetValue(value, out string? maybe))
                        _notConverted.Add($"Code '{codeName}' line {n + 1}: {value} may mean object '{maybe}', whose number is different in the modded file; check it.");
            }
            return string.Join('\n', lines);
        }

        private static IEnumerable<int> NumbersOutsideStrings(string line)
        {
            int i = 0;
            while (i < line.Length)
            {
                char c = line[i];
                if (c == '"' || c == '\'')
                {
                    int close = line.IndexOf(c, i + 1);
                    i = close < 0 ? line.Length : close + 1;
                    continue;
                }
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                    yield break;
                if (char.IsDigit(c))
                {
                    int start = i;
                    while (i < line.Length && char.IsDigit(line[i]))
                        i++;
                    bool partOfName = start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_' || line[start - 1] == '.' || line[start - 1] == '$');
                    bool decimalPart = i < line.Length && (line[i] == '.' || char.IsLetter(line[i]) || line[i] == '_');
                    if (!partOfName && !decimalPart && int.TryParse(line[start..i], out int value))
                        yield return value;
                    continue;
                }
                i++;
            }
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
            return [.. BitConverter.GetBytes(image.Width), .. BitConverter.GetBytes(image.Height),
                    .. BitConverter.GetBytes(item.TargetX), .. BitConverter.GetBytes(item.TargetY),
                    .. BitConverter.GetBytes(item.BoundingWidth), .. BitConverter.GetBytes(item.BoundingHeight),
                    .. image.GetPixels().ToByteArray(PixelMapping.RGBA)!];
        }

        private bool ExportFullFrame(TextureWorker worker, UndertaleTexturePageItem item, string path, string label)
        {
            IMagickImage<byte> image;
            try
            {
                image = worker.GetTextureFor(item, label, includePadding: true);
            }
            catch (InvalidDataException)
            {
                _notConverted.Add($"{label} is drawn bigger than its frame ({item.TargetWidth}x{item.TargetHeight} in {item.BoundingWidth}x{item.BoundingHeight}), so it wasn't converted.");
                return false;
            }
            using (image)
                TextureWorker.SaveImageToFile(image, path);
            return true;
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
                    if (ExportFullFrame(moddedTextures, item, FilePath("sprites", Path.Combine(name, i + ".png")), $"Sprite '{name}' frame {i}"))
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
                if (ExportFullFrame(moddedTextures, background.Texture, FilePath("backgrounds", name + ".png"), $"Background '{name}'"))
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
            var parts = new List<string>
            {
                $"{room.Width}x{room.Height} speed {room.Speed} persistent {room.Persistent} color {room.BackgroundColor} {room.DrawBackgroundColor} flags {room.Flags}",
                $"world {room.World} {room.Top},{room.Left},{room.Right},{room.Bottom} gravity {room.GravityX},{room.GravityY} {room.MetersPerPixel}",
                $"caption {room.Caption?.Content} code {NameOf(room.CreationCodeId)}"
            };
            foreach (var bg in room.Backgrounds)
                parts.Add($"bg {bg.Enabled} {bg.Foreground} {NameOf(bg.BackgroundDefinition)} {bg.X},{bg.Y} {bg.TiledHorizontally},{bg.TiledVertically} {bg.SpeedX},{bg.SpeedY} {bg.Stretch}");
            foreach (var view in room.Views)
                parts.Add($"view {view.Enabled} {view.ViewX},{view.ViewY},{view.ViewWidth},{view.ViewHeight} {view.PortX},{view.PortY},{view.PortWidth},{view.PortHeight} {view.BorderX},{view.BorderY} {view.SpeedX},{view.SpeedY} {NameOf(view.ObjectId)}");
            foreach (var tile in room.Tiles)
                parts.Add($"tile {tile.InstanceID} {NameOf(tile.ObjectDefinition)} {tile.X},{tile.Y} {tile.SourceX},{tile.SourceY} {tile.Width}x{tile.Height} {tile.TileDepth} {tile.ScaleX},{tile.ScaleY} {tile.Color}");
            foreach (var layer in room.Layers ?? [])
            {
                parts.Add($"layer {layer.LayerName?.Content} {layer.LayerId} {layer.LayerType} {layer.LayerDepth} {layer.XOffset},{layer.YOffset} {layer.HSpeed},{layer.VSpeed} {layer.IsVisible}");
                switch (layer.Data)
                {
                    case UndertaleRoom.Layer.LayerBackgroundData bg:
                        parts.Add($"  bg {NameOf(bg.Sprite)} {bg.Visible} {bg.Foreground} {bg.TiledHorizontally},{bg.TiledVertically} {bg.Stretch} {bg.Color} {bg.FirstFrame} {bg.AnimationSpeed}");
                        break;
                    case UndertaleRoom.Layer.LayerTilesData tiles:
                        parts.Add($"  tiles {NameOf(tiles.Background)} {tiles.TilesX}x{tiles.TilesY} {string.Join(",", (tiles.TileData ?? []).SelectMany(row => row ?? []))}");
                        break;
                    case UndertaleRoom.Layer.LayerAssetsData assets:
                        foreach (var tile in assets.LegacyTiles ?? [])
                            parts.Add($"  tile {NameOf(tile.ObjectDefinition)} {tile.X},{tile.Y} {tile.SourceX},{tile.SourceY} {tile.Width}x{tile.Height} {tile.TileDepth}");
                        foreach (var sprite in assets.Sprites ?? [])
                            parts.Add($"  sprite {sprite.Name?.Content} {NameOf(sprite.Sprite)} {sprite.X},{sprite.Y} {sprite.ScaleX},{sprite.ScaleY} {sprite.Color} {sprite.Rotation}");
                        break;
                }
            }
            return string.Join("|", parts);
        }

        private IEnumerable<(UndertaleRoom Room, UndertaleRoom.GameObject Instance)> AddedInstances()
        {
            foreach (UndertaleRoom room in modded.Rooms)
            {
                if (room?.Name?.Content is not string name || original.Rooms.ByName(name) is not UndertaleRoom before)
                    continue;
                var ids = before.GameObjects.Select(g => g.InstanceID).ToHashSet();
                foreach (UndertaleRoom.GameObject instance in room.GameObjects)
                    if (!ids.Contains(instance.InstanceID))
                        yield return (room, instance);
            }
        }

        private void Rooms()
        {
            foreach (UndertaleRoom room in modded.Rooms)
            {
                if (room?.Name?.Content is not string name)
                    continue;
                UndertaleRoom? before = original.Rooms.ByName(name);
                if (before is null)
                {
                    _notConverted.Add($"Room '{name}' is new; PJUM can't create rooms yet.");
                    continue;
                }
                if (RoomFingerprint(before) != RoomFingerprint(room))
                    _notConverted.Add($"Room '{name}' has changes besides its object instances (settings, tiles, backgrounds, views or layers); only the instances were converted.");

                RoomJson patch = RoomPatch(before, room, name);
                if (patch.Add.Count == 0 && patch.Change.Count == 0 && patch.Remove.Count == 0)
                    continue;
                File.WriteAllText(FilePath("rooms", name + ".json"), JsonSerializer.Serialize(patch, RoomImporter.JsonOptions));
                Count("rooms");
            }
            Removed(original.Rooms, n => modded.Rooms.ByName(n) is not null, "Room", _notConverted);
        }

        private RoomJson RoomPatch(UndertaleRoom before, UndertaleRoom room, string name)
        {
            var patch = new RoomJson();
            var beforeById = new Dictionary<uint, UndertaleRoom.GameObject>();
            foreach (UndertaleRoom.GameObject instance in before.GameObjects)
                beforeById.TryAdd(instance.InstanceID, instance);
            var afterIds = room.GameObjects.Select(g => g.InstanceID).ToHashSet();
            bool layered = room.Layers is { Count: > 0 };

            foreach (UndertaleRoom.GameObject now in room.GameObjects)
            {
                if (beforeById.TryGetValue(now.InstanceID, out UndertaleRoom.GameObject? was))
                {
                    if (NameOf(was.CreationCode) != NameOf(now.CreationCode) || NameOf(was.PreCreateCode) != NameOf(now.PreCreateCode))
                        _notConverted.Add($"Instance {now.InstanceID} in room '{name}' points at different creation code; that wasn't converted.");
                    if (layered && RoomImporter.LayerOf(before, was)?.LayerName?.Content != RoomImporter.LayerOf(room, now)?.LayerName?.Content)
                        _notConverted.Add($"Instance {now.InstanceID} in room '{name}' moved to another layer; that wasn't converted.");

                    var change = new RoomInstanceJson { Id = now.InstanceID };
                    bool changed = false;
                    if (NameOf(was.ObjectDefinition) != NameOf(now.ObjectDefinition) && now.ObjectDefinition is not null) { change.Object = now.ObjectDefinition.Name.Content; changed = true; }
                    if (was.X != now.X) { change.X = now.X; changed = true; }
                    if (was.Y != now.Y) { change.Y = now.Y; changed = true; }
                    if (was.ScaleX != now.ScaleX) { change.ScaleX = now.ScaleX; changed = true; }
                    if (was.ScaleY != now.ScaleY) { change.ScaleY = now.ScaleY; changed = true; }
                    if (was.Rotation != now.Rotation) { change.Rotation = now.Rotation; changed = true; }
                    if (was.Color != now.Color) { change.Color = now.Color; changed = true; }
                    if (was.ImageIndex != now.ImageIndex) { change.ImageIndex = now.ImageIndex; changed = true; }
                    if (was.ImageSpeed != now.ImageSpeed) { change.ImageSpeed = now.ImageSpeed; changed = true; }
                    if (changed)
                        patch.Change.Add(change);
                    continue;
                }

                if (now.ObjectDefinition?.Name?.Content is not string objectName)
                {
                    _notConverted.Add($"Added instance {now.InstanceID} in room '{name}' has no object, so it wasn't converted.");
                    continue;
                }
                if (now.CreationCode is not null || now.PreCreateCode is not null)
                {
                    _notConverted.Add($"Added instance {now.InstanceID} ({objectName}) in room '{name}' has its own creation code; PJUM can't add that yet, so the instance wasn't converted.");
                    continue;
                }
                patch.Add.Add(new RoomInstanceJson
                {
                    Object = objectName,
                    X = now.X,
                    Y = now.Y,
                    ScaleX = now.ScaleX != 1 ? now.ScaleX : null,
                    ScaleY = now.ScaleY != 1 ? now.ScaleY : null,
                    Rotation = now.Rotation != 0 ? now.Rotation : null,
                    Color = now.Color != 0xFFFFFFFF ? now.Color : null,
                    ImageIndex = now.ImageIndex != 0 ? now.ImageIndex : null,
                    ImageSpeed = now.ImageSpeed != 0 ? now.ImageSpeed : null,
                    Layer = layered ? RoomImporter.LayerOf(room, now)?.LayerName?.Content : null
                });
            }

            foreach (uint id in beforeById.Keys)
                if (!afterIds.Contains(id))
                    patch.Remove.Add(id);
            return patch;
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
