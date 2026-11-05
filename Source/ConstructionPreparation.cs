using System;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal static class ConstructionPreparation
{
    internal static DesignationDef Definition(string kind) => kind switch
    {
        "deconstruct" => DesignationDefOf.Deconstruct,
        "mine" => DesignationDefOf.Mine,
        "cut_plant" => DesignationDefOf.CutPlant,
        _ => throw new ArgumentException("Unknown construction preparation designation.")
    };
    internal static Designation? Find(Thing thing, string kind)
    {
        if (thing.Destroyed || !thing.Spawned || thing.Map == null) return null;
        if (kind == "mine")
        {
            if (!thing.def.mineable || !ReferenceEquals(thing.Position.GetFirstMineable(thing.Map), thing)) return null;
            return thing.Map.designationManager.DesignationAt(thing.Position, Definition(kind));
        }
        return thing.Map.designationManager.DesignationOn(thing, Definition(kind));
    }
    internal static JObject Read(Thing thing) => new()
    {
        ["deconstruct"] = Find(thing, "deconstruct") != null,
        ["mine"] = Find(thing, "mine") != null,
        ["cut_plant"] = Find(thing, "cut_plant") != null
    };
}
