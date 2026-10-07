using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GraphQL;
using Newtonsoft.Json.Linq;
using Verse;

namespace Neko.RimWorld;

internal sealed class NativeBehaviorOperation : IFrameQueryOperation
{
    private readonly Game game = Current.Game;
    private readonly SessionDescriptor session;
    private readonly IEnumerator<int> work;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly int startedTick = Find.TickManager.TicksGame;
    private bool disposed;
    internal readonly ReadContext Context;
    internal readonly JObject Result = new();
    public QueryOutcome? Outcome { get; private set; }

    internal NativeBehaviorOperation(string operation, JObject arguments, SessionDescriptor session)
    {
        this.session = session;
        Context = new ReadContext(new VerseGameData(), session, 12);
        work = Read(operation, (JObject)arguments.DeepClone()).GetEnumerator();
    }

    private IEnumerable<int> Read(string operation, JObject args)
    {
        ActionInspectionContract.Validate(operation, args);
        foreach (var step in operation == "inspect_actions" ? ActionInspection.Read(args, this) : ThreatObservation.Read(args, this)) yield return step;
    }

    public bool Advance()
    {
        if (Outcome != null) return true;
        Context.RestartSlice(); var slice = Stopwatch.StartNew();
        try {
            if (Current.Game != game) throw ReadContext.Error("world_changed", "Loaded game changed during behavior inspection");
            if (elapsed.Elapsed.TotalSeconds > 8) throw ReadContext.Error("observation_deadline", "Behavior inspection exceeded its read deadline");
            do {
                if (!work.MoveNext()) { Finish(); return true; }
            } while (slice.Elapsed.TotalMilliseconds < 2);
        } catch (Exception e) when (ReadContext.Recoverable(e)) {
            if (e is ExecutionError error && error.Code is "invalid_arguments" or "world_changed" or "ambiguous_reference") {
                Outcome = QueryOutcome.Failure(error.Code!, error.Message); Dispose(); return true;
            }
            Result["status"] = "partial";
            Result["read_error"] = new JObject { ["code"] = (e as ExecutionError)?.Code ?? "behavior_unavailable", ["message"] = e.Message };
            Finish();
        }
        return Outcome != null;
    }

    private void Finish()
    {
        var partial = (string?)Result["status"] == "partial";
        Result["status"] = partial ? "partial" : "ok";
        Result["sample"] = new JObject { ["load_epoch"] = session.LoadEpoch, ["started_tick"] = startedTick, ["finished_tick"] = Find.TickManager.TicksGame, ["same_tick"] = startedTick == Find.TickManager.TicksGame, ["atomic"] = false };
        Result["read_only"] = true;
        Result["read_ms"] = elapsed.Elapsed.TotalMilliseconds;
        session.GameTick = Find.TickManager.TicksGame;
        Outcome = Result.ToString(Newtonsoft.Json.Formatting.None).Length > 90000
            ? QueryOutcome.Failure("observation_result_too_large", "Narrow the behavior inspection")
            : partial ? QueryOutcome.Partial(Result, "behavior_partial", "Some requested behavior evidence could not be completed; inspect read_error and per-target coverage.")
            : new QueryOutcome { Status = ProtocolStatuses.Ok, Data = Result };
        Dispose();
    }

    public void Dispose() { if (disposed) return; disposed = true; work.Dispose(); }

    internal IEnumerable<int> Resolve(JObject reference, bool pawn, Action<Thing> found)
    {
        // Player orders only address spawned objects. Do not traverse every holder
        // on the map to resolve one actor or a ground target.
        var map = SpatialQuery.ResolveMap((string?)reference["map"])!;
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
