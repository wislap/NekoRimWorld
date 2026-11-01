using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld
{
    internal static class SituationNativeFacts
    {
        internal static bool Present, Fail;
        internal static IEnumerable<int> Read(string name, Verse.Map map, JObject section)
        {
            if (name == "conditions" && Fail) throw new InvalidOperationException("sample failure");
            if (name == "conditions" && Present) ((JArray)section["items"]!).Add(new JObject { ["id"] = "condition", ["def"] = "Eclipse" });
            yield break;
        }
    }
    internal static class AttentionSamplingTests
    {
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        internal static void Run()
        {
            Verse.Find.TickManager = new Verse.TickManager(); Verse.Find.CurrentMap = new Verse.Map();
            var signals = new AttentionSignals(); var events = new List<JObject>();
            bool reject = false;
            bool Emit(JObject item) { if (reject) return false; events.Add((JObject)item.DeepClone()); return true; }
            Verse.LongEventHandler.AnyEventNowOrWaiting = true; signals.Sample(Emit);
            Verse.LongEventHandler.AnyEventNowOrWaiting = false; signals.Sample(Emit); signals.Sample(Emit);
            Check(events.Count == 1 && (string?)events[0]["kind"] == "attention_snapshot", "initial snapshot or not-due early return");
            SituationNativeFacts.Present = true; Verse.Find.TickManager.TicksGame = 250; reject = true;
            signals.Sample(Emit); signals.Sample(Emit);
            Check(events.Count == 1, "rejected publication cannot commit");
            reject = false; signals.Sample(Emit);
            Check(events.Count == 2 && (string?)events[1]["edge"] == "appeared", "pending edge must retry exactly once");
            SituationNativeFacts.Fail = true; Verse.Find.TickManager.TicksGame = 500; signals.Sample(Emit);
            Check(!events.Any(e => (string?)e["edge"] == "disappeared"), "failed read cannot imply disappearance");
            SituationNativeFacts.Fail = false; SituationNativeFacts.Present = false; Verse.Find.TickManager.TicksGame = 750; signals.Sample(Emit);
            Check(events.Count(e => (string?)e["edge"] == "disappeared") == 1, "recovered read proves disappearance");
            var parts = signals.Metrics()["parts"]!;
            Check((long?)parts["sample"]?["count"] == 4 && (long?)parts["sample"]?["failures"] == 2 &&
                (long?)parts["conditions"]?["count"] == 4 && (long?)parts["conditions"]?["failures"] == 1 &&
                (long?)parts["difference_delivery"]?["failures"] == 1 &&
                (long?)parts["pending_delivery"]?["count"] == 2 && (long?)parts["pending_delivery"]?["failures"] == 1,
                "sampling counts or failure attribution");
            foreach (var part in ((JObject)parts).Properties())
                Check((double?)part.Value["total_ms"] >= (double?)part.Value["max_ms"] && (double?)part.Value["max_ms"] >= 0, "invalid elapsed aggregate");
            var throwing = new AttentionSignals();
            Verse.Find.TickManager.TicksGame = 1000;
            try { throwing.Sample(_ => throw new InvalidOperationException("emit failure")); throw new Exception("expected emit exception"); }
            catch (InvalidOperationException) { }
            Check((long?)throwing.Metrics()["parts"]?["sample"]?["failures"] == 1 &&
                (long?)throwing.Metrics()["parts"]?["difference_delivery"]?["failures"] == 1, "exception timing must be recorded");
            signals.Reset(); signals.ClearMetrics(); Check((long?)signals.Metrics()["parts"]?["sample"]?["count"] == 0, "epoch reset");
        }
    }
}
namespace Verse
{
    internal enum ProgramState { Playing }
    internal static class UnityData { internal static bool IsInMainThread = true; }
    internal static class Current { internal static ProgramState ProgramState = ProgramState.Playing; }
    internal static class LongEventHandler { internal static bool AnyEventNowOrWaiting; }
    internal sealed class TickManager { internal int TicksGame; }
    internal sealed class Map { internal string GetUniqueLoadID() => "map"; }
    internal static class Find { internal static TickManager TickManager = new(); internal static Map CurrentMap = new(); }
}
