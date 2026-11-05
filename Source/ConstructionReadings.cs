using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal static class ConstructionReadings
{
    private static bool ResearchFinished(BuildableDef def) => def.researchPrerequisites == null || def.researchPrerequisites.All(ResearchReadings.FinishedReadOnly);
    internal static IEnumerable<BuildableDef> Definitions() => DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.category == ThingCategory.Building && d.blueprintDef != null && d.BuildableByPlayer).Cast<BuildableDef>().Concat(DefDatabase<TerrainDef>.AllDefsListForReading.Where(d => d.blueprintDef != null && d.BuildableByPlayer));
    internal static BuildableDef Definition(JObject e) {
        var def = Definitions().SingleOrDefault(d => d.defName == (string?)e["definition"] && (d is TerrainDef ? "floor" : "building") == (string?)e["kind"]);
        return def ?? throw new ArgumentException("Unsupported construction definition.");
    }
    internal static ThingDef? Stuff(BuildableDef d, JObject e) {
        var name = (string?)e["stuff"]; var stuff = name == null ? null : DefDatabase<ThingDef>.GetNamedSilentFail(name);
        if (d.MadeFromStuff ? stuff == null || !stuff.IsStuff || !stuff.stuffProps.CanMake(d) : stuff != null) throw new ArgumentException("Select a valid native construction material.");
        return stuff;
    }
    internal static IntVec3 Position(JObject e) => new((int)e["x"]!, 0, (int)e["z"]!);
    internal static CellRect Footprint(BuildableDef d, JObject e) => GenAdj.OccupiedRect(Position(e), new Rot4((int)e["rotation"]!), d.Size);
    internal static JObject Rect(CellRect r) => new() { ["min_x"] = r.minX, ["max_x"] = r.maxX, ["min_z"] = r.minZ, ["max_z"] = r.maxZ };
    internal static JObject Catalog(JObject args, SessionDescriptor session) {
        var names = ColonyReadContract.Texts(args, "names");
        var defs = Definitions().OrderBy(d => d.defName, StringComparer.Ordinal).Where(d => names.Length == 0 || names.Contains(d.defName, StringComparer.OrdinalIgnoreCase) || names.Contains(d.LabelCap.ToString(), StringComparer.OrdinalIgnoreCase)).ToArray();
        var rows = defs.Select(d => { var td = d as ThingDef; return new JObject {
            ["definition"] = d.defName, ["label"] = d.LabelCap.ToString(), ["size"] = new JObject { ["x"] = d.Size.x, ["z"] = d.Size.z },
            ["has_interaction_cell"] = td?.hasInteractionCell ?? false, ["interaction_offset"] = td == null || !td.hasInteractionCell ? null : new JObject { ["x"] = td.interactionCellOffset.x, ["z"] = td.interactionCellOffset.z },
            ["interaction_offsets"] = new JArray((td?.multipleInteractionCellOffsets?.Count > 0 ? td.multipleInteractionCellOffsets : td?.hasInteractionCell == true ? new List<IntVec3> { td.interactionCellOffset } : new List<IntVec3>()).Select(c => new JObject { ["x"] = c.x, ["z"] = c.z })),
            ["is_room_boundary"] = td != null && (td.IsDoor || td.passability == Traversability.Impassable && td.Fillage == FillCategory.Full && !td.IsFence),
            ["passability"] = d.passability.ToString(), ["is_door"] = td?.IsDoor ?? false, ["layer"] = d is TerrainDef ? "floor" : td?.building?.isPowerConduit == true ? "conduit" : "building", ["holds_roof"] = td?.holdsRoof ?? false,
            ["terrain_layer"] = d is TerrainDef terrain ? new JValue(ConstructionTerrainState.Layer(terrain)) : JValue.CreateNull(),
            ["terrain_affordance"] = JsonValues.Text(d.terrainAffordanceNeeded?.defName), ["materials"] = new JArray(d.MadeFromStuff ? DefDatabase<ThingDef>.AllDefsListForReading.Where(s => s.IsStuff && s.stuffProps.CanMake(d)).Select(s => s.defName) : Array.Empty<string>()),
            ["cost_stuff_count"] = d.CostStuffCount, ["costs"] = new JArray((d.CostList ?? new List<ThingDefCountClass>()).Select(c => new JObject { ["definition"] = c.thingDef.defName, ["count"] = c.count })),
            ["research_prerequisites"] = new JArray((d.researchPrerequisites ?? new List<ResearchProjectDef>()).Select(r => r.defName)), ["available_now"] = ResearchFinished(d),
            ["native_placement_required"] = true
        }; });
        var result = ColonyReadContract.Page(args, rows, "Loaded player-buildable buildings and terrain; placement remains site-specific.", session); result["format"] = "construction_catalog.v1"; result["definitions"] = result["rows"]!.DeepClone(); result.Remove("rows"); return result;
    }
    internal static JObject Object(Thing t) {
        var def = t.def.entityDefToBuild ?? t.def; var r = t.OccupiedRect();
        return new JObject { ["id"] = t.GetUniqueLoadID(), ["map_id"] = JsonValues.Text(t.MapHeld?.GetUniqueLoadID()), ["definition"] = def.defName, ["x"] = t.Position.x, ["z"] = t.Position.z, ["rotation"] = t.Rotation.AsInt, ["stuff"] = JsonValues.Text((t is Blueprint blueprint ? blueprint.EntityToBuildStuff() : t.Stuff)?.defName),
            ["stage"] = t is Frame ? "frame" : t is Blueprint ? "blueprint" : "built", ["occupied_rect"] = Rect(r),
            ["configuration"] = Configuration(t),
            ["interaction_cell"] = t.def.hasInteractionCell ? new JObject { ["x"] = t.InteractionCell.x, ["z"] = t.InteractionCell.z } : null,
            ["work_left"] = t is Frame f ? (JToken)f.WorkLeft : JValue.CreateNull() };
    }
    private static JObject Configuration(Thing t) {
        var result = new JObject { ["faction"] = JsonValues.Text(t.Faction?.GetUniqueLoadID()), ["hit_points"] = t.HitPoints, ["forbidden"] = t.TryGetComp<CompForbiddable>()?.Forbidden };
        if (t is Blueprint_Build blueprint) {
            foreach (var name in new[] { "selectedStyleDef", "styleOverridden", "glowerColorOverride" }) { var field = typeof(Blueprint_Build).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!; var value = field.GetValue(blueprint); result[name] = value is Def def ? new JValue(def.defName) : value == null ? JValue.CreateNull() : JToken.FromObject(value); }
            if (blueprint is Blueprint_Storage) { var field = typeof(Blueprint_Storage).GetField("settings", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!; var settings = field.GetValue(blueprint) as StorageSettings; result["storage"] = settings == null ? JValue.CreateNull() : new JObject { ["priority"] = settings.Priority.ToString(), ["filter"] = PolicyReadings.Filter(settings.filter) }; }
        }
        return result;
    }
    internal static IEnumerable<int> Spatial(JObject args, Action<JObject> publish) {
        var map = GameWorldReferences.ResolveMap((string?)args["map"]); var b = (JObject)args["bounds"]!; int x0 = (int)b["min_x"]!, z0 = (int)b["min_z"]!, w = (int)b["width"]!, h = (int)b["height"]!;
        var cells = new SpatialCellGroups(); var objects = new Dictionary<string, JObject>(); int unknown = 0;
        for (int z = z0; z < z0 + h; z++) { for (int x = x0; x < x0 + w; x++) {
            var c = new IntVec3(x, 0, z); bool known = c.InBounds(map) && !c.Fogged(map);
            var row = new JObject { ["x"] = x, ["z"] = z, ["known"] = known, ["status"] = known ? "observed" : c.InBounds(map) ? "fogged" : "outside_map" };
            if (!known) unknown++;
            else { row["terrain"] = c.GetTerrain(map).defName; row["terrain_layers"] = ConstructionTerrainState.Layers(map, c); row["roof"] = JsonValues.Text(c.GetRoof(map)?.defName); row["walkable"] = c.Walkable(map); row["roof_within_support_range"] = RoofCollapseUtility.WithinRangeOfRoofHolder(c, map);
                row["zone_id"] = JsonValues.Text(map.zoneManager.ZoneAt(c)?.GetUniqueLoadID()); row["area_ids"] = new JArray(map.areaManager.AllAreas.Where(a => a[c]).Select(a => a.GetUniqueLoadID()));
                foreach (var t in map.thingGrid.ThingsListAtFast(c)) if (!t.Destroyed && (t is Building || t is Blueprint || t is Frame)) objects[t.GetUniqueLoadID()] = Object(t);
            } cells.Add(row);
        } yield return 0; }
        publish(new JObject { ["format"] = "construction_observation.v1", ["status"] = unknown == 0 ? "ok" : "partial", ["map_id"] = map.GetUniqueLoadID(), ["bounds"] = b.DeepClone(), ["cell_groups"] = cells.Groups,
            ["cell_groups_description"] = "Every property except _xz applies unchanged to every absolute world coordinate [x,z] in that group. Groups preserve all cell properties; no flat cells array is also emitted.",
            ["objects"] = new JArray(objects.Values), ["coverage"] = new JObject { ["cells_complete"] = true, ["objects_complete"] = unknown == 0, ["visibility_complete"] = unknown == 0, ["unknown_cells"] = unknown, ["atomic"] = false } });
    }
    internal static JObject SiteState(Map map, JObject e) {
        var d = Definition(e); var cells = new JArray();
        foreach (var c in Footprint(d, e)) { if (!c.InBounds(map) || c.Fogged(map)) throw new ArgumentException("Construction footprint must be visible and in bounds."); cells.Add(new JObject { ["x"] = c.x, ["z"] = c.z, ["terrain"] = c.GetTerrain(map).defName, ["terrain_layers"] = ConstructionTerrainState.Layers(map, c), ["roof"] = JsonValues.Text(c.GetRoof(map)?.defName),
            ["objects"] = new JArray(map.thingGrid.ThingsListAtFast(c).Where(t => !t.Destroyed && (t is Building || t is Blueprint || t is Frame || t is Plant)).Select(t => t.GetUniqueLoadID()).OrderBy(id => id, StringComparer.Ordinal)) }); }
        return new JObject { ["element_id"] = e["element_id"]!.DeepClone(), ["map_id"] = map.GetUniqueLoadID(), ["element"] = e.DeepClone(), ["stage"] = "absent", ["site"] = cells };
    }
    internal static void Qualify(Map map, JObject e) {
        ColonyManagementContract.ValidateElement(e); var d = Definition(e); var stuff = Stuff(d, e); if (!ResearchFinished(d)) throw new ArgumentException("Construction research is not complete.");
        if (d.blueprintDef.thingClass.Assembly != typeof(Thing).Assembly || d is ThingDef td && td.thingClass.Assembly != typeof(Thing).Assembly) throw new ArgumentException("Custom construction runtime types require explicit native support.");
        if (d.placeWorkers?.Any(type => type.Assembly != typeof(Thing).Assembly) == true) throw new ArgumentException("Custom placement workers require explicit native support.");
        foreach (var c in Footprint(d, e)) { if (!c.InBounds(map) || c.Fogged(map)) throw new ArgumentException("Construction footprint must be visible and in bounds.");
            if (map.thingGrid.ThingsListAtFast(c).Any(t => (t is Blueprint || t is Frame || t is Building) && (GenSpawn.SpawningWipes(d.blueprintDef, t.def, false) || d is ThingDef future && GenSpawn.SpawningWipes(future, t.def, false)) || t is Plant p && p.def.plant.IsTree)) throw new ArgumentException("Construction would replace an existing object or require preparation."); }
        var report = GenConstruct.CanPlaceBlueprintAt(d, Position(e), new Rot4((int)e["rotation"]!), map, false, null, null, stuff);
        if (!report.Accepted) throw new ArgumentException("Native placement refused: " + report.Reason);
    }
    internal static JObject Preview(JObject args) {
        var map = GameWorldReferences.ResolveMap((string?)args["map"]); var rows = new JArray();
        foreach (JObject e in (JArray)args["elements"]!) { var row = new JObject { ["element_id"] = e["element_id"]!.DeepClone(), ["map_id"] = map.GetUniqueLoadID(), ["element"] = e.DeepClone() }; try { Qualify(map, e); row["state"] = SiteState(map, e); row["status"] = "eligible"; } catch (Exception ex) when (ReadContext.Recoverable(ex)) { row["status"] = "blocked"; row["reason"] = ex.Message; } rows.Add(row); }
        return new JObject { ["format"] = "construction_preview.v1", ["map_id"] = map.GetUniqueLoadID(), ["elements"] = rows, ["coverage"] = new JObject { ["complete"] = true, ["simultaneous_plan_qualification"] = false } };
    }
}
