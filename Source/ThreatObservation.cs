using System;
using System.Collections.Generic;
using System.Linq;
using GraphQL;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace Neko.RimWorld;

internal static class ThreatObservation
{
    internal static IEnumerable<int> Read(JObject args, NativeBehaviorOperation operation)
    {
        GameImplementation.RequireKnownBinary();
        yield return 0;
        operation.Context.RestartSlice();
        var map = GameWorldReferences.ResolveMap((string?)args["map"])!;
        var result = operation.Result;
        result["map"] = map.GetUniqueLoadID();
        try {
            GameImplementation.RequireInspected((typeof(GenHostility),nameof(GenHostility.AnyHostileActiveThreatToPlayer)));
            result["native_active_threat_to_player"] = GenHostility.AnyHostileActiveThreatToPlayer(map, false, false);
        } catch(ExecutionError error) {
            result["native_active_threat_to_player"]=null;
            result["active_classification"] = new JObject { ["status"]="unavailable",["reason"]=error.Code,["message"]=error.Message };
            result["status"]="partial";
        }
        result["implementation_ref"] = GameImplementation.Source("RimWorld.GenHostility");
        result["meaning"] = "Native active-threat classification with dormant and fogged targets excluded. Active is not a statement that an attack is occurring or imminent. False is not an overall safety verdict.";
        var rows = new JArray(); result["visible_hostile_targets"] = rows;
        var targets = WorldNativeAccess.HostileTargetSnapshot(map,Faction.OfPlayer);
        int visible = 0, unknownVisibility=0, limit = (int?)args["limit"] ?? 20;
        foreach (var attackTarget in targets) {
            if(attackTarget.GetType().Assembly!=typeof(Pawn).Assembly) {
                unknownVisibility++;result["status"]="partial";
                if(rows.Count<limit)rows.Add(new JObject { ["status"]="unavailable",["reason"]="unsupported_mod_attack_target",["runtime_type"]=attackTarget.GetType().FullName,["visibility"]="unknown" });
                yield return 0;continue;
            }
            var thing = attackTarget.Thing;
            if (thing.Destroyed || !thing.Spawned || thing.Map != map || thing.Fogged()) continue;
            visible++;
            if (rows.Count < limit) {
                try {
                    if (thing.GetType().Assembly != typeof(Pawn).Assembly) throw ReadContext.Error("unsupported_mod_behavior", "Custom threat target implementation is uninspected");
                    GameImplementation.RequireInspected((typeof(GenHostility),nameof(GenHostility.IsActiveThreatToPlayer)));
                    var pawn = thing as Pawn;
                    var dormant = thing.TryGetComp<CompCanBeDormant>();
                    rows.Add(new JObject {
                        ["target"] = WorldStateReader.Identity(thing), ["sample_tick"] = Find.TickManager.TicksGame,
                        ["hostile_to_player"] = thing.HostileTo(Faction.OfPlayer),
                        ["native_active_threat_to_player"] = GenHostility.IsActiveThreatToPlayer(attackTarget, false),
                        ["dormancy_component_present"] = dormant != null, ["dormancy_component_awake"] = dormant == null ? null : (JToken)dormant.Awake,
                        ["activity_dormant"] = pawn?.activity == null ? null : (JToken)pawn.activity.IsDormant,
                        ["dead"] = pawn == null ? null : (JToken)pawn.Dead, ["downed"] = pawn == null ? null : (JToken)pawn.Downed,
                        ["current_job"] = WorldStateReader.JobState(pawn?.CurJob),
                        ["duty"] = pawn?.mindState?.duty?.def?.defName,
                        ["interpretation"] = "Current native state, not a forecast. Job targets report intent/state and do not prove a hit or future movement."
                    });
                } catch (Exception e) when (ReadContext.Recoverable(e)) {
                    rows.Add(new JObject { ["target"] = WorldStateReader.Identity(thing), ["status"] = "unavailable", ["reason"] = (e as ExecutionError)?.Code ?? e.GetType().Name });
                    result["status"] = "partial";
                }
            }
            yield return 0;
        }
        result["scope"] = new JObject { ["selection"] = "native attack-target cache hostile to player faction, visible spawned targets on this map", ["include_fogged"] = false, ["visible_matching_targets"] = unknownVisibility==0?(JToken)visible:JValue.CreateNull(),
            ["known_visible_matching_targets"]=visible,["unknown_visibility_targets"]=unknownVisibility,["cached_hostile_targets"]=targets.Length,
            ["returned"] = rows.Count, ["detail_complete"] = visible <= limit && unknownVisibility==0, ["meaning"] = "Cached hostility membership and observed visibility, separate from active classification. Not all Pawn objects or every environmental hazard. Hidden targets and non-attack hazards are not established absent." };
        var checks = new JArray(); result["subject_attack_checks"] = checks;
        if (args["subjects"] is JArray requested) foreach (JObject reference in requested) {
            Pawn? subject = null;
            foreach (var step in operation.Resolve(reference, true, t => subject = (Pawn)t)) yield return step;
            if (subject!.Map != map) throw ReadContext.Error("invalid_arguments", "Threat subjects must be on the selected map");
            var environment = new WorldEnvironmentReader(map, subject);
            environment.Observe(subject.Position);
            var influences = new JArray(); bool complete = false;
            foreach (var step in environment.Threats(influences, value => complete = value)) yield return step;
            checks.Add(new JObject {
                ["subject"] = WorldStateReader.Identity(subject), ["sample_tick"] = Find.TickManager.TicksGame,
                ["current_cell"] = WorldStateReader.Cell(map, subject.Position),
                ["environment"] = environment.Result(true, influences, complete),
                ["meaning"] = "Attack range and line-of-fire eligibility against this current cell using initialized supported verbs; not firing activity, hit probability, future movement or a safe verdict."
            });
        }
        result["subject_checks_requested"] = args["subjects"] != null;
    }
}
