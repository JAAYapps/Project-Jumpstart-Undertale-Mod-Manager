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

    public static UndertaleRoom CreateEmptyRoom(UndertaleData data, string name)
    {
        UndertaleRoom room = RoomLayers.EmptyRoom(data, text => data.Strings.MakeString(text));
        room.Name = data.Strings.MakeString(name);
        data.Rooms.Add(room);
        data.GeneralInfo.RoomOrder.Add(new(room));
        return room;
    }

    public static void Validate(UndertaleData data, ModAddress addr, RoomJson json, ISet<string> newObjects, ISet<string> newBackgrounds, ISet<string> newSprites, ISet<string> newCode)
    {
        UndertaleRoom room = data.Rooms.ByName(addr.AssetName)
                             ?? throw new InvalidOperationException($"Room '{addr.AssetName}' doesn't exist; Room validation failed.");

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

        bool layered = data.IsGameMaker2() || room.Layers is { Count: > 0 };
        HashSet<string> newInstanceLayers = ValidateLayers(data, room, json, newBackgrounds, newSprites);
        var removedLayers = new HashSet<string>(json.RemoveLayers ?? []);
        foreach (RoomInstanceJson add in json.Add)
        {
            CheckCode(add, $"An added instance in room '{room.Name.Content}'");
            if (add.Id is not null)
                throw new InvalidOperationException($"An added instance in room '{room.Name.Content}' has \"id\": {add.Id}. Added instances get their id from the merge; remove \"id\", or move it to \"change\".");
            if (string.IsNullOrEmpty(add.Object))
                throw new InvalidOperationException($"An added instance in room '{room.Name.Content}' has no \"object\".");
            CheckObject(add.Object, $"An added instance in room '{room.Name.Content}'");
            if (add.After is uint after && after != 0)
                CheckId(after, "\"after\"");
            if (add.Layer is not null && (!layered || removedLayers.Contains(add.Layer) || FindLayer(room, add.Layer) is null) && !newInstanceLayers.Contains(add.Layer))
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
                if (add.After is uint after && after != 0)
                    CheckTile(after, "\"after\"");
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

    private static HashSet<string> ValidateLayers(UndertaleData data, UndertaleRoom room, RoomJson json, ISet<string> newBackgrounds, ISet<string> newSprites)
    {
        string roomName = room.Name.Content;
        var newInstanceLayers = new HashSet<string>();
        if (json.RemoveLayers is null && json.Layers is null)
            return newInstanceLayers;
        if (!data.IsGameMaker2())
            throw new InvalidOperationException($"Room '{roomName}' has \"layers\", but only GameMaker 2 games have room layers.");

        var removedLayers = new HashSet<string>(json.RemoveLayers ?? []);
        foreach (string name in removedLayers)
            if (RoomLayers.Named(room, name) is null)
                throw new InvalidOperationException($"\"removeLayers\": room '{roomName}' has no layer named '{name}'.");

        void CheckSprite(string? name, string where)
        {
            if (!string.IsNullOrEmpty(name) && data.Sprites.ByName(name) is null && !newSprites.Contains(name))
                throw new InvalidOperationException($"{where} uses sprite '{name}', which doesn't exist and no mod creates it.");
        }

        var listed = new HashSet<string>();
        foreach (RoomLayerJson layerJson in json.Layers ?? [])
        {
            if (string.IsNullOrEmpty(layerJson.Name))
                throw new InvalidOperationException($"A layer in room '{roomName}' has no \"name\".");
            string name = layerJson.Name;
            string where = $"Layer '{name}' in room '{roomName}'";
            string inLayer = $"layer '{name}' in room '{roomName}'";
            if (!listed.Add(name))
                throw new InvalidOperationException($"{where} is listed twice in \"layers\".");
            UndertaleRoom.Layer? existing = removedLayers.Contains(name) ? null : RoomLayers.Named(room, name);
            UndertaleRoom.LayerType type;
            if (layerJson.New == true)
            {
                if (existing is not null)
                    throw new InvalidOperationException($"{where} is marked \"new\", but the room already has a layer with that name.");
                if (!Enum.TryParse(layerJson.Type, true, out type) || !RoomLayers.CarriedTypes.Contains(type))
                    throw new InvalidOperationException($"{where} has \"type\": \"{layerJson.Type}\"; PJUM can create Instances, Tiles, Background, Assets and Path layers.");
                if (type == UndertaleRoom.LayerType.Instances)
                    newInstanceLayers.Add(name);
            }
            else
            {
                if (existing is null)
                    throw new InvalidOperationException($"Room '{roomName}' has no layer named '{name}'. To add one, give it \"new\": true and a \"type\".");
                type = existing.LayerType;
                if (layerJson.Type is not null && (!Enum.TryParse(layerJson.Type, true, out UndertaleRoom.LayerType given) || given != type))
                    throw new InvalidOperationException($"{where} is a {type} layer, not \"{layerJson.Type}\". To change its type, list it in \"removeLayers\" and add it again as new.");
            }

            void Only(object? section, string key, UndertaleRoom.LayerType needed)
            {
                if (section is not null && type != needed)
                    throw new InvalidOperationException($"{where} is a {type} layer, so it can't have \"{key}\"; that is for {needed} layers.");
            }
            Only(layerJson.Grid, "grid", UndertaleRoom.LayerType.Tiles);
            Only(layerJson.Background, "background", UndertaleRoom.LayerType.Background);
            Only(layerJson.Tiles, "tiles", UndertaleRoom.LayerType.Assets);
            Only(layerJson.Sprites, "sprites", UndertaleRoom.LayerType.Assets);

            if (layerJson.Grid is RoomGridJson grid)
            {
                if (!string.IsNullOrEmpty(grid.Tileset) && data.Backgrounds.ByName(grid.Tileset) is null && !newBackgrounds.Contains(grid.Tileset))
                    throw new InvalidOperationException($"{where} uses tileset '{grid.Tileset}', which doesn't exist and no mod creates it.");
                uint width = existing?.TilesData?.TilesX ?? 0, height = existing?.TilesData?.TilesY ?? 0;
                if (grid.Rows is uint[][] rows)
                {
                    height = (uint)rows.Length;
                    width = rows.Length == 0 || rows[0] is null ? 0 : (uint)rows[0].Length;
                    if (rows.Any(r => r is null || r.Length != width))
                        throw new InvalidOperationException($"{where}: every row in \"grid.rows\" must have the same number of cells.");
                }
                foreach (uint[] cell in grid.Cells ?? [])
                    if (cell is null || cell.Length != 3 || cell[0] >= width || cell[1] >= height)
                        throw new InvalidOperationException($"{where}: each \"grid.cells\" entry is [x, y, tile] inside its {width}x{height} grid, not [{string.Join(", ", cell ?? [])}].");
            }

            if (layerJson.Background is RoomLayerBackgroundJson background)
                CheckSprite(background.Sprite, where);

            if (layerJson.Tiles is RoomTilesJson tiles)
            {
                var tileIds = existing?.AssetsData?.LegacyTiles?.Select(t => t.InstanceID).ToHashSet() ?? [];
                void CheckTile(uint id, string key)
                {
                    if (!tileIds.Contains(id))
                        throw new InvalidOperationException($"\"{key}\": {inLayer} has no tile {id}.");
                }
                foreach (RoomTileJson add in tiles.Add)
                {
                    if (add.After is uint after && after != 0)
                        CheckTile(after, "after");
                    if (add.Id is not null)
                        throw new InvalidOperationException($"An added tile in {inLayer} has \"id\": {add.Id}. Added tiles get their id from the merge; remove \"id\", or move it to \"change\".");
                    if (string.IsNullOrEmpty(add.Sprite))
                        throw new InvalidOperationException($"An added tile in {inLayer} has no \"sprite\".");
                    CheckSprite(add.Sprite, $"An added tile in {inLayer}");
                }
                foreach (RoomTileJson change in tiles.Change)
                {
                    if (change.Id is not uint id)
                        throw new InvalidOperationException($"A changed tile in {inLayer} has no \"id\".");
                    CheckTile(id, "tiles.change");
                    CheckSprite(change.Sprite, $"Tile {id} in room '{roomName}'");
                }
                foreach (uint id in tiles.Remove)
                    CheckTile(id, "tiles.remove");
            }

            if (layerJson.Sprites is RoomSpritesJson sprites)
            {
                var names = existing?.AssetsData?.Sprites?.Select(s => s.Name?.Content).OfType<string>().ToHashSet() ?? [];
                void CheckName(string? spriteName, string key)
                {
                    if (spriteName is null || !names.Contains(spriteName))
                        throw new InvalidOperationException($"\"{key}\": {inLayer} has no sprite named '{spriteName}'.");
                }
                var added = new HashSet<string>();
                foreach (RoomSpriteJson add in sprites.Add)
                {
                    if (string.IsNullOrEmpty(add.Name))
                        throw new InvalidOperationException($"An added sprite in {inLayer} has no \"name\".");
                    if ((names.Contains(add.Name) && !sprites.Remove.Contains(add.Name)) || !added.Add(add.Name))
                        throw new InvalidOperationException($"{where} already has a sprite named '{add.Name}'.");
                    if (!string.IsNullOrEmpty(add.After))
                        CheckName(add.After, "after");
                    CheckSprite(add.Sprite, $"Sprite '{add.Name}' in {inLayer}");
                }
                foreach (RoomSpriteJson change in sprites.Change)
                {
                    CheckName(change.Name, "sprites.change");
                    CheckSprite(change.Sprite, $"Sprite '{change.Name}' in {inLayer}");
                }
                foreach (string remove in sprites.Remove)
                    CheckName(remove, "sprites.remove");
            }
        }
        return newInstanceLayers;
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
            room.GameObjects.Insert(InsertIndex(room.GameObjects, g => g.InstanceID, add.After, $"{roomKey}#instances", room.Name.Content, "instance", modName), instance);
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
            if (settings.EnableViews is bool views) { room.Flags = WithFlag(room.Flags, UndertaleRoom.RoomEntryFlags.EnableViews, views); Track(roomKey + ".enableViews", modName); }
            if (settings.ClearViewBackground is bool clearView) { room.Flags = WithFlag(room.Flags, UndertaleRoom.RoomEntryFlags.ClearViewBackground, clearView); Track(roomKey + ".clearViewBackground", modName); }
            if (settings.DoNotClearDisplayBuffer is bool keepDisplay) { room.Flags = WithFlag(room.Flags, UndertaleRoom.RoomEntryFlags.DoNotClearDisplayBuffer, keepDisplay); Track(roomKey + ".doNotClearDisplayBuffer", modName); }
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

        foreach (string name in json.RemoveLayers ?? [])
        {
            string key = $"{roomKey}/layer:{name}";
            UndertaleRoom.Layer? layer = RoomLayers.Named(room, name);
            if (layer is null)
            {
                if (_removedBy.TryGetValue(key, out string? by) && by != modName)
                    Warnings.Add($"[{modName}] removes layer '{name}' in room '{room.Name.Content}', but '{by}' already removed it.");
                continue;
            }
            foreach (UndertaleRoom.GameObject instance in layer.InstancesData?.Instances.ToList() ?? [])
            {
                RemoveInstance(room, instance);
                _removedBy[$"{roomKey}#{instance.InstanceID}"] = modName;
            }
            room.Layers.Remove(layer);
            _removedBy[key] = modName;
            _addedLayers.Remove(key);
        }

        foreach (RoomLayerJson layerJson in json.Layers ?? [])
            ApplyLayer(room, roomKey, layerJson, modName);

        if (json.Tiles is RoomTilesJson tiles)
            ApplyTiles(room.Tiles, tiles, roomKey, $"{roomKey}#tiles", room.Name.Content, modName);
    }

    private void ApplyLayer(UndertaleRoom room, string roomKey, RoomLayerJson json, string modName)
    {
        string name = json.Name!;
        string key = $"{roomKey}/layer:{name}";
        UndertaleRoom.Layer? layer = RoomLayers.Named(room, name);
        if (json.New == true)
        {
            UndertaleRoom.LayerType type = Enum.Parse<UndertaleRoom.LayerType>(json.Type!, true);
            string by = _addedLayers.GetValueOrDefault(key, "another mod");
            if (layer is not null && layer.LayerType != type)
            {
                Warnings.Add($"[{modName}] adds a {type} layer '{name}' in room '{room.Name.Content}', but '{by}' already added a {layer.LayerType} layer with that name; skipped.");
                return;
            }
            if (layer is not null)
                Warnings.Add($"[{modName}] adds layer '{name}' in room '{room.Name.Content}', but '{by}' already added one with that name; the two were merged into one layer.");
            else
            {
                layer = NewLayer(room, name, type, json.Depth ?? 0);
                _addedLayers[key] = modName;
            }
        }
        else if (layer is null)
        {
            string by = _removedBy.GetValueOrDefault(key, modName);
            Warnings.Add($"[{modName}] changes layer '{name}' in room '{room.Name.Content}', but '{by}' removed it; skipped.");
            return;
        }

        if (json.Depth is int depth) { layer.LayerDepth = depth; Track(key + ".depth", modName); }
        if (json.Visible is bool visible) { layer.IsVisible = visible; Track(key + ".visible", modName); }
        if (json.X is float x) { layer.XOffset = x; Track(key + ".x", modName); }
        if (json.Y is float y) { layer.YOffset = y; Track(key + ".y", modName); }
        if (json.HSpeed is float hspeed) { layer.HSpeed = hspeed; Track(key + ".hspeed", modName); }
        if (json.VSpeed is float vspeed) { layer.VSpeed = vspeed; Track(key + ".vspeed", modName); }

        if (json.Grid is RoomGridJson grid)
        {
            UndertaleRoom.Layer.LayerTilesData tilesData = layer.TilesData;
            if (grid.Tileset is string tileset) { tilesData.Background = tileset == "" ? null : Background(tileset); Track(key + ".tileset", modName); }
            if (grid.Rows is uint[][] rows)
            {
                tilesData.TileData = rows.Select(r => (uint[])r.Clone()).ToArray();
                tilesData.TilesX = rows.Length == 0 ? 0 : (uint)rows[0].Length;
                tilesData.TilesY = (uint)rows.Length;
                foreach (string cellKey in _changedBy.Keys.Where(k => k.StartsWith(key + ".cell ", StringComparison.Ordinal)).ToList())
                    Track(cellKey, modName);
                Track(key + ".grid", modName);
            }
            foreach (uint[] cell in grid.Cells ?? [])
            {
                if (cell[0] >= tilesData.TilesX || cell[1] >= tilesData.TilesY)
                {
                    Warnings.Add($"[{modName}] sets grid cell ({cell[0]}, {cell[1]}) in layer '{name}' of room '{room.Name.Content}', but another mod resized that grid to {tilesData.TilesX}x{tilesData.TilesY}; skipped.");
                    continue;
                }
                tilesData.TileData[cell[1]][cell[0]] = cell[2];
                string cellKey = $"{key}.cell {cell[0]},{cell[1]}";
                if (!_changedBy.ContainsKey(cellKey) && _changedBy.TryGetValue(key + ".grid", out string? wholeGrid))
                    _changedBy[cellKey] = wholeGrid;
                Track(cellKey, modName);
            }
        }

        if (json.Background is RoomLayerBackgroundJson bg)
        {
            UndertaleRoom.Layer.LayerBackgroundData bgData = layer.BackgroundData;
            if (bg.Sprite is string sprite) { bgData.Sprite = sprite == "" ? null : Sprite(sprite); Track(key + ".sprite", modName); }
            if (bg.Visible is bool bgVisible) { bgData.Visible = bgVisible; Track(key + ".backgroundVisible", modName); }
            if (bg.Foreground is bool foreground) { bgData.Foreground = foreground; Track(key + ".foreground", modName); }
            if (bg.TileX is bool tileX) { bgData.TiledHorizontally = tileX; Track(key + ".tileX", modName); }
            if (bg.TileY is bool tileY) { bgData.TiledVertically = tileY; Track(key + ".tileY", modName); }
            if (bg.Stretch is bool stretch) { bgData.Stretch = stretch; Track(key + ".stretch", modName); }
            if (bg.Color is uint color) { bgData.Color = color; Track(key + ".color", modName); }
            if (bg.FirstFrame is float firstFrame) { bgData.FirstFrame = firstFrame; Track(key + ".firstFrame", modName); }
            if (bg.AnimationSpeed is float speed) { bgData.AnimationSpeed = speed; Track(key + ".animationSpeed", modName); }
            if (bg.AnimationSpeedType is uint speedType) { bgData.AnimationSpeedType = (AnimationSpeedType)speedType; Track(key + ".animationSpeedType", modName); }
        }

        if (json.Tiles is RoomTilesJson layerTiles)
            ApplyTiles(layer.AssetsData.LegacyTiles, layerTiles, roomKey, key + "#tiles", room.Name.Content, modName);

        if (json.Sprites is RoomSpritesJson sprites)
            ApplySprites(layer.AssetsData.Sprites, sprites, key, room.Name.Content, name, modName);
    }

    private UndertaleRoom.Layer NewLayer(UndertaleRoom room, string name, UndertaleRoom.LayerType type, int depth)
    {
        var layer = new UndertaleRoom.Layer
        {
            LayerName = data.Strings.MakeString(name),
            LayerId = NextLayerId(),
            LayerType = type,
            LayerDepth = depth,
            EffectEnabled = true,
            Data = type switch
            {
                UndertaleRoom.LayerType.Instances => new UndertaleRoom.Layer.LayerInstancesData(),
                UndertaleRoom.LayerType.Tiles => new UndertaleRoom.Layer.LayerTilesData { TileData = [] },
                UndertaleRoom.LayerType.Background => new UndertaleRoom.Layer.LayerBackgroundData(),
                UndertaleRoom.LayerType.Assets => new UndertaleRoom.Layer.LayerAssetsData
                {
                    LegacyTiles = new(),
                    Sprites = new(),
                    Sequences = data.IsVersionAtLeast(2, 3) ? new() : null,
                    NineSlices = data.IsVersionAtLeast(2, 3) && !data.IsVersionAtLeast(2, 3, 2) ? new() : null,
                    ParticleSystems = data.IsVersionAtLeast(2, 3) && data.IsNonLTSVersionAtLeast(2023, 2) ? new() : null,
                    TextItems = data.IsVersionAtLeast(2024, 6) ? new() : null
                },
                _ => null
            }
        };
        int at = 0;
        while (at < room.Layers.Count && room.Layers[at].LayerDepth <= depth)
            at++;
        room.Layers.Insert(at, layer);
        layer.ParentRoom = room;
        return layer;
    }

    private uint? _nextLayerId;

    private uint NextLayerId()
    {
        uint id = _nextLayerId ?? data.Rooms.SelectMany(r => r?.Layers ?? []).Select(l => l.LayerId + 1).DefaultIfEmpty(0u).Max();
        _nextLayerId = id + 1;
        return id;
    }

    private void ApplySprites(IList<UndertaleRoom.SpriteInstance> list, RoomSpritesJson sprites, string layerKey, string room, string layer, string modName)
    {
        foreach (string name in sprites.Remove)
        {
            string key = $"{layerKey}/sprite:{name}";
            UndertaleRoom.SpriteInstance? sprite = list.FirstOrDefault(s => s.Name?.Content == name);
            if (sprite is null)
            {
                if (_removedBy.TryGetValue(key, out string? by) && by != modName)
                    Warnings.Add($"[{modName}] removes sprite '{name}' in layer '{layer}' of room '{room}', but '{by}' already removed it.");
                continue;
            }
            list.Remove(sprite);
            _removedBy[key] = modName;
        }

        foreach (RoomSpriteJson change in sprites.Change)
        {
            string key = $"{layerKey}/sprite:{change.Name}";
            UndertaleRoom.SpriteInstance? sprite = list.FirstOrDefault(s => s.Name?.Content == change.Name);
            if (sprite is null)
            {
                string by = _removedBy.GetValueOrDefault(key, modName);
                Warnings.Add($"[{modName}] changes sprite '{change.Name}' in layer '{layer}' of room '{room}', but '{by}' removed it; skipped.");
                continue;
            }
            if (change.Sprite is string asset) { sprite.Sprite = asset == "" ? null : Sprite(asset); Track(key + ".sprite", modName); }
            if (change.X is int x) { sprite.X = x; Track(key + ".x", modName); }
            if (change.Y is int y) { sprite.Y = y; Track(key + ".y", modName); }
            if (change.ScaleX is float scaleX) { sprite.ScaleX = scaleX; Track(key + ".scaleX", modName); }
            if (change.ScaleY is float scaleY) { sprite.ScaleY = scaleY; Track(key + ".scaleY", modName); }
            if (change.Color is uint color) { sprite.Color = color; Track(key + ".color", modName); }
            if (change.AnimationSpeed is float speed) { sprite.AnimationSpeed = speed; Track(key + ".animationSpeed", modName); }
            if (change.AnimationSpeedType is uint speedType) { sprite.AnimationSpeedType = (AnimationSpeedType)speedType; Track(key + ".animationSpeedType", modName); }
            if (change.FrameIndex is float frame) { sprite.FrameIndex = frame; Track(key + ".frameIndex", modName); }
            if (change.Rotation is float rotation) { sprite.Rotation = rotation; Track(key + ".rotation", modName); }
        }

        foreach (RoomSpriteJson add in sprites.Add)
        {
            if (list.Any(s => s.Name?.Content == add.Name))
            {
                Warnings.Add($"[{modName}] adds sprite '{add.Name}' in layer '{layer}' of room '{room}', but another mod already added one with that name; skipped.");
                continue;
            }
            int at = add.After is not string after
                ? list.Count
                : InsertAt(list, after == "" ? null : s => s.Name?.Content == after, after, $"{layerKey}#sprites", room, "sprite", modName);
            list.Insert(at, new UndertaleRoom.SpriteInstance
            {
                Name = data.Strings.MakeString(add.Name!),
                Sprite = string.IsNullOrEmpty(add.Sprite) ? null : Sprite(add.Sprite),
                X = add.X ?? 0,
                Y = add.Y ?? 0,
                ScaleX = add.ScaleX ?? 1,
                ScaleY = add.ScaleY ?? 1,
                Color = add.Color ?? 0xFFFFFFFF,
                AnimationSpeed = add.AnimationSpeed ?? 1,
                AnimationSpeedType = (AnimationSpeedType)(add.AnimationSpeedType ?? 0),
                FrameIndex = add.FrameIndex ?? 0,
                Rotation = add.Rotation ?? 0
            });
        }
    }

    private void ApplyTiles(IList<UndertaleRoom.Tile> list, RoomTilesJson tiles, string roomKey, string listKey, string room, string modName)
    {
        bool sprites = data.IsGameMaker2();
        foreach (uint id in tiles.Remove)
        {
            string key = $"{roomKey}#tile{id}";
            UndertaleRoom.Tile? tile = list.FirstOrDefault(t => t.InstanceID == id);
            if (tile is null)
            {
                if (_removedBy.TryGetValue(key, out string? by) && by != modName)
                    Warnings.Add($"[{modName}] removes tile {id} in room '{room}', but '{by}' already removed it.");
                continue;
            }
            list.Remove(tile);
            _removedBy[key] = modName;
        }

        foreach (RoomTileJson change in tiles.Change)
        {
            uint id = change.Id!.Value;
            string key = $"{roomKey}#tile{id}";
            UndertaleRoom.Tile? tile = list.FirstOrDefault(t => t.InstanceID == id);
            if (tile is null)
            {
                string by = _removedBy.GetValueOrDefault(key, modName);
                Warnings.Add($"[{modName}] changes tile {id} in room '{room}', but '{by}' removed it; skipped.");
                continue;
            }
            if (!sprites && change.Background is not null) { tile.BackgroundDefinition = Background(change.Background); Track(key + ".background", modName); }
            if (sprites && change.Sprite is not null) { tile.SpriteDefinition = Sprite(change.Sprite); Track(key + ".sprite", modName); }
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
            var tile = new UndertaleRoom.Tile
            {
                spriteMode = sprites,
                InstanceID = data.GeneralInfo.LastTile++,
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
            };
            if (sprites)
                tile.SpriteDefinition = Sprite(add.Sprite!);
            else
                tile.BackgroundDefinition = Background(add.Background!);
            list.Insert(InsertIndex(list, t => t.InstanceID, add.After, listKey, room, "tile", modName), tile);
        }
    }

    private readonly Dictionary<(string List, string After), int> _insertedAfter = new();
    private readonly Dictionary<string, string> _addedLayers = new();

    private int InsertIndex<T>(IList<T> list, Func<T, uint> idOf, uint? after, string listKey, string room, string kind, string modName) =>
        after is not uint anchor
            ? list.Count
            : InsertAt(list, anchor == 0 ? null : item => idOf(item) == anchor, anchor.ToString(), listKey, room, kind, modName);

    private int InsertAt<T>(IList<T> list, Func<T, bool>? isAnchor, string anchor, string listKey, string room, string kind, string modName)
    {
        int anchorIndex = -1;
        if (isAnchor is not null)
        {
            for (int i = 0; i < list.Count; i++)
                if (isAnchor(list[i])) { anchorIndex = i; break; }
            if (anchorIndex < 0)
            {
                Warnings.Add($"[{modName}] places a new {kind} after {kind} {anchor} in room '{room}', but that {kind} was removed; it was added at the end.");
                return list.Count;
            }
        }
        int already = _insertedAfter.GetValueOrDefault((listKey, anchor));
        _insertedAfter[(listKey, anchor)] = already + 1;
        return Math.Min(list.Count, anchorIndex + 1 + already);
    }

    private UndertaleCode? Code(string? name) =>
        string.IsNullOrEmpty(name) ? null : data.Code.ByName(name) ?? throw new InvalidOperationException($"Code '{name}' doesn't exist.");

    private UndertaleBackground Background(string name) =>
        data.Backgrounds.ByName(name) ?? throw new InvalidOperationException($"Background '{name}' doesn't exist.");

    private UndertaleSprite Sprite(string name) =>
        data.Sprites.ByName(name) ?? throw new InvalidOperationException($"Sprite '{name}' doesn't exist.");

    private static UndertaleRoom.RoomEntryFlags WithFlag(UndertaleRoom.RoomEntryFlags flags, UndertaleRoom.RoomEntryFlags flag, bool on) =>
        on ? flags | flag : flags & ~flag;

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
