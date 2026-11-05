using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal static class AreaCellFlags
{
    internal static string FieldName(IntVec3 cell) => "area_flag:" + cell.x.ToString(CultureInfo.InvariantCulture) + ":" + cell.z.ToString(CultureInfo.InvariantCulture);
    internal static IntVec3[] Coordinates(Map map, JToken selector, bool allowManualId = false)
    {
        if (selector is not JArray cells || cells.Count < 1 || cells.Count > 64) throw new ArgumentException("Select between 1 and 64 area cells per transaction.");
        var result = new List<IntVec3>();
        foreach (var token in cells) {
            if (token is not JObject row || row.Properties().Any(p => p.Name != "x" && p.Name != "z" && p.Name != "enabled" && (!allowManualId || p.Name != "manual_id")) || row["x"]?.Type != JTokenType.Integer || row["z"]?.Type != JTokenType.Integer || row["enabled"] != null && row["enabled"]!.Type != JTokenType.Boolean) throw new ArgumentException("Each selected area cell needs integer x/z and an optional boolean enabled flag.");
            var cell = new IntVec3((int)row["x"]!, 0, (int)row["z"]!);
            if (!cell.InBounds(map) || cell.Fogged(map)) throw new ArgumentException("Selected area cells must be visible and in bounds.");
            result.Add(cell);
        }
        if (result.Distinct().Count() != result.Count) throw new ArgumentException("Selected area coordinates must be unique.");
        return result.OrderBy(c => c.z).ThenBy(c => c.x).ToArray();
    }
    internal static JObject Read(Area area, IEnumerable<IntVec3> cells) => new() {
        ["native_instance"] = AreaZoneReadings.Instance(area),
        ["cells"] = new JArray(cells.OrderBy(c => c.z).ThenBy(c => c.x).Select(c => new JObject { ["x"] = c.x, ["z"] = c.z, ["enabled"] = area[c], ["manual_id"] = NativeAreaManualChanges.ManualId(area, c) is string id ? new JValue(id) : JValue.CreateNull() }))
    };
    internal static JObject ReadQuery(Map map, string[] ids, JToken selector)
    {
        if (ids.Length != 1) throw new ArgumentException("A local area-cell read requires exactly one area ID.");
        var area = AreaZoneReadings.ResolveArea(map, ids[0]); if (AreaZoneReadings.Kind(area) == "unsupported") throw new ArgumentException("Unsupported native area type.");
        var coordinates = Coordinates(map, selector);
        return new JObject { ["status"] = "ok", ["map_id"] = map.GetUniqueLoadID(), ["structure"] = AreaZoneReadings.Structure(map), ["structure_complete"] = true,
            ["objects"] = new JArray(new JObject { ["id"] = area.GetUniqueLoadID(), ["lookup_id"] = ids[0], ["map_id"] = map.GetUniqueLoadID(), ["kind"] = AreaZoneReadings.Kind(area),
                ["label"] = area.Label, ["status"] = "observed", ["complete"] = true, ["manual_ids_complete"] = NativeAreaManualChanges.Complete, ["write_flags"] = Read(area, coordinates),
                ["cell_fields"] = new JArray(coordinates.Select(cell => new JObject { ["x"] = cell.x, ["z"] = cell.z, ["object"] = area.GetUniqueLoadID(), ["field"] = FieldName(cell) })) }), ["total"] = 1, ["complete"] = true,
            ["missing_ids"] = new JArray(), ["write_map_supported"] = map == Find.CurrentMap, ["coverage"] = "Only explicitly selected area cells; all other cells are unchanged and outside this observation." };
    }
}
