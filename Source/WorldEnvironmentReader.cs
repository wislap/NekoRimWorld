using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
using Verse.AI;
namespace Neko.RimWorld;

// Same cell/environment evidence for regions and path corridors. No hazard score.
internal sealed class WorldEnvironmentReader
{
    private readonly Map map;
    private readonly Pawn? actor;
    private readonly Dictionary<string, int> terrain = new(), roof = new();
    private readonly Dictionary<int, JObject> rooms = new();
    private readonly Dictionary<IntVec3, JObject> pathSamples = new();
    private readonly List<JObject> roofSupports = new();
    private readonly HashSet<Thing> things = new();
    private readonly Dictionary<string, int> gasCells = new();
    private readonly Dictionary<string, byte> gasMax = new();
    private readonly List<IntVec3> visibleCells = new();
    private readonly HashSet<IntVec3> visited = new();
    private readonly Dictionary<IntVec3, List<int>> pathIndices = new();
    private readonly Dictionary<Room, int> roomVersions = new();
    private readonly Dictionary<Thing, (IntVec3 Position, Map? Map, bool Spawned, int Hp, bool? DoorOpen)> objectVersions = new();
    internal bool CellsComplete => !errors;
    internal bool Changed => roomVersions.Any(r => r.Key.Dereferenced || r.Key.CellCount != r.Value) || objectVersions.Any(r => r.Key.Destroyed || r.Key.Map != r.Value.Map || r.Key.Spawned != r.Value.Spawned || r.Key.Position != r.Value.Position || r.Key.HitPoints != r.Value.Hp || (r.Key is Building_Door d && d.Open != r.Value.DoorOpen));
    internal void SetPath(IReadOnlyList<IntVec3> cells) { for (int i = 0; i < cells.Count; i++) { if (!pathIndices.TryGetValue(cells[i], out var values)) pathIndices[cells[i]] = values = new List<int>(); values.Add(i); } }

