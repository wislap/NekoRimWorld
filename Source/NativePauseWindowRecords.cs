using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

// Pure bounded evidence projection; identity is scoped to the actual stack and object.
internal sealed class NativePauseWindowRecords
{
    private sealed class Origin {
        internal object Stack = null!;
        internal object Scope = null!;
        internal int Id;
        internal int? Tick;
        internal string[] Frames = Array.Empty<string>();
    }
    internal sealed class Row {
        internal object Instance = null!;
        internal int Id;
        internal int StackIndex;
        internal string Type = "";
    }
    private readonly ConditionalWeakTable<object, Origin> origins = new ConditionalWeakTable<object, Origin>();
    private static string Limit(string value, int length) => value.Length <= length ? value : value.Substring(0, length);
    internal void Opened(object stack, object scope, object instance, int id, int? tick, IEnumerable<string> frames) {
        var origin = new Origin { Stack = stack, Scope = scope, Id = id, Tick = tick,
            Frames = frames.Where(x => !string.IsNullOrWhiteSpace(x)).Take(8).Select(x => Limit(x, 240)).ToArray() };
        origins.Remove(instance); origins.Add(instance, origin);
    }
    internal JObject Capture(object? stack, object? scope, int? tick, IEnumerable<Row>? rows) {
        if (stack == null || scope == null || rows == null) return new JObject { ["status"] = "unavailable", ["sampled_tick"] = tick,
            ["count"] = null, ["truncated"] = false, ["windows"] = new JArray() };
        var all = rows.ToArray(); var list = new JArray(); bool partial = all.Length > 16;
        foreach (var row in all.Take(16)) {
            Origin origin;
            bool known = origins.TryGetValue(row.Instance, out origin!) && ReferenceEquals(origin.Stack, stack)
                         && ReferenceEquals(origin.Scope, scope) && origin.Id == row.Id;
            if (row.Type.Length > 240) partial = true;
            list.Add(new JObject { ["window_id"] = row.Id, ["type"] = Limit(row.Type, 240), ["stack_index"] = row.StackIndex,
                ["opened_tick"] = known ? (JToken?)origin.Tick : null,
                ["opening_source"] = new JObject { ["status"] = known && origin.Frames.Length > 0 ? "observed" : "unknown",
                    ["frames"] = new JArray(known ? origin.Frames : Array.Empty<string>()) } });
        }
        return new JObject { ["status"] = partial ? "partial" : "observed", ["sampled_tick"] = tick,
            ["count"] = all.Length, ["truncated"] = all.Length > 16, ["windows"] = list };
    }
}
