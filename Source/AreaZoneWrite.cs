using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal sealed partial class VerseWriteFieldResolver
{
    private static JObject CellSchema() => JObject.Parse("{'type':'array','maxItems':1024,'items':{'type':'object','additionalProperties':false,'properties':{'x':{'type':'integer'},'z':{'type':'integer'}},'required':['x','z']}}");
    private static Registration AreaZoneRegistration(string op, string? target)
    {
        var properties = new JObject { ["op"] = new JObject { ["const"] = op }, ["map"] = new JObject { ["type"] = "string", ["minLength"] = 1 } };
        var required = new JArray("op", "map", "value");
        var valueProperties = new JObject { ["cells"] = CellSchema() }; var valueRequired = new JArray("cells");
        if (target != null) { properties[target] = new JObject { ["type"] = "string", ["minLength"] = 1 }; required.Add(target); }
        else { if (op == "create_zone") { valueProperties["kind"] = new JObject { ["enum"] = new JArray("stockpile", "growing") }; valueRequired.Add("kind"); } valueProperties["label"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 }; }
        properties["value"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = valueProperties, ["required"] = valueRequired };
        var expected = target == null ? JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'map_id':{'type':'string','minLength':1},'zone_ids':{'type':'array','items':{'type':'string'}},'allowed_area_ids':{'type':'array','items':{'type':'string'}}},'required':['map_id','zone_ids','allowed_area_ids']}")
            : new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = new JObject { ["native_instance"] = new JObject { ["type"] = "string", ["minLength"] = 1 }, ["cells"] = JObject.Parse("{'type':'array','items':{'type':'object','additionalProperties':false,'properties':{'x':{'type':'integer'},'z':{'type':'integer'}},'required':['x','z']}}") }, ["required"] = new JArray("native_instance", "cells") };
        if (op == "set_area_cells") { expected["properties"]!["manual_revision"] = new JObject { ["type"] = "string" }; ((JArray)expected["required"]!).Add("manual_revision"); }
        var arguments = new JObject { ["map"] = new JObject { ["arg"] = "map" }, ["limit"] = 100 }; if (target != null) arguments["ids"] = new JArray(new JObject { ["arg"] = target });
        var extract = new JObject
        {
            ["kind"] = "field",
            ["object_id"] = target == null ? JValue.CreateNull() : new JObject { ["arg"] = target },
            ["path"] = new JArray(target == null ? "structure" : "write_cells"),
            ["allow_null"] = false,
            ["objects_path"] = target == null ? new JArray() : new JArray("objects"),
            ["identity_path"] = target == null ? new JArray() : new JArray("lookup_id"),
            ["status_path"] = new JArray("status"),
            ["status_value"] = target == null ? "ok" : "observed"
        };
        if (op == "set_area_cells") extract["require"] = new JArray(new JObject { ["path"] = new JArray("manual_ids_complete"), ["value"] = true });
        extract["match"] = new JArray(new JObject { ["path"] = new JArray("map_id"), ["value"] = new JObject { ["arg"] = "map" } });
        return new Registration(new JObject
        {
            ["op"] = op,
            ["schema"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties, ["required"] = required },
            ["expect_schema"] = expected,
            ["description"] = "Current player map only, with native designator qualification. Zone edits preserve Home, cannot overlap another zone and require one connected nonempty region. Roof areas cannot clear the opposite roof instruction implicitly.",
            ["observation"] = new JObject { ["read"] = new JObject { ["tool"] = "inspect_areas", ["arguments"] = arguments, ["native_operation"] = "inspect_areas" }, ["extract"] = extract }
        }, (r, o) => op == "set_area_cells" ? r.ResolveAreaMaskFields(o) : One(r.ResolveAreaZone(o), o));
    }
    // Property avoids cross-partial static initializer ordering.
    private static Registration[] AreaZoneCatalog => new[] { AreaZoneRegistration("create_zone", null), AreaZoneRegistration("create_allowed_area", null), AreaZoneRegistration("set_zone_cells", "zone"), AreaZoneRegistration("set_area_cells", "area"), AreaRemovalRegistration(true), AreaRemovalRegistration(false) };
    private IPreparedWriteField ResolveAreaZone(JObject operation)
    {
        if (operation.Properties().Any(p => !new[] { "op", "map", "zone", "area", "expect", "value", "evidence" }.Contains(p.Name))) throw new ArgumentException("Invalid area or zone operation.");
        var map = GameWorldReferences.ResolveMap((string?)operation["map"]); if ((string?)operation["map"] != map.GetUniqueLoadID()) throw new ArgumentException("Use the canonical native map ID for area and zone writes."); var op = (string?)operation["op"];
        if (op == "create_zone") return new ZoneAddition(map);
        if (op == "create_allowed_area") return new AllowedAreaAddition(map);
        if (op == "remove_zone") { var zone = AreaZoneReadings.ResolveZone(map, (string?)operation["zone"]); AreaZoneReadings.Supported(zone); return new RegionRemoval(map, zone); }
        if (op == "remove_allowed_area") { var area = AreaZoneReadings.ResolveArea(map, (string?)operation["area"]); if (area.GetType() != typeof(Area_Allowed)) throw new ArgumentException("Only ordinary allowed areas can be removed."); return new RegionRemoval(map, (Area_Allowed)area); }
        if (op == "set_zone_cells") { var zone = AreaZoneReadings.ResolveZone(map, (string?)operation["zone"]); AreaZoneReadings.Supported(zone); return new CellUpdate(map, zone); }

        throw new ArgumentException("Unsupported area or zone operation.");
    }
    private static void AreaWriteMap(Map map) { if (map.Disposed || map != Find.CurrentMap) throw new ArgumentException("Native area and zone qualification requires the player's current map."); }
    private static IntVec3[] DesiredCells(Map map, JToken value, bool zone, bool newValue = true)
    {
        if (value["cells"] is not JArray cells || newValue && cells.Count > 1024 || zone && cells.Count == 0) throw new ArgumentException("Select at most 1024 cells; a zone must remain nonempty.");
        var result = new List<IntVec3>();
        foreach (var t in cells)
        {
            if (t is not JObject row || row.Count != 2 || row["x"]?.Type != JTokenType.Integer || row["z"]?.Type != JTokenType.Integer) throw new ArgumentException("Each cell needs integer x and z only.");
            var c = new IntVec3((int)row["x"]!, 0, (int)row["z"]!); if (!c.InBounds(map) || c.Fogged(map)) throw new ArgumentException("Changed area or zone cells must be visible and in bounds."); result.Add(c);
        }
        if (result.Distinct().Count() != result.Count) throw new ArgumentException("Cell coordinates must be unique.");
        if (newValue && zone && result.Count > 0)
        {
            var remaining = new HashSet<IntVec3>(result); var pending = new Queue<IntVec3>(); pending.Enqueue(result[0]); remaining.Remove(result[0]);
            while (pending.Count != 0) { var c = pending.Dequeue(); foreach (var next in new[] { new IntVec3(c.x - 1, 0, c.z), new IntVec3(c.x + 1, 0, c.z), new IntVec3(c.x, 0, c.z - 1), new IntVec3(c.x, 0, c.z + 1) }) if (remaining.Remove(next)) pending.Enqueue(next); }
            if (remaining.Count != 0) throw new ArgumentException("A single zone must be connected by cardinally adjacent cells.");
        }
        return result.ToArray();
    }
    private static void QualifyZone(Map map, Zone? zone, string kind, IEnumerable<IntVec3> desired)
    {
        AreaWriteMap(map); Designator designator = kind == "stockpile" ? new Designator_ZoneAddStockpile_Resources() : new Designator_ZoneAdd_Growing();
        foreach (var c in desired)
        {
            var occupant = map.zoneManager.ZoneAt(c); if (occupant != null && !ReferenceEquals(occupant, zone)) throw new ArgumentException("A zone cannot overwrite another zone.");
            if (zone != null && zone.ContainsCell(c)) continue;
            var report = designator.CanDesignateCell(c); if (!report.Accepted) throw new ArgumentException("Native zone designation refused: " + report.Reason);
        }
    }
    private static void PreserveHome(Map map, Action action)
    {
        var home = map.areaManager.Home; var before = new HashSet<IntVec3>(home.ActiveCells);
        try { action(); } finally { foreach (var c in home.ActiveCells.Where(c => !before.Contains(c)).ToArray()) home[c] = false; foreach (var c in before) if (!home[c]) home[c] = true; }
    }
    private static void SetZoneCells(Zone zone, IntVec3[] desired)
    {
        var wanted = new HashSet<IntVec3>(desired);
        PreserveHome(zone.Map, () => { foreach (var c in desired) if (!zone.ContainsCell(c)) zone.AddCell(c); foreach (var c in zone.Cells.Where(c => !wanted.Contains(c)).ToArray()) zone.RemoveCell(c); });
    }
    private sealed class CellUpdate : IPreparedStructuralWrite, IWriteAggregateScope
    {
        private readonly Map map; private readonly Zone zone;
        internal CellUpdate(Map map, Zone zone) { this.map = map; this.zone = zone; }
        public string ObjectId => zone.GetUniqueLoadID(); public string Field => "zone_cells";
        public object NativeTarget => zone; public bool IsDestroyed => map.Disposed || !map.zoneManager.AllZones.Contains(zone); public bool MayChangeByGame => true;
        public string AggregateScope => ObjectId; public bool ChangesStructure => false;
        public JToken Read() => AreaZoneReadings.CellState(zone, zone.Cells);
        private IntVec3[] Qualify(JToken value, bool newValue = true) {
            var desired = DesiredCells(map, value, true, newValue);
            foreach (var cell in zone.Cells.Except(desired)) if (cell.Fogged(map)) throw new ArgumentException("Removed zone cells must be visible.");
            QualifyZone(map, zone, zone is Zone_Stockpile ? "stockpile" : "growing", desired); return desired;
        }
        public void Validate(JToken value) { if (value is not JObject obj || obj.Count != 1 || obj["cells"] == null) throw new ArgumentException("Only cells may be updated."); Qualify(value); }
        private void Apply(JToken value, bool newValue = true) => SetZoneCells(zone, Qualify(value, newValue));
        public void Write(JToken value) => Apply(value); public void Restore(JToken value) => Apply(value, false);
        public JToken AppliedValue() => Read(); public JToken UndoExpected() => Read(); public IPreparedWriteField CreateUndo() => new CellUndo(this);
        private sealed class CellUndo : IPreparedWriteField {
            private readonly CellUpdate owner; internal CellUndo(CellUpdate owner) { this.owner = owner; }
            public string ObjectId => owner.ObjectId; public string Field => owner.Field; public bool IsDestroyed => owner.IsDestroyed; public bool MayChangeByGame => true; public object NativeTarget => owner.NativeTarget;
            public JToken Read() => owner.Read(); public void Validate(JToken value) => owner.Qualify(value, false); public void Write(JToken value) => owner.Apply(value, false); public void Restore(JToken value) => owner.Apply(value, false);
        }
    }
    private sealed class ZoneAddition : IPreparedStructuralWrite, IWriteAggregateScope
    {
        private readonly Map map; private Zone? created; private bool removed;
        internal ZoneAddition(Map map) { this.map = map; }
        public string ObjectId => map.GetUniqueLoadID(); public string Field => "zone_structure"; public object NativeTarget => map; public bool IsDestroyed => map.Disposed; public bool MayChangeByGame => true;
        public string AggregateScope => ObjectId + ":zones"; public bool ChangesStructure => true;
        public JToken Read() { var state = AreaZoneReadings.Structure(map); if (created != null && !removed) state["created"] = map.zoneManager.AllZones.Contains(created) ? AreaZoneReadings.ZoneState(created) : JValue.CreateNull(); return state; }
        public void Validate(JToken value) { if (value is not JObject obj || obj.Properties().Any(p => !new[] { "kind", "cells", "label" }.Contains(p.Name)) || !new[] { "stockpile", "growing" }.Contains((string?)value["kind"])) throw new ArgumentException("Select stockpile or growing zone."); if (obj["label"] != null && (obj["label"]!.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)obj["label"]) || ((string)obj["label"]!).Length > 128)) throw new ArgumentException("Zone label must contain 1–128 characters."); QualifyZone(map, null, (string)value["kind"]!, DesiredCells(map, value, true)); }
        public void Write(JToken value) { Validate(value); created = (string?)value["kind"] == "stockpile" ? (Zone)new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager) : new Zone_Growing(map.zoneManager); if (value["label"] != null) created.RenamableLabel = (string)value["label"]!; map.zoneManager.RegisterZone(created); SetZoneCells(created, DesiredCells(map, value, true)); }
        public void Restore(JToken original) { if (created != null && map.zoneManager.AllZones.Contains(created)) created.Delete(false); removed = true; }
        public JToken AppliedValue() => Read(); public JToken UndoExpected() => Read(); public IPreparedWriteField CreateUndo() => new ZoneAdditionUndo(this);
        private void Unreferenced() { if (created is Zone_Stockpile grouped && grouped.GetSlotGroup().StorageGroup != null) throw new InvalidOperationException("Grouped stockpile lifecycle is unsupported."); if (created != null && AreaZoneReadings.ZoneReferences(created).Count != 0) throw new InvalidOperationException("Created stockpile is referenced by a production bill."); }
        private sealed class ZoneAdditionUndo : IPreparedStructuralWrite
        {
            private readonly ZoneAddition owner; private IntVec3[]? before; internal ZoneAdditionUndo(ZoneAddition owner) { this.owner = owner; }
            public string ObjectId => owner.ObjectId; public string Field => owner.Field; public bool IsDestroyed => owner.IsDestroyed; public bool MayChangeByGame => true; public object NativeTarget => owner.map; public JToken Read() => owner.Read();
            public void Validate(JToken value) { AreaWriteMap(owner.map); if (owner.created == null || !owner.map.zoneManager.AllZones.Contains(owner.created)) throw new ArgumentException("Original created zone no longer exists."); owner.Unreferenced(); }
            public void Write(JToken value) { Validate(value); before = owner.created!.Cells.ToArray(); owner.Restore(value); }
            public void Restore(JToken value) { if (owner.created == null || before == null) return; owner.map.zoneManager.RegisterZone(owner.created); SetZoneCells(owner.created, before); owner.removed = false; }
            public JToken AppliedValue() => Read(); public JToken UndoExpected() => Read(); public IPreparedWriteField CreateUndo() => throw new NotSupportedException();
        }
    }
    private sealed class AllowedAreaAddition : IPreparedStructuralWrite, IWriteAggregateScope
    {
        private readonly Map map; private Area_Allowed? created; private bool removed; private int index;
        internal AllowedAreaAddition(Map map) { this.map = map; }
        public string ObjectId => map.GetUniqueLoadID(); public string Field => "area_structure"; public object NativeTarget => map;
        public bool IsDestroyed => map.Disposed; public bool MayChangeByGame => true; public string AggregateScope => ObjectId + ":areas"; public bool ChangesStructure => true;
        public JToken Read() { var state = AreaZoneReadings.Structure(map); if (created != null && !removed) state["created"] = map.areaManager.AllAreas.Contains(created) ? AreaZoneReadings.AreaState(created) : JValue.CreateNull(); return state; }
        public void Validate(JToken value)
        {
            AreaWriteMap(map);
            if (!map.areaManager.CanMakeNewAllowed()) throw new ArgumentException("Native allowed-area capacity is reached.");
            if (value is not JObject obj || obj.Properties().Any(p => p.Name != "cells" && p.Name != "label")) throw new ArgumentException("Only cells and an optional label are accepted.");
            if (obj["label"] != null && (obj["label"]!.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)obj["label"]) || ((string)obj["label"]!).Length > 128)) throw new ArgumentException("Area label must contain 1–128 characters.");
            DesiredCells(map, value, false);
        }
        public void Write(JToken value) { Validate(value); if (!map.areaManager.TryMakeNewAllowed(out created)) throw new InvalidOperationException("Native allowed-area creation refused."); if (value["label"] != null) created.SetLabel((string)value["label"]!); foreach (var c in DesiredCells(map, value, false)) created[c] = true; index = map.areaManager.AllAreas.IndexOf(created); }
        public void Restore(JToken value) { if (created != null && map.areaManager.AllAreas.Contains(created)) { if (AreaZoneReadings.AreaUsers(created).Count != 0) throw new InvalidOperationException("Created area is referenced by a pawn."); created.Delete(); } removed = true; }
        public JToken AppliedValue() => Read(); public JToken UndoExpected() => Read(); public IPreparedWriteField CreateUndo() => new AllowedAreaUndo(this);
        private sealed class AllowedAreaUndo : IPreparedStructuralWrite
        {
            private readonly AllowedAreaAddition owner; internal AllowedAreaUndo(AllowedAreaAddition owner) { this.owner = owner; }
            public string ObjectId => owner.ObjectId; public string Field => owner.Field; public object NativeTarget => owner.map; public bool IsDestroyed => owner.IsDestroyed; public bool MayChangeByGame => true;
            public JToken Read() => owner.Read();
            public void Validate(JToken value) { AreaWriteMap(owner.map); if (owner.created == null || !owner.map.areaManager.AllAreas.Contains(owner.created) || AreaZoneReadings.AreaUsers(owner.created).Count != 0) throw new ArgumentException("Only the original unreferenced area can be undone."); }
            public void Write(JToken value) { Validate(value); owner.Restore(value); }
            public void Restore(JToken value) { if (owner.created == null) return; if (!owner.map.areaManager.AllAreas.Contains(owner.created)) { owner.map.areaManager.AllAreas.Insert(owner.index, owner.created); } owner.removed = false; }
            public JToken AppliedValue() => Read(); public JToken UndoExpected() => Read(); public IPreparedWriteField CreateUndo() => throw new NotSupportedException();
        }
    }
    private static Registration AreaRemovalRegistration(bool zone)
    {
        var op = zone ? "remove_zone" : "remove_allowed_area"; var target = zone ? "zone" : "area";
        var expected = JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'map_id':{'type':'string','minLength':1},'zone_ids':{'type':'array','items':{'type':'string'}},'allowed_area_ids':{'type':'array','items':{'type':'string'}},'removed_id':{'type':'string','minLength':1},'removed':{'type':'object'}},'required':['map_id','zone_ids','allowed_area_ids','removed_id','removed']}");
        var properties = new JObject
        {
            ["op"] = new JObject { ["const"] = op },
            ["map"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            [target] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            ["value"] = new JObject { ["const"] = false }
        };
        return new Registration(new JObject
        {
            ["op"] = op,
            ["schema"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties, ["required"] = new JArray("op", "map", target, "value") },
            ["expect_schema"] = expected,
            ["description"] = "Remove an ordinary unreferenced zone or allowed area. Ground inventory and plants are retained. Undo retains the original native object and requires its cells, configuration, references and map structure to remain compatible.",
            ["observation"] = new JObject
            {
                ["read"] = new JObject { ["tool"] = "inspect_areas", ["arguments"] = new JObject { ["map"] = new JObject { ["arg"] = "map" }, ["ids"] = new JArray(new JObject { ["arg"] = target }), ["limit"] = 100 }, ["native_operation"] = "inspect_areas" },
                ["extract"] = new JObject
                {
                    ["kind"] = "field",
                    ["object_id"] = new JObject { ["arg"] = target },
                    ["path"] = new JArray("delete_guard"),
                    ["allow_null"] = false,
                    ["objects_path"] = new JArray("objects"),
                    ["identity_path"] = new JArray("id"),
                    ["status_path"] = new JArray("status"),
                    ["status_value"] = "observed",
                    ["match"] = new JArray(new JObject { ["path"] = new JArray("map_id"), ["value"] = new JObject { ["arg"] = "map" } })
                }
            }
        }, (r, o) => One(r.ResolveAreaZone(o), o));
    }
    private sealed class RegionRemoval : IPreparedStructuralWrite, IWriteAggregateScope
    {
        private readonly Map map; private readonly Zone? zone; private readonly Area_Allowed? area; private readonly string id;
        private IntVec3[]? originalCells; private int index; private bool removed; private JToken? retainedState;
        internal RegionRemoval(Map map, Zone zone) { this.map = map; this.zone = zone; id = zone.GetUniqueLoadID(); }
        internal RegionRemoval(Map map, Area_Allowed area) { this.map = map; this.area = area; id = area.GetUniqueLoadID(); }
        public string ObjectId => map.GetUniqueLoadID(); public string Field => (zone != null ? "zone_removed:" : "area_removed:") + id;
        public object NativeTarget => map; public bool IsDestroyed => map.Disposed; public bool MayChangeByGame => true;
        public string AggregateScope => id; public bool ChangesStructure => true;
        public JToken Read() => AreaZoneReadings.RemovalState(map, id, zone != null);
        private JToken OriginalState() => zone != null ? AreaZoneReadings.ZoneState(zone) : AreaZoneReadings.AreaState(area!);
        private bool Present() => zone != null ? map.zoneManager.AllZones.Contains(zone) : map.areaManager.AllAreas.Contains(area!);
        private void Unreferenced()
        {
            if (zone is Zone_Stockpile grouped && grouped.GetSlotGroup().StorageGroup != null) throw new InvalidOperationException("Grouped stockpile lifecycle is unsupported.");
            if (zone != null && AreaZoneReadings.ZoneReferences(zone).Count != 0) throw new InvalidOperationException("Zone is referenced by a production bill.");
            if (area != null && AreaZoneReadings.AreaUsers(area).Count != 0) throw new InvalidOperationException("Allowed area is referenced by a pawn.");
        }
        public void Validate(JToken value)
        {
            AreaWriteMap(map);
            if (value.Type != JTokenType.Boolean || (bool)value) throw new ArgumentException("Removal value must be false.");
            if (!Present()) throw new ArgumentException("The original native region is no longer registered.");
            if (zone != null) AreaZoneReadings.Supported(zone);
            else if (area!.GetType() != typeof(Area_Allowed)) throw new ArgumentException("Only ordinary allowed areas may be removed.");
            Unreferenced();
        }
        public void Write(JToken value)
        {
            Validate(value); originalCells = (zone?.Cells ?? area!.ActiveCells).ToArray(); index = zone != null ? map.zoneManager.AllZones.IndexOf(zone) : map.areaManager.AllAreas.IndexOf(area!);
            if (zone != null) zone.Delete(false); else area!.Delete();
            removed = true; retainedState = OriginalState().DeepClone();
        }
        private void QualifyRestore()
        {
            AreaWriteMap(map);
            if (zone != null)
            {
                if (map.zoneManager.AllZones.Any(other => other.GetUniqueLoadID() == id && !ReferenceEquals(other, zone))) throw new InvalidOperationException("Native zone ID has been reused by another object.");
                if (originalCells != null) { foreach (var c in originalCells) if (!c.InBounds(map) || c.Fogged(map)) throw new ArgumentException("Original zone cells must remain visible and in bounds."); QualifyZone(map, Present() ? zone : null, zone is Zone_Stockpile ? "stockpile" : "growing", originalCells); }
            }
            else
            {
                if (map.areaManager.AllAreas.Any(other => other.GetUniqueLoadID() == id && !ReferenceEquals(other, area))) throw new InvalidOperationException("Native allowed-area ID has been reused by another object.");
                if (!Present() && !map.areaManager.CanMakeNewAllowed()) throw new ArgumentException("Native allowed-area capacity is reached.");
            }
        }
        public void Restore(JToken original)
        {
            if (originalCells == null) return; QualifyRestore();
            if (zone != null)
            {
                if (!Present()) { map.zoneManager.RegisterZone(zone); map.zoneManager.AllZones.Remove(zone); map.zoneManager.AllZones.Insert(index, zone); }
                SetZoneCells(zone, originalCells);
            }
            else if (!Present()) map.areaManager.AllAreas.Insert(index, area!);
            removed = false;
        }
        public JToken AppliedValue() => Read(); public JToken UndoExpected() => Read(); public IPreparedWriteField CreateUndo() => new RegionRestore(this);
        private sealed class RegionRestore : IPreparedStructuralWrite, IWriteAggregateScope
        {
            private readonly RegionRemoval owner; internal RegionRestore(RegionRemoval owner) { this.owner = owner; }
            public string ObjectId => owner.ObjectId; public string Field => owner.Field; public object NativeTarget => owner.map; public bool IsDestroyed => owner.IsDestroyed; public bool MayChangeByGame => true;
            public string AggregateScope => owner.AggregateScope; public bool ChangesStructure => true;
            public JToken Read() => owner.Read();
            public void Validate(JToken value)
            {
                if (!owner.removed || owner.Present() || owner.retainedState == null || !JToken.DeepEquals(owner.retainedState, owner.OriginalState())) throw new ArgumentException("Retained original region changed after removal.");
                owner.Unreferenced(); owner.QualifyRestore();
            }
            public void Write(JToken value) { Validate(value); owner.Restore(value); }
            public void Restore(JToken value) => owner.Write(new JValue(false));
            public JToken AppliedValue() => Read(); public JToken UndoExpected() => Read(); public IPreparedWriteField CreateUndo() => throw new NotSupportedException();
        }
    }
    private sealed class RegionRemovalLedger : IPreparedWriteField
    {
        private readonly Map map; private readonly string id; private readonly bool zone;
        internal RegionRemovalLedger(Map map, string id, bool zone) { this.map = map; this.id = id; this.zone = zone; }
        public string ObjectId => map.GetUniqueLoadID(); public string Field => (zone ? "zone_removed:" : "area_removed:") + id; public object NativeTarget => map; public bool IsDestroyed => map.Disposed; public bool MayChangeByGame => true;
        public JToken Read() => AreaZoneReadings.RemovalState(map, id, zone);
        public void Validate(JToken value) => throw new NotSupportedException(); public void Write(JToken value) => throw new NotSupportedException(); public void Restore(JToken value) => throw new NotSupportedException();
    }
    private sealed class AreaStructureLedger : IPreparedWriteField
    {
        private readonly Map map; private readonly string? createdId;
        internal AreaStructureLedger(Map map, JToken recorded) { this.map = map; createdId = (string?)recorded["created"]?["id"]; }
        public string ObjectId => map.GetUniqueLoadID(); public string Field => "area_structure"; public bool IsDestroyed => map.Disposed; public bool MayChangeByGame => true; public object NativeTarget => map;
        public JToken Read() { var state = AreaZoneReadings.Structure(map); if (createdId != null) { var area = map.areaManager.AllAreas.SingleOrDefault(a => a.GetUniqueLoadID() == createdId); state["created"] = area == null ? JValue.CreateNull() : AreaZoneReadings.AreaState(area); } return state; }
        public void Validate(JToken value) => throw new NotSupportedException(); public void Write(JToken value) => throw new NotSupportedException(); public void Restore(JToken value) => throw new NotSupportedException();
    }
    private sealed class ZoneStructureLedger : IPreparedWriteField
    {
        private readonly Map map; private readonly string? createdId; internal ZoneStructureLedger(Map map, JToken recorded) { this.map = map; createdId = (string?)recorded["created"]?["id"]; }
        public string ObjectId => map.GetUniqueLoadID(); public string Field => "zone_structure"; public bool IsDestroyed => map.Disposed; public bool MayChangeByGame => true; public object NativeTarget => map;
        public JToken Read() { var state = AreaZoneReadings.Structure(map); if (createdId != null) { var zone = map.zoneManager.AllZones.SingleOrDefault(z => z.GetUniqueLoadID() == createdId); state["created"] = zone == null ? JValue.CreateNull() : AreaZoneReadings.ZoneState(zone); } return state; }
        public void Validate(JToken value) => throw new NotSupportedException(); public void Write(JToken value) => throw new NotSupportedException(); public void Restore(JToken value) => throw new NotSupportedException();
    }
    internal IPreparedWriteField? ResolveAreaZoneLedger(string id, string field, object? target, JToken value)
    {
        var flags = ResolveAreaFlagsLedger(id, field, target, value); if (flags != null) return flags;
        if (target is Zone zone && zone.GetUniqueLoadID() == id && field == "zone_cells") return new CellUpdate(zone.Map, zone);

        if (target is Map map && map.GetUniqueLoadID() == id) { if (field == "zone_structure") return new ZoneStructureLedger(map, value); if (field == "area_structure") return new AreaStructureLedger(map, value); if (field.StartsWith("zone_removed:", StringComparison.Ordinal)) return new RegionRemovalLedger(map, field.Substring("zone_removed:".Length), true); if (field.StartsWith("area_removed:", StringComparison.Ordinal)) return new RegionRemovalLedger(map, field.Substring("area_removed:".Length), false); }
        return null;
    }
}

