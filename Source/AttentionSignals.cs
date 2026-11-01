using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Verse;

namespace Neko.RimWorld;

internal sealed class AttentionSignals
{
    private JObject? baseline;
    private readonly AttentionPublication publication = new();
    private int lastTick = -1;
    internal void Reset() { baseline = null; lastTick = -1; publication.Reset(); }
    internal void Sample(Func<JObject, bool> emit)
    {
        if (!UnityData.IsInMainThread || Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting || Find.TickManager == null) return;
        if (publication.Pending) {
            if (!publication.Publish(emit, out var committed)) return;
            baseline = committed;
        }
        int tick = Find.TickManager.TicksGame;
        if (lastTick >= 0 && tick - lastTick < 250) return;
        lastTick = tick;
        var map = Find.CurrentMap;
        var facts = new JObject();
        foreach (var category in AttentionFacts.Categories) {
            var section = new JObject { ["status"] = "reading", ["source"] = category == "alerts" ? "RimWorld.AlertsReadout" : category == "conditions" ? "RimWorld.GameConditionManager" : "Verse.AttackTargetsCache + Verse.AI.Group.LordManager", ["items"] = new JArray() };
            try {
                if (map == null) throw new InvalidOperationException("No current map");
                foreach (var ignored in SituationNativeFacts.Read(category, map, section)) { }
                if ((string?)section["status"] == "reading") section["status"] = "ok";
            } catch (Exception error) when (ReadContext.Recoverable(error)) {
                section["status"] = ((JArray)section["items"]!).Count == 0 ? "unavailable" : "partial";
                section["reason"] = error.GetType().Name;
            }
            facts[category] = AttentionFacts.Project(category, section);
        }
        string? mapId = map?.GetUniqueLoadID();
        if (baseline == null) {
            var snapshot = new JObject { ["kind"] = "attention_snapshot", ["sample_tick"] = tick, ["map_id"] = mapId };
            foreach (var category in AttentionFacts.Categories) snapshot[category] = facts[category]!.DeepClone();
            facts["map_id"] = mapId;
            publication.Begin(new List<JObject> { snapshot }, facts);
            if (publication.Publish(emit, out var committed)) baseline = committed;
            return;
        }
        // Map changes invalidate the map-scoped baseline rather than implying that
        // entities on the old map have disappeared.
        if ((string?)baseline["map_id"] != mapId) {
            var snapshot = new JObject { ["kind"] = "attention_snapshot", ["sample_tick"] = tick, ["map_id"] = mapId };
            foreach (var category in AttentionFacts.Categories) snapshot[category] = facts[category]!.DeepClone();
            facts["map_id"] = mapId;
            publication.Begin(new List<JObject> { snapshot }, facts);
            if (publication.Publish(emit, out var committed)) baseline = committed;
            return;
        }
        var changes = AttentionFacts.Compare(baseline, facts, tick, mapId);
        facts["map_id"] = mapId;
        publication.Begin(changes, AttentionFacts.Merge(baseline, facts));
        if (publication.Publish(emit, out var nextBaseline)) baseline = nextBaseline;
    }
}
