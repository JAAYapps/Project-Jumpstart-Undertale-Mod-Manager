using System;
using System.Collections.Generic;
using System.IO;
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
// KEY: the model already does the hard graph wiring. UndertaleGameObject
// .EventHandlerFor(EventType, uint subtype, data) finds-or-creates the event,
// the action, and an empty UndertaleCode named gml_Object_<obj>_<type>_<subtype>,
// and returns that code. So this importer never builds the
// UndertalePointerList<UndertalePointerList<Event>> by hand — it just declares
// which events exist and lets EventHandlerFor wire them. The event's GML flows
// through the normal code/ importer (Tier 2 compiles gml_Object_<obj>_<evt>.gml).
//
// So objects handle: sprite/parent/mask references (by name), the common
// properties, the full physics block, and event PRESENCE. Behavior = code path.
//
// JSON (at <route>/objects/<obj>.json):
//   {
//     "sprite": "spr_x", "parent": "obj_y", "maskSprite": "spr_x",
//     "visible": true, "solid": false, "persistent": false, "depth": 0,
//     "events": [ { "type": "Step", "subtype": 0 }, { "type": "Create" } ],
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
    [JsonPropertyName("sprite")]     public string Sprite { get; set; }
    [JsonPropertyName("parent")]     public string Parent { get; set; }
    [JsonPropertyName("maskSprite")] public string MaskSprite { get; set; }

    [JsonPropertyName("visible")]    public bool? Visible { get; set; }
    [JsonPropertyName("solid")]      public bool? Solid { get; set; }
    [JsonPropertyName("persistent")] public bool? Persistent { get; set; }
    [JsonPropertyName("depth")]      public int? Depth { get; set; }

    [JsonPropertyName("events")]     public List<ObjectEventJson> Events { get; set; } = new();
    [JsonPropertyName("physics")]    public PhysicsJson Physics { get; set; }
}

public sealed class ObjectEventJson
{
    [JsonPropertyName("type")]    public string Type { get; set; }     // "Create", "Step", "Collision", ...
    [JsonPropertyName("subtype")] public uint Subtype { get; set; }    // 0 for Create/Destroy; varies otherwise
}

public sealed class PhysicsJson
{
    [JsonPropertyName("uses")]           public bool? Uses { get; set; }
    [JsonPropertyName("sensor")]         public bool? Sensor { get; set; }
    [JsonPropertyName("shape")]          public string Shape { get; set; }   // "Circle" | "Box" | "Custom"
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
            obj = new UndertaleGameObject { Name = data.Strings.MakeString(name) };
            data.GameObjects.Add(obj);
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
        // EventHandlerFor creates the event/action and an empty code entry
        // (gml_Object_<name>_<type>_<subtype>) if absent. The code/ importer
        // then compiles the GML into that code entry. We only ensure presence.
        foreach (ObjectEventJson ev in json.Events)
        {
            EventType type = ParseEventType(ev.Type, name);
            obj.EventHandlerFor(type, ev.Subtype, data);
        }
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

    private static EventType ParseEventType(string type, string obj)
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
            ObjectJson json = JsonSerializer.Deserialize<ObjectJson>(
                File.ReadAllText(file),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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