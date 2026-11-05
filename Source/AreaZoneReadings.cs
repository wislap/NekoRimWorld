using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal static class AreaZoneReadings
{
    internal static string Instance(object native) => NativeObjectIdentities.Token(native);
    internal static JArray Cells(IEnumerable<IntVec3> cells) => new(cells.OrderBy(c => c.z).ThenBy(c => c.x).Select(c => new JObject { ["x"] = c.x, ["z"] = c.z }));
    internal static JObject CellState(object native, IEnumerable<IntVec3> cells) {
        var state = new JObject { ["native_instance"] = Instance(native), ["cells"] = Cells(cells) };
        if (native is Area area && Kind(area) != "unsupported") state["manual_revision"] = NativeAreaManualChanges.Revision(area);
        return state;
    }
    internal static JObject Structure(Map map) => new()
    {
        ["map_id"] = map.GetUniqueLoadID(),
        ["zone_ids"] = new JArray(map.zoneManager.AllZones.Select(z => z.GetUniqueLoadID())),
        ["allowed_area_ids"] = new JArray(map.areaManager.AllAreas.OfType<Area_Allowed>().Select(a => a.GetUniqueLoadID()))
    };
    internal static Area ResolveArea(Map map, string? id)
    {
        if (id == "home") return map.areaManager.Home;
        if (id == "build_roof") return map.areaManager.BuildRoof;
        if (id == "no_roof") return map.areaManager.NoRoof;
        return map.areaManager.AllAreas.SingleOrDefault(a => a.GetUniqueLoadID() == id) ?? throw new ArgumentException("Area not found.");
    }
    internal static Zone ResolveZone(Map map, string? id) => map.zoneManager.AllZones.SingleOrDefault(z => z.GetUniqueLoadID() == id) ?? throw new ArgumentException("Zone not found.");
    internal static void Supported(Zone zone) { if (zone.GetType() != typeof(Zone_Stockpile) && zone.GetType() != typeof(Zone_Growing)) throw new ArgumentException("Only ordinary stockpile and growing zones are supported."); }
    internal static string Kind(Area area) => area.GetType() == typeof(Area_Allowed) ? "allowed" : area.GetType() == typeof(Area_Home) ? "home" : area.GetType() == typeof(Area_BuildRoof) ? "build_roof" : area.GetType() == typeof(Area_NoRoof) ? "no_roof" : "unsupported";
    private static readonly FieldInfo GrowingPlantField = typeof(Zone_Growing).GetField("plantDefToGrow", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new MissingFieldException(typeof(Zone_Growing).FullName, "plantDefToGrow");
    private static ThingDef GrowingPlant(Zone_Growing growing) => GrowingPlantField.GetValue(growing) as ThingDef
        ?? (PollutionUtility.SettableEntirelyPolluted(growing) ? ThingDefOf.Plant_Toxipotato : ThingDefOf.Plant_Potato);
    internal static JObject ZoneState(Zone zone)
    {
        var row = CellState(zone, zone.Cells); row["id"] = zone.GetUniqueLoadID(); row["map_id"] = zone.Map.GetUniqueLoadID(); row["kind"] = zone is Zone_Stockpile ? "stockpile" : zone is Zone_Growing ? "growing" : "unsupported";
        row["label"] = zone.RenamableLabel; row["hidden"] = zone.Hidden;
        if (zone is Zone_Stockpile stockpile) row["settings"] = new JObject { ["priority"] = stockpile.GetStoreSettings().Priority.ToString(), ["filter"] = PolicyReadings.Filter(stockpile.GetStoreSettings().filter) };
        if (zone is Zone_Growing growing) row["settings"] = new JObject { ["plant"] = GrowingPlant(growing).defName, ["allow_sow"] = growing.allowSow, ["allow_cut"] = growing.allowCut };
        if (zone is Zone_Stockpile storage) { var group = storage.GetSlotGroup().StorageGroup; row["storage_group_id"] = group == null ? JValue.CreateNull() : new JValue(group.GetUniqueLoadID()); row["storage_group_members"] = group == null ? new JArray() : JValue.CreateNull(); row["storage_group_members_complete"] = group == null; }
        row["bill_references"] = ZoneReferences(zone); row["references_complete"] = true;
        return row;
    }
    internal static JArray ZoneReferences(Zone zone)
    {
        if (zone is not Zone_Stockpile stockpile) return new JArray();
        var group = stockpile.GetSlotGroup();
        return new JArray(BillUtility.GlobalBills().OfType<Bill_Production>()
            .Where(b => ReferenceEquals(b.GetSlotGroup(), group) || ReferenceEquals(b.GetIncludeSlotGroup(), group))
            .OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal).Select(b => new JObject
            {
                ["bill_id"] = b.GetUniqueLoadID(),
                ["store"] = ReferenceEquals(b.GetSlotGroup(), group),
                ["count_scope"] = ReferenceEquals(b.GetIncludeSlotGroup(), group)
            }));
    }
    internal static JObject RemovalState(Map map, string id, bool zone)
    {
        var state = Structure(map); state["removed_id"] = id;
        if (zone) { var current = map.zoneManager.AllZones.SingleOrDefault(z => z.GetUniqueLoadID() == id); state["removed"] = current == null ? JValue.CreateNull() : ZoneState(current); }
        else { var current = map.areaManager.AllAreas.SingleOrDefault(a => a.GetUniqueLoadID() == id); state["removed"] = current == null ? JValue.CreateNull() : AreaState(current); }
        return state;
    }
    private static readonly FieldInfo AllowedAreasField = typeof(Pawn_PlayerSettings).GetField("allowedAreas", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(Pawn_PlayerSettings).FullName, "allowedAreas");
    internal static JArray AreaUsers(Area area)
    {
        var pawns = PawnsFinder.All_AliveOrDead.Concat(PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead).Concat(PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive).Distinct();
        return new JArray(pawns.Where(p => p.playerSettings != null && AllowedAreasField.GetValue(p.playerSettings) is IDictionary assignments && assignments.Values.Cast<object>().Any(a => ReferenceEquals(a, area)))
            .Select(p => p.GetUniqueLoadID()).OrderBy(id => id, StringComparer.Ordinal));
    }
    internal static JObject AreaState(Area area)
    {
        var row = CellState(area, area.ActiveCells); row["id"] = area.GetUniqueLoadID(); row["map_id"] = area.Map.GetUniqueLoadID(); row["kind"] = Kind(area); row["label"] = area.Label;
        var color = area.Color; row["color"] = new JObject { ["r"] = JsonValues.Number(color.r), ["g"] = JsonValues.Number(color.g), ["b"] = JsonValues.Number(color.b), ["a"] = JsonValues.Number(color.a) };
        row["users"] = AreaUsers(area); row["users_complete"] = true; row["manual_ids"] = NativeAreaManualChanges.Ids(area); row["manual_ids_complete"] = NativeAreaManualChanges.Complete; return row;
    }
    internal static JObject Read(JObject args)
    {
        var map = GameWorldReferences.ResolveMap((string?)args["map"]); var ids = ColonyReadContract.Texts(args, "ids"); if (args["cells"] != null) return AreaCellFlags.ReadQuery(map, ids, args["cells"]!); var rows = new List<JObject>();
        foreach (var zone in map.zoneManager.AllZones)
        {
            var id = zone.GetUniqueLoadID(); if (ids.Length != 0 && !ids.Contains(id, StringComparer.Ordinal)) continue;
            var row = ZoneState(zone); row["lookup_id"] = id; row["status"] = "observed"; row["complete"] = true; row["write_cells"] = CellState(zone, zone.Cells); row["delete_guard"] = RemovalState(map, id, true); rows.Add(row);
        }
        foreach (var area in map.areaManager.AllAreas)
        {
            var id = area.GetUniqueLoadID(); var kind = Kind(area); var aliasSelected = ids.Contains(kind, StringComparer.Ordinal) && kind != "allowed" && kind != "unsupported";
            if (ids.Length != 0 && !ids.Contains(id, StringComparer.Ordinal) && !aliasSelected) continue;
            if (kind == "unsupported") { rows.Add(new JObject { ["id"] = id, ["lookup_id"] = id, ["map_id"] = map.GetUniqueLoadID(), ["kind"] = kind, ["label"] = area.Label, ["status"] = "unsupported", ["complete"] = false, ["reason"] = "This area class has no supported manual-source/write contract." }); continue; }
            var row = AreaState(area); row["lookup_id"] = aliasSelected ? kind : id;
            row["status"] = "observed"; row["complete"] = true; row["write_cells"] = CellState(area, area.ActiveCells); if (area.GetType() == typeof(Area_Allowed)) row["delete_guard"] = RemovalState(map, id, false); rows.Add(row);
        }
        int offset = (int?)args["offset"] ?? 0, limit = (int?)args["limit"] ?? 20;
        var page = rows.Skip(offset).Take(limit).ToArray();
        return new JObject
        {
            ["status"] = "ok",
            ["map_id"] = map.GetUniqueLoadID(),
            ["structure"] = Structure(map),
            ["structure_complete"] = true,
            ["objects"] = new JArray(page),
            ["total"] = rows.Count,
            ["complete"] = offset == 0 && page.Length == rows.Count,
            ["missing_ids"] = new JArray(ids.Where(id => !rows.Any(row => (string?)row["id"] == id || (string?)row["lookup_id"] == id))),
            ["write_map_supported"] = map == Find.CurrentMap,
            ["write_note"] = "Native designator qualification uses the player's current map. Zone changes preserve Home; Home and roof areas are managed explicitly."
        };
    }
}
