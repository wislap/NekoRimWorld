using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

// Main-thread aggregate only: no timers, events, or retained sample data.
internal sealed class AttentionSamplingMetrics
{
    private sealed class Aggregate { internal long Count, Failures, Total, Max; }
    private readonly Dictionary<string, Aggregate> values = new();
    private readonly Func<long> clock;
    private readonly long frequency;
    internal AttentionSamplingMetrics() : this(Stopwatch.GetTimestamp, Stopwatch.Frequency) { }
    internal AttentionSamplingMetrics(Func<long> clock, long frequency) { this.clock = clock; this.frequency = frequency; }
    internal long Start() => clock();
    internal void Record(string name, long started, bool failed)
    {
        var elapsed = clock() - started;
        if (!values.TryGetValue(name, out var value)) values[name] = value = new Aggregate();
        value.Count++; if (failed) value.Failures++;
        value.Total += elapsed; value.Max = Math.Max(value.Max, elapsed);
    }
    internal void Clear() => values.Clear();
    internal JObject Report()
    {
        var parts = new JObject();
        foreach (var name in new[] { "sample", "alerts", "conditions", "threats", "difference_delivery", "pending_delivery" }) {
            values.TryGetValue(name, out var value); value ??= new Aggregate();
            parts[name] = new JObject { ["count"] = value.Count, ["failures"] = value.Failures,
                ["total_ms"] = value.Total * 1000.0 / frequency, ["max_ms"] = value.Max * 1000.0 / frequency };
        }
        return new JObject { ["scope"] = "current load epoch; due attention samples and separate pending delivery retries",
            ["measurement"] = "Stopwatch wall time on main thread; sample includes section reads, projection, difference and initial delivery; parts overlap sample; pending delivery is separate. Alert cache reads exclude native GetReport computation; see observer_metrics for detached observation overhead.",
            ["failure_meaning"] = "sample includes incomplete projected facts, section read partial/unavailable, rejected publication, or exception; section failure is partial/unavailable native read or exception; pending delivery failure means rejected or exception",
            ["parts"] = parts };
    }
}
