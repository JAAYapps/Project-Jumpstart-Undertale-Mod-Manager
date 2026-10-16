using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using UndertaleModLib;
using UndertaleModLib.Models;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Objects;

// ---------------------------------------------------------------------------
// TIER 1: game objects. Declarative properties + event WIRING. Verified against
// UndertaleGameObject.cs.
//
// KEY: ParseEvents finds-or-creates each declared event, its code action, and
// an empty UndertaleCode named gml_Object_<obj>_<type>_<subtype>. It does this
// itself rather than through UndertaleGameObject.EventHandlerFor, which leaves
// the action's fields zeroed and can't name a 2.3+ collision entry. The event's
// GML flows through the normal code/ importer (Tier 2 compiles
// gml_Object_<obj>_<evt>.gml).
//
// So objects handle: sprite/parent/mask references (by name), the common
// properties, the full physics block, and event PRESENCE. Behavior = code path.
//
// JSON (at <route>/objects/<obj>.json):
//   {
//     "sprite": "spr_x", "parent": "obj_y", "maskSprite": "spr_x",
//     "visible": true, "solid": false, "persistent": false, "depth": 0,
//     "events": [ { "type": "Create" }, { "type": "Step", "subtype": "EndStep" },
//                 { "type": "Collision", "subtype": "obj_other" } ],
//     "physics": { "uses": false, "sensor": false, "shape": "Circle",
//                  "density": 0.5, "restitution": 0.1, "group": 0,
//                  "linearDamping": 0.1, "angularDamping": 0.1,
//                  "friction": 0.2, "awake": false, "kinematic": false }
//   }
// Reference fields (sprite/parent/maskSprite) must resolve to existing assets;
// a missing reference is an error (fail-loud). Omitted properties keep engine
// defaults on create, or leave the existing value on replace.
// ---------------------------------------------------------------------------

public sealed class ObjectJson
{
    [JsonPropertyName("sprite")]     public string? Sprite { get; set; }
    [JsonPropertyName("parent")]     public string? Parent { get; set; }
    [JsonPropertyName("maskSprite")] public string? MaskSprite { get; set; }

    [JsonPropertyName("visible")]    public bool? Visible { get; set; }
    [JsonPropertyName("solid")]      public bool? Solid { get; set; }
    [JsonPropertyName("persistent")] public bool? Persistent { get; set; }
    [JsonPropertyName("depth")]      public int? Depth { get; set; }

    [JsonPropertyName("events")]     public List<ObjectEventJson> Events { get; set; } = new();
    [JsonPropertyName("physics")]    public PhysicsJson? Physics { get; set; }
}

public sealed class ObjectEventJson
{
    [JsonPropertyName("type")]    public string? Type { get; set; }     // "Create", "Step", "Collision", ...
    [JsonPropertyName("subtype")] public JsonElement Subtype { get; set; } // omitted = 0; a number, or a name ("EndStep", "obj_other")
    [JsonPropertyName("code")]    public string? Code { get; set; }     // code entry to run; default gml_Object_<obj>_<type>_<subtype>
}

public sealed class PhysicsJson
{
    [JsonPropertyName("uses")]           public bool? Uses { get; set; }
    [JsonPropertyName("sensor")]         public bool? Sensor { get; set; }
    [JsonPropertyName("shape")]          public string? Shape { get; set; }   // "Circle" | "Box" | "Custom"
    [JsonPropertyName("density")]        public float? Density { get; set; }
    [JsonPropertyName("restitution")]    public float? Restitution { get; set; }
    [JsonPropertyName("group")]          public uint? Group { get; set; }
    [JsonPropertyName("linearDamping")]  public float? LinearDamping { get; set; }
    [JsonPropertyName("angularDamping")] public float? AngularDamping { get; set; }
    [JsonPropertyName("friction")]       public float? Friction { get; set; }
    [JsonPropertyName("awake")]          public bool? Awake { get; set; }
    [JsonPropertyName("kinematic")]      public bool? Kinematic { get; set; }
}

