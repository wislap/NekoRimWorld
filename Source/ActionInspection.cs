using System;
using System.Collections.Generic;
using System.Linq;
using GraphQL;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal static class ActionInspection
{
    internal static (Type Type,string Method)[] ReadDependencies(string action) => new[] {
        (typeof(FloatMenuContext),".ctor"),
        (typeof(FloatMenuMakerMap),nameof(FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn)),
        (action switch { "equip"=>typeof(FloatMenuOptionProvider_Equip), "wear"=>typeof(FloatMenuOptionProvider_Wear), "ingest"=>typeof(FloatMenuOptionProvider_Ingest), _=>throw ReadContext.Error("invalid_arguments","Unknown inspected action") },nameof(FloatMenuOptionProvider.SelectedPawnValid)),
        (action switch { "equip"=>typeof(FloatMenuOptionProvider_Equip), "wear"=>typeof(FloatMenuOptionProvider_Wear), _=>typeof(FloatMenuOptionProvider_Ingest) },nameof(FloatMenuOptionProvider.Applies)),
        (action switch { "equip"=>typeof(FloatMenuOptionProvider_Equip), "wear"=>typeof(FloatMenuOptionProvider_Wear), _=>typeof(FloatMenuOptionProvider_Ingest) },nameof(FloatMenuOptionProvider.TargetThingValid)),
        (typeof(NativeActionConditions),action=="equip"?"ReadEquip":action=="wear"?"ReadWear":"ReadIngest")
    };
    private static FloatMenuOptionProvider Provider(string action) => action switch {
        "equip" => new FloatMenuOptionProvider_Equip(), "wear" => new FloatMenuOptionProvider_Wear(),
        "ingest" => new FloatMenuOptionProvider_Ingest(), _ => throw ReadContext.Error("invalid_arguments", "Unknown inspected action")
    };

    internal static IEnumerable<int> Read(JObject args, NativeBehaviorOperation operation)
    {
        // Cold binary/patch metadata is outside a live-object read slice.
        yield return 0;
        operation.Context.RestartSlice();
        Pawn? actor = null;
        foreach (var step in operation.Resolve((JObject)args["actor"]!, true, t => actor = (Pawn)t)) yield return step;
        var map = actor!.Map;
        if (map != Find.CurrentMap) throw ReadContext.Error("actor_not_on_current_map", "Native player-order inspection uses the current displayed map; no map switch was made");
        var actions = (args["actions"] as JArray)?.Values<string>().Select(s => s!).ToArray() ?? ActionInspectionContract.Actions;
        var result = operation.Result;
        result["actor"] = WorldStateReader.Identity(actor);
        result["actor_state"] = new JObject {
            ["can_take_order"] = actor.CanTakeOrder, ["current_job"] = WorldStateReader.JobState(actor.CurJob),
            ["drafted"] = actor.Drafted, ["downed"] = actor.Downed, ["dead"] = actor.Dead,
            ["needs"] = actor.needs == null ? null : new JArray(actor.needs.AllNeeds.Select(n => new JObject { ["definition"] = n.def.defName, ["level"] = n.CurLevel, ["maximum"] = n.MaxLevel, ["fraction"] = n.CurLevelPercentage }))
        };
        result["interpretation"] = "Audited native player-order eligibility conditions, without constructing GUI menu options. No callback, job, reservation or forbidden change was executed. Availability is not desirability, automatic work, follow-up confirmation or successful completion.";
        var targets = new List<Thing>(); var errors = new JArray(); result["unresolved_targets"] = errors;
        if (args["targets"] is JArray requested) {
            foreach (JObject reference in requested) {
                Thing? target = null; Exception? error = null;
                using var resolving = operation.Resolve(reference, false, t => target = t).GetEnumerator();
                while (true) {
                    bool more = false; try { more = resolving.MoveNext(); } catch (ExecutionError e) when (e.Code != "world_changed") { error = e; }
                    if (error != null || !more) break;
                    yield return resolving.Current;
                }
                if (error != null) { errors.Add(new JObject { ["requested"] = reference, ["status"] = "unavailable", ["reason"] = (error as ExecutionError)?.Code }); result["status"] = "partial"; }
                else if (target!.Map != map) { errors.Add(new JObject { ["requested"] = reference, ["status"] = "unavailable", ["reason"] = "different_map" }); result["status"] = "partial"; }
                else targets.Add(target!);
            }
            result["discovery"] = new JObject { ["mode"] = "explicit_targets", ["complete"] = errors.Count == 0 };
        } else {
            int radius = (int?)args["radius"] ?? 20, limit = (int?)args["limit"] ?? 12, scanned = 0;
            var candidates = new List<Thing>();
            foreach (var t in map.listerThings.AllThings.ToArray()) {
                if ((++scanned & 63) == 0) yield return 0;
                if (!t.Spawned || t.Destroyed || t.Position.Fogged(map) || t.Position.DistanceToSquared(actor.Position) > radius * radius) continue;
                if (actions.Any(a => Relevant(a, t))) candidates.Add(t);
            }
            targets.AddRange(candidates.OrderBy(t => t.Position.DistanceToSquared(actor.Position)).ThenBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).Take(limit));
            result["discovery"] = new JObject { ["mode"] = "visible_spawned_near_actor", ["radius"] = radius, ["matching_targets"] = candidates.Count, ["returned"] = targets.Count, ["complete"] = targets.Count == candidates.Count, ["selection_order"] = "geometric distance, then stable ID; not desirability or route ranking" };
        }
        var rows = new JArray(); result["targets"] = rows;
        foreach (var target in targets.Distinct()) {
            if (target.Destroyed || !target.Spawned || target.Map != map) { rows.Add(new JObject { ["status"] = "unavailable", ["reason"] = "target_changed" }); result["status"] = "partial"; continue; }
            if (target.Position.Fogged(map)) { rows.Add(new JObject { ["requested_id"] = target.GetUniqueLoadID(), ["status"] = "hidden", ["actions"] = new JArray() }); result["status"] = "partial"; continue; }
            var item = new JObject { ["target"] = WorldStateReader.Identity(target), ["sample_tick"] = Find.TickManager.TicksGame, ["forbidden_flag"] = target.TryGetComp<CompForbiddable>()?.Forbidden };
            var options = new JArray(); item["actions"] = options; rows.Add(item);
            foreach (var action in actions) {
                try { options.Add(Inspect(actor, target, action)); }
                catch (Exception e) when (ReadContext.Recoverable(e)) { options.Add(new JObject { ["action"] = action, ["status"] = "unavailable", ["reason"] = (e as ExecutionError)?.Code ?? e.GetType().Name, ["message"] = e.Message, ["cause"] = e.InnerException?.GetType().Name + ": " + e.InnerException?.Message }); result["status"] = "partial"; }
                yield return 0;
            }
        }
    }

    private static bool Relevant(string action, Thing thing) => action switch {
        "equip" => thing.HasComp<CompEquippable>(), "wear" => thing is Apparel,
        "ingest" => thing.def.ingestible?.showIngestFloatOption == true, _ => false
    };

    private static JObject Inspect(Pawn actor, Thing target, string action)
    {
        var provider = Provider(action);
        GameImplementation.RequireInspected(ReadDependencies(action));
        var result = new JObject { ["action"] = action, ["interaction"] = "player_order", ["implementation_ref"] = GameImplementation.Source(provider.GetType().FullName!), ["executed"] = false };
        if (!Relevant(action, target)) { result["status"] = "not_offered"; result["reason"] = "target_not_applicable"; return result; }
        if (!actor.CanTakeOrder) { result["status"] = "not_offered"; result["reason"] = "actor_cannot_take_order"; return result; }
        if (actor.GetType().Assembly != typeof(Pawn).Assembly || target.GetType().Assembly != typeof(Pawn).Assembly || (target is ThingWithComps withComps && withComps.AllComps.Any(c => c.GetType().Assembly != typeof(Pawn).Assembly))) throw ReadContext.Error("unsupported_mod_behavior", "Custom actor/target/component behavior has not been inspected");
        var previousProvider = FloatMenuMakerMap.currentProvider; var previousPawn = FloatMenuMakerMap.makingFor;
        try {
            FloatMenuMakerMap.currentProvider = provider; FloatMenuMakerMap.makingFor = actor;
            var context = new FloatMenuContext(new List<Pawn> { actor }, target.Position.ToVector3Shifted(), actor.Map);
            var acceptance = FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn(actor);
            if (!acceptance.Accepted) { result["status"] = "not_offered"; result["reason"] = acceptance.Reason; return result; }
            if (!provider.SelectedPawnValid(actor, context) || !provider.Applies(context) || !provider.TargetThingValid(target, context)) { result["status"] = "not_offered"; result["reason"] = "native_provider_not_applicable"; return result; }
            var conditions = NativeActionConditions.Read(action, actor, target);
            foreach (var field in conditions.Properties()) result[field.Name] = field.Value.DeepClone();
            return result;
        } finally { FloatMenuMakerMap.currentProvider = previousProvider; FloatMenuMakerMap.makingFor = previousPawn; }
    }
}
