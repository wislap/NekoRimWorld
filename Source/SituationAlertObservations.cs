using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Neko.RimWorld;

// The native UI caches active/label but discards report culprits. Observe its
// existing calculation, without calling GetReport or retaining live targets.
internal static class SituationAlertObservations
{
    private sealed class Observation { internal JObject Facts = new(); internal string Id = ""; }
    private static ConditionalWeakTable<Alert, Observation> reports = new();
    private static long sequence;
    private static long observations, failures, elapsedTicks, maximumTicks;
    private static int firstTick = -1, lastTick = -1, firstFrame = -1, lastFrame = -1;
    internal static bool HookMatched { get; private set; }
    internal static void Clear()
    {
        reports = new(); sequence = observations = failures = elapsedTicks = maximumTicks = 0;
        firstTick = lastTick = firstFrame = lastFrame = -1;
    }
    internal static JObject Metrics() => new() {
        ["observations"] = observations, ["failures"] = failures,
        ["total_ms"] = elapsedTicks * 1000.0 / Stopwatch.Frequency, ["max_ms"] = maximumTicks * 1000.0 / Stopwatch.Frequency,
        ["first_tick"] = firstTick, ["last_tick"] = lastTick, ["first_frame"] = firstFrame, ["last_frame"] = lastFrame,
        ["scope"] = "current load epoch; all native Alert.Recalculate returns, including inactive reports",
        ["measurement"] = "detached observation body; excludes native GetReport/label computation and timing-counter bookkeeping"
    };
    internal static AlertReport Observe(AlertReport report, Alert alert)
    {
        var started = Stopwatch.GetTimestamp();
        try {
            var value = reports.GetValue(alert, _ => new Observation { Id = "alert-observation:" + ++sequence });
            value.Facts = new JObject {
                ["native_computed_tick"] = Find.TickManager?.TicksGame,
                ["native_computed_frame"] = UnityEngine.Time.frameCount,
                ["report_active"] = report.active,
                ["targets"] = Targets(report.AllCulprits),
                ["source"] = "RimWorld.Alert.Recalculate/GetReport:return"
            };
            value.Facts["observer_ms"] = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        } catch (Exception error) when (ReadContext.Recoverable(error)) {
            failures++;
            var value = reports.GetValue(alert, _ => new Observation { Id = "alert-observation:" + ++sequence });
            value.Facts = new JObject { ["status"] = "unavailable", ["reason"] = error.GetType().Name };
        } finally {
            var elapsed = Stopwatch.GetTimestamp() - started;
            observations++; elapsedTicks += elapsed; maximumTicks = Math.Max(maximumTicks, elapsed);
            lastTick = Find.TickManager?.TicksGame ?? -1; lastFrame = UnityEngine.Time.frameCount;
            if (firstFrame < 0) { firstTick = lastTick; firstFrame = lastFrame; }
        }
        return report;
    }
    internal static JObject Read(Alert alert)
    {
        if (reports.TryGetValue(alert, out var value)) {
            var result = (JObject)value.Facts.DeepClone(); result["id"] = value.Id; return result;
        }
        return new JObject { ["status"] = "unavailable", ["reason"] = HookMatched ? "native_report_not_observed" : "native_report_hook_unavailable" };
    }
    internal static JArray Targets(IEnumerable<GlobalTargetInfo>? targets)
    {
        var rows = new JArray();
        if (targets == null) return rows;
        foreach (var target in targets) {
            if (target.HasThing) {
                var thing = target.Thing;
                rows.Add(new JObject { ["kind"] = "thing", ["id"] = thing.GetUniqueLoadID(), ["def"] = thing.def.defName,
                    ["map_id"] = thing.MapHeld?.GetUniqueLoadID(), ["x"] = thing.PositionHeld.x, ["z"] = thing.PositionHeld.z });
            } else if (target.HasWorldObject) rows.Add(new JObject { ["kind"] = "world_object", ["id"] = target.WorldObject.GetUniqueLoadID() });
            else rows.Add(new JObject { ["kind"] = "cell", ["map_id"] = target.Map?.GetUniqueLoadID(), ["x"] = target.Cell.x, ["z"] = target.Cell.z, ["tile"] = target.Tile.ToString() });
        }
        return rows;
    }

    [HarmonyPatch(typeof(Alert), nameof(Alert.Recalculate))]
    private static class CaptureNativeReport
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var original = instructions.ToList();
            try { GameImplementation.RequireKnownBinary(); }
            catch (Exception error) when (ReadContext.Recoverable(error)) { HookMatched = false; return original; }
            var target = AccessTools.Method(typeof(Alert), nameof(Alert.GetReport));
            var matches = original.Count(i => i.Calls(target));
            // Refuse a changed implementation, and do not inject into any other call.
            HookMatched = matches == 1;
            if (!HookMatched) return original;
            var result = new List<CodeInstruction>();
            foreach (var instruction in original) {
                result.Add(instruction);
                if (instruction.Calls(target)) {
                    result.Add(new CodeInstruction(OpCodes.Ldarg_0));
                    result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SituationAlertObservations), nameof(Observe))));
                }
            }
            return result;
        }
    }
}
