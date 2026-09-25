using System.Collections.Generic;
using RimWorld;
using Verse;
namespace Neko.RimWorld;

// One classification for discovery, aggregates and spatial observations.
internal static class ThingCapabilities
{
    internal static bool Has(Thing thing, string capability) => capability switch
    {
        "pawn" => thing is Pawn,
        "building" => thing is Building,
        "item" => thing.def.category == ThingCategory.Item,
        "plant" => thing is Plant,
        "medicine" => thing.def.IsMedicine,
        "sleeping_place" => thing is Building_Bed,
        "openable" => thing is Building_Door,
        "powered" => thing.TryGetComp<CompPowerTrader>() != null,
        "refuelable" => thing.TryGetComp<CompRefuelable>() != null,
        "storage" => thing is ISlotGroupParent,
        "production" => thing is IBillGiver,
        "bill_giver" => thing is IBillGiver,
        "hazard" => thing is Fire || thing is Building_Trap || thing is Building_Turret || thing.TryGetComp<CompExplosive>() != null,
        _ => false,
    };

    internal static bool Matches(Thing thing, IEnumerable<string> capabilities)
    {
        foreach (var capability in capabilities) if (Has(thing, capability)) return true;
        return false;
    }

    internal static IEnumerable<string> For(Thing thing)
    {
        foreach (var capability in WorldObservationContract.Capabilities)
            if (Has(thing, capability)) yield return capability;
    }
}
