using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GraphQL;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal sealed class ColonyVitals : FrameQueryOperation
{
    private readonly Game game = Current.Game;
    private readonly SessionDescriptor session;
    private readonly JObject args;
    private readonly int startTick = Find.TickManager.TicksGame;
    private readonly JObject result = new() { ["format"] = ColonyVitalsContract.Version };
    private readonly JArray domains = new();
    private Map? map;
    private bool partial;

    internal ColonyVitals(JObject args, SessionDescriptor session)
    {
        this.args = (JObject)args.DeepClone(); this.session = session;
        result["domains"] = domains;
        InitializeWork(Read().GetEnumerator());
    }
    protected override void RestartReadSlice() { }
    protected override void ValidateFrame()
    {
        if (Current.Game != game || map?.Disposed == true) throw ReadContext.Error("world_changed", "Loaded game or selected map changed during colony vitals read.");
        EnsureLifetime(8, "vitals_deadline", "Colony vitals read exceeded its lifetime.");
    }
    protected override void Complete() => Finish();
    protected override void HandleError(Exception error)
    {
        if (error is ExecutionError execution && execution.Code is "invalid_arguments" or "world_changed" or "object_unavailable" or "unsupported_game_implementation")
            Outcome = QueryOutcome.Failure(execution.Code!, execution.Message);
        else { partial = true; result["read_error"] = Error(error); Finish(); }
    }
    private void Finish()
    {
        session.GameTick = Find.TickManager.TicksGame;
        result["status"] = partial ? "partial" : "ok";
        result["sample"] = new JObject { ["colony_id"] = session.ColonyId, ["load_epoch"] = session.LoadEpoch,
            ["map_id"] = map?.GetUniqueLoadID(), ["started_tick"] = startTick, ["finished_tick"] = session.GameTick,
            ["snapshot_sequence"] = session.SnapshotSequence, ["atomic"] = false };
        result["read_ms"] = ElapsedMilliseconds;
        if (Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > ProtocolCodec.MaxFrameBytes - 8192) { Outcome = QueryOutcome.Failure("vitals_result_too_large", "Native colony vitals exceed the bridge delivery bound; no silent truncation."); return; }
        Outcome = new QueryOutcome { Status = partial ? ProtocolStatuses.Partial : ProtocolStatuses.Ok, Data = result,
            Issues = partial ? new[] { new QueryIssue { Code = "vitals_partial", Message = "Inspect individual domain status and reasons.", Path = new JArray() } } : Array.Empty<QueryIssue>() };
    }
    private IEnumerable<int> Read()
    {
        ColonyVitalsContract.Validate(args);
        GameImplementation.RequireKnownBinary();
        map = GameWorldReferences.ResolveMap((string?)args["map"]);
        // One population for every domain, so counts agree within the sample.
        var colonists = map.mapPawns.FreeColonistsSpawned.ToArray();
        result["colonists"] = colonists.Length;
        result["population"] = "Native FreeColonistsSpawned on the selected map at read start.";
        foreach (var name in ColonyVitalsContract.Domains) {
            var domain = new JObject { ["domain"] = name, ["status"] = "reading", ["sample_tick"] = Find.TickManager.TicksGame,
                ["numbers"] = new JObject(), ["method"] = new JObject() };
            domains.Add(domain);
            using var work = ReadDomain(name, colonists, domain).GetEnumerator();
            while (true) {
                bool more = false; Exception? failure = null;
                try { more = work.MoveNext(); } catch (Exception error) when (ReadContext.Recoverable(error)) { failure = error; }
                if (failure != null) { domain["status"] = "unavailable"; domain["error"] = Error(failure); partial = true; break; }
                if (!more) { if ((string?)domain["status"] == "reading") domain["status"] = "ok"; break; }
                yield return work.Current;
            }
            domain["finished_tick"] = Find.TickManager.TicksGame;
            if ((string?)domain["status"] != "ok") partial = true;
            yield return 0;
        }
    }
    private IEnumerable<int> ReadDomain(string name, Pawn[] colonists, JObject domain) => name switch {
        "life" => ColonyVitalsLife.Read(colonists, domain),
        "food" => ColonyVitalsFood.Read(map!, colonists, domain),
        "shelter" => ColonyVitalsShelter.Read(map!, colonists, domain),
        _ => ColonyVitalsLabor.Read(colonists, domain)
    };
    private static JObject Error(Exception error) => new() { ["code"] = (error as ExecutionError)?.Code ?? error.GetType().Name, ["message"] = error.Message };

    internal static JObject PawnRow(Pawn pawn) => new() { ["id"] = pawn.GetUniqueLoadID(), ["label"] = pawn.LabelShortCap, ["x"] = pawn.Position.x, ["z"] = pawn.Position.z };
    internal static JObject ThingRow(Thing thing) => new() { ["id"] = thing.GetUniqueLoadID(), ["def"] = thing.def.defName, ["label"] = thing.LabelCap,
        ["count"] = thing.stackCount, ["x"] = thing.Position.x, ["z"] = thing.Position.z };
}
