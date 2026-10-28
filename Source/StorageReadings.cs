using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal static class StorageReadings
{
    internal static (ISlotGroupParent storage, Thing? thing, Zone_Stockpile? zone) Resolve(string id, string? mapId)
    {
        var maps = Find.Maps.Where(m => mapId == null || m.GetUniqueLoadID() == mapId).ToArray();
        var things = maps.SelectMany(m => m.listerThings.AllThings).Where(t => t.GetUniqueLoadID() == id && t.Faction == Faction.OfPlayer && t is ISlotGroupParent).ToArray();
        var zones = maps.SelectMany(m => m.zoneManager.AllZones).OfType<Zone_Stockpile>().Where(z => z.GetUniqueLoadID() == id).ToArray();
        if (things.Length + zones.Length != 1) throw new ArgumentException("Player storage not found or ambiguous.");
        return things.Length == 1 ? ((ISlotGroupParent)things[0], things[0], null) : (zones[0], null, zones[0]);
    }

    internal static JObject Read(JObject args)
    {
        var ids = ColonyReadContract.Texts(args, "ids"); var items = ColonyReadContract.Texts(args, "items");
        if (ids.Length != 1 || items.Length > 32) throw ReadContext.Error("invalid_arguments", "Select one storage and at most 32 definitions.");
        var mapId = (string?)args["map"];
        var target = Resolve(ids[0], mapId);
        var settings = target.storage.GetStoreSettings() ?? throw new ArgumentException("Storage settings unavailable.");
        var allows = new JArray();
        foreach (var name in items) {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
            if (def == null) throw ReadContext.Error("object_unavailable", "Item definition not found: " + name);
            allows.Add(new JObject { ["definition"] = def.defName, ["allowed"] = settings.filter.Allows(def) });
        }
        return new JObject { ["status"] = "ok", ["objects"] = new JArray(new JObject { ["id"] = ids[0], ["map_id"] = mapId, ["status"] = "observed", ["priority"] = settings.Priority.ToString(), ["allows"] = allows }) };
    }
}
