using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Verse;

namespace Neko.RimWorld;

// Reads the current native value of each ledger entry through the same field
// implementations writes use, plus the manual provenance of tracked area cells.
internal sealed class LedgerSnapshotService
{
    private readonly NekoSaveState save;
    private readonly ManualAreaObserver manualAreas;
    private readonly Func<NativeWriteFields> writeFields;
    internal LedgerSnapshotService(NekoSaveState save, ManualAreaObserver manualAreas, Func<NativeWriteFields> writeFields) { this.save = save; this.manualAreas = manualAreas; this.writeFields = writeFields; }

    internal JObject Capture(JArray? fields) {
        if (!UnityData.IsInMainThread || Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting)
            throw new InvalidOperationException("Ledger snapshot is unavailable.");
        var ledger = writeFields().Ledger;
        var snapshot = save.Ledger.ReadSnapshot(save.LedgerSave.Valid, save.Targets, WriteTargetReferences.Destroyed, ledger.Resolve, fields);
        bool relevant = fields == null || fields.Any(field => ((string?)field["field"])?.StartsWith("area_flag:", StringComparison.Ordinal) == true);
        snapshot["manual_sources"] = manualAreas.ObservedSources(fields); snapshot["manual_sources_complete"] = !relevant || manualAreas.DataValid;
        snapshot["manual_tracking_scope"] = "explicitly_observed_or_committed_area_cells";
        return snapshot;
    }
}
