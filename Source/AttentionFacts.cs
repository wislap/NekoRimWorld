using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

// Detached, bounded protocol facts and pure edge comparison. No native objects
// survive a sample and incomplete partitions never prove absence.
internal static class AttentionFacts
{
    internal static bool CapabilityAvailable(bool eventHooksReady, bool alertHookMatched) => eventHooksReady && alertHookMatched;
    internal const int MaxRows = 32, MaxTargets = 16, MaxText = 128;
    internal static readonly string[] Categories = { "alerts", "conditions", "threats" };
    private static string? Text(JToken? value) { var text = (string?)value; return text == null ? null : text.Substring(0, Math.Min(text.Length, MaxText)); }
    internal static JObject Project(string category, JObject section)
    {
        var output = new JObject { ["status"] = (string?)section["status"] ?? "unavailable", ["source"] = Text(section["source"]), ["rows"] = new JArray() };
        var rows = (JArray)output["rows"]!;
        foreach (var input in ((JArray?)section["items"] ?? new JArray()).OfType<JObject>()) {
            if (rows.Count == MaxRows) { output["status"] = "partial"; output["truncated"] = true; break; }
            var id = Text(input["id"]); if (string.IsNullOrEmpty(id) || id != (string?)input["id"]) { output["status"] = "partial"; continue; }
            var row = new JObject { ["id"] = id };
            string[] keys = category == "alerts" ? new[] { "runtime_type", "label", "priority" } : category == "conditions" ? new[] { "def", "scope", "map_id", "start_tick", "duration_ticks", "permanent" } : new[] { "kind", "faction_id", "faction_def", "visible_hostile_count", "map_id" };
            foreach (var key in keys) {
                var v = input[key];
                if (v?.Type == JTokenType.String) { row[key] = Text(v); if ((string?)row[key] != (string?)v) row["text_truncated"] = true; }
                else row[key] = v?.DeepClone();
            }
            if (category == "alerts") {
                var report = input["report"] as JObject;
                row["native_computed_tick"] = report?["native_computed_tick"]?.DeepClone();
                row["targets"] = Targets(report?["targets"] as JArray, row);
                if (report == null || (string?)report["status"] == "unavailable") row["targets_complete"] = false;
            }
            if (category == "threats") row["members"] = Targets(input["members"] as JArray, row);
            rows.Add(row);
        }
        return output;
    }
    // A situation read spans frames: retain each partition's sample interval,
    // and use the existing bounded event projection without rereading the world.
    internal static JObject SnapshotFromSections(JObject sections, JObject sample)
    {
        var snapshot = new JObject { ["kind"] = "attention_snapshot", ["sample_tick"] = sample["finished_tick"]?.DeepClone(),
            ["map_id"] = sample["map_id"]?.DeepClone(), ["sample"] = sample.DeepClone() };
        foreach (var category in Categories) {
            var section = sections[category] as JObject;
            var input = section == null ? new JObject { ["status"] = "unavailable", ["items"] = new JArray() }
                                        : (JObject)section.DeepClone();
            if ((string?)input["status"] == "reading")
                input["status"] = ((JArray?)input["items"])?.Count > 0 ? "partial" : "unavailable";
            var part = Project(category, input);
            part["sample_tick"] = input["sample_tick"]?.DeepClone();
            part["finished_tick"] = input["finished_tick"]?.DeepClone();
            snapshot[category] = part;
        }
        return snapshot;
    }
    private static JArray Targets(JArray? input, JObject row)
    {
        var result = new JArray(); bool complete = true;
        foreach (var item in (input ?? new JArray()).OfType<JObject>()) {
            if (result.Count == MaxTargets) { complete = false; break; }
            var id = Text(item["id"]); var map = Text(item["map_id"]); var kind = Text(item["kind"]);
            if (id != (string?)item["id"] || map != (string?)item["map_id"]) { complete = false; continue; }
            // Cell targets have no ID; preserve their stable map and coordinates.
            result.Add(new JObject { ["id"] = id, ["kind"] = kind, ["map_id"] = map, ["x"] = id == null ? item["x"]?.DeepClone() : null, ["z"] = id == null ? item["z"]?.DeepClone() : null, ["tile"] = id == null ? Text(item["tile"]) : null });
        }
        row["targets_complete"] = complete;
        return result;
    }
    internal static List<JObject> Compare(JObject previous, JObject current, long tick, string? mapId)
    {
        var changes = new List<JObject>();
        foreach (var category in Categories) {
            var oldPart = (JObject?)previous[category] ?? new JObject(); var part = (JObject?)current[category] ?? new JObject();
            if ((string?)oldPart["status"] != (string?)part["status"]) changes.Add(Change(category, category, "status_changed", oldPart, part, tick, mapId));
            var oldRows = ((JArray?)oldPart["rows"] ?? new JArray()).OfType<JObject>().GroupBy(r => (string)r["id"]!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var newRows = ((JArray?)part["rows"] ?? new JArray()).OfType<JObject>().GroupBy(r => (string)r["id"]!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var pair in newRows.OrderBy(p => p.Key, StringComparer.Ordinal)) {
                oldRows.TryGetValue(pair.Key, out var old);
                if (old == null) changes.Add(Change(category, pair.Key, "appeared", null, pair.Value, tick, mapId));
                else {
                    if (category == "alerts" && old["priority"]?.Type == JTokenType.String && pair.Value["priority"]?.Type == JTokenType.String && !JToken.DeepEquals(old["priority"], pair.Value["priority"])) changes.Add(Change(category, pair.Key, "priority_changed", old, pair.Value, tick, mapId));
                    if (category == "alerts") {
                        var oldTargets = ((JArray?)old["targets"] ?? new JArray()).Select(x => x.ToString(Newtonsoft.Json.Formatting.None)).ToHashSet();
                        var newTargets = ((JArray?)pair.Value["targets"] ?? new JArray()).Select(x => x.ToString(Newtonsoft.Json.Formatting.None)).ToHashSet();
                        var added = newTargets.Except(oldTargets).OrderBy(x => x, StringComparer.Ordinal).Select(JObject.Parse).ToArray();
                        var removed = (bool?)pair.Value["targets_complete"] == true ? oldTargets.Except(newTargets).OrderBy(x => x, StringComparer.Ordinal).Select(JObject.Parse).ToArray() : Array.Empty<JObject>();
                        if (added.Length != 0 || removed.Length != 0) { var c = Change(category, pair.Key, "targets_changed", old, pair.Value, tick, mapId); c["added"] = new JArray(added); c["removed"] = new JArray(removed); changes.Add(c); }
                    }
                }
            }
            if ((string?)part["status"] == "ok") foreach (var pair in oldRows.OrderBy(p => p.Key, StringComparer.Ordinal)) if (!newRows.ContainsKey(pair.Key)) changes.Add(Change(category, pair.Key, "disappeared", pair.Value, null, tick, mapId));
        }
        return changes;
    }
    private static JObject Change(string category, string id, string edge, JObject? before, JObject? after, long tick, string? mapId) => new() {
        ["kind"] = "attention_changed", ["sample_tick"] = tick, ["map_id"] = mapId, ["category"] = category, ["source_id"] = id, ["edge"] = edge,
        ["previous"] = before?.DeepClone(), ["current"] = after?.DeepClone()
    };
    internal static JObject Merge(JObject previous, JObject current)
    {
        var result = (JObject)current.DeepClone();
        foreach (var category in Categories) {
            var next = (JObject)result[category]!;
            var oldById = (((JArray?)previous[category]?["rows"]) ?? new JArray()).OfType<JObject>().GroupBy(r => (string)r["id"]!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            if (category == "alerts") foreach (var row in ((JArray)next["rows"]!).OfType<JObject>()) {
                if (!oldById.TryGetValue((string)row["id"]!, out var prior)) continue;
                if (row["priority"]?.Type != JTokenType.String) row["priority"] = prior["priority"]?.DeepClone();
                if ((bool?)row["targets_complete"] == true) continue;
                var targets = (JArray)row["targets"]!;
                var seen = targets.Select(t => t.ToString(Newtonsoft.Json.Formatting.None)).ToHashSet();
                foreach (var target in ((JArray?)prior["targets"] ?? new JArray())) if (seen.Add(target.ToString(Newtonsoft.Json.Formatting.None))) targets.Add(target.DeepClone());
            }
            if ((string?)next["status"] == "ok") continue;
            var rows = (JArray)next["rows"]!; var known = rows.OfType<JObject>().Select(r => (string?)r["id"]).ToHashSet();
            foreach (var row in (((JArray?)previous[category]?["rows"]) ?? new JArray()).OfType<JObject>()) if (!known.Contains((string?)row["id"])) rows.Add(row.DeepClone());
        }
        return result;
    }
}

// Tracks only the current detached sample while it is being published. A
// failed enqueue retains the first unsent event; successfully sent edges are
// never re-enqueued. BridgeRuntime remains the sole transport and cursor owner.
internal sealed class AttentionPublication
{
    private List<JObject>? pending;
    private JObject? nextBaseline;
    private int next;
    internal bool Pending => pending != null;
    internal void Reset() { pending = null; nextBaseline = null; next = 0; }
    internal void Begin(List<JObject> events, JObject baseline)
    {
        if (Pending) throw new InvalidOperationException("Attention publication already pending.");
        pending = events; nextBaseline = baseline; next = 0;
    }
    internal bool Publish(Func<JObject, bool> emit, out JObject? committed)
    {
        committed = null;
        if (pending == null) return true;
        while (next < pending.Count) {
            if (!emit(pending[next])) return false;
            next++;
        }
        committed = nextBaseline;
        Reset();
        return true;
    }
}
