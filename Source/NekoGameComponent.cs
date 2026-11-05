using System.Diagnostics;
using Verse;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>
/// Save-scoped, game-thread boundary for the bridge. Network code hands this
/// component protocol DTOs; this component is the only layer that resolves them
/// against live Verse state.
/// </summary>
public sealed class NekoGameComponent : GameComponent
{
    internal NekoSaveState Save { get; } = new();
    private readonly NekoSession session;
    internal PauseObserver Pause { get; }
    internal ManualAreaObserver ManualAreas { get; }
    private readonly LedgerSnapshotService ledgerSnapshots;
    private readonly RequestDispatcher requests;
    private readonly ColonyObservations colonyObservations = new(ColonyObservationReadings.Sample, ColonyObservationReadings.Qualify, beginRead: spec => (string?)spec["kind"] == "resources" ? new ResourceObservation(spec) : null);
    private readonly AttentionSignals attentionSignals = new();
    internal JObject AttentionSamplingMetrics() => attentionSignals.Metrics();
    private bool attentionSubscribed;
    private long lastEventHeartbeat;

    // RimWorld discovers GameComponents with this exact constructor signature.
    // Do not retain the Game reference: the process-scoped runtime must never
    // keep an old game graph alive after returning to the main menu or loading.
    public NekoGameComponent(Game game)
    {
        session = new NekoSession(Save);
        Pause = new PauseObserver(session);
        ManualAreas = new ManualAreaObserver(Save, session);
        NativeWriteFields WriteFields() => new(new VerseGameData());
        ledgerSnapshots = new LedgerSnapshotService(Save, ManualAreas, WriteFields);
        requests = new RequestDispatcher(Save, session, Pause, ManualAreas, ledgerSnapshots, colonyObservations, WriteFields,
            () => { attentionSignals.Reset(); attentionSubscribed = true; });
    }

    // Read by the isolated study controller through reflection.
    internal JObject CaptureWriteLedgerSnapshot() => ledgerSnapshots.Capture(null);
    internal JObject CapturePauseState() => Pause.CaptureState();

    public override void ExposeData() => Save.ExposeData();

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
        if (string.IsNullOrEmpty(session.LoadEpoch))
        {
            return;
        }

        // One fresh capability inspection for this update; action guards still
        // perform their own inspections at the point of use.
        bool hooksReady = NekoRimWorldMod.RefreshNativeCapabilities();
        if (attentionSubscribed && AttentionFacts.CapabilityAvailable(hooksReady, SituationAlertObservations.HookMatched)) attentionSignals.Sample(data => NekoRimWorldMod.Runtime.EmitEvent(session.Current(), data));
        VerseKnowledge.Update();
        Pause.RefreshPolicy();
        Pause.ObservePause();
        colonyObservations.Advance(NekoRimWorldMod.Runtime.ReadyConnectionEpoch, Find.TickManager?.TicksGame ?? 0, data => NekoRimWorldMod.Runtime.EmitEvent(session.Current(), data), Find.TickManager?.Paused ?? false);
        if (Stopwatch.GetTimestamp() - lastEventHeartbeat >= Stopwatch.Frequency * 2) {
            lastEventHeartbeat = Stopwatch.GetTimestamp();
            var tm = Find.TickManager;
            session.Emit("heartbeat", new JObject { ["pause_state"] = tm == null ? new JObject { ["status"] = "unavailable" } : Pause.EvidenceWithoutObservation(tm) });
        }

        var runtime = NekoRimWorldMod.Runtime;
        runtime.TouchSession(
            session.LoadEpoch,
            Find.TickManager?.TicksGame ?? 0,
            session.SnapshotSequence);
        requests.Update(runtime);
    }

    private void StartSession(string source)
    {
        colonyObservations.Clear();
        requests.Reset();
        WorldObservation.Clear();
        SituationAlertObservations.Clear();
        attentionSignals.ClearMetrics();
        attentionSignals.Reset(); attentionSubscribed = false;
        VerseKnowledge.StartOnce();
        GameImplementation.PrepareBehaviorReads();
        Save.NormalizeOrCreateColonyId();

        session.Start(source, Find.TickManager?.TicksGame ?? 0);
        Pause.Start();
        lastEventHeartbeat = 0;
        NekoRimWorldMod.Runtime.AttachSession(session.Current(), writerReady: Save.LedgerSave.Valid);
        Log.Message($"[N.E.K.O RimWorld] Game session attached ({source}).");
    }

    internal SessionDescriptor ObservationSession() => session.Current();
}
