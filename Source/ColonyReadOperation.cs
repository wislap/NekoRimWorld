using System;
using System.Collections.Generic;
using System.Text;
using GraphQL;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Verse;

namespace Neko.RimWorld;

internal sealed class ColonyReadOperation : FrameQueryOperation
{
    private readonly Game game = Current.Game;
    private readonly SessionDescriptor session;
    private readonly int startedTick = Find.TickManager.TicksGame;
    private JObject? result;
    private ReadContext? context;
    internal ColonyReadOperation(string operation, JObject args, SessionDescriptor session)
    {
        this.session = session;
        InitializeWork(Read(operation, (JObject)args.DeepClone()).GetEnumerator());
    }
    private IEnumerable<int> Read(string operation, JObject args)
    {
        ColonyReadContract.Validate(operation, args);
        GameImplementation.RequireKnownBinary();
        yield return 0;
        if (operation == "inspect_resources") {
            context = new ReadContext(new VerseGameData(), session, 12);
            foreach (var step in ResourceReadings.ReadSteps(args, session, context, value => result = value)) yield return step;
            yield break;
        }
        result = operation switch {
            "inspect_factions" => FactionReadings.Read(args, session), "inspect_quests" => QuestReadings.Read(args, session),
            "inspect_research" => ResearchReadings.Read(args, session), "inspect_planet" => PlanetReadings.Read(args, session),
            "inspect_trade" => TradeReadings.Read(args, session), "evaluate_journey" => JourneyEvaluation.Read(args, session),
            "evaluate_trade" => TradeReadings.Evaluate(args, session), _ => throw ReadContext.Error("invalid_arguments", "Unknown colony read.")
        };
        yield return 0;
    }
    protected override void RestartReadSlice() => context?.RestartSlice();
    protected override void ValidateFrame()
    {
        if (Current.Game != game) throw ReadContext.Error("world_changed", "Loaded game changed during colony-state read.");
        EnsureLifetime(8, "observation_deadline", "Colony-state read exceeded its lifetime.");
    }
    protected override void Complete()
    {
        session.GameTick = Find.TickManager.TicksGame;
        result!["format"] = ColonyReadContract.Version; result["read_only"] = true; result["read_ms"] = ElapsedMilliseconds;
        result["sample"] = new JObject { ["colony_id"] = session.ColonyId, ["load_epoch"] = session.LoadEpoch, ["snapshot_sequence"] = session.SnapshotSequence,
            ["started_tick"] = startedTick, ["finished_tick"] = session.GameTick, ["same_tick"] = startedTick == session.GameTick, ["atomic"] = false };
        Outcome = Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > ProtocolCodec.MaxFrameBytes - 8192
            ? QueryOutcome.Failure("observation_result_too_large", "Narrow the requested colony-state view.")
            : (string?)result["status"] == "partial" ? QueryOutcome.Partial(result, "colony_state_partial", "Inspect returned coverage and unknowns.")
            : new QueryOutcome { Status = ProtocolStatuses.Ok, Data = result };
    }
    protected override void HandleError(Exception error) => Outcome = QueryOutcome.Failure((error as ExecutionError)?.Code ?? "colony_state_unavailable", error.Message);
    protected override void ReleaseReferences() { result = null; context = null; }
}
