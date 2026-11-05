using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal static class AreaCellFlags
{
    internal static string FieldName(IntVec3 cell) => "area_flag:" + cell.x.ToString(CultureInfo.InvariantCulture) + ":" + cell.z.ToString(CultureInfo.InvariantCulture);
    internal static IntVec3[] Coordinates(Map map, JToken selector, bool allowManualId = false)
    {
        if (selector is not JArray cells || cells.Count < 1 || cells.Count > 64) throw new ArgumentException("Select between 1 and 64 area cells per transaction.");
        var result = new List<IntVec3>();
        foreach (var token in cells) {
            if (token is not JObject row || row.Properties().Any(p => p.Name != "x" && p.Name != "z" && p.Name != "enabled" && (!allowManualId || p.Name != "manual_id")) || row["x"]?.Type != JTokenType.Integer || row["z"]?.Type != JTokenType.Integer || row["enabled"] != null && row["enabled"]!.Type != JTokenType.Boolean) throw new ArgumentException("Each selected area cell needs integer x/z and an optional boolean enabled flag.");
            var cell = new IntVec3((int)row["x"]!, 0, (int)row["z"]!);
            if (!cell.InBounds(map) || cell.Fogged(map)) throw new ArgumentException("Selected area cells must be visible and in bounds.");
            result.Add(cell);
        }
        if (result.Distinct().Count() != result.Count) throw new ArgumentException("Selected area coordinates must be unique.");
        return result.OrderBy(c => c.z).ThenBy(c => c.x).ToArray();
    }
    internal static JObject Read(Area area, IEnumerable<IntVec3> cells) => new() {
        ["native_instance"] = AreaZoneReadings.Instance(area),
        ["cells"] = new JArray(cells.OrderBy(c => c.z).ThenBy(c => c.x).Select(c => new JObject { ["x"] = c.x, ["z"] = c.z, ["enabled"] = area[c], ["manual_id"] = NativeAreaManualChanges.ManualId(area, c) is string id ? new JValue(id) : JValue.CreateNull() }))
    };
    internal static JObject ReadQuery(Map map, string[] ids, JToken selector)
    {
        if (ids.Length != 1) throw new ArgumentException("A local area-cell read requires exactly one area ID.");
        var area = AreaZoneReadings.ResolveArea(map, ids[0]); if (AreaZoneReadings.Kind(area) == "unsupported") throw new ArgumentException("Unsupported native area type.");
        var coordinates = Coordinates(map, selector);
        return new JObject { ["status"] = "ok", ["map_id"] = map.GetUniqueLoadID(), ["structure"] = AreaZoneReadings.Structure(map), ["structure_complete"] = true,
            ["objects"] = new JArray(new JObject { ["id"] = area.GetUniqueLoadID(), ["lookup_id"] = ids[0], ["map_id"] = map.GetUniqueLoadID(), ["kind"] = AreaZoneReadings.Kind(area),
                ["label"] = area.Label, ["status"] = "observed", ["complete"] = true, ["manual_ids_complete"] = NativeAreaManualChanges.Complete, ["write_flags"] = Read(area, coordinates),
                ["cell_fields"] = new JArray(coordinates.Select(cell => new JObject { ["x"] = cell.x, ["z"] = cell.z, ["object"] = area.GetUniqueLoadID(), ["field"] = FieldName(cell) })) }), ["total"] = 1, ["complete"] = true,
            ["missing_ids"] = new JArray(), ["write_map_supported"] = map == Find.CurrentMap, ["coverage"] = "Only explicitly selected area cells; all other cells are unchanged and outside this observation." };
    }
}

