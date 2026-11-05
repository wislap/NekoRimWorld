using System;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

// Native pause ownership: observes user, letter and window pauses, applies
// agent pause requests and reports pause evidence for the attached session.
internal sealed class PauseObserver
{
    private readonly NekoSession session;
    private readonly PausePolicy pausePolicy = new();
    private string policyConnection = "";
    private bool lastSpeedPaused;
    private bool lastForcePaused;
    private bool lastWindowPaused;
    private bool loadPausePristine;
    private long sessionInitialPauseTransitionRevision;

    internal PauseObserver(NekoSession session) { this.session = session; }
    internal string Connection => policyConnection;

    internal void Start()
    {
        policyConnection = ""; pausePolicy.Bind("", session.ColonyId, session.LoadEpoch); pausePolicy.Observe("unknown");
        sessionInitialPauseTransitionRevision = pausePolicy.TransitionRevision;
        lastSpeedPaused = Find.TickManager?.CurTimeSpeed == TimeSpeed.Paused;
        lastForcePaused = Find.TickManager?.ForcePaused ?? false;
        lastWindowPaused = Find.WindowStack?.WindowsForcePause ?? false;
        loadPausePristine = true;
    }

    internal void SetLetterDefs(System.Collections.Generic.IEnumerable<string> defs) => pausePolicy.Set(defs);

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
        session.Emit("letter", new JObject { ["letter_id"] = letter.GetUniqueLoadID(), ["def"] = letter.def.defName, ["auto_pause_mode"] = level, ["is_choice_letter"] = letter is ChoiceLetter, ["reflected"] = reflected });
    }
    internal void ObserveLetterPause(bool before) {
        var tm = Find.TickManager; if (tm == null) return;
        bool after = tm.CurTimeSpeed == TimeSpeed.Paused;
        if (!before && after && !lastSpeedPaused) { pausePolicy.Observe("automatic_letter"); lastSpeedPaused = true; EmitPause("automatic_letter", tm); }
    }
    internal void RefreshPolicy() { var connection = NekoRimWorldMod.Runtime.ReadyConnectionEpoch; if (policyConnection != connection) policyConnection = connection; pausePolicy.Bind(connection, session.ColonyId, session.LoadEpoch); }
    private void EmitPause(string source, TickManager tm) {
        loadPausePristine = false;
        session.Emit("pause_changed", new JObject {
        ["paused"] = tm.Paused, ["speed_paused"] = tm.CurTimeSpeed == TimeSpeed.Paused,
        ["force_paused"] = tm.ForcePaused, ["windows_force_paused"] = Find.WindowStack?.WindowsForcePause ?? false, ["source"] = source,
        ["pause_windows"] = NativePauseWindows.Capture(),
        ["pause_state"] = EvidenceWithoutObservation(tm),
        ["pause_revision"] = pausePolicy.Revision
        });
    }
    // Native ownership metadata, also inspected by the isolated study controller.
    // This observes state only; it is not a model recovery capability.
    internal JObject EvidenceWithoutObservation(TickManager tm) {
        if (!NativeEventHookSupport.Ready() || Find.WindowStack == null || Current.ProgramState != ProgramState.Playing ||
            NekoRimWorldMod.Runtime.ReadyConnectionEpoch != policyConnection)
            return new JObject { ["status"] = "unavailable" };
        var state = pausePolicy.Evidence(policyConnection, session.ColonyId, session.LoadEpoch, tm.TicksGame,
            tm.Paused, tm.CurTimeSpeed == TimeSpeed.Paused, tm.ForcePaused, Find.WindowStack.WindowsForcePause);
        state["monotonic"] = MonotonicEvidence.Capture();
        return state;
    }
    internal JObject Evidence() {
        try {
            CaptureState();
            return EvidenceWithoutObservation(Find.TickManager);
        } catch (Exception) { return new JObject { ["status"] = "unavailable" }; }
    }
    internal JObject CaptureState() {
        if (!UnityData.IsInMainThread || Current.ProgramState != ProgramState.Playing ||
            !NativeEventHookSupport.Ready() || Find.TickManager == null || Find.WindowStack == null)
            throw new InvalidOperationException("Native pause ownership metadata is unavailable.");
        RefreshPolicy(); ObservePause();
        var tm = Find.TickManager;
        var state = pausePolicy.State(policyConnection, session.ColonyId, session.LoadEpoch, tm.TicksGame,
            tm.Paused, tm.CurTimeSpeed == TimeSpeed.Paused, tm.ForcePaused, Find.WindowStack.WindowsForcePause);
        state["monotonic"] = MonotonicEvidence.Capture();
        state["load_pause_pristine"] = loadPausePristine;
        state["session_initial_pause_transition_revision"] = sessionInitialPauseTransitionRevision;
        state["session_source"] = session.Source;
        state["session_start_tick"] = session.StartTick;
        state["pause_windows"] = NativePauseWindows.Capture();
        return state;
    }
    internal void ObservePause() {
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
    internal QueryOutcome SetPaused(JObject args) {
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
}
