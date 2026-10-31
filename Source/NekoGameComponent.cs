using System;
using System.Diagnostics;
using System.Threading;
using Verse;
using RimWorld;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;

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
    private string policyConnection = "";
    private string sessionSource = "";
    private long sessionStartTick;
    private readonly PausePolicy pausePolicy = new();
    private bool lastSpeedPaused;
    private bool lastForcePaused;
    private bool lastWindowPaused;
    private long lastEventHeartbeat;
    private bool loadPausePristine;
    private long sessionInitialPauseTransitionRevision;

    internal void ObserveUserPause(bool before) {
        var tm = Find.TickManager; if (tm == null) return;
        bool after = tm.CurTimeSpeed == TimeSpeed.Paused;
        if (before != after) { pausePolicy.Observe(after ? "user" : "none"); lastSpeedPaused = after; EmitPause("user", tm); }
    }
    internal void ReceiveLetter(Letter letter) {
        var tm = Find.TickManager; if (tm == null || letter?.def == null) return;
        RefreshPolicy();
        string level = letter.def.pauseMode.ToString();
        bool matched = NativeEventHookSupport.Ready() && pausePolicy.Matches(letter.def.defName);
        bool reflected = false;
        if (matched && tm.CurTimeSpeed != TimeSpeed.Paused) { tm.TogglePaused(); if (tm.CurTimeSpeed == TimeSpeed.Paused) { reflected = true; pausePolicy.Observe("reflex"); lastSpeedPaused = true; EmitPause("reflex", tm); } }
        Emit("letter", new JObject { ["letter_id"] = letter.GetUniqueLoadID(), ["def"] = letter.def.defName, ["auto_pause_mode"] = level, ["is_choice_letter"] = letter is ChoiceLetter, ["reflected"] = reflected });
    }
    internal void ObserveLetterPause(bool before) {
        var tm = Find.TickManager; if (tm == null) return;
        bool after = tm.CurTimeSpeed == TimeSpeed.Paused;
        if (!before && after && !lastSpeedPaused) { pausePolicy.Observe("automatic_letter"); lastSpeedPaused = true; EmitPause("automatic_letter", tm); }
    }
    private void RefreshPolicy() { var connection = NekoRimWorldMod.Runtime.ReadyConnectionEpoch; if (policyConnection != connection) policyConnection = connection; pausePolicy.Bind(connection, colonyId, loadEpoch); }
    private void Emit(string kind, JObject data) { data["kind"] = kind; NekoRimWorldMod.Runtime.EmitEvent(CurrentSession(), data); }
    private void EmitPause(string source, TickManager tm) {
        loadPausePristine = false;
        Emit("pause_changed", new JObject {
        ["paused"] = tm.Paused, ["speed_paused"] = tm.CurTimeSpeed == TimeSpeed.Paused,
        ["force_paused"] = tm.ForcePaused, ["windows_force_paused"] = Find.WindowStack?.WindowsForcePause ?? false, ["source"] = source,
        ["pause_revision"] = pausePolicy.Revision
        });
    }
    // Native ownership metadata, also inspected by the isolated study controller.
    // This observes state only; it is not a model recovery capability.
    internal JObject CapturePauseState() {
        if (!UnityData.IsInMainThread || Current.ProgramState != ProgramState.Playing ||
            !NativeEventHookSupport.Ready() || Find.TickManager == null || Find.WindowStack == null)
            throw new InvalidOperationException("Native pause ownership metadata is unavailable.");
        RefreshPolicy(); ObservePause();
        return new JObject {
            ["connection_epoch"] = policyConnection, ["colony_id"] = colonyId, ["load_epoch"] = loadEpoch,
            ["owner"] = pausePolicy.Owner, ["pause_revision"] = pausePolicy.Revision,
            ["pause_transition_revision"] = pausePolicy.TransitionRevision, ["load_pause_pristine"] = loadPausePristine,
            ["session_initial_pause_transition_revision"] = sessionInitialPauseTransitionRevision,
            ["session_source"] = sessionSource, ["session_start_tick"] = sessionStartTick,
            ["speed_paused"] = Find.TickManager.CurTimeSpeed == TimeSpeed.Paused,
            ["windows_force_paused"] = Find.WindowStack?.WindowsForcePause ?? false
        };
    }
    private void ObservePause() {
        var tm = Find.TickManager; if (tm == null) return;
        bool speed = tm.CurTimeSpeed == TimeSpeed.Paused, force = tm.ForcePaused;
        bool window = Find.WindowStack?.WindowsForcePause ?? false;
        bool speedChanged = speed != lastSpeedPaused, windowChanged = window != lastWindowPaused;
        if (speedChanged || force != lastForcePaused || windowChanged) {
            if (speedChanged) pausePolicy.Observe(speed ? "unknown" : "none");
            string source = PausePolicy.ObservedSource(speedChanged, windowChanged);
            lastSpeedPaused = speed; lastForcePaused = force; lastWindowPaused = window; EmitPause(source, tm);
        }
    }
    private QueryOutcome SetPaused(JObject args) {
        if (!NativeEventHookSupport.Ready()) return QueryOutcome.Failure("unsupported", "Native pause hooks are unavailable.", ProtocolStatuses.Unsupported);
        var tm = Find.TickManager; bool paused = (bool)args["paused"]!; string role = (string)args["role"]!;
        if (tm == null || !UnityData.IsInMainThread || Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting) return QueryOutcome.Failure("unavailable", "Pause control is unavailable.");
        ObservePause(); bool before = tm.CurTimeSpeed == TimeSpeed.Paused;
        if (!paused && tm.ForcePaused) return QueryOutcome.Failure("force_paused", "A native force pause is active.");
        string? expectedOwner = (string?)args["expected_owner"];
        if (expectedOwner != null && (!before || !pausePolicy.CanResumeExpected(expectedOwner))) return QueryOutcome.Failure("pause_owner_changed", "The expected agent pause is no longer current.");
        if (!paused && before && !pausePolicy.CanResume) return QueryOutcome.Failure("pause_owned", "A user or unknown pause cannot be released by an agent.");
        if (before != paused) { tm.TogglePaused(); if ((tm.CurTimeSpeed == TimeSpeed.Paused) != paused) return QueryOutcome.Failure("pause_rejected", "Native pause control rejected the request."); pausePolicy.Observe(paused ? role : "none"); lastSpeedPaused = paused; EmitPause(role, tm); }
        var data = new JObject { ["paused"] = paused, ["role"] = role };
        if (expectedOwner != null) data["expected_owner"] = expectedOwner;
        return new QueryOutcome { Data = data };
    }


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
        NekoRimWorldMod.RefreshNativeCapabilities();
        RefreshPolicy();
        ObservePause();
        if (Stopwatch.GetTimestamp() - lastEventHeartbeat >= Stopwatch.Frequency * 2) { lastEventHeartbeat = Stopwatch.GetTimestamp(); Emit("heartbeat", new JObject()); }

        var runtime = NekoRimWorldMod.Runtime;
        var tickManager = Find.TickManager;
        runtime.TouchSession(
            loadEpoch,
            tickManager?.TicksGame ?? 0,
            snapshotSequence);

        if (activeOperation != null) {
            if (activeRequest!.Type == ProtocolMessageTypes.WriteRequest && !runtime.IsActiveRequest(activeRequest)) { activeOperation.Dispose(); activeOperation = null; activeRequest = null; activeSession = null; return; }
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
        sessionSource = source;
        sessionStartTick = Find.TickManager?.TicksGame ?? 0;
        WorldObservation.Clear();
        SituationAlertObservations.Clear();
        VerseKnowledge.StartOnce();
        GameImplementation.PrepareBehaviorReads();
        NormalizeOrCreateColonyId();

        loadEpoch = Guid.NewGuid().ToString("N");
        writeCoordinator = new WriteCoordinator(writeLedger, writeTargets);
        writeCoordinator.StartEpoch(loadEpoch);
        snapshotSequence = 0;
        policyConnection = ""; pausePolicy.Bind("", colonyId, loadEpoch); pausePolicy.Observe("unknown");
        sessionInitialPauseTransitionRevision = pausePolicy.TransitionRevision;
        lastSpeedPaused = Find.TickManager?.CurTimeSpeed == TimeSpeed.Paused;
        lastForcePaused = Find.TickManager?.ForcePaused ?? false;
        lastWindowPaused = Find.WindowStack?.WindowsForcePause ?? false;
        loadPausePristine = true;
        lastEventHeartbeat = 0;
        NekoRimWorldMod.Runtime.AttachSession(CurrentSession(), writerReady: writeLedgerSave.Valid);
        Log.Message($"[N.E.K.O RimWorld] Game session attached ({source}).");
    }

    private void HandleInbound(
        BridgeRuntime runtime,
        ProtocolEnvelope request)
    {
        var session = CurrentSession();
        if (writeLedgerSave.Valid && request.Payload.Operation != "write_ledger_snapshot" && request.Payload.Operation != "write_field_identities" && request.Payload.Operation != "write_undo_fields") writeTargets.Prune(writeLedger);

        bool writing = string.Equals(request.Type, ProtocolMessageTypes.WriteRequest, StringComparison.Ordinal);
        bool policy = request.Type == ProtocolMessageTypes.PolicyRequest;
        if (!writing && !policy && !string.Equals(request.Type, ProtocolMessageTypes.QueryRequest, StringComparison.Ordinal))
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
            if (policy) { runtime.EnqueueControl(ProtocolEnvelope.Create(ProtocolMessageTypes.PolicyResult, request.ConnectionEpoch, 0, new ProtocolPayload { Status = ProtocolStatuses.StaleSession, ErrorCode = "stale_epoch", Message = "The requested colony load is no longer active.", Issues = Array.Empty<QueryIssue>() }, session, request.RequestId)); return; }
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

        if (policy) {
            if (!NativeEventHookSupport.Ready()) {
                runtime.EnqueueControl(ProtocolEnvelope.Create(ProtocolMessageTypes.PolicyResult, request.ConnectionEpoch, 0, new ProtocolPayload { Status = ProtocolStatuses.Unsupported, ErrorCode = "unsupported", Message = "Native event hooks are unavailable.", Issues = Array.Empty<QueryIssue>() }, session, request.RequestId)); return;
            }
            RefreshPolicy();
            if (policyConnection != request.ConnectionEpoch || !runtime.EnableEvents(request, out bool firstSubscription)) return;
            if (firstSubscription) Emit("session_snapshot", new JObject { ["origin"] = sessionSource, ["start_tick"] = sessionStartTick });
            pausePolicy.Set(((JArray)request.Payload.Arguments!["pause_on_letter_defs"]!).Values<string>()!);
            var data = (JObject)request.Payload.Arguments.DeepClone();
            Emit("policy_changed", (JObject)data.DeepClone());
            runtime.EnqueueControl(ProtocolEnvelope.Create(ProtocolMessageTypes.PolicyResult, request.ConnectionEpoch, 0, new ProtocolPayload { Status = ProtocolStatuses.Ok, Data = data, Issues = Array.Empty<QueryIssue>() }, session, request.RequestId)); return;
        }
        if (!writing && !policy && (request.Payload.Operation == "write_field_identities" || request.Payload.Operation == "write_undo_fields")) {
            var sample = new SessionDescriptor(colonyId, loadEpoch, Find.TickManager?.TicksGame ?? 0, checked(snapshotSequence + 1));
            QueryOutcome result;
            try {
                if (!UnityData.IsInMainThread || Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting) throw new InvalidOperationException("World unavailable.");
                var args = request.Payload.Arguments ?? throw new ArgumentException("Arguments required.");
                JObject data;
                if (request.Payload.Operation == "write_undo_fields") {
                    ColonyWriteContract.ValidateRequest("undo", args);
                    data = writeCoordinator!.UndoFields((string)args["token"]!);
                } else {
                    if (args.Properties().Count() != 1 || args["changeset"] is not JObject changeset) throw new ArgumentException("changeset required.");
                    if ((string?)changeset["load_epoch"] != loadEpoch) throw new ArgumentException("Stale changeset epoch.");
                    data = WriteCoordinator.FieldIdentities(changeset, new VerseWriteFieldResolver(new VerseGameData()));
                }
                result = new QueryOutcome { Data = data };
            } catch (Exception error) when (IsRecoverable(error)) { result = QueryOutcome.Failure("identity_unavailable", "Field identities could not be resolved."); }
            Publish(runtime, request, sample, result); return;
        }
        if (!writing && !policy && request.Payload.Operation == "write_ledger_snapshot") {
            var sample = new SessionDescriptor(colonyId, loadEpoch, Find.TickManager?.TicksGame ?? 0, checked(snapshotSequence + 1));
            QueryOutcome result;
            if (request.Payload.Arguments == null || request.Payload.Arguments.HasValues)
                result = QueryOutcome.Failure("invalid_arguments", "write_ledger_snapshot requires empty arguments.");
            else if (!UnityData.IsInMainThread || Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting)
                result = QueryOutcome.Failure("unavailable", "Ledger snapshot is unavailable.");
            else {
                var resolver = new VerseWriteFieldResolver(new VerseGameData());
                result = new QueryOutcome { Data = writeLedger.ReadSnapshot(writeLedgerSave.Valid, writeTargets, WriteTargetReferences.Destroyed,
                    (id, field, target, value) => field == "production_bills" ? resolver.ResolveLedgerProductionBills(id, target, value) : resolver.ResolveHeldLedgerField(id, field, target) ?? resolver.ResolveLedgerField(id, field)) };
            }
            Publish(runtime, request, sample, result); return;
        }
        var next = checked(snapshotSequence + 1);
        var sampledSession = new SessionDescriptor(colonyId, loadEpoch, session.GameTick, next);
        if (writing && request.Payload.Mode == "set_paused") {
            Publish(runtime, request, sampledSession, SetPaused(request.Payload.Arguments!)); return;
        }
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
        if (request.Type == ProtocolMessageTypes.WriteRequest && request.Payload.Mode != "set_paused" && !outcome.IsReplay && outcome.Status == ProtocolStatuses.Ok && (string?)outcome.Data?["status"] is "committed" or "undone")
            Emit("write", new JObject { ["mode"] = request.Payload.Mode, ["request_id"] = request.RequestId, ["receipt"] = outcome.Data!.DeepClone() });
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
