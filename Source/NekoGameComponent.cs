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

        var runtime = NekoRimWorldMod.Runtime;
        var tickManager = Find.TickManager;
        runtime.TouchSession(
            loadEpoch,
            tickManager?.TicksGame ?? 0,
            snapshotSequence);

        var startedAt = Stopwatch.GetTimestamp();
        var deadlineTimestamp = startedAt > long.MaxValue - QueryBudgetStopwatchTicks
            ? long.MaxValue
            : startedAt + QueryBudgetStopwatchTicks;
        var processed = 0;

        while (processed < MaxQueriesPerUpdate &&
               Stopwatch.GetTimestamp() - startedAt < QueryBudgetStopwatchTicks &&
               runtime.TryDequeueInbound(out var request))
        {
            processed++;
            HandleInbound(runtime, request, deadlineTimestamp);
        }
    }

    private void StartSession(string source)
    {
        NormalizeOrCreateColonyId();

        loadEpoch = Guid.NewGuid().ToString("N");
        snapshotSequence = 0;
        NekoRimWorldMod.Runtime.AttachSession(CurrentSession());
        Log.Message($"[N.E.K.O RimWorld] Game session attached ({source}).");
    }

    private void HandleInbound(
        BridgeRuntime runtime,
        ProtocolEnvelope request,
        long deadlineTimestamp)
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

        if (!TryReadQueryName(request.Payload, out var queryName))
        {
            EnqueueQueryFailure(
                runtime,
                request,
                session,
                "invalid_query",
                "payload.query must be a non-empty string.");
            return;
        }

        if (!string.Equals(queryName, ColonyQueries.ColonyDigest, StringComparison.Ordinal))
        {
            runtime.EnqueueControl(ProtocolEnvelope.CreateQueryResult(
                request,
                request.ConnectionEpoch,
                sequence: 0,
                session,
                status: ProtocolStatuses.Unsupported,
                errorCode: "unsupported_query",
                message: "The requested query is not supported."));
            return;
        }

        try
        {
            var nextSnapshotSequence = checked(snapshotSequence + 1);
            QueryDataDto data;
            try
            {
                data = ColonyQueries.BuildColonyDigest(
                    nextSnapshotSequence,
                    deadlineTimestamp);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                Log.WarningOnce(
                    $"[N.E.K.O RimWorld] Snapshot read failed unexpectedly: {exception.GetType().Name}",
                    0x4e4b5110);
                data = ColonyQueries.CreateFailureDigest(
                    nextSnapshotSequence,
                    session.GameTick,
                    ReadPausedSafely());
            }

            var digest = data.ColonyDigest!;
            var isPartial = data.Omissions.Length != 0;
            snapshotSequence = nextSnapshotSequence;
            session = new SessionDescriptor(
                colonyId,
                loadEpoch,
                digest.GameTick,
                snapshotSequence);
            runtime.TouchSession(
                loadEpoch,
                session.GameTick,
                session.SnapshotSequence);
            runtime.EnqueueControl(ProtocolEnvelope.CreateQueryResult(
                request,
                request.ConnectionEpoch,
                sequence: 0,
                session,
                status: isPartial
                    ? ProtocolStatuses.Partial
                    : ProtocolStatuses.Ok,
                data: data,
                message: isPartial
                    ? "The colony snapshot is partial; inspect data.omissions."
                    : null));
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            Log.ErrorOnce(
                $"[N.E.K.O RimWorld] colony_digest failed: {exception}",
                0x4e454b51);
            EnqueueQueryFailure(
                runtime,
                request,
                CurrentSession(),
                "query_failed",
                "The colony query failed inside RimWorld.");
        }
    }

    private static bool TryReadQueryName(
        ProtocolPayload payload,
        out string queryName)
    {
        queryName = payload.Query?.Trim() ?? string.Empty;
        return queryName.Length > 0;
    }

    private static void EnqueueQueryFailure(
        BridgeRuntime runtime,
        ProtocolEnvelope request,
        SessionDescriptor session,
        string errorCode,
        string message)
    {
        runtime.EnqueueControl(ProtocolEnvelope.CreateQueryResult(
            request,
            request.ConnectionEpoch,
            sequence: 0,
            session,
            status: ProtocolStatuses.Failed,
            errorCode: errorCode,
            message: message));
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

    private static bool ReadPausedSafely()
    {
        try
        {
            return Find.TickManager?.Paused ?? true;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return true;
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException &&
        exception is not AccessViolationException &&
        exception is not ThreadAbortException;
}
