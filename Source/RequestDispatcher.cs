using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Verse;

namespace Neko.RimWorld;

// Game-thread request routing: validates inbound envelopes against the
// attached session, runs queries, policies and writes, and publishes results.
internal sealed class RequestDispatcher
{
    private const int MaxQueriesPerUpdate = 1;
    private static readonly long QueryBudgetStopwatchTicks =
        Math.Max(1L, Stopwatch.Frequency * 2L / 1000L);

    private readonly NekoSaveState save;
    private readonly NekoSession session;
    private readonly PauseObserver pause;
    private readonly ManualAreaObserver manualAreas;
    private readonly LedgerSnapshotService ledgerSnapshots;
    private readonly ColonyObservations colonyObservations;
    private readonly Func<NativeWriteFields> writeFields;
    private readonly Action eventsSubscribed;
    private IFrameQueryOperation? activeOperation;
    private ProtocolEnvelope? activeRequest;
    private SessionDescriptor? activeSession;

    internal RequestDispatcher(NekoSaveState save, NekoSession session, PauseObserver pause, ManualAreaObserver manualAreas, LedgerSnapshotService ledgerSnapshots,
        ColonyObservations colonyObservations, Func<NativeWriteFields> writeFields, Action eventsSubscribed)
    {
        this.save = save; this.session = session; this.pause = pause; this.manualAreas = manualAreas; this.ledgerSnapshots = ledgerSnapshots;
        this.colonyObservations = colonyObservations; this.writeFields = writeFields; this.eventsSubscribed = eventsSubscribed;
    }

    internal void Reset() { activeOperation?.Dispose(); activeOperation = null; activeRequest = null; activeSession = null; }

