using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

// Individual area cell flags, addressed as area_flag:x:z ledger fields.
internal sealed class AreaCellFlagsWriteFields : IWriteFieldSet
{
    public IEnumerable<WriteOperationBinding> Operations => new[] {
        new WriteOperationBinding("set_area_cell_flags", ResolveAreaFlagFields),
        new WriteOperationBinding("set_area_cells", ResolveAreaMaskFields),
    };
    private static Area AreaFlagTarget(JObject operation)
    {
        if (operation.Properties().Any(p => !new[] { "op", "map", "area", "value", "expect", "evidence" }.Contains(p.Name))) throw new ArgumentException("Invalid area-cell operation.");
        var map = GameWorldReferences.ResolveMap((string?)operation["map"]); if ((string?)operation["map"] != map.GetUniqueLoadID()) throw new ArgumentException("Use the canonical native map ID for area-cell writes.");
        var area = AreaZoneReadings.ResolveArea(map, (string?)operation["area"]); if (AreaZoneReadings.Kind(area) == "unsupported") throw new ArgumentException("Unsupported native area type."); return area;
    }
    private static JObject CellFlag(string identity, bool enabled, JToken? manualId) => new() { ["native_instance"] = identity, ["enabled"] = enabled, ["manual_id"] = manualId?.DeepClone() ?? JValue.CreateNull() };
    private static bool ValidManualId(JToken? token) => token?.Type == JTokenType.Null || token?.Type == JTokenType.String && Guid.TryParse((string?)token, out _);
    private static IReadOnlyList<ResolvedWriteField> ResolveAreaFlagFields(JObject operation)
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
    private static IReadOnlyList<ResolvedWriteField> ResolveAreaMaskFields(JObject operation)
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
            AreaZoneWriteFields.AreaWriteMap(map);
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
    public IReadableWriteField? ResolveLedger(string id, string field, object? target, JToken value)
    {
        if (!field.StartsWith("area_flag:", StringComparison.Ordinal) || target is not Area area || area.GetUniqueLoadID() != id) return null;
        var pieces = field.Split(':'); if (pieces.Length != 3 || !int.TryParse(pieces[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) || !int.TryParse(pieces[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var z)) return null;
        var cell = new IntVec3(x, 0, z); if (!cell.InBounds(area.Map)) return null; var result = new AreaFlagCell(area, cell); return result.Field == field ? result : null;
    }
}
