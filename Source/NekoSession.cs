using Newtonsoft.Json.Linq;
using Verse;

namespace Neko.RimWorld;

// The attached game session: load epoch, published snapshot sequence and the
// origin of this session, plus event emission stamped with that identity.
internal sealed class NekoSession
{
    private readonly NekoSaveState save;
    internal NekoSession(NekoSaveState save) { this.save = save; }
    internal string ColonyId => save.ColonyId;
    internal string LoadEpoch { get; private set; } = string.Empty;
    internal long SnapshotSequence { get; set; }
    internal string Source { get; private set; } = string.Empty;
    internal long StartTick { get; private set; }
    internal WriteCoordinator? Writes { get; private set; }

    internal void Start(string source, long startTick)
    {
        Source = source;
        StartTick = startTick;
        LoadEpoch = System.Guid.NewGuid().ToString("N");
        Writes = new WriteCoordinator(save.Ledger, save.Targets);
        Writes.StartEpoch(LoadEpoch);
        SnapshotSequence = 0;
    }

    internal SessionDescriptor Current() => new(save.ColonyId, LoadEpoch, Find.TickManager?.TicksGame ?? 0, SnapshotSequence);

    internal void Emit(string kind, JObject data) { data["kind"] = kind; NekoRimWorldMod.Runtime.EmitEvent(Current(), data); }
}
