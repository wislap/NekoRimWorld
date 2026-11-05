using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Verse;

namespace Neko.RimWorld;

// User designator changes to area cells the ledger tracks: records their
// provenance and reports the current value of each recorded manual source.
internal sealed class ManualAreaObserver
{
    private readonly NekoSaveState save;
    private readonly NekoSession session;
    private bool faulted;
    internal ManualAreaObserver(NekoSaveState save, NekoSession session) { this.save = save; this.session = session; }
    internal NativeAreaManualRegistry Registry => save.NativeManualAreas;
    internal bool DataValid => !faulted && Registry.Valid;
    internal JObject[] TrackedFields(string id) => save.Ledger.AreaFields(id);
    internal bool EnsureInterest(Area area, IntVec3 cell) {
        if (!DataValid) return false;
        if (save.Ledger.Find(area.GetUniqueLoadID(), AreaCellFlags.FieldName(cell)) != null) Registry.EnsureTracked(area, cell);
        return Registry.HasArea(area);
    }
    internal void Bump(Area area) { if (DataValid && Registry.HasArea(area)) Registry.Bump(area); }
    internal void ObserveChange(Area area, IntVec3 cell, bool before, bool after, string command) {
        try {
            if (!DataValid) return;
            var field = AreaCellFlags.FieldName(cell); var prior = save.Ledger.Find(area.GetUniqueLoadID(), field);
            if (prior != null) Registry.EnsureTracked(area, cell);
            var ownership = prior == null ? null : new JObject { ["changeset_id"] = prior["changeset_id"]!.DeepClone(), ["value"] = prior["value"]!.DeepClone() };
            var source = Registry.Change(area, cell, before, after, command, ownership, Find.TickManager?.TicksGame ?? 0, session.LoadEpoch);
            if (source == null) return;
            if (prior != null) save.Ledger.RecordManualChange(area.GetUniqueLoadID(), field, source);
            session.Emit("area_manual_changed", new JObject { ["source"] = "user_designator", ["changes"] = new JArray(source) });
        } catch (Exception e) when (ReadContext.Recoverable(e)) { faulted = true; Log.WarningOnce("[N.E.K.O RimWorld] Native manual area provenance is unavailable; guarded area writing is disabled.", 0x4e4b4134); }
    }

    // Cells of an allowed area the planner created become tracked immediately.
    internal void TrackCommittedAreas(JArray readback) {
        foreach (var row in readback.OfType<JObject>().Where(row => (string?)row["field"] == "area_structure" && row["value"]?["created"] is JObject)) {
            var created = (JObject)row["value"]!["created"]!;
            var map = Find.Maps.SingleOrDefault(m => m.GetUniqueLoadID() == (string?)row["object"]);
            var area = map?.areaManager.AllAreas.SingleOrDefault(a => a.GetUniqueLoadID() == (string?)created["id"]);
            if (area == null || NativeObjectIdentities.Token(area) != (string?)created["native_instance"] || created["cells"] is not JArray cells) continue;
            foreach (var cell in cells.OfType<JObject>()) Registry.EnsureTracked(area, new IntVec3((int)cell["x"]!, 0, (int)cell["z"]!));
        }
    }

    internal JArray ObservedSources(JArray? fields) {
        var manual = Registry.Snapshot(fields);
        foreach (var source in manual.OfType<JObject>()) {
            source["current_status"] = "unresolved"; source["current_value"] = JValue.CreateNull();
            if (!DataValid || source["cell"] is not JObject cell || cell["x"]?.Type != JTokenType.Integer || cell["z"]?.Type != JTokenType.Integer) continue;
            var map = Find.Maps.SingleOrDefault(m => !m.Disposed && m.GetUniqueLoadID() == (string?)source["map_id"]);
            var area = map?.areaManager.AllAreas.SingleOrDefault(a => a.GetUniqueLoadID() == (string?)source["object"]);
            var position = new IntVec3((int)cell["x"]!, 0, (int)cell["z"]!);
            if (area == null || !position.InBounds(map!) || NativeObjectIdentities.Token(area) != (string?)source["new_value"]?["native_instance"]) continue;
            source["current_status"] = "observed";
            source["current_value"] = new JObject { ["native_instance"] = NativeObjectIdentities.Token(area), ["enabled"] = area[position], ["manual_id"] = Registry.ManualId(area, position) is string id ? new JValue(id) : JValue.CreateNull() };
        }
        return manual;
    }
}