public static class ObjectImporter
{
    public static void Apply(UndertaleData data, ModAddress addr, string jsonFile, bool create)
    {
        ObjectJson json = ReadJson(jsonFile);
        string name = addr.AssetName;

        UndertaleGameObject obj;
        if (create)
        {
            obj = CreateEmpty(data, name);
        }
        else
        {
            obj = data.GameObjects.ByName(name)
                  ?? throw new InvalidOperationException(
                      $"Object '{name}' expected to exist (Replace) but was not found.");
        }

        // --- reference properties (fail-loud on missing target) ---
        if (json.Sprite is not null)
            obj.Sprite = ResolveSprite(data, json.Sprite, name, "sprite");
        if (json.MaskSprite is not null)
            obj.TextureMaskId = ResolveSprite(data, json.MaskSprite, name, "maskSprite");
        if (json.Parent is not null)
            obj.ParentId = data.GameObjects.ByName(json.Parent)
                ?? throw new InvalidOperationException(
                    $"Object '{name}' parent '{json.Parent}' not found.");

        // --- simple properties (only override when present) ---
        if (json.Visible.HasValue)    obj.Visible = json.Visible.Value;
        if (json.Solid.HasValue)      obj.Solid = json.Solid.Value;
        if (json.Persistent.HasValue) obj.Persistent = json.Persistent.Value;
        if (json.Depth.HasValue)      obj.Depth = json.Depth.Value;

        // --- physics block ---
        if (json.Physics is PhysicsJson p)
        {
            if (p.Uses.HasValue)           obj.UsesPhysics = p.Uses.Value;
            if (p.Sensor.HasValue)         obj.IsSensor = p.Sensor.Value;
            if (p.Shape is not null)       obj.CollisionShape = ParseShape(p.Shape, name);
            if (p.Density.HasValue)        obj.Density = p.Density.Value;
            if (p.Restitution.HasValue)    obj.Restitution = p.Restitution.Value;
            if (p.Group.HasValue)          obj.Group = p.Group.Value;
            if (p.LinearDamping.HasValue)  obj.LinearDamping = p.LinearDamping.Value;
            if (p.AngularDamping.HasValue) obj.AngularDamping = p.AngularDamping.Value;
            if (p.Friction.HasValue)       obj.Friction = p.Friction.Value;
            if (p.Awake.HasValue)          obj.Awake = p.Awake.Value;
            if (p.Kinematic.HasValue)      obj.Kinematic = p.Kinematic.Value;
        }

        // --- event WIRING (behavior comes via the code/ importer) ---
        // ParseEvents creates the event/action and an empty code entry
        // (gml_Object_<name>_<type>_<subtype>) if absent. The code/ importer
        // then compiles the GML into that code entry. We only ensure presence.
        ParseEvents(data, obj, json.Events);
    }

    public static UndertaleGameObject CreateEmpty(UndertaleData data, string name)
    {
        var obj = new UndertaleGameObject { Name = data.Strings.MakeString(name) };
        data.GameObjects.Add(obj);
        return obj;
    }

    private static UndertaleSprite ResolveSprite(UndertaleData data, string sprite, string obj, string field)
        => data.Sprites.ByName(sprite)
           ?? throw new InvalidOperationException($"Object '{obj}' {field} '{sprite}' not found.");

    private static CollisionShapeFlags ParseShape(string shape, string obj)
    {
        return shape.ToLowerInvariant() switch
        {
            "circle" => CollisionShapeFlags.Circle,
            "box"    => CollisionShapeFlags.Box,
            "custom" => CollisionShapeFlags.Custom,
            _ => throw new InvalidOperationException(
                $"Object '{obj}' has unknown collision shape '{shape}' (Circle|Box|Custom).")
        };
    }

    // Wires the events declared in JSON onto the object: finds or creates each
    // event and its single code action, then makes sure the action points at a
    // code entry. Idempotent on purpose — the merge applies objects both before
    // the code compile (so the entries exist for the code/ importer to fill, and
    // collision events never hit CodeImportGroup's "attach manually" throw) and
    // after it (so anything the compile rerouted is still wired).
    private static void ParseEvents(UndertaleData data, UndertaleGameObject obj, List<ObjectEventJson> events)
    {
        string name = obj.Name.Content;

        foreach (ObjectEventJson ev in events)
        {
            EventType type = ParseEventType(ev.Type, name);
            if ((int)type >= obj.Events.Count)
                throw new InvalidOperationException(
                    $"Object '{name}' event type '{type}' is not supported by this game's GameMaker version.");

            uint subtype = ParseEventSubtype(data, type, ev.Subtype, name);

            UndertalePointerList<UndertaleGameObject.Event> subEvents = obj.Events[(int)type];
            UndertaleGameObject.Event? target = subEvents.FirstOrDefault(e => e.EventSubtype == subtype);
            if (target is null)
                subEvents.Add(target = new UndertaleGameObject.Event { EventSubtype = subtype });

            UndertaleGameObject.EventAction? action = target.Actions.FirstOrDefault();
            if (action is null)
            {
                // The values every compiled "execute code" action carries. A bare
                // new EventAction() leaves them all zero.
                action = new UndertaleGameObject.EventAction
                {
                    LibID = 1,
                    ID = 603,
                    Kind = 7,
                    UseRelative = false,
                    IsQuestion = false,
                    UseApplyTo = true,
                    ExeType = 2,
                    ActionName = data.Strings.MakeString(""),
                    ArgumentCount = 1,
                    Who = -1,
                    Relative = false,
                    IsNot = false
                };
                target.Actions.Add(action);
            }

            // An explicit "code" always wins. Otherwise an event that is already
            // wired (a vanilla object being modified) keeps its own entry.
            if (ev.Code is not null)
            {
                if (action.CodeId?.Name?.Content != ev.Code)
                    action.CodeId = FindOrCreateCode(data, ev.Code);
            }
            else if (action.CodeId is null)
            {
                action.CodeId = FindOrCreateCode(data, DefaultCodeName(data, name, type, subtype));
            }
        }
    }

