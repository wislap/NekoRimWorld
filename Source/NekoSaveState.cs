using System;
using Verse;

namespace Neko.RimWorld;

// Everything the bridge persists inside a save: colony identity, the write
// ledger and its target references, construction intents, native identity
// tokens and manual area provenance.
internal sealed class NekoSaveState
{
    private string colonyId = string.Empty;
    internal string ColonyId => colonyId;
    internal WriteLedgerSaveState LedgerSave { get; } = new();
    internal WriteLedger Ledger { get; } = new();
    internal WriteTargetIndex Targets { get; } = new(WriteTargetReferences.Destroyed);
    private readonly WriteTargetReferences targetReferences = new();
    internal ConstructionIntents ConstructionIntents { get; } = new();
    internal NativeObjectIdentityRegistry NativeIdentities { get; } = new();
    internal NativeAreaManualRegistry NativeManualAreas { get; } = new();

    internal void ExposeData()
    {
        Scribe_Values.Look(ref colonyId, "nekoColonyId", string.Empty);
        if (Scribe.mode == LoadSaveMode.Saving) {
            if (LedgerSave.Valid) Targets.Prune(Ledger);
            LedgerSave.PrepareSave(Ledger);
        }
        Scribe_Values.Look(ref LedgerSave.Data, "nekoWriteLedger", string.Empty);
        if (Scribe.mode == LoadSaveMode.LoadingVars) {
            LedgerSave.Loaded(Ledger);
            if (!LedgerSave.Valid) {
                Log.Warning("[N.E.K.O RimWorld] Invalid write ledger; writing is disabled for this save.");
            }
        }
        ConstructionIntents.ExposeData();
        NativeIdentities.ExposeData();
        // Policy business IDs use restored saved tokens. Native Scribe refs
        // still resolve the policy's current label-bearing load ID first.
        targetReferences.ExposeData(Targets);
        NativeManualAreas.ExposeData();
    }

    internal void NormalizeOrCreateColonyId()
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
}
