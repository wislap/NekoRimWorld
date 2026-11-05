using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

// The reference entries hold no ledger values. Unresolved cross-references are
// unknown and never imply destruction.
internal sealed class WriteTargetReference : IExposable
{
    internal string Id = string.Empty;
    internal ILoadReferenceable? Target;
    public WriteTargetReference() { }
    internal WriteTargetReference(string id, ILoadReferenceable target) { Id = id; Target = target; }
    public void ExposeData() {
        Scribe_Values.Look(ref Id, "id", string.Empty);
        Scribe_References.Look(ref Target, "target");
    }
}

internal sealed class WriteTargetReferences
{
    private List<WriteTargetReference>? records = new();
    internal static bool Destroyed(object target) {
        if (target is Thing thing) return thing.Destroyed;
        if (target is Bill bill) return bill.deleted || (bill.billStack?.billGiver is Thing giver && giver.Destroyed);
        if (target is Zone zone) return zone.zoneManager != null && !zone.zoneManager.AllZones.Contains(zone);
        if (target is Area area) return area.Map.Disposed || !area.Map.areaManager.AllAreas.Any(candidate => ReferenceEquals(candidate, area));
        if (target is FoodPolicy policy) return Current.Game != null && !Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Any(candidate => ReferenceEquals(candidate, policy));
        if (target is Map map) return map.Disposed;
        return false;
    }
    internal void ExposeData(WriteTargetIndex index) {
        if (Scribe.mode == LoadSaveMode.Saving)
            records = index.Entries().Where(x => x.Value is ILoadReferenceable).Select(x => new WriteTargetReference(x.Key, (ILoadReferenceable)x.Value)).ToList();
        Scribe_Collections.Look(ref records, "nekoWriteTargets", LookMode.Deep);
        // Deep entries resolve separately in the cross-reference phase; bind
        // only after that entire phase has completed.
        if (Scribe.mode == LoadSaveMode.PostLoadInit && records != null)
            index.BindMany(records.Where(record => record != null && !string.IsNullOrEmpty(record.Id) && record.Target != null && (record.Target is FoodPolicy policy ? PolicyReadings.Id(policy) == record.Id : record.Target.GetUniqueLoadID() == record.Id))
                .Select(record => new KeyValuePair<string, object?>(record.Id, record.Target)));
    }
}
