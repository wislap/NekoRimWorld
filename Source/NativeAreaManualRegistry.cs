using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Verse;

namespace Neko.RimWorld;

internal sealed class NativeAreaManualRecord : IExposable
{
    internal string Id = "", MapId = "", NativeType = "", NativeInstance = "", Revision = "";
    internal Area? Target;
    internal List<string>? Sources = new();
    public NativeAreaManualRecord() { }
    public void ExposeData()
    {
        Scribe_Values.Look(ref Id, "id", ""); Scribe_Values.Look(ref MapId, "mapId", ""); Scribe_Values.Look(ref NativeType, "nativeType", "");
        Scribe_Values.Look(ref NativeInstance, "nativeInstance", ""); Scribe_Values.Look(ref Revision, "revision", "");
        Scribe_References.Look(ref Target, "target"); Scribe_Collections.Look(ref Sources, "sources", LookMode.Value);
    }
}

// An area fence is established by observation; cell fences are sparse and only
// established by explicit selectors or committed AI fields. Neither lookup nor
// native UI bulk handling enumerates Area.ActiveCells or the map's population.
internal sealed class NativeAreaManualRegistry
{
    private sealed class ReferenceComparer : IEqualityComparer<Area>
    {
        public bool Equals(Area? a, Area? b) => ReferenceEquals(a, b);
        public int GetHashCode(Area value) => RuntimeHelpers.GetHashCode(value);
    }
    private sealed class CellSource
    {
        internal readonly JObject Value; internal readonly string Json;
        internal CellSource(JObject value, string json) { Value = value; Json = json; }
    }
    private sealed class Fence
    {
        internal readonly Area Area; internal readonly string Instance; internal string Revision;
        internal readonly HashSet<IntVec3> Watched = new(); internal readonly Dictionary<IntVec3, CellSource> Sources = new();
        internal Fence(Area area, string instance, string revision) { Area = area; Instance = instance; Revision = revision; }
    }
    private readonly Dictionary<Area, Fence> fences = new(new ReferenceComparer());
    private List<NativeAreaManualRecord>? records = new();
    private readonly List<JObject> invalid = new();
    internal bool Valid { get; private set; } = true;
    private static string NewId() => Guid.NewGuid().ToString("N");
    private static bool GuidId(JToken? token) => token?.Type == JTokenType.String && Guid.TryParseExact((string?)token, "N", out _);
    private static bool Text(JToken? token) => token?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string?)token);
    private static bool Exists(Area area) => area.Map != null && !area.Map.Disposed && Find.Maps.Any(m => ReferenceEquals(m, area.Map))
        && area.Map.areaManager.AllAreas.Any(a => ReferenceEquals(a, area));
    private void RequireValid() { if (!Valid) throw new InvalidOperationException("Manual area provenance is invalid; original save records are preserved."); }
    private Fence Get(Area area)
    {
        RequireValid();
        if (fences.TryGetValue(area, out var fence)) return fence;
        if (!Exists(area) || AreaZoneReadings.Kind(area) == "unsupported") throw new ArgumentException("Manual area observation requires a supported live original area.");
        fence = new Fence(area, NativeObjectIdentities.Token(area), NewId()); fences.Add(area, fence); return fence;
    }
    internal void EnsureTracked(Area area, IntVec3 cell)
    {
        if (!cell.InBounds(area.Map)) throw new ArgumentException("Manual area cell is outside its original map.");
        Get(area).Watched.Add(cell);
    }
    internal string? ManualId(Area area, IntVec3 cell)
    {
        EnsureTracked(area, cell); return fences[area].Sources.TryGetValue(cell, out var source) ? (string?)source.Value["manual_id"] : null;
    }
    internal string Revision(Area area) => Get(area).Revision;
    internal bool HasArea(Area area) => Valid && fences.ContainsKey(area);
    internal JArray Ids(Area area)
    {
        RequireValid();
        if (!fences.TryGetValue(area, out var fence)) return new JArray();
        return new JArray(fence.Sources.OrderBy(p => p.Key.z).ThenBy(p => p.Key.x).Select(p => new JObject {
            ["x"] = p.Key.x, ["z"] = p.Key.z, ["manual_id"] = p.Value.Value["manual_id"]!.DeepClone() }));
    }
    internal (IntVec3 Cell, bool Value)[] Watched(Area area) => Valid && fences.TryGetValue(area, out var fence)
        ? fence.Watched.OrderBy(c => c.z).ThenBy(c => c.x).Select(c => (c, area[c])).ToArray() : Array.Empty<(IntVec3, bool)>();
    internal void Bump(Area area) { if (Valid && fences.TryGetValue(area, out var fence)) fence.Revision = NewId(); }
    internal JObject? Change(Area area, IntVec3 cell, bool before, bool after, string command, JObject? ownership, long tick, string originLoadEpoch)
    {
        if (!Valid || before == after) return null;
        if (!Exists(area) || !cell.InBounds(area.Map) || AreaZoneReadings.Kind(area) == "unsupported") throw new ArgumentException("Manual area source requires a live original area and in-bounds cell.");
        if (string.IsNullOrWhiteSpace(command) || tick < 0 || !Guid.TryParseExact(originLoadEpoch, "N", out _)) throw new ArgumentException("Manual area source requires its native UI command, game tick and original load epoch.");
        if (ownership != null && (ownership.Count != 2 || !Text(ownership["changeset_id"]) || ownership["value"] == null)) throw new ArgumentException("Manual area ownership must preserve its original AI value and changeset.");
        if (!fences.TryGetValue(area, out var fence)) { if (ownership == null) return null; fence = Get(area); }
        fence.Revision = NewId();
        if (!fence.Watched.Contains(cell) && ownership == null) return null;
        fence.Watched.Add(cell);
        var id = NewId(); var previous = fence.Sources.TryGetValue(cell, out var source) ? source.Value["manual_id"]!.DeepClone() : JValue.CreateNull();
        long order = source == null ? 1 : checked((long)source.Value["manual_order"]! + 1);
        var value = new JObject { ["manual_id"] = id, ["manual_order"] = order, ["origin_load_epoch"] = originLoadEpoch, ["source"] = "user_designator", ["map_id"] = area.Map.GetUniqueLoadID(), ["object"] = area.GetUniqueLoadID(),
            ["field"] = AreaCellFlags.FieldName(cell), ["cell"] = new JObject { ["x"] = cell.x, ["z"] = cell.z }, ["area_kind"] = AreaZoneReadings.Kind(area), ["designator"] = command,
            ["old_value"] = new JObject { ["native_instance"] = fence.Instance, ["enabled"] = before, ["manual_id"] = previous },
            ["new_value"] = new JObject { ["native_instance"] = fence.Instance, ["enabled"] = after, ["manual_id"] = id },
            ["ownership"] = ownership == null ? JValue.CreateNull() : ownership.DeepClone(), ["tick"] = tick };
        fence.Sources[cell] = new CellSource(value, value.ToString(Formatting.None)); return (JObject)value.DeepClone();
    }

    private static void Keys(JObject obj, params string[] keys)
    {
        if (!obj.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(keys.OrderBy(n => n, StringComparer.Ordinal))) throw new ArgumentException("Unknown or missing manual source fields.");
    }
    private static IntVec3 ValidateSource(NativeAreaManualRecord record, JObject value)
    {
        Keys(value, "manual_id", "manual_order", "origin_load_epoch", "source", "map_id", "object", "field", "cell", "area_kind", "designator", "old_value", "new_value", "ownership", "tick");
        if (!GuidId(value["manual_id"]) || !GuidId(value["origin_load_epoch"]) || value["manual_order"]?.Type != JTokenType.Integer || (long)value["manual_order"]! < 1 || !Text(value["source"]) || (string?)value["source"] != "user_designator" || !Text(value["map_id"]) || (string?)value["map_id"] != record.MapId || !Text(value["object"]) || (string?)value["object"] != record.Id
            || !Text(value["designator"]) || value["tick"]?.Type != JTokenType.Integer || (long)value["tick"]! < 0 || value["cell"] is not JObject cell || value["old_value"] is not JObject before || value["new_value"] is not JObject after)
            throw new ArgumentException("Invalid manual source identity or native values.");
        Keys(cell, "x", "z"); Keys(before, "native_instance", "enabled", "manual_id"); Keys(after, "native_instance", "enabled", "manual_id");
        if (cell["x"]?.Type != JTokenType.Integer || cell["z"]?.Type != JTokenType.Integer || (long)cell["x"]! < int.MinValue || (long)cell["x"]! > int.MaxValue || (long)cell["z"]! < int.MinValue || (long)cell["z"]! > int.MaxValue) throw new ArgumentException("Invalid manual source cell.");
        var position = new IntVec3((int)cell["x"]!, 0, (int)cell["z"]!);
        if (!position.InBounds(record.Target!.Map) || !Text(value["field"]) || (string?)value["field"] != AreaCellFlags.FieldName(position) || !Text(value["area_kind"]) || (string?)value["area_kind"] != AreaZoneReadings.Kind(record.Target)
            || !Text(before["native_instance"]) || !Text(after["native_instance"]) || (string?)before["native_instance"] != record.NativeInstance || (string?)after["native_instance"] != record.NativeInstance
            || before["enabled"]?.Type != JTokenType.Boolean || after["enabled"]?.Type != JTokenType.Boolean || (bool)before["enabled"]! == (bool)after["enabled"]!
            || (before["manual_id"]?.Type != JTokenType.Null && !GuidId(before["manual_id"])) || !JToken.DeepEquals(after["manual_id"], value["manual_id"])
            || JToken.DeepEquals(before["manual_id"], after["manual_id"])) throw new ArgumentException("Manual source does not belong to its original area cell.");
        if (value["ownership"] is JObject ownership) { Keys(ownership, "changeset_id", "value"); if (!Text(ownership["changeset_id"]) || ownership["value"] == null) throw new ArgumentException("Invalid manual source ownership."); }
        else if (value["ownership"]?.Type != JTokenType.Null) throw new ArgumentException("Invalid manual source ownership.");
        return position;
    }

    internal void ExposeData()
    {
        if (Scribe.mode == LoadSaveMode.Saving && Valid) records = fences.Values.Where(f => Exists(f.Area) && f.Sources.Count > 0)
            .OrderBy(f => f.Area.Map.GetUniqueLoadID(), StringComparer.Ordinal).ThenBy(f => f.Area.GetUniqueLoadID(), StringComparer.Ordinal)
            .Select(f => new NativeAreaManualRecord { Target = f.Area, Id = f.Area.GetUniqueLoadID(), MapId = f.Area.Map.GetUniqueLoadID(), NativeType = f.Area.GetType().FullName ?? f.Area.GetType().Name,
                NativeInstance = f.Instance, Revision = f.Revision, Sources = f.Sources.OrderBy(p => p.Key.z).ThenBy(p => p.Key.x).Select(p => p.Value.Json).ToList() }).ToList();
        if (Scribe.mode == LoadSaveMode.LoadingVars) { fences.Clear(); invalid.Clear(); Valid = true; }
        Scribe_Collections.Look(ref records, "nekoAreaManualSources", LookMode.Deep);
        if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
        fences.Clear(); invalid.Clear(); records ??= new List<NativeAreaManualRecord>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records) {
            try {
                if (record == null || record.Target == null || !Exists(record.Target) || record.Target.GetUniqueLoadID() != record.Id || record.Target.Map.GetUniqueLoadID() != record.MapId
                    || (record.Target.GetType().FullName ?? record.Target.GetType().Name) != record.NativeType || AreaZoneReadings.Kind(record.Target) == "unsupported"
                    || !Guid.TryParseExact(record.NativeInstance, "N", out _) || NativeObjectIdentities.Token(record.Target) != record.NativeInstance || !Guid.TryParseExact(record.Revision, "N", out _) || fences.ContainsKey(record.Target))
                    throw new ArgumentException("Saved manual provenance cannot bind to the original native area.");
                var fence = new Fence(record.Target, record.NativeInstance, record.Revision);
                foreach (var raw in record.Sources ?? new List<string>()) {
                    var value = JObject.Parse(raw, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }); var position = ValidateSource(record, value);
                    if (fence.Sources.ContainsKey(position) || !usedIds.Add((string)value["manual_id"]!)) throw new ArgumentException("Ambiguous duplicate manual source identity or cell.");
                    fence.Sources.Add(position, new CellSource(value, raw)); fence.Watched.Add(position);
                }
                if (fence.Sources.Count > 0) fences.Add(record.Target, fence);
            } catch (Exception error) when (ReadContext.Recoverable(error)) {
                Valid = false;
                foreach (var raw in record?.Sources ?? new List<string>()) invalid.Add(new JObject { ["status"] = "invalid_saved_source", ["object"] = record?.Id,
                    ["native_instance"] = record?.NativeInstance, ["raw_source"] = raw, ["reason"] = error.Message });
            }
        }
    }
    internal JArray Snapshot(JArray? fields = null)
    {
        var selected = fields == null ? null : new HashSet<string>(fields.Select(field => (string)field["object"]! + "\0" + (string)field["field"]!), StringComparer.Ordinal);
        var result = new JArray(fences.Values.Where(f => Exists(f.Area)).OrderBy(f => f.Area.Map.GetUniqueLoadID(), StringComparer.Ordinal).ThenBy(f => f.Area.GetUniqueLoadID(), StringComparer.Ordinal)
            .SelectMany(f => f.Sources.OrderBy(p => p.Key.z).ThenBy(p => p.Key.x).Select(p => p.Value.Value)).Where(source => selected == null || selected.Contains((string)source["object"]! + "\0" + (string)source["field"]!)).Select(source => source.DeepClone()));
        foreach (var source in invalid) if (fields == null || fields.Any(field => (string?)field["object"] == (string?)source["object"])) result.Add(source.DeepClone());
        return result;
    }
}
