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

    public static void Validate(UndertaleData data, ModAddress addr, RoomJson json, ISet<string> newObjects)
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

        bool layered = room.Layers is { Count: > 0 };
        foreach (RoomInstanceJson add in json.Add)
        {
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
            if (change.Layer is not null)
                throw new InvalidOperationException($"Instance {id} in room '{room.Name.Content}': moving an instance to another layer isn't supported yet.");
        }

        foreach (uint id in json.Remove)
            CheckId(id, "\"remove\"");
    }

    public void Apply(ModAddress addr, RoomJson json, string modName)
    {
        UndertaleRoom room = data.Rooms.ByName(addr.AssetName)
                             ?? throw new InvalidOperationException($"Room '{addr.AssetName}' doesn't exist; PJUM can't create rooms yet.");
        string roomKey = $"{addr.Game}/{addr.Container}/rooms:{room.Name.Content}";

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
                ImageSpeed = add.ImageSpeed ?? 0
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