    private static UndertaleCode FindOrCreateCode(UndertaleData data, string codeName)
        => data.Code.ByName(codeName) ?? UndertaleCode.CreateEmptyEntry(data, codeName);

    // gml_Object_<obj>_<type>_<subtype>, the name the code/ importer looks up.
    // GM 2.3+ names a collision event after the other object rather than its index.
    private static string DefaultCodeName(UndertaleData data, string obj, EventType type, uint subtype)
    {
        if (type == EventType.Collision && data.IsVersionAtLeast(2, 3)
            && subtype < data.GameObjects.Count
            && data.GameObjects[(int)subtype]?.Name?.Content is string other)
            return $"gml_Object_{obj}_Collision_{other}";

        return $"gml_Object_{obj}_{type}_{subtype}";
    }

    // "subtype" may be omitted (0), a number, or a name: the other object's name
    // for Collision, otherwise the event's own subtype enum (BeginStep, DrawGUI,
    // vk_space, User0, ...).
    private static uint ParseEventSubtype(UndertaleData data, EventType type, JsonElement raw, string obj)
    {
        switch (raw.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                return 0;
            case JsonValueKind.Number when raw.TryGetUInt32(out uint n):
                return n;
            case JsonValueKind.String:
                break;
            default:
                throw new InvalidOperationException(
                    $"Object '{obj}' {type} event subtype must be a number or a name (got {raw.GetRawText()}).");
        }

        string text = raw.GetString()!;
        if (uint.TryParse(text, out uint numeric))
            return numeric;

        if (type == EventType.Collision)
        {
            UndertaleGameObject other = data.GameObjects.ByName(text)
                ?? throw new InvalidOperationException(
                    $"Object '{obj}' collision event target '{text}' not found.");
            return (uint)data.GameObjects.IndexOf(other);
        }

        Type? names = type switch
        {
            EventType.Step => typeof(EventSubtypeStep),
            EventType.Keyboard or EventType.KeyPress or EventType.KeyRelease => typeof(EventSubtypeKey),
            EventType.Mouse => typeof(EventSubtypeMouse),
            EventType.Other => typeof(EventSubtypeOther),
            EventType.Draw => typeof(EventSubtypeDraw),
            EventType.Gesture => typeof(EventSubtypeGesture),
            _ => null
        };
        if (names is not null && Enum.TryParse(names, text, ignoreCase: true, out object? value))
            return (uint)value;

        throw new InvalidOperationException(
            $"Object '{obj}' has unknown {type} event subtype '{text}'" +
            (names is null ? " (this event type takes a number)." : $". Valid: {string.Join(", ", Enum.GetNames(names))}."));
    }

    private static EventType ParseEventType(string? type, string obj)
    {
        if (Enum.TryParse<EventType>(type, ignoreCase: true, out EventType t))
            return t;
        throw new InvalidOperationException(
            $"Object '{obj}' has unknown event type '{type}'. " +
            $"Valid: {string.Join(", ", Enum.GetNames(typeof(EventType)))}.");
    }

    private static ObjectJson ReadJson(string file)
    {
        try
        {
            ObjectJson? json = JsonSerializer.Deserialize<ObjectJson>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (json is null)
                throw new InvalidOperationException($"Object JSON '{file}' parsed to null.");
            return json;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Object JSON '{file}' is invalid: {ex.Message}", ex);
        }
    }
}