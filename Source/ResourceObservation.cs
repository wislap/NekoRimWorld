using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GraphQL;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Verse;

namespace Neko.RimWorld;

// One exact definition per operation. The existing resource traversal owns
// quantities, source partitions and coverage; this adapter only schedules and
// projects that observation into the bounded observation manager's row format.
internal sealed class ResourceObservation : FrameQueryOperation
{
    private readonly Game game;
    private readonly SessionDescriptor session;
    private readonly string mapId;
    private readonly string definition;
    private readonly long startedTick;
    private Map? map;
    private ReadContext? context;
    private JObject? source;

    internal ResourceObservation(JObject spec)
    {
        if ((string?)spec["kind"] != "resources" || spec["map_id"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)spec["map_id"])
            || spec["target_ids"] is not JArray ids || ids.Count != 1 || ids[0]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)ids[0]))
            throw ReadContext.Error("invalid_arguments", "Resource observation requires one exact definition and a canonical map_id.");
        game = Current.Game ?? throw ReadContext.Error("world_unavailable", "No loaded game is available for resource observation.");
        session = game.GetComponent<NekoGameComponent>().ObservationSession();
        var tickManager = Find.TickManager ?? throw ReadContext.Error("world_unavailable", "Game ticks are unavailable for resource observation.");
        startedTick = tickManager.TicksGame;
        mapId = spec["map_id"]!.Value<string>()!; definition = ids[0]!.Value<string>()!;
        InitializeWork(Read().GetEnumerator());
    }

    private IEnumerable<int> Read()
    {
        GameImplementation.RequireKnownBinary();
        map = GameWorldReferences.ResolveMap(mapId);
        if (map.GetUniqueLoadID() != mapId) throw ReadContext.Error("invalid_arguments", "Resource observation map_id must identify the selected native map.");
        var def = DefDatabase<ThingDef>.GetNamedSilentFail(definition);
        if (def == null || def.defName != definition) throw ReadContext.Error("unknown_definition", "Resource observation target must be an exact loaded definition name.");
        context = new ReadContext(new VerseGameData(), session, 12);
        yield return 0;
        var args = new JObject { ["map"] = mapId, ["items"] = new JArray(definition), ["aspects"] = new JArray("quantity", "distribution"), ["offset"] = 0, ["limit"] = 1 };
        foreach (var step in ResourceReadings.ReadSteps(args, session, context, value => source = value)) yield return step;
    }

    protected override void RestartReadSlice() => context?.RestartSlice();
    protected override void ValidateFrame()
    {
        if (!ReferenceEquals(Current.Game, game) || game.GetComponent<NekoGameComponent>().ObservationSession().LoadEpoch != session.LoadEpoch)
            throw ReadContext.Error("world_changed", "Loaded colony changed during resource observation.");
        if (map != null && (map.Disposed || !Find.Maps.Contains(map))) throw ReadContext.Error("world_changed", "Selected map became unavailable during resource observation.");
        EnsureLifetime(8, "observation_deadline", "Resource observation exceeded its cooperative lifetime.");
    }

    private JObject Sample() => new() { ["colony_id"] = session.ColonyId, ["load_epoch"] = session.LoadEpoch, ["snapshot_sequence"] = session.SnapshotSequence,
        ["started_tick"] = startedTick, ["finished_tick"] = Find.TickManager == null ? JValue.CreateNull() : new JValue(Find.TickManager.TicksGame), ["atomic"] = false };

    protected override void Complete()
    {
        if (source?["rows"] is not JArray rows || rows.Count != 1 || rows[0] is not JObject original || (string?)original["def_name"] != definition)
            throw ReadContext.Error("resource_observation_incomplete", "Resource reader did not return the exact selected definition.");
        var row = (JObject)original.DeepClone();
        bool complete = (string?)source["status"] == "ok" && (string?)row["coverage"] == "complete_in_scope"
            && source["page"]?["has_more"]?.Type == JTokenType.Boolean && (bool)source["page"]!["has_more"]! == false
            && (int?)source["total"] == 1 && row["observed_units"]?.Type == JTokenType.Integer && row["sources"] is JArray && source["scope"] is JObject;
        row["id"] = definition; row["definition"] = definition; row["map_id"] = mapId; row["status"] = complete ? "observed" : "partial";
        row["scope"] = source["scope"]?.DeepClone() ?? JValue.CreateNull();
        var result = new JObject { ["objects"] = new JArray(row), ["complete"] = complete, ["map_id"] = mapId, ["read_only"] = true, ["sample"] = Sample() };
        if (source["issues"] != null) result["issues"] = source["issues"]!.DeepClone();
        if (Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > ProtocolCodec.MaxFrameBytes - 8192)
            throw ReadContext.Error("observation_result_too_large", "Selected resource observation exceeds the native result budget.");
        Outcome = complete ? new QueryOutcome { Status = ProtocolStatuses.Ok, Data = result }
            : QueryOutcome.Partial(result, "resource_observation_partial", "Selected resource observation has incomplete native coverage.");
    }

    protected override void HandleError(Exception error)
    {
        var code = (error as ExecutionError)?.Code ?? "resource_observation_unavailable";
        // Never turn failure into an empty stock or zero quantity. The manager
        // receives an explicit unknown row for the requested target identity.
        var result = new JObject { ["objects"] = new JArray(new JObject { ["id"] = definition, ["definition"] = definition, ["map_id"] = mapId,
            ["status"] = "unknown", ["coverage"] = "unknown", ["reason"] = new JObject { ["code"] = code, ["message"] = error.Message } }),
            ["complete"] = false, ["map_id"] = mapId, ["read_only"] = true, ["sample"] = Sample() };
        Outcome = QueryOutcome.Partial(result, code, "The selected resource observation is unavailable; quantity is unknown.");
    }

    protected override void ReleaseReferences() { source = null; context = null; map = null; }
}
