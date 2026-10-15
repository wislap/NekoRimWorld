using System.Linq;
using Newtonsoft.Json.Linq;
using Verse;

namespace Neko.RimWorld;

internal static class GameWorldReferences
{
    internal static Map ResolveMap(string? requested)
    {
        if (requested == null || requested == "current")
            return Find.CurrentMap ?? throw ReadContext.Error("object_unavailable", "There is no current loaded map.");
        var map = Find.Maps.FirstOrDefault(item => item != null && !item.Disposed && item.GetUniqueLoadID() == requested);
        if (map == null) throw ReadContext.Error("object_unavailable", "Selected map is not loaded: " + requested);
        return map;
    }

    internal static JObject DescribeCell(Map map, IntVec3 cell) => new()
    {
        ["kind"] = "cell",
        ["map"] = map.GetUniqueLoadID(),
        ["x"] = cell.x,
        ["z"] = cell.z,
    };
}
