using System.Text.Json;
using System.Text.Json.Serialization;
using UndertaleModLib;
using UndertaleModLib.Models;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;
using PjumModLib;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Rooms;

public sealed class RoomImporter(UndertaleData data)
{
    private readonly Dictionary<string, string> _changedBy = new();
    private readonly Dictionary<string, List<string>> _overridden = new();
    private readonly Dictionary<string, string> _removedBy = new();

    public List<string> Warnings { get; } = new();

    public IEnumerable<ConflictLogEntry> Conflicts =>
        _overridden.Select(kv => new ConflictLogEntry(kv.Key, _changedBy[kv.Key], kv.Value));

    public static RoomJson Read(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<RoomJson>(File.ReadAllText(file), RoomJson.JsonOptions)
                   ?? throw new InvalidOperationException($"Room JSON '{file}' parsed to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Room JSON '{file}' is invalid: {ex.Message}", ex);
        }
    }

    public static void Validate(UndertaleData data, ModAddress addr, RoomJson json, ISet<string> newObjects, ISet<string> newBackgrounds, ISet<string> newCode)
    {
        UndertaleRoom room = data.Rooms.ByName(addr.AssetName)
                             ?? throw new InvalidOperationException($"Room '{addr.AssetName}' doesn't exist; PJUM can't create rooms yet.");

        void CheckObject(string? name, string where)
        {
            if (name is not null && data.GameObjects.ByName(name) is null && !newObjects.Contains(name))
                throw new InvalidOperationException($"{where} uses object '{name}', which doesn't exist and no mod creates it.");
        }

        void CheckId(uint id, string where)
        {
            if (!room.GameObjects.Any(g => g.InstanceID == id))
                throw new InvalidOperationException($"{where}: room '{room.Name.Content}' has no instance {id}.");
        }

        void CheckCode(RoomInstanceJson instance, string where)
        {
            foreach (string? code in new[] { instance.CreationCode, instance.PreCreateCode })
                if (!string.IsNullOrEmpty(code) && data.Code.ByName(code) is null && !newCode.Contains(code))
                    throw new InvalidOperationException($"{where} uses code '{code}', which doesn't exist and isn't one of the mods' code files.");
        }

        bool layered = room.Layers is { Count: > 0 };
        foreach (RoomInstanceJson add in json.Add)
        {
            CheckCode(add, $"An added instance in room '{room.Name.Content}'");
            if (add.Id is not null)
                throw new InvalidOperationException($"An added instance in room '{room.Name.Content}' has \"id\": {add.Id}. Added instances get their id from the merge; remove \"id\", or move it to \"change\".");
            if (string.IsNullOrEmpty(add.Object))
                throw new InvalidOperationException($"An added instance in room '{room.Name.Content}' has no \"object\".");
            CheckObject(add.Object, $"An added instance in room '{room.Name.Content}'");
            if (add.Layer is not null && (!layered || FindLayer(room, add.Layer) is null))
                throw new InvalidOperationException($"Room '{room.Name.Content}' has no instance layer named '{add.Layer}'.");
        }

        foreach (RoomInstanceJson change in json.Change)
        {
            if (change.Id is not uint id)
                throw new InvalidOperationException($"A changed instance in room '{room.Name.Content}' has no \"id\".");
            CheckId(id, "\"change\"");
            CheckObject(change.Object, $"Instance {id} in room '{room.Name.Content}'");
            CheckCode(change, $"Instance {id} in room '{room.Name.Content}'");
            if (change.Layer is not null)
                throw new InvalidOperationException($"Instance {id} in room '{room.Name.Content}': moving an instance to another layer isn't supported yet.");
        }

        foreach (uint id in json.Remove)
            CheckId(id, "\"remove\"");

        void CheckBackground(string? name, string where)
        {
            if (name is not null && data.Backgrounds.ByName(name) is null && !newBackgrounds.Contains(name))
                throw new InvalidOperationException($"{where} uses background '{name}', which doesn't exist and no mod creates it.");
        }

        foreach (RoomBackgroundJson slot in json.Backgrounds ?? [])
        {
            if (slot.Slot < 0 || slot.Slot >= room.Backgrounds.Count)
                throw new InvalidOperationException($"Room '{room.Name.Content}' has background slots 0 to {room.Backgrounds.Count - 1}, not {slot.Slot}.");
            CheckBackground(slot.Background, $"Background slot {slot.Slot} in room '{room.Name.Content}'");
        }
        foreach (RoomViewJson view in json.Views ?? [])
        {
            if (view.Slot < 0 || view.Slot >= room.Views.Count)
                throw new InvalidOperationException($"Room '{room.Name.Content}' has views 0 to {room.Views.Count - 1}, not {view.Slot}.");
            CheckObject(view.Follow, $"View {view.Slot} in room '{room.Name.Content}'");
        }

        if (json.Tiles is RoomTilesJson tiles)
        {
            if (layered)
                throw new InvalidOperationException($"Room '{room.Name.Content}' keeps its tiles in layers; PJUM can't change tile layers yet.");
            void CheckTile(uint id, string where)
            {
                if (!room.Tiles.Any(t => t.InstanceID == id))
                    throw new InvalidOperationException($"{where}: room '{room.Name.Content}' has no tile {id}.");
            }
            foreach (RoomTileJson add in tiles.Add)
            {
                if (add.Id is not null)
                    throw new InvalidOperationException($"An added tile in room '{room.Name.Content}' has \"id\": {add.Id}. Added tiles get their id from the merge; remove \"id\", or move it to \"change\".");
                if (string.IsNullOrEmpty(add.Background))
                    throw new InvalidOperationException($"An added tile in room '{room.Name.Content}' has no \"background\".");
                CheckBackground(add.Background, $"An added tile in room '{room.Name.Content}'");
            }
            foreach (RoomTileJson change in tiles.Change)
            {
                if (change.Id is not uint id)
                    throw new InvalidOperationException($"A changed tile in room '{room.Name.Content}' has no \"id\".");
                CheckTile(id, "\"tiles.change\"");
                CheckBackground(change.Background, $"Tile {id} in room '{room.Name.Content}'");
            }
            foreach (uint id in tiles.Remove)
                CheckTile(id, "\"tiles.remove\"");
        }
    }

    public void Apply(ModAddress addr, RoomJson json, string modName)
    {
        UndertaleRoom room = data.Rooms.ByName(addr.AssetName)
                             ?? throw new InvalidOperationException($"Room '{addr.AssetName}' doesn't exist; PJUM can't create rooms yet.");
        string roomKey = $"{addr.Game}/{addr.Container}/rooms:{room.Name.Content}";
        ApplyRoomParts(room, roomKey, json, modName);

        foreach (uint id in json.Remove)
        {
            string key = $"{roomKey}#{id}";
            UndertaleRoom.GameObject? instance = room.GameObjects.FirstOrDefault(g => g.InstanceID == id);
            if (instance is null)
            {
                if (_removedBy.TryGetValue(key, out string? by) && by != modName)
                    Warnings.Add($"[{modName}] removes instance {id} in room '{room.Name.Content}', but '{by}' already removed it.");
                continue;
            }
            RemoveInstance(room, instance);
            _removedBy[key] = modName;
        }

        foreach (RoomInstanceJson change in json.Change)
        {
            uint id = change.Id!.Value;
            string key = $"{roomKey}#{id}";
            UndertaleRoom.GameObject? instance = room.GameObjects.FirstOrDefault(g => g.InstanceID == id);
            if (instance is null)
            {
                string by = _removedBy.GetValueOrDefault(key, modName);
                Warnings.Add($"[{modName}] changes instance {id} in room '{room.Name.Content}', but '{by}' removed it; skipped.");
                continue;
            }

            if (change.Object is not null) { instance.ObjectDefinition = Object(change.Object); Track(key + ".object", modName); }
            if (change.X is int x) { instance.X = x; Track(key + ".x", modName); }
            if (change.Y is int y) { instance.Y = y; Track(key + ".y", modName); }
            if (change.ScaleX is float scaleX) { instance.ScaleX = scaleX; Track(key + ".scaleX", modName); }
            if (change.ScaleY is float scaleY) { instance.ScaleY = scaleY; Track(key + ".scaleY", modName); }
            if (change.Rotation is float rotation) { instance.Rotation = rotation; Track(key + ".rotation", modName); }
            if (change.Color is uint color) { instance.Color = color; Track(key + ".color", modName); }
            if (change.ImageIndex is int imageIndex) { instance.ImageIndex = imageIndex; Track(key + ".imageIndex", modName); }
            if (change.ImageSpeed is float imageSpeed) { instance.ImageSpeed = imageSpeed; Track(key + ".imageSpeed", modName); }
            if (change.CreationCode is string creationCode) { instance.CreationCode = Code(creationCode); Track(key + ".creationCode", modName); }
            if (change.PreCreateCode is string preCreateCode) { instance.PreCreateCode = Code(preCreateCode); Track(key + ".preCreateCode", modName); }
        }

        foreach (RoomInstanceJson add in json.Add)
        {
            UndertaleGameObject obj = Object(add.Object!);
            var instance = new UndertaleRoom.GameObject
            {
                InstanceID = data.GeneralInfo.LastObj++,
                ObjectDefinition = obj,
                X = add.X ?? 0,
                Y = add.Y ?? 0,
                ScaleX = add.ScaleX ?? 1,
                ScaleY = add.ScaleY ?? 1,
                Rotation = add.Rotation ?? 0,
                Color = add.Color ?? 0xFFFFFFFF,
                ImageIndex = add.ImageIndex ?? 0,
                ImageSpeed = add.ImageSpeed ?? 0,
                CreationCode = Code(add.CreationCode),
                PreCreateCode = Code(add.PreCreateCode)
            };
            room.GameObjects.Add(instance);
            room.InstanceCreationOrderIDs?.InstanceIDs.Add(instance.InstanceID);

            if (room.Layers is { Count: > 0 })
            {
                UndertaleRoom.Layer layer = (add.Layer is null ? null : FindLayer(room, add.Layer))
                                            ?? RoomLayers.InstanceLayers(room).FirstOrDefault(l => l.LayerDepth == obj.Depth)
                                            ?? RoomLayers.InstanceLayers(room).FirstOrDefault()
                                            ?? throw new InvalidOperationException($"Room '{room.Name.Content}' has no instance layer to add '{obj.Name.Content}' to.");
                layer.InstancesData.Instances.Add(instance);
            }
        }
    }

    private void ApplyRoomParts(UndertaleRoom room, string roomKey, RoomJson json, string modName)
    {
        if (json.Settings is RoomSettingsJson settings)
        {
            if (settings.Width is uint width) { room.Width = width; Track(roomKey + ".width", modName); }
            if (settings.Height is uint height) { room.Height = height; Track(roomKey + ".height", modName); }
            if (settings.Speed is uint speed) { room.Speed = speed; Track(roomKey + ".speed", modName); }
            if (settings.Persistent is bool persistent) { room.Persistent = persistent; Track(roomKey + ".persistent", modName); }
            if (settings.BackgroundColor is uint color) { room.BackgroundColor = color; Track(roomKey + ".backgroundColor", modName); }
            if (settings.DrawBackgroundColor is bool draw) { room.DrawBackgroundColor = draw; Track(roomKey + ".drawBackgroundColor", modName); }
            if (settings.Caption is string caption) { room.Caption = data.Strings.MakeString(caption); Track(roomKey + ".caption", modName); }
        }

        foreach (RoomBackgroundJson slot in json.Backgrounds ?? [])
        {
            UndertaleRoom.Background bg = room.Backgrounds[slot.Slot];
            bg.Enabled = slot.Enabled;
            bg.BackgroundDefinition = slot.Background is null ? null : data.Backgrounds.ByName(slot.Background)
                                      ?? throw new InvalidOperationException($"Background '{slot.Background}' doesn't exist.");
            bg.X = slot.X;
            bg.Y = slot.Y;
            bg.TiledHorizontally = slot.TileX;
            bg.TiledVertically = slot.TileY;
            bg.SpeedX = slot.SpeedX;
            bg.SpeedY = slot.SpeedY;
            bg.Foreground = slot.Foreground;
            bg.Stretch = slot.Stretch;
            Track($"{roomKey}.background{slot.Slot}", modName);
        }

        foreach (RoomViewJson view in json.Views ?? [])
        {
            UndertaleRoom.View v = room.Views[view.Slot];
            v.Enabled = view.Enabled;
            v.ViewX = view.ViewX;
            v.ViewY = view.ViewY;
            v.ViewWidth = view.ViewWidth;
            v.ViewHeight = view.ViewHeight;
            v.PortX = view.PortX;
            v.PortY = view.PortY;
            v.PortWidth = view.PortWidth;
            v.PortHeight = view.PortHeight;
            v.BorderX = view.BorderX;
            v.BorderY = view.BorderY;
            v.SpeedX = view.SpeedX;
            v.SpeedY = view.SpeedY;
            v.ObjectId = view.Follow is null ? null : Object(view.Follow);
            Track($"{roomKey}.view{view.Slot}", modName);
        }

        if (json.Tiles is not RoomTilesJson tiles)
            return;

        foreach (uint id in tiles.Remove)
        {
            string key = $"{roomKey}#tile{id}";
            UndertaleRoom.Tile? tile = room.Tiles.FirstOrDefault(t => t.InstanceID == id);
            if (tile is null)
            {
                if (_removedBy.TryGetValue(key, out string? by) && by != modName)
                    Warnings.Add($"[{modName}] removes tile {id} in room '{room.Name.Content}', but '{by}' already removed it.");
                continue;
            }
            room.Tiles.Remove(tile);
            _removedBy[key] = modName;
        }

        foreach (RoomTileJson change in tiles.Change)
        {
            uint id = change.Id!.Value;
            string key = $"{roomKey}#tile{id}";
            UndertaleRoom.Tile? tile = room.Tiles.FirstOrDefault(t => t.InstanceID == id);
            if (tile is null)
            {
                string by = _removedBy.GetValueOrDefault(key, modName);
                Warnings.Add($"[{modName}] changes tile {id} in room '{room.Name.Content}', but '{by}' removed it; skipped.");
                continue;
            }
            if (change.Background is not null) { tile.BackgroundDefinition = Background(change.Background); Track(key + ".background", modName); }
            if (change.X is int x) { tile.X = x; Track(key + ".x", modName); }
            if (change.Y is int y) { tile.Y = y; Track(key + ".y", modName); }
            if (change.SourceX is int sourceX) { tile.SourceX = sourceX; Track(key + ".sourceX", modName); }
            if (change.SourceY is int sourceY) { tile.SourceY = sourceY; Track(key + ".sourceY", modName); }
            if (change.Width is uint width) { tile.Width = width; Track(key + ".width", modName); }
            if (change.Height is uint height) { tile.Height = height; Track(key + ".height", modName); }
            if (change.Depth is int depth) { tile.TileDepth = depth; Track(key + ".depth", modName); }
            if (change.ScaleX is float scaleX) { tile.ScaleX = scaleX; Track(key + ".scaleX", modName); }
            if (change.ScaleY is float scaleY) { tile.ScaleY = scaleY; Track(key + ".scaleY", modName); }
            if (change.Color is uint color) { tile.Color = color; Track(key + ".color", modName); }
        }

        foreach (RoomTileJson add in tiles.Add)
        {
            room.Tiles.Add(new UndertaleRoom.Tile
            {
                InstanceID = data.GeneralInfo.LastTile++,
                BackgroundDefinition = Background(add.Background!),
                X = add.X ?? 0,
                Y = add.Y ?? 0,
                SourceX = add.SourceX ?? 0,
                SourceY = add.SourceY ?? 0,
                Width = add.Width ?? 0,
                Height = add.Height ?? 0,
                TileDepth = add.Depth ?? 0,
                ScaleX = add.ScaleX ?? 1,
                ScaleY = add.ScaleY ?? 1,
                Color = add.Color ?? 0xFFFFFFFF
            });
        }
    }

    private UndertaleCode? Code(string? name) =>
        string.IsNullOrEmpty(name) ? null : data.Code.ByName(name) ?? throw new InvalidOperationException($"Code '{name}' doesn't exist.");

    private UndertaleBackground Background(string name) =>
        data.Backgrounds.ByName(name) ?? throw new InvalidOperationException($"Background '{name}' doesn't exist.");

    private static UndertaleRoom.Layer? FindLayer(UndertaleRoom room, string name) =>
        RoomLayers.InstanceLayers(room).FirstOrDefault(l => l.LayerName?.Content == name);

    private UndertaleGameObject Object(string name) =>
        data.GameObjects.ByName(name) ?? throw new InvalidOperationException($"Object '{name}' doesn't exist.");

    private void Track(string key, string modName)
    {
        if (_changedBy.TryGetValue(key, out string? previous) && previous != modName)
        {
            if (!_overridden.TryGetValue(key, out List<string>? losers))
                _overridden[key] = losers = new List<string>();
            if (!losers.Contains(previous))
                losers.Add(previous);
        }
        _changedBy[key] = modName;
    }

    private static void RemoveInstance(UndertaleRoom room, UndertaleRoom.GameObject instance)
    {
        room.GameObjects.Remove(instance);
        room.InstanceCreationOrderIDs?.InstanceIDs.Remove(instance.InstanceID);
        foreach (UndertaleRoom.Layer layer in RoomLayers.InstanceLayers(room))
            layer.InstancesData.Instances.Remove(instance);
    }
}