internal sealed partial class VerseWriteFieldResolver
{
    private static Registration[] AreaCellFlagsCatalog => new[] { AreaFlagsRegistration() };
    private static Registration AreaFlagsRegistration()
    {
        var cells = JObject.Parse("{'type':'array','minItems':1,'maxItems':64,'items':{'type':'object','additionalProperties':false,'properties':{'x':{'type':'integer'},'z':{'type':'integer'},'enabled':{'type':'boolean'}},'required':['x','z','enabled']}}");
        var value = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = new JObject { ["cells"] = cells }, ["required"] = new JArray("cells") };
        var expected = (JObject)value.DeepClone(); expected["properties"]!["native_instance"] = new JObject { ["type"] = "string", ["minLength"] = 1 }; ((JArray)expected["required"]!).Add("native_instance");
        expected["properties"]!["cells"]!["items"]!["properties"]!["manual_id"] = new JObject { ["type"] = new JArray("string", "null") };
        ((JArray)expected["properties"]!["cells"]!["items"]!["required"]!).Add("manual_id");
        return new Registration(new JObject { ["op"] = "set_area_cell_flags", ["schema"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JObject { ["op"] = new JObject { ["const"] = "set_area_cell_flags" }, ["map"] = new JObject { ["type"] = "string", ["minLength"] = 1 }, ["area"] = new JObject { ["type"] = "string", ["minLength"] = 1 }, ["value"] = value }, ["required"] = new JArray("op", "map", "area", "value") }, ["expect_schema"] = expected,
            ["description"] = "Update at most 64 selected area cells. Every unselected cell is preserved. Each selected coordinate, including an unchanged requested flag, has a stable area_flag:x:z ledger field. Requires current-map native roof qualification and never clears the opposite roof instruction.",
            ["observation"] = new JObject {
                ["read"] = new JObject { ["tool"] = "inspect_areas", ["native_operation"] = "inspect_areas", ["arguments"] = new JObject { ["map"] = new JObject { ["arg"] = "map" }, ["ids"] = new JArray(new JObject { ["arg"] = "area" }), ["cells"] = new JObject { ["value_path"] = new JArray("cells") } } },
                ["extract"] = new JObject { ["kind"] = "field", ["object_id"] = new JObject { ["arg"] = "area" }, ["path"] = new JArray("write_flags"), ["allow_null"] = false, ["objects_path"] = new JArray("objects"), ["identity_path"] = new JArray("lookup_id"),
                    ["status_path"] = new JArray("status"), ["status_value"] = "observed", ["require"] = new JArray(new JObject { ["path"] = new JArray("manual_ids_complete"), ["value"] = true }), ["match"] = new JArray(new JObject { ["path"] = new JArray("map_id"), ["value"] = new JObject { ["arg"] = "map" } }) } } }, (r, operation) => r.ResolveAreaFlagFields(operation));
    }
    private Area AreaFlagTarget(JObject operation)
    {
        if (operation.Properties().Any(p => !new[] { "op", "map", "area", "value", "expect", "evidence" }.Contains(p.Name))) throw new ArgumentException("Invalid area-cell operation.");
        var map = GameWorldReferences.ResolveMap((string?)operation["map"]); if ((string?)operation["map"] != map.GetUniqueLoadID()) throw new ArgumentException("Use the canonical native map ID for area-cell writes.");
        var area = AreaZoneReadings.ResolveArea(map, (string?)operation["area"]); if (AreaZoneReadings.Kind(area) == "unsupported") throw new ArgumentException("Unsupported native area type."); return area;
    }
    private static JObject CellFlag(string identity, bool enabled, JToken? manualId) => new() { ["native_instance"] = identity, ["enabled"] = enabled, ["manual_id"] = manualId?.DeepClone() ?? JValue.CreateNull() };
    private static bool ValidManualId(JToken? token) => token?.Type == JTokenType.Null || token?.Type == JTokenType.String && Guid.TryParse((string?)token, out _);
    private IReadOnlyList<ResolvedWriteField> ResolveAreaFlagFields(JObject operation)
    {
        var area = AreaFlagTarget(operation);
        if (operation["value"] is not JObject value || value.Count != 1 || value["cells"] is not JArray desired || desired.Any(c => c["enabled"]?.Type != JTokenType.Boolean)
            || operation["expect"] is not JObject expected || expected.Count != 2 || expected["native_instance"]?.Type != JTokenType.String || expected["cells"] is not JArray before || before.Any(c => c["enabled"]?.Type != JTokenType.Boolean || !ValidManualId(c["manual_id"]))) throw new ArgumentException("Complete local expected flags and desired flags required.");
        var selected = AreaCellFlags.Coordinates(area.Map, desired); var observedCoordinates = AreaCellFlags.Coordinates(area.Map, before, true);
        if (!selected.SequenceEqual(observedCoordinates)) throw new ArgumentException("Expected and desired local area coordinates must match.");
        var oldFlags = before.ToDictionary(c => new IntVec3((int)c["x"]!, 0, (int)c["z"]!), c => (bool)c["enabled"]!);
        var oldManualIds = before.ToDictionary(c => new IntVec3((int)c["x"]!, 0, (int)c["z"]!), c => c["manual_id"]!);
        var newFlags = desired.ToDictionary(c => new IntVec3((int)c["x"]!, 0, (int)c["z"]!), c => (bool)c["enabled"]!);
        var identity = (string)expected["native_instance"]!;
        return selected.Select(cell => new ResolvedWriteField(new AreaFlagCell(area, cell), CellFlag(identity, oldFlags[cell], oldManualIds[cell]), CellFlag(identity, newFlags[cell], oldManualIds[cell]))).ToArray();
    }
    private static IntVec3[] MaskCoordinates(Map map, JToken? token, bool newValue)
    {
        if (token is not JArray cells || newValue && cells.Count > 1024) throw new ArgumentException("A complete area mask is required; use local flag batches for large desired masks.");
        var result = new List<IntVec3>();
        foreach (var t in cells) { if (t is not JObject row || row.Count != 2 || row["x"]?.Type != JTokenType.Integer || row["z"]?.Type != JTokenType.Integer) throw new ArgumentException("Area mask cells require integer x/z only."); var cell = new IntVec3((int)row["x"]!, 0, (int)row["z"]!); if (!cell.InBounds(map)) throw new ArgumentException("Area mask coordinates must be in bounds."); result.Add(cell); }
        if (result.Distinct().Count() != result.Count) throw new ArgumentException("Area mask coordinates must be unique."); return result.OrderBy(c => c.z).ThenBy(c => c.x).ToArray();
    }
    private IReadOnlyList<ResolvedWriteField> ResolveAreaMaskFields(JObject operation)
    {
        var area = AreaFlagTarget(operation);
        if (operation["value"] is not JObject value || value.Count != 1 || operation["expect"] is not JObject expected || expected.Count != 3 || expected["native_instance"]?.Type != JTokenType.String || expected["manual_revision"]?.Type != JTokenType.String) throw new ArgumentException("Complete old area mask and desired mask required.");
        var before = new HashSet<IntVec3>(MaskCoordinates(area.Map, expected["cells"], false)); var desired = new HashSet<IntVec3>(MaskCoordinates(area.Map, value["cells"], true));
        var identity = (string)expected["native_instance"]!;
        var guard = new WholeAreaGuard(area, new JObject { ["native_instance"] = identity, ["cells"] = AreaZoneReadings.Cells(before), ["manual_revision"] = expected["manual_revision"]!.DeepClone() });
        // Only a complete mask + manual revision proof may authorize binding
        // live per-cell generations to this full-mask observation.
        guard.Validate();
        var changed = before.Except(desired).Concat(desired.Except(before)).OrderBy(c => c.z).ThenBy(c => c.x).ToArray();
        if (changed.Length == 0) throw new ArgumentException("no_changes: the requested area mask already matches its observed mask.");
        if (changed.Length > 64) throw new ArgumentException("Area mask changes exceed 64 cells; submit explicit local flag batches.");
        return changed.Select(cell => { var manualId = NativeAreaManualChanges.ManualId(area, cell); return new ResolvedWriteField(new AreaFlagCell(area, cell, guard), CellFlag(identity, before.Contains(cell), manualId == null ? JValue.CreateNull() : new JValue(manualId)), CellFlag(identity, desired.Contains(cell), manualId == null ? JValue.CreateNull() : new JValue(manualId))); }).ToArray();
    }
    private sealed class WholeAreaGuard
    {
        internal readonly Area Area; private readonly JToken expected; private bool checkedBeforeWrite; internal bool Used;
        internal WholeAreaGuard(Area area, JToken expected) { Area = area; this.expected = expected.DeepClone(); }
        internal void Validate() { NativeAreaManualChanges.RequireAvailable(); if (Used || checkedBeforeWrite) return; var current = AreaZoneReadings.CellState(Area, Area.ActiveCells); if (!JToken.DeepEquals(expected, current)) throw new WritePreconditionConflictException(expected, current); checkedBeforeWrite = true; }
    }
    private sealed class AreaFlagCell : IPreparedWriteField, IWriteAggregateScope, IWriteConflictScope
    {
        private readonly Area area; private readonly Map map; private readonly IntVec3 cell; private readonly WholeAreaGuard? whole;
        internal AreaFlagCell(Area area, IntVec3 cell, WholeAreaGuard? whole = null) { this.area = area; map = area.Map; this.cell = cell; this.whole = whole; }
        public string ObjectId => area.GetUniqueLoadID(); public string Field => AreaCellFlags.FieldName(cell); public object NativeTarget => area;
        public bool IsDestroyed => map.Disposed || !map.areaManager.AllAreas.Contains(area); public bool MayChangeByGame => true; public string AggregateScope => ObjectId; public bool ChangesStructure => false;
        public bool ConflictsWith(IPreparedWriteField other) => whole != null && other is AreaFlagCell flag && ReferenceEquals(flag.area, area) && !ReferenceEquals(flag.whole, whole);
        public JToken Read() => CellFlag(AreaZoneReadings.Instance(area), area[cell], NativeAreaManualChanges.ManualId(area, cell) is string manualId ? new JValue(manualId) : JValue.CreateNull());
        private void Qualify(JToken value, bool requireIdentity = true)
        {
            NativeAreaManualChanges.RequireAvailable();
            AreaWriteMap(map);
            if (IsDestroyed || !cell.InBounds(map) || cell.Fogged(map) || value is not JObject obj || obj.Count != 3 || !ValidManualId(obj["manual_id"]) || obj["enabled"]?.Type != JTokenType.Boolean || obj["native_instance"]?.Type != JTokenType.String || requireIdentity && (string?)obj["native_instance"] != AreaZoneReadings.Instance(area)) throw new ArgumentException("Original native area, visible coordinate and exact native-instance flag required.");
            if (!(bool)obj["enabled"]! || area[cell] || area is not Area_BuildRoof && area is not Area_NoRoof) return;
            var other = area is Area_BuildRoof ? (Area)map.areaManager.NoRoof : map.areaManager.BuildRoof;
            if (other[cell]) throw new ArgumentException("Clear the opposing roof instruction explicitly first.");
            Designator designator = area is Area_BuildRoof ? new Designator_AreaBuildRoof() : new Designator_AreaNoRoof(); var report = designator.CanDesignateCell(cell); if (!report.Accepted) throw new ArgumentException("Native roof designation refused: " + report.Reason);
        }
        public void Validate(JToken value) { whole?.Validate(); Qualify(value, false); }
        public void Write(JToken value) { Qualify(value); if (!JToken.DeepEquals(value["manual_id"], Read()["manual_id"])) throw new WritePreconditionConflictException(value, Read()); if (whole != null) whole.Used = true; area[cell] = (bool)value["enabled"]!; }
        public void Restore(JToken value) { Qualify(value); if (!JToken.DeepEquals(value["manual_id"], Read()["manual_id"])) throw new WritePreconditionConflictException(value, Read()); area[cell] = (bool)value["enabled"]!; }
    }
    private IPreparedWriteField? ResolveAreaFlagsLedger(string id, string field, object? target, JToken value)
    {
        if (!field.StartsWith("area_flag:", StringComparison.Ordinal) || target is not Area area || area.GetUniqueLoadID() != id) return null;
        var pieces = field.Split(':'); if (pieces.Length != 3 || !int.TryParse(pieces[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) || !int.TryParse(pieces[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var z)) return null;
        var cell = new IntVec3(x, 0, z); if (!cell.InBounds(area.Map)) return null; var result = new AreaFlagCell(area, cell); return result.Field == field ? result : null;
    }
}
