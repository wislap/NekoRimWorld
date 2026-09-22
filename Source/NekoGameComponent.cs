using System;
using System.Diagnostics;
using System.Threading;
using Verse;

namespace Neko.RimWorld;

/// <summary>
/// Save-scoped, game-thread boundary for the bridge. Network code hands this
/// component protocol DTOs; this component is the only layer that resolves them
/// against live Verse state.
/// </summary>
public sealed class NekoGameComponent : GameComponent
{
    private const int MaxQueriesPerUpdate = 1;
    private static readonly long QueryBudgetStopwatchTicks =
        Math.Max(1L, Stopwatch.Frequency * 2L / 1000L);

    private string colonyId = string.Empty;
    private string loadEpoch = string.Empty;
    private long snapshotSequence;
    private SpatialQuery.Operation? spatial;
    private ProtocolEnvelope? spatialRequest;
    private SessionDescriptor? spatialSession;


    // RimWorld discovers GameComponents with this exact constructor signature.
    // Do not retain the Game reference: the process-scoped runtime must never
    // keep an old game graph alive after returning to the main menu or loading.
    public NekoGameComponent(Game game)
    {
    }

    public override void ExposeData()
    {
        Scribe_Values.Look(ref colonyId, "nekoColonyId", string.Empty);
    }

    public override void StartedNewGame()
    {
        StartSession("new_game");
    }

    public override void LoadedGame()
    {
        StartSession("loaded_game");
    }

    public override void GameComponentUpdate()
    {
        if (string.IsNullOrEmpty(loadEpoch))
        {
            return;
        }

        VerseKnowledge.Update();

        var runtime = NekoRimWorldMod.Runtime;
        var tickManager = Find.TickManager;
        runtime.TouchSession(
            loadEpoch,
            tickManager?.TicksGame ?? 0,
            snapshotSequence);

        if (spatial != null) {
            if (spatial.Advance()) {
                Publish(runtime, spatialRequest!, spatialSession!, spatial.Outcome!);
                spatial.Dispose(); spatial = null; spatialRequest = null; spatialSession = null;
            }
            return;
        }
        var startedAt = Stopwatch.GetTimestamp();
        var processed = 0;

        while (processed < MaxQueriesPerUpdate &&
               Stopwatch.GetTimestamp() - startedAt < QueryBudgetStopwatchTicks &&
               runtime.TryDequeueInbound(out var request))
        {
            processed++;
            HandleInbound(runtime, request);
        }
    }

    private void StartSession(string source)
    {
        spatial?.Dispose(); spatial = null; spatialRequest = null; spatialSession = null;
        VerseKnowledge.StartOnce();
        NormalizeOrCreateColonyId();

        loadEpoch = Guid.NewGuid().ToString("N");
        snapshotSequence = 0;
        NekoRimWorldMod.Runtime.AttachSession(CurrentSession());
        Log.Message($"[N.E.K.O RimWorld] Game session attached ({source}).");
    }

    private void HandleInbound(
        BridgeRuntime runtime,
        ProtocolEnvelope request)
    {
        var session = CurrentSession();

        if (!string.Equals(request.Type, ProtocolMessageTypes.QueryRequest, StringComparison.Ordinal))
        {
            EnqueueProtocolError(
                runtime,
                request,
                session,
                "invalid_message_type",
                "The game-thread queue accepts query_request messages only.");
            return;
        }

        if (string.IsNullOrWhiteSpace(request.RequestId) || !Guid.TryParse(request.RequestId, out _))
        {
            EnqueueProtocolError(
                runtime,
                request,
                session,
                "invalid_request_id",
                "request_id must be a UUID.");
            return;
        }

        if (!string.Equals(request.ColonyId, colonyId, StringComparison.Ordinal) ||
            !string.Equals(request.LoadEpoch, loadEpoch, StringComparison.Ordinal))
        {
            runtime.EnqueueControl(ProtocolEnvelope.CreateQueryResult(
                request,
                request.ConnectionEpoch,
                sequence: 0,
                session,
                status: ProtocolStatuses.StaleSession,
                errorCode: "stale_session",
                message: "The requested colony load is no longer active."));
            return;
        }

        var next = checked(snapshotSequence + 1);
        var sampledSession = new SessionDescriptor(colonyId, loadEpoch, session.GameTick, next);
        if (request.Payload.Operation == "spatial_query") {
            spatial = new SpatialQuery.Operation(request.Payload.Arguments!, sampledSession);
            spatialRequest = request; spatialSession = sampledSession;
            return;
        }
        QueryOutcome outcome;
        try
        {
            outcome = QueryExecutor.Execute(request.Payload.Operation!, request.Payload.Arguments!, sampledSession);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            Log.WarningOnce($"[N.E.K.O RimWorld] Query read failed: {exception.GetType().Name}", 0x4e4b5110);
            outcome = QueryOutcome.Failure("query_failed", "The game-thread query could not complete.");
        }
        Publish(runtime, request, sampledSession, outcome);
    }

    private void Publish(BridgeRuntime runtime, ProtocolEnvelope request, SessionDescriptor sampledSession, QueryOutcome outcome)
    {
        var next = sampledSession.SnapshotSequence;
        snapshotSequence = next;
        runtime.TouchSession(loadEpoch, sampledSession.GameTick, next);
        var response = ProtocolEnvelope.CreateQueryResult(request, request.ConnectionEpoch, 0,
            sampledSession, outcome.Status, outcome.Data, outcome.Issues, outcome.ErrorCode, outcome.Message, outcome.Execution);
        // Validate the whole output before queuing it. Never silently crop business data.
        try { ProtocolCodec.Serialize(response); }
        catch (ProtocolException exception)
        {
            response = ProtocolEnvelope.CreateQueryResult(request, request.ConnectionEpoch, 0,
                sampledSession, ProtocolStatuses.Failed, errorCode: exception.ErrorCode,
                message: "The query result exceeded transport limits or contained invalid data.");
        }
        runtime.EnqueueControl(response);
    }

    private static void EnqueueProtocolError(
        BridgeRuntime runtime,
        ProtocolEnvelope request,
        SessionDescriptor session,
        string errorCode,
        string message)
    {
        var requestIdIsSafe =
            !string.IsNullOrWhiteSpace(request.RequestId) &&
            Guid.TryParse(request.RequestId, out _);
        runtime.EnqueueControl(ProtocolEnvelope.CreateProtocolError(
            request.ConnectionEpoch,
            sequence: 0,
            errorCode,
            message,
            request: requestIdIsSafe ? request : null,
            session));
    }

    private SessionDescriptor CurrentSession()
    {
        var tickManager = Find.TickManager;
        return new SessionDescriptor(
            colonyId,
            loadEpoch,
            tickManager?.TicksGame ?? 0,
            snapshotSequence);
    }

    private void NormalizeOrCreateColonyId()
    {
        if (Guid.TryParse(colonyId, out var parsed) && parsed != Guid.Empty)
        {
            colonyId = parsed.ToString("N");
            return;
        }

        var invalidPersistedValue = !string.IsNullOrWhiteSpace(colonyId);
        colonyId = Guid.NewGuid().ToString("N");
        if (invalidPersistedValue)
        {
            Log.WarningOnce(
                "[N.E.K.O RimWorld] Invalid persisted colony_id was replaced with a new UUID.",
                0x4e4b5111);
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException &&
        exception is not AccessViolationException &&
        exception is not ThreadAbortException;
}