    private readonly List<JObject> hazards = new();
    private int total, hidden, polluted, snow, wet; private float minTemp = float.MaxValue, maxTemp = float.MinValue, minGlow = 1, maxGlow = 0;
    private int tempCount; private bool errors; private readonly List<string> failures = new();
    internal WorldEnvironmentReader(Map map, Pawn? actor) { this.map = map; this.actor = actor; }
    internal static JObject CellState(Map map, IntVec3 c, Pawn? actor)
    {
        var room = c.GetRoom(map); var gases = new JObject(); foreach (GasType g in Enum.GetValues(typeof(GasType))) gases[g.ToString()] = map.gasGrid.DensityAt(c, g);
        return new JObject { ["cell"] = WorldStateReader.Cell(map, c), ["terrain"] = c.GetTerrain(map).defName, ["walkable"] = c.Walkable(map), ["roof"] = c.GetRoof(map)?.defName, ["roof_within_support_range"] = c.Roofed(map) ? (JToken)RoofCollapseUtility.WithinRangeOfRoofHolder(c, map) : JValue.CreateNull(), ["temperature_c"] = GenTemperature.GetTemperatureForCell(c, map), ["glow_fraction"] = map.glowGrid.GroundGlowAt(c), ["snow_depth"] = map.snowGrid.GetDepth(c), ["polluted"] = c.IsPolluted(map), ["gases_0_255"] = gases, ["room"] = room == null ? null : RoomState(room), ["native_danger_for_actor"] = actor == null ? null : c.GetDangerFor(actor, map).ToString(), ["native_danger_meaning"] = "Native region temperature/vacuum classification, not fire, enemy or overall safety." };
    }
    internal static JObject RoomState(Room room)
    {
        return new JObject { ["id"] = room.ID, ["role"] = room.Role?.defName, ["cell_count"] = room.CellCount, ["outdoors"] = room.PsychologicallyOutdoors, ["temperature_c"] = room.Temperature, ["vacuum"] = room.Vacuum, ["cleanliness"] = room.GetStat(RoomStatDefOf.Cleanliness), ["meaning"] = "Room membership is sampled now; room IDs may change after topology changes." };
    }
    internal void Observe(IntVec3 c)
    {
        if (!c.InBounds(map) || !visited.Add(c)) return; total++;
        if (c.Fogged(map)) { hidden++; return; }
        visibleCells.Add(c);
        try
        {
            var td = c.GetTerrain(map); Add(terrain, td.defName); if (td.IsWater) wet++;
            var rd = c.GetRoof(map); Add(roof, rd?.defName ?? "none");
            float temp = GenTemperature.GetTemperatureForCell(c, map); minTemp = Math.Min(minTemp, temp); maxTemp = Math.Max(maxTemp, temp); tempCount++;
            if (pathIndices.ContainsKey(c)) pathSamples[c] = CellState(map, c, actor);
            float glow = map.glowGrid.GroundGlowAt(c); minGlow = Math.Min(minGlow, glow); maxGlow = Math.Max(maxGlow, glow);
            if (c.IsPolluted(map)) polluted++; if (map.snowGrid.GetDepth(c) > 0) snow++;
            foreach (GasType gas in Enum.GetValues(typeof(GasType))) { byte d = map.gasGrid.DensityAt(c, gas); if (d > 0) { string key = gas.ToString(); Add(gasCells, key); gasMax[key] = gasMax.TryGetValue(key, out var max) ? Math.Max(max, d) : d; } }
            var room = c.GetRoom(map); if (room != null && !rooms.ContainsKey(room.ID)) { rooms[room.ID] = RoomState(room); roomVersions[room] = room.CellCount; }
            foreach (var t in map.thingGrid.ThingsListAtFast(c).ToArray())
            {
                if (t.Destroyed || !things.Add(t)) continue; objectVersions[t] = (t.Position, t.Map, t.Spawned, t.HitPoints, (t as Building_Door)?.Open);
                if (t.def.holdsRoof) roofSupports.Add(WorldStateReader.Identity(t));
                if (t is Fire fire) hazards.Add(new JObject { ["type"] = "fire", ["source"] = WorldStateReader.Identity(t), ["fire_size"] = fire.fireSize });
                else if (t is Building_Trap) hazards.Add(new JObject { ["type"] = "trap", ["source"] = WorldStateReader.Identity(t), ["meaning"] = "Observed trap object; actor-specific trigger/arming is a separate state facet." });
                var explosive = t.TryGetComp<CompExplosive>(); if (explosive != null && explosive.wickStarted) hazards.Add(new JObject { ["type"] = "active_fuse", ["source"] = WorldStateReader.Identity(t) });
                if (t is not Fire && t.IsBurning()) hazards.Add(new JObject { ["type"] = "burning_object", ["source"] = WorldStateReader.Identity(t) });
            }
        }
        catch (Exception e) when (ReadContext.Recoverable(e)) { errors = true; if (failures.Count < 8) failures.Add(e.GetType().Name + " at " + c); }
    }
    internal IEnumerable<int> Threats(JArray output, Action<bool> done)
    {
        bool complete = actor != null; int pairs = 0, visitedSources = 0;
        if (actor == null) { done(false); yield break; }
        foreach (var t in map.listerThings.AllThings.ToArray())
        {
            if ((++visitedSources & 63) == 0) yield return 0;
            if (t.Destroyed || !t.Spawned || t.Position.Fogged(map) || t is not (Pawn or Building_Turret) || !(t.HostileTo(actor) || t is Pawn aggressive && aggressive.InAggroMentalState)) continue;
            if (t is Pawn p && (p.Dead || p.Downed) || t is Building_TurretGun gun && !gun.Active) continue;
            var sourcePosition = t.Position; var sourceTick = Find.TickManager.TicksGame;
            var verbs = WorldNativeAccess.ExistingAttacks(t, out var trackersComplete);
            if (t is Pawn wearer && wearer.apparel != null && wearer.apparel.WornApparel.Any(a => !WorldNativeAccess.NativeMethod(a, "AllowVerbCast"))) { complete = false; output.Add(new JObject { ["source"] = WorldStateReader.Identity(t), ["status"] = "unsupported_apparel_attack_hook" }); continue; }
            objectVersions[t] = (t.Position, t.Map, t.Spawned, t.HitPoints, (t as Building_Door)?.Open);
            var equipment = (t as Pawn)?.equipment?.AllEquipmentListForReading.ToArray() ?? Array.Empty<ThingWithComps>();
            string? job = (t as Pawn)?.CurJob?.GetUniqueLoadID(); bool? active = (t as Building_TurretGun)?.Active;
            if (verbs.Count == 0) { complete = false; output.Add(new JObject { ["source"] = WorldStateReader.Identity(t), ["status"] = "unavailable_attack", ["reason"] = "No initialized attack verbs; query does not create or randomly select combat verbs." }); continue; }
            var affected = new HashSet<IntVec3>(); bool sourceComplete = trackersComplete; int visits = 0;
            foreach (var verb in verbs)
            {
                if (verb.GetType().Assembly != typeof(Verb).Assembly || !WorldNativeAccess.NativeMethod(verb, "CanHitTargetFrom")) { complete = false; sourceComplete = false; continue; }
                foreach (var c in visibleCells)
                {
                    if ((++visits & 63) == 0) yield return 0;
                    if ((c - sourcePosition).LengthHorizontalSquared > Math.Pow(verb.EffectiveRange + 2, 2)) continue;
                    if (++pairs > 32768) { complete = false; sourceComplete = false; break; }
                    try { if (verb.CanHitTargetFrom(sourcePosition, new LocalTargetInfo(c))) affected.Add(c); }
                    catch (Exception e) when (ReadContext.Recoverable(e)) { complete = false; sourceComplete = false; }
                    if ((pairs & 15) == 0) yield return 0;
                }
                if (pairs > 32768) break;
            }
            if (t.Destroyed || !t.Spawned || t.Map != map || t.Position != sourcePosition || job != (t as Pawn)?.CurJob?.GetUniqueLoadID() || active != (t as Building_TurretGun)?.Active || !equipment.SequenceEqual((t as Pawn)?.equipment?.AllEquipmentListForReading.ToArray() ?? Array.Empty<ThingWithComps>())) sourceComplete = false;
            if (!sourceComplete) complete = false;
            if (affected.Count > 0 || !sourceComplete) output.Add(new JObject { ["source"] = WorldStateReader.Identity(t), ["checked_from"] = WorldStateReader.Cell(map, sourcePosition), ["started_tick"] = sourceTick, ["finished_tick"] = Find.TickManager.TicksGame, ["attack_trackers_complete"] = trackersComplete, ["activity"] = t is Pawn attacking ? new JObject { ["mental_state"] = attacking.MentalStateDef?.defName, ["current_job"] = WorldStateReader.JobState(attacking.CurJob) } : t is Building_Turret turret ? new JObject { ["current_target"] = WorldStateReader.TargetState(turret.CurrentTarget) } : null, ["status"] = sourceComplete ? "checked" : "partial", ["affected_observed_cells"] = affected.Count, ["example_cells"] = new JArray(affected.Take(8).Select(c => WorldStateReader.Cell(map, c))), ["path_indices"] = new JArray(pathIndices.Where(p => affected.Contains(p.Key)).SelectMany(p => p.Value)), ["source_inside_footprint"] = visited.Contains(t.Position), ["meaning"] = "Native range/line-of-fire eligibility against sampled cells using existing verbs; not hit probability, future movement, firing schedule or all abilities." });
            if (pairs > 32768) break; yield return 0;
        }
        done(complete);
    }
    internal JObject Result(bool scanComplete, JArray threats, bool threatComplete)
    {
        var bands = new JArray(); JObject? band = null; string? prior = null;
        foreach (var pair in pathIndices.SelectMany(p => p.Value.Select(i => (Index: i, Cell: p.Key))).OrderBy(p => p.Index))
        {
            if (!pathSamples.TryGetValue(pair.Cell, out var sample)) { prior = null; continue; }
            var classification = new JObject { ["temperature_c"] = sample["temperature_c"], ["gases_0_255"] = sample["gases_0_255"], ["polluted"] = sample["polluted"], ["roof"] = sample["roof"], ["native_danger_for_actor"] = sample["native_danger_for_actor"], ["walkable"] = sample["walkable"] };
            string signature = classification.ToString(Newtonsoft.Json.Formatting.None);
            if (signature != prior) { band = new JObject { ["first_index"] = pair.Index, ["last_index"] = pair.Index, ["first_cell"] = WorldStateReader.Cell(map, pair.Cell), ["last_cell"] = WorldStateReader.Cell(map, pair.Cell), ["environment"] = classification }; bands.Add(band); prior = signature; }
            else { band!["last_index"] = pair.Index; band["last_cell"] = WorldStateReader.Cell(map, pair.Cell); }
        }

        foreach (var h in hazards) { var id = (string?)h["source"]?["id"]; var t = things.FirstOrDefault(t => t.GetUniqueLoadID() == id); var indices = t == null ? new int[0] : pathIndices.Where(p => t.OccupiedRect().Contains(p.Key)).SelectMany(p => p.Value).ToArray(); if(pathIndices.Count>0){h["on_path"] = indices.Length > 0; h["path_indices"] = new JArray(indices);} h["relation"] = pathIndices.Count == 0 ? "inside_region" : indices.Length > 0 ? "on_path" : "near_path"; }
        return new JObject
        {
            ["path_environment_bands"] = new JArray(bands.Take(200)),
            ["path_environment_bands_total"] = bands.Count,
            ["path_environment_bands_complete"] = bands.Count <= 200,
            ["roof_support_objects"] = new JArray(roofSupports.Take(100)),
            ["roof_support_objects_total"] = roofSupports.Count,
            ["map_context"] = new JObject { ["weather"] = map.weatherManager.curWeather?.defName, ["active_conditions"] = new JArray(map.gameConditionManager.ActiveConditions.Select(g => new JObject { ["definition"] = g.def.defName, ["label"] = g.Label, ["ticks_left"] = g.TicksLeft })) }, ["observed_cells"] = visibleCells.Count,
            ["hidden_cells"] = hidden,
            ["scanned_cells"] = total,
            ["terrain_cells"] = JObject.FromObject(terrain),
            ["roof_cells"] = JObject.FromObject(roof),
            ["water_cells"] = wet,
            ["snow_cells"] = snow,
            ["polluted_cells"] = polluted,
            ["temperature_c"] = tempCount == 0 ? null : new JObject { ["min"] = minTemp, ["max"] = maxTemp },
            ["glow_fraction"] = tempCount == 0 ? null : new JObject { ["min"] = minGlow, ["max"] = maxGlow },
            ["gases"] = new JArray(gasCells.Select(g => new JObject { ["type"] = g.Key, ["cells"] = g.Value, ["max_density_0_255"] = gasMax[g.Key] })),
            ["rooms"] = new JArray(rooms.Values),
            ["local_hazards"] = new JArray(hazards.Take(200)),
            ["local_hazards_total"] = hazards.Count,
            ["local_hazards_listing_complete"] = hazards.Count <= 200,
            ["attack_influences"] = threats, ["attack_check_status"] = actor==null?"not_computed_actor_required":threatComplete?"completed_for_supported_attacks":"partial",
            ["coverage"] = new JObject { ["cell_scan_complete"] = scanComplete && !errors, ["changes_detected"] = Changed, ["visibility_complete"] = hidden == 0, ["native_attack_checks_complete"] = threatComplete && scanComplete && !errors && !Changed, ["not_modeled"] = new JArray("future fire spread", "future enemy movement", "projectile travel or explosion blast simulation", "arbitrary mod hazards", "roof collapse prediction"), ["read_errors"] = new JArray(failures), ["meaning"] = "No universal safe verdict. Hidden cells and unmodeled effects are not proven absent. Range checks can include sources outside the footprint." }
        };
    }
    internal IReadOnlyCollection<Thing> Things => things;
    private static void Add(Dictionary<string, int> d, string key) => d[key] = d.TryGetValue(key, out var n) ? n + 1 : 1;
}
