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
    private IFrameQueryOperation? activeOperation;
    private ProtocolEnvelope? activeRequest;
    private SessionDescriptor? activeSession;
    private readonly WriteLedgerSaveState writeLedgerSave = new();
    private readonly WriteLedger writeLedger = new();
    private readonly WriteTargetIndex writeTargets = new(WriteTargetReferences.Destroyed);
    private readonly WriteTargetReferences writeTargetReferences = new();
    private WriteCoordinator? writeCoordinator;


    // RimWorld discovers GameComponents with this exact constructor signature.
    // Do not retain the Game reference: the process-scoped runtime must never
    // keep an old game graph alive after returning to the main menu or loading.
    public NekoGameComponent(Game game)
    {
    }

    public override void ExposeData()
    {
        Scribe_Values.Look(ref colonyId, "nekoColonyId", string.Empty);
        if (Scribe.mode == LoadSaveMode.Saving) {
            if (writeLedgerSave.Valid) writeTargets.Prune(writeLedger);
            writeLedgerSave.PrepareSave(writeLedger);
        }
        Scribe_Values.Look(ref writeLedgerSave.Data, "nekoWriteLedger", string.Empty);
        if (Scribe.mode == LoadSaveMode.LoadingVars) {
            writeLedgerSave.Loaded(writeLedger);
            if (!writeLedgerSave.Valid) {
                Log.Warning("[N.E.K.O RimWorld] Invalid write ledger; writing is disabled for this save.");
            }
        }
        writeTargetReferences.ExposeData(writeTargets);
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

        if (activeOperation != null) {
            if (activeOperation.Advance()) {
                var completed = activeOperation;
                var request = activeRequest!;
                var sample = activeSession!;
                if (request.Type == ProtocolMessageTypes.WriteRequest)
                    sample = WriteReceiptSession.AtTick(sample, Find.TickManager?.TicksGame ?? sample.GameTick);
                // Completion owns no queue slot during delivery. Even an
                // unexpected publication error cannot replay it every frame.
                activeOperation = null; activeRequest = null; activeSession = null;
                try { Publish(runtime, request, sample, completed.Outcome!); }
                finally { completed.Dispose(); }
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
        activeOperation?.Dispose(); activeOperation = null; activeRequest = null; activeSession = null;
        WorldObservation.Clear();
        SituationAlertObservations.Clear();
        VerseKnowledge.StartOnce();
        GameImplementation.PrepareBehaviorReads();
        NormalizeOrCreateColonyId();

        loadEpoch = Guid.NewGuid().ToString("N");
        writeCoordinator = new WriteCoordinator(writeLedger, writeTargets);
        writeCoordinator.StartEpoch(loadEpoch);
        snapshotSequence = 0;
        NekoRimWorldMod.Runtime.AttachSession(CurrentSession(), writerReady: writeLedgerSave.Valid);
        Log.Message($"[N.E.K.O RimWorld] Game session attached ({source}).");
    }

    private void HandleInbound(
        BridgeRuntime runtime,
        ProtocolEnvelope request)
    {
        var session = CurrentSession();
        if (writeLedgerSave.Valid) writeTargets.Prune(writeLedger);

        bool writing = string.Equals(request.Type, ProtocolMessageTypes.WriteRequest, StringComparison.Ordinal);
        if (!writing && !string.Equals(request.Type, ProtocolMessageTypes.QueryRequest, StringComparison.Ordinal))
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
            if (writing) {
                runtime.EnqueueControl(ColonyWriteContract.UnavailableResult(request, session, true));
                return;
            }
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
        if (writing) {
            var args = (Newtonsoft.Json.Linq.JObject)request.Payload.Arguments!.DeepClone();
            var mode = request.Payload.Mode!;
            var coordinator = writeCoordinator!;
            activeOperation = new CoordinatedWriteOperation(() => coordinator.Execute(mode, args, new VerseWriteFieldResolver(new VerseGameData()), () => loadEpoch,
                () => writeLedgerSave.Valid && UnityData.IsInMainThread && Current.ProgramState == ProgramState.Playing && !LongEventHandler.AnyEventNowOrWaiting,
                Find.TickManager?.TicksGame ?? 0));
            activeRequest = request; activeSession = sampledSession;
            return;
        }
        if (Array.IndexOf(ColonyReadContract.Operations, request.Payload.Operation) >= 0 || request.Payload.Operation == SituationSnapshotContract.Operation || request.Payload.Operation == "query" || Array.IndexOf(WorldObservationContract.Operations, request.Payload.Operation) >= 0 || Array.IndexOf(ActionInspectionContract.Operations, request.Payload.Operation) >= 0) {
            activeOperation = request.Payload.Operation == SituationSnapshotContract.Operation ? new SituationSnapshot(request.Payload.Arguments!, sampledSession)
                : Array.IndexOf(ColonyReadContract.Operations, request.Payload.Operation) >= 0 ? new ColonyReadOperation(request.Payload.Operation!, request.Payload.Arguments!, sampledSession)
                : request.Payload.Operation == "query" ? QueryExecutor.BeginPublicQuery(request.Payload.Arguments!,sampledSession)
                : Array.IndexOf(ActionInspectionContract.Operations, request.Payload.Operation) >= 0 ? new NativeBehaviorOperation(request.Payload.Operation!, request.Payload.Arguments!, sampledSession)
                : new WorldObservation.Operation(request.Payload.Operation!, request.Payload.Arguments!, sampledSession);
            activeRequest = request; activeSession = sampledSession;
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
        ProtocolEnvelope response;
        if (request.Type == ProtocolMessageTypes.WriteRequest) {
            try {
                response = ProtocolEnvelope.Create(ProtocolMessageTypes.WriteResult, request.ConnectionEpoch, 0,
                    new ProtocolPayload { Status = outcome.Status, Data = outcome.Data, Issues = outcome.Issues, ErrorCode = outcome.ErrorCode, Message = outcome.Message }, sampledSession, request.RequestId);
                ProtocolCodec.Serialize(response);
            } catch (ProtocolException error) {
                response = ProtocolEnvelope.Create(ProtocolMessageTypes.WriteResult, request.ConnectionEpoch, 0,
                    new ProtocolPayload { Status = ProtocolStatuses.Failed, ErrorCode = error.ErrorCode, Message = "The write result could not be encoded.", Issues = Array.Empty<QueryIssue>() }, sampledSession, request.RequestId);
            }
        } else response = QueryResultDelivery.Build(request, sampledSession, outcome);
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