    internal void Update(BridgeRuntime runtime)
    {
        if (activeOperation != null) {
            if (activeRequest!.Type == ProtocolMessageTypes.WriteRequest && !runtime.IsActiveRequest(activeRequest)) { Reset(); return; }
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

    private void HandleInbound(
        BridgeRuntime runtime,
        ProtocolEnvelope request)
    {
        var current = session.Current();
        var colonyId = session.ColonyId; var loadEpoch = session.LoadEpoch;
        if (save.LedgerSave.Valid && request.Payload.Operation != "write_ledger_snapshot" && request.Payload.Operation != "write_field_identities" && request.Payload.Operation != "write_undo_fields") save.Targets.Prune(save.Ledger);

        bool writing = string.Equals(request.Type, ProtocolMessageTypes.WriteRequest, StringComparison.Ordinal);
        bool policy = request.Type == ProtocolMessageTypes.PolicyRequest;
        if (!writing && !policy && !string.Equals(request.Type, ProtocolMessageTypes.QueryRequest, StringComparison.Ordinal))
        {
            EnqueueProtocolError(
                runtime,
                request,
                current,
                "invalid_message_type",
                "The game-thread queue accepts query_request messages only.");
            return;
        }

        if (string.IsNullOrWhiteSpace(request.RequestId) || !Guid.TryParse(request.RequestId, out _))
        {
            EnqueueProtocolError(
                runtime,
                request,
                current,
                "invalid_request_id",
                "request_id must be a UUID.");
            return;
        }

        if (!string.Equals(request.ColonyId, colonyId, StringComparison.Ordinal) ||
            !string.Equals(request.LoadEpoch, loadEpoch, StringComparison.Ordinal))
        {
            if (policy) { runtime.EnqueueControl(ProtocolEnvelope.Create(ProtocolMessageTypes.PolicyResult, request.ConnectionEpoch, 0, new ProtocolPayload { Status = ProtocolStatuses.StaleSession, ErrorCode = "stale_epoch", Message = "The requested colony load is no longer active.", Issues = Array.Empty<QueryIssue>() }, current, request.RequestId)); return; }
            if (writing) {
                runtime.EnqueueControl(ColonyWriteContract.UnavailableResult(request, current, true));
                return;
            }
            runtime.EnqueueControl(ProtocolEnvelope.CreateQueryResult(
                request,
                request.ConnectionEpoch,
                sequence: 0,
                current,
                status: ProtocolStatuses.StaleSession,
                errorCode: "stale_session",
                message: "The requested colony load is no longer active."));
            return;
        }

        if (policy) {
            if (!NativeEventHookSupport.Ready()) {
                runtime.EnqueueControl(ProtocolEnvelope.Create(ProtocolMessageTypes.PolicyResult, request.ConnectionEpoch, 0, new ProtocolPayload { Status = ProtocolStatuses.Unsupported, ErrorCode = "unsupported", Message = "Native event hooks are unavailable.", Issues = Array.Empty<QueryIssue>() }, current, request.RequestId)); return;
            }
            pause.RefreshPolicy();
            if (pause.Connection != request.ConnectionEpoch || !runtime.EnableEvents(request, out bool firstSubscription)) return;
            if (firstSubscription) { eventsSubscribed(); session.Emit("session_snapshot", new JObject { ["origin"] = session.Source, ["start_tick"] = session.StartTick, ["pause_state"] = pause.Evidence() }); }
            if ((string?)request.Payload.Arguments!["source"] == "planner") {
                var registered = colonyObservations.Replace(request.Payload.Arguments, request.ConnectionEpoch, Find.TickManager.TicksGame);
                runtime.EnqueueControl(ProtocolEnvelope.Create(ProtocolMessageTypes.PolicyResult, request.ConnectionEpoch, 0, new ProtocolPayload { Status = ProtocolStatuses.Ok, Data = registered, Issues = Array.Empty<QueryIssue>() }, current, request.RequestId)); return;
            }
            pause.SetLetterDefs(((JArray)request.Payload.Arguments!["pause_on_letter_defs"]!).Values<string>()!);
            var data = (JObject)request.Payload.Arguments.DeepClone();
            session.Emit("policy_changed", (JObject)data.DeepClone());
            runtime.EnqueueControl(ProtocolEnvelope.Create(ProtocolMessageTypes.PolicyResult, request.ConnectionEpoch, 0, new ProtocolPayload { Status = ProtocolStatuses.Ok, Data = data, Issues = Array.Empty<QueryIssue>() }, current, request.RequestId)); return;
        }
        if (!writing && (request.Payload.Operation == "write_field_identities" || request.Payload.Operation == "write_undo_fields")) {
            var sample = new SessionDescriptor(colonyId, loadEpoch, Find.TickManager?.TicksGame ?? 0, checked(session.SnapshotSequence + 1));
            QueryOutcome result;
            try {
                if (!UnityData.IsInMainThread || Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting) throw new InvalidOperationException("World unavailable.");
                var args = request.Payload.Arguments ?? throw new ArgumentException("Arguments required.");
                JObject data;
                if (request.Payload.Operation == "write_undo_fields") {
                    ColonyWriteContract.ValidateRequest("undo", args);
                    data = session.Writes!.UndoFields((string)args["token"]!);
                } else {
                    if (args.Properties().Count() != 1 || args["changeset"] is not JObject changeset) throw new ArgumentException("changeset required.");
                    if ((string?)changeset["load_epoch"] != loadEpoch) throw new ArgumentException("Stale changeset epoch.");
                    data = WriteCoordinator.FieldIdentities(changeset, writeFields().Operations);
                }
                result = new QueryOutcome { Data = data };
            } catch (Exception error) when (IsRecoverable(error)) { result = QueryOutcome.Failure("identity_unavailable", "Field identities could not be resolved."); }
            Publish(runtime, request, sample, result); return;
        }
        if (!writing && request.Payload.Operation == "write_ledger_snapshot") {
            var sample = new SessionDescriptor(colonyId, loadEpoch, Find.TickManager?.TicksGame ?? 0, checked(session.SnapshotSequence + 1));
            QueryOutcome result;
            if (request.Payload.Arguments == null)
                result = QueryOutcome.Failure("invalid_arguments", "write_ledger_snapshot requires arguments.");
            else if (!UnityData.IsInMainThread || Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting)
                result = QueryOutcome.Failure("unavailable", "Ledger snapshot is unavailable.");
            else {
                try { result = new QueryOutcome { Data = ledgerSnapshots.Capture(WriteLedger.Selection(request.Payload.Arguments)) }; }
                catch (ArgumentException error) { result = QueryOutcome.Failure("invalid_arguments", error.Message); }
            }
            Publish(runtime, request, sample, result); return;
        }
        var next = checked(session.SnapshotSequence + 1);
        var sampledSession = new SessionDescriptor(colonyId, loadEpoch, current.GameTick, next);
        if (writing && request.Payload.Mode == "set_paused") {
            Publish(runtime, request, sampledSession, pause.SetPaused(request.Payload.Arguments!)); return;
        }
        if (writing) {
            var args = (JObject)request.Payload.Arguments!.DeepClone();
            var mode = request.Payload.Mode!;
            var coordinator = session.Writes!;
            activeOperation = new CoordinatedWriteOperation(() => NativeAreaManualChanges.WithinModelWrite(() => coordinator.Execute(mode, args, writeFields().Operations, () => session.LoadEpoch,
                () => save.LedgerSave.Valid && UnityData.IsInMainThread && Current.ProgramState == ProgramState.Playing && !LongEventHandler.AnyEventNowOrWaiting,
                Find.TickManager?.TicksGame ?? 0)));
            activeRequest = request; activeSession = sampledSession;
            return;
        }
        if (Array.IndexOf(ColonyManagementContract.Operations, request.Payload.Operation) >= 0 || Array.IndexOf(ColonyReadContract.Operations, request.Payload.Operation) >= 0 || request.Payload.Operation == SituationSnapshotContract.Operation || request.Payload.Operation == ColonyVitalsContract.Operation || request.Payload.Operation == "query" || Array.IndexOf(WorldObservationContract.Operations, request.Payload.Operation) >= 0 || Array.IndexOf(ActionInspectionContract.Operations, request.Payload.Operation) >= 0) {
            activeOperation = request.Payload.Operation == SituationSnapshotContract.Operation ? new SituationSnapshot(request.Payload.Arguments!, sampledSession)
                : request.Payload.Operation == ColonyVitalsContract.Operation ? new ColonyVitals(request.Payload.Arguments!, sampledSession)
                : Array.IndexOf(ColonyReadContract.Operations, request.Payload.Operation) >= 0 ? new ColonyReadOperation(request.Payload.Operation!, request.Payload.Arguments!, sampledSession)
                : Array.IndexOf(ColonyManagementContract.Operations, request.Payload.Operation) >= 0 ? new ColonyManagementOperation(request.Payload.Operation!, request.Payload.Arguments!, sampledSession)
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
        session.SnapshotSequence = next;
        runtime.TouchSession(session.LoadEpoch, sampledSession.GameTick, next);
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
        if (request.Type == ProtocolMessageTypes.WriteRequest && outcome.Status == ProtocolStatuses.Ok && (string?)outcome.Data?["status"] == "committed" && outcome.Data?["readback"] is JArray readback)
            manualAreas.TrackCommittedAreas(readback);
        if (request.Type == ProtocolMessageTypes.WriteRequest && request.Payload.Mode != "set_paused" && !outcome.IsReplay && outcome.Status == ProtocolStatuses.Ok && (string?)outcome.Data?["status"] is "committed" or "undone")
            session.Emit("write", new JObject { ["mode"] = request.Payload.Mode, ["request_id"] = request.RequestId, ["receipt"] = outcome.Data!.DeepClone() });
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

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException &&
        exception is not AccessViolationException &&
        exception is not ThreadAbortException;
}
