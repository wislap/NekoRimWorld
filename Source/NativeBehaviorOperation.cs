using System;
using System.Collections.Generic;
using System.Linq;
using GraphQL;
using Newtonsoft.Json.Linq;
using Verse;

namespace Neko.RimWorld;

internal sealed class NativeBehaviorOperation : FrameQueryOperation
{
    private readonly Game game = Current.Game;
    private readonly SessionDescriptor session;
    private readonly int startedTick = Find.TickManager.TicksGame;
    internal readonly ReadContext Context;
    internal readonly JObject Result = new();

    internal NativeBehaviorOperation(string operation, JObject arguments, SessionDescriptor session)
    {
        this.session = session;
        Context = new ReadContext(new VerseGameData(), session, 12);
        InitializeWork(Read(operation, (JObject)arguments.DeepClone()).GetEnumerator());
    }

    private IEnumerable<int> Read(string operation, JObject args)
    {
        ActionInspectionContract.Validate(operation, args);
        foreach (var step in operation == "inspect_actions" ? ActionInspection.Read(args, this) : ThreatObservation.Read(args, this)) yield return step;
    }

    protected override void RestartReadSlice() => Context.RestartSlice();
    protected override void ValidateFrame()
    {
        if (Current.Game != game) throw ReadContext.Error("world_changed", "Loaded game changed during behavior inspection");
        EnsureLifetime(8, "observation_deadline", "Behavior inspection exceeded its read deadline");
    }
    protected override void Complete() => Finish();
    protected override void HandleError(Exception e)
    {
        if (e is ExecutionError error && error.Code is "invalid_arguments" or "world_changed" or "ambiguous_reference") {
            Outcome = QueryOutcome.Failure(error.Code!, error.Message);
            return;
        }
        Result["status"] = "partial";
        Result["read_error"] = new JObject { ["code"] = (e as ExecutionError)?.Code ?? "behavior_unavailable", ["message"] = e.Message };
        Finish();
    }

    private void Finish()
    {
        var partial = (string?)Result["status"] == "partial";
        Result["status"] = partial ? "partial" : "ok";
        Result["sample"] = new JObject { ["load_epoch"] = session.LoadEpoch, ["started_tick"] = startedTick, ["finished_tick"] = Find.TickManager.TicksGame, ["same_tick"] = startedTick == Find.TickManager.TicksGame, ["atomic"] = false };
        Result["read_only"] = true;
        Result["read_ms"] = ElapsedMilliseconds;
        session.GameTick = Find.TickManager.TicksGame;
        Outcome = Result.ToString(Newtonsoft.Json.Formatting.None).Length > 90000
            ? QueryOutcome.Failure("observation_result_too_large", "Narrow the behavior inspection")
            : partial ? QueryOutcome.Partial(Result, "behavior_partial", "Some requested behavior evidence could not be completed; inspect read_error and per-target coverage.")
            : new QueryOutcome { Status = ProtocolStatuses.Ok, Data = Result };
    }


    internal IEnumerable<int> Resolve(JObject reference, bool pawn, Action<Thing> found)
    {
        // Player orders only address spawned objects. Do not traverse every holder
        // on the map to resolve one actor or a ground target.
        var map = GameWorldReferences.ResolveMap((string?)reference["map"])!;
        var members = pawn ? map.mapPawns.AllPawnsSpawned.Cast<Thing>().ToArray() : map.listerThings.AllThings.ToArray();
        var matches = new List<Thing>();
        string? id = (string?)reference["id"], name = (string?)reference["name"];
        for (int i = 0; i < members.Length; i++) {
            if ((i & 63) == 0) yield return 0;
            if (map.Disposed) throw ReadContext.Error("object_unavailable", "Selected map was unloaded");
            var thing = members[i];
            if (thing.Destroyed || !thing.Spawned || thing.Map != map) continue;
            if (id != null && thing.GetUniqueLoadID() != id) continue;
            if (name != null && !string.Equals(thing.LabelShortCap, name, StringComparison.OrdinalIgnoreCase) && !string.Equals(thing.LabelCap, name, StringComparison.OrdinalIgnoreCase)) continue;
            matches.Add(thing);
            if (id != null || matches.Count > 1) break;
        }
        if (matches.Count > 1) throw ReadContext.Error("ambiguous_reference", "Reference matches several spawned objects; supply an exact ID");
        if (matches.Count == 0) throw ReadContext.Error("object_unavailable", "No matching spawned object on the selected map; held/world targets were not searched");
        found(matches[0]);
    }
}
