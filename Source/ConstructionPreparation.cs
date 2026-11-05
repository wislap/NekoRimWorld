using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal static class ConstructionPreparation
{
    internal static DesignationDef Definition(string kind) => kind switch
    {
        "deconstruct" => DesignationDefOf.Deconstruct,
        "mine" => DesignationDefOf.Mine,
        "cut_plant" => DesignationDefOf.CutPlant,
        _ => throw new ArgumentException("Unknown construction preparation designation.")
    };
    internal static Designation? Find(Thing thing, string kind)
    {
        if (thing.Destroyed || !thing.Spawned || thing.Map == null) return null;
        if (kind == "mine")
        {
            if (!thing.def.mineable || !ReferenceEquals(thing.Position.GetFirstMineable(thing.Map), thing)) return null;
            return thing.Map.designationManager.DesignationAt(thing.Position, Definition(kind));
        }
        return thing.Map.designationManager.DesignationOn(thing, Definition(kind));
    }
    internal static JObject Read(Thing thing) => new()
    {
        ["deconstruct"] = Find(thing, "deconstruct") != null,
        ["mine"] = Find(thing, "mine") != null,
        ["cut_plant"] = Find(thing, "cut_plant") != null
    };
}

internal sealed partial class VerseWriteFieldResolver
{
    private static Registration PreparationRegistration(string kind)
    {
        var op = "set_" + kind;
        var schema = new JObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JObject
            {
                ["op"] = new JObject { ["const"] = op },
                ["map"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["thing"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["value"] = new JObject { ["type"] = "boolean" }
            },
            ["required"] = new JArray("op", "map", "thing", "value")
        };
        return new Registration(new JObject
        {
            ["op"] = op,
            ["schema"] = schema,
            ["description"] = "A normal preparation designation on the exact visible native object on the player's current map. Does not complete work, clear competing instructions or enable god mode. False removes only this designation.",
            ["observation"] = new JObject
            {
                ["read"] = new JObject { ["tool"] = "look", ["arguments"] = new JObject { ["object"] = new JObject { ["kind"] = "thing", ["id"] = new JObject { ["arg"] = "thing" }, ["map"] = new JObject { ["arg"] = "map" } }, ["aspects"] = new JArray("state"), ["focus"] = new JObject { ["state"] = new JArray("condition") } }, ["native_operation"] = "inspect_state" },
                ["extract"] = new JObject
                {
                    ["kind"] = "field",
                    ["object_id"] = new JObject { ["arg"] = "thing" },
                    ["path"] = new JArray("facets", "condition", "construction_designations", kind),
                    ["allow_null"] = false,
                    ["objects_path"] = new JArray("objects"),
                    ["identity_path"] = new JArray("target", "id"),
                    ["status_path"] = new JArray("facets", "condition", "status"),
                    ["status_value"] = "observed",
                    ["match"] = new JArray(new JObject { ["path"] = new JArray("target", "map"), ["value"] = new JObject { ["arg"] = "map" } })
                }
            }
        },
            (r, operation) => One(r.ResolvePreparation(operation, kind), operation));
    }
    private static Registration[] PreparationCatalog => new[] { PreparationRegistration("deconstruct"), PreparationRegistration("mine"), PreparationRegistration("cut_plant") };
    private IPreparedWriteField ResolvePreparation(JObject operation, string kind)
    {
        if (operation.Properties().Any(p => !new[] { "op", "map", "thing", "value", "expect", "evidence" }.Contains(p.Name))) throw new ArgumentException("Invalid construction preparation operation.");
        var map = GameWorldReferences.ResolveMap((string?)operation["map"]);
        if ((string?)operation["map"] != map.GetUniqueLoadID()) throw new ArgumentException("Use the canonical native map ID for construction preparation.");
        var thing = map.listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == (string?)operation["thing"])
            ?? throw new ArgumentException("Construction preparation target not found on the selected map.");
        return new PreparationField(thing, kind);
    }
    private sealed class PreparationField : IPreparedWriteField
    {
        private readonly Thing thing;
        private readonly string kind;
        private readonly Map map;
        private readonly IntVec3 cell;
        private Designation? removed;
        internal PreparationField(Thing thing, string kind) { this.thing = thing; this.kind = kind; map = thing.Map; cell = thing.Position; }
        public string ObjectId => thing.GetUniqueLoadID();
        public string Field => "designation:" + kind;
        public object NativeTarget => thing;
        public bool IsDestroyed => thing.Destroyed || map.Disposed;
        public bool MayChangeByGame => true;
        private void RequireOriginal()
        {
            if (thing.Destroyed || !thing.Spawned || thing.Map != map || thing.Position != cell || !cell.InBounds(map) || cell.Fogged(map)) throw new ArgumentException("Original construction preparation target is no longer visible at its prepared position.");
            if (kind == "mine" && (!thing.def.mineable || !ReferenceEquals(cell.GetFirstMineable(map), thing))) throw new ArgumentException("Original mineable is no longer the native target at this cell.");
        }
        public JToken Read() { RequireOriginal(); return new JValue(ConstructionPreparation.Find(thing, kind) != null); }
        private Designator Designator() => kind switch
        {
            "deconstruct" => new Designator_Deconstruct(),
            "mine" => new Designator_Mine(),
            "cut_plant" => new Designator_PlantsCut(),
            _ => throw new ArgumentException("Unsupported preparation designation.")
        };
        public void Validate(JToken value)
        {
            RequireOriginal();
            if (value.Type != JTokenType.Boolean) throw new ArgumentException("Preparation designation must be boolean.");
            if (map != Find.CurrentMap) throw new ArgumentException("Native preparation qualification requires the player's current map.");
            if (!Official(thing.def)) throw new ArgumentException("Construction preparation supports loaded official definitions.");
            if (!(bool)value || ConstructionPreparation.Find(thing, kind) != null) return;
            if (kind != "mine" && thing.TryGetComp<CompForbiddable>()?.Forbidden == true) throw new ArgumentException("Clear the target's forbidden flag explicitly before adding a thing designation.");
            if (DebugSettings.godMode) throw new ArgumentException("Preparation designation cannot use native god-mode execution.");
            if (kind == "deconstruct" && (thing is not Building || thing is Blueprint || thing is Frame || thing.GetStatValue(StatDefOf.WorkToBuild) <= 0)) throw new ArgumentException("Only an existing building requiring ordinary deconstruction work is supported.");
            if (kind == "mine" && map.designationManager.DesignationAt(cell, DesignationDefOf.SmoothWall) != null) throw new ArgumentException("Clear the competing smooth-wall designation explicitly before mining.");
            if (kind == "cut_plant" && map.designationManager.AllDesignationsOn(thing).Any(d => d.def != DesignationDefOf.CutPlant)) throw new ArgumentException("Clear competing plant designations explicitly before cutting.");
            var report = Designator().CanDesignateThing(thing);
            if (!report.Accepted) throw new ArgumentException("Native preparation designation refused: " + report.Reason);
        }
        public void Write(JToken value)
        {
            Validate(value);
            var current = ConstructionPreparation.Find(thing, kind);
            if ((bool)value)
            {
                if (current != null) return;
                if (removed != null) { map.designationManager.AddDesignation(removed); removed = null; }
                else Designator().DesignateThing(thing);
            }
            else if (current != null) { removed = current; map.designationManager.RemoveDesignation(current); }
        }
        public void Restore(JToken value) => Write(value);
    }
    internal IPreparedWriteField? ResolvePreparationLedger(string id, string field, object? target)
    {
        if (target is not Thing thing || thing.GetUniqueLoadID() != id || !field.StartsWith("designation:", StringComparison.Ordinal)) return null;
        var kind = field.Substring("designation:".Length);
        return new[] { "deconstruct", "mine", "cut_plant" }.Contains(kind) && thing.Map != null ? new PreparationField(thing, kind) : null;
    }
}
