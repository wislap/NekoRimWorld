using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GraphQL;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
using Verse.AI;
namespace Neko.RimWorld;

internal static class WorldObservation
{
    // Detached JSON only. No live game object/path survives in a continuation.
    private sealed class Page { internal string Load = "", Signature = ""; internal JObject Data = new(); internal List<JObject> Rows = new(); internal DateTime Expires; }
    private static readonly Dictionary<string, Page> pages = new();
    internal static void Clear() => pages.Clear();
    internal static QueryOutcome Execute(string op, JObject args, SessionDescriptor session) { using var work = new Operation(op, args, session); while (!work.Advance()) { } return work.Outcome!; }
    internal sealed class Operation : IFrameQueryOperation
    {
        private readonly Game game = Current.Game; private readonly SessionDescriptor session; private readonly ReadContext context;
        private readonly string op; private readonly JObject args; private readonly IEnumerator<int> work;
        private readonly Stopwatch lifetime = Stopwatch.StartNew(); private bool disposed;
        private readonly int startTick = Find.TickManager.TicksGame;
        public QueryOutcome? Outcome { get; private set; }
        private readonly JObject result = new() { ["status"] = "reading" };
        private string? cachedPageId;
        private Map? map; private WorldEnvironmentReader? environment; private readonly List<(Thing Thing, Map Map, IntVec3 Position)> resolvedVersions = new();
        internal Operation(string op, JObject args, SessionDescriptor session) { this.op = op; this.args = (JObject)args.DeepClone(); this.session = session; context = new ReadContext(new VerseGameData(), session, 12); work = Run().GetEnumerator(); }
        public bool Advance() { if (Outcome != null) return true; context.RestartSlice(); var timer = Stopwatch.StartNew(); try { if (map?.Disposed == true) throw ReadContext.Error("object_unavailable", "Map unloaded during read"); if (Current.Game != game) throw ReadContext.Error("world_changed", "Loaded game changed during read"); if (lifetime.Elapsed.TotalSeconds > 8) throw ReadContext.Error("observation_deadline", "Read lifetime exceeded; use a smaller footprint or fewer targets"); do { if (!work.MoveNext()) { Finish(false); return true; } } while (timer.Elapsed.TotalMilliseconds < 2); } catch (Exception e) when (ReadContext.Recoverable(e)) { if (e is ExecutionError error && error.Code is "invalid_arguments" or "object_unavailable" or "world_changed") Outcome = QueryOutcome.Failure(error.Code!, error.Message); else { result["read_error"] = new JObject { ["code"] = (e as ExecutionError)?.Code ?? "observation_unavailable", ["message"] = e.Message }; if (environment != null) result["environment"] = environment.Result(false, new JArray(), false); Finish(true); } Dispose(); } return Outcome != null; }
        private void Finish(bool partial) { if (Outcome != null) return; partial |= (string?)result["status"] == "partial"; if ((environment?.Changed ?? false) || resolvedVersions.Any(v => v.Thing.Destroyed || v.Thing.MapHeld != v.Map || v.Thing.PositionHeld != v.Position)) { partial = true; result["changed_during_read"] = true; } session.GameTick = Find.TickManager.TicksGame; result["status"] = partial ? "partial" : "ok"; result["consistency"] = "Read-only samples over the reported interval; same tick does not guarantee an atomic snapshot of UI or other mod changes."; if (result["sample"] == null) result["sample"] = new JObject { ["load_epoch"] = session.LoadEpoch, ["snapshot_sequence"] = session.SnapshotSequence, ["started_tick"] = startTick, ["finished_tick"] = session.GameTick, ["same_tick"] = startTick == session.GameTick, ["atomic"] = false }; result["delivery_sample"] = new JObject { ["game_tick"] = session.GameTick, ["snapshot_sequence"] = session.SnapshotSequence }; result["read_ms"] = lifetime.Elapsed.TotalMilliseconds; if (cachedPageId != null && pages.TryGetValue(cachedPageId, out var cached)) { cached.Data = (JObject)result.DeepClone(); cached.Data.Remove("objects"); cached.Data.Remove("page"); } if (result.ToString(Formatting.None).Length > 90000) { Outcome = QueryOutcome.Failure("observation_result_too_large", "Select fewer objects or a narrower footprint; no truncated result presented as complete"); } else Outcome = new QueryOutcome { Status = partial ? ProtocolStatuses.Partial : ProtocolStatuses.Ok, Data = result, Issues = partial ? new[] { new QueryIssue { Code = "observation_partial", Message = "Some requested evidence could not be completed; inspect per-facet coverage", Path = new JArray() } } : Array.Empty<QueryIssue>() }; Dispose(); }
        public void Dispose() { if (disposed) return; disposed = true; work.Dispose(); }
        private sealed class Target { internal Thing? Thing; internal Map Map = null!; internal IntVec3 Cell; internal bool Held; }
        private IEnumerable<int> Resolve(JObject reference, Action<Target> set, bool pawn = false)
        {
            var raw = (JObject)reference.DeepClone(); raw["kind"] = raw["x"] != null ? "cell" : pawn ? "pawn" : "thing";
            if (pawn && raw["x"] != null) throw ReadContext.Error("invalid_arguments", "Pawn context requires id/name");
            var r = new SpatialQuery.Reference();
            var selectedMap = SpatialQuery.ResolveMap((string?)raw["map"])!; r.Map = selectedMap;
            if (raw["x"] != null) r.Cell = new IntVec3((int)raw["x"]!, 0, (int)raw["z"]!);
            else
            {
                bool pawnsOnly = pawn || (string?)reference["kind"] == "pawn";
                var queue = new Queue<Thing>(selectedMap.listerThings.ThingsInGroup(pawnsOnly ? ThingRequestGroup.Pawn : ThingRequestGroup.Everything).ToArray());
                var allHolders = new List<IThingHolder>(); selectedMap.GetChildHolders(allHolders);
                var holders = new Queue<IThingHolder>(allHolders); var seenHolders = new HashSet<IThingHolder>(); var seenThings = new HashSet<Thing>(); var matches = new List<object>(); int scanned = 0;
                while (queue.Count > 0 || holders.Count > 0)
                {
                    if ((scanned++ & 31) == 0) yield return 0;
                    if (selectedMap.Disposed) throw ReadContext.Error("object_unavailable", "Map unloaded during lookup");
                    if (scanned > 100000) throw ReadContext.Error("identity_scan_limit", "Identity lookup exceeded bounded membership");
                    if (queue.Count == 0)
                    {
                        var holder = holders.Dequeue(); if (!seenHolders.Add(holder)) continue;
                        WorldNativeAccess.RequireNative(holder, "GetDirectlyHeldThings", "GetChildHolders");
                        var contents = holder.GetDirectlyHeldThings(); if (contents != null) foreach (var item in contents.ToArray()) if (!item.Spawned && (item.ParentHolder == null || ThingOwnerUtility.AreImmediateContentsReal(item.ParentHolder))) queue.Enqueue(item);
                        var children = new List<IThingHolder>(); holder.GetChildHolders(children); foreach (var child in children) holders.Enqueue(child); continue;
                    }
                    var item2 = queue.Dequeue(); if (!seenThings.Add(item2) || item2.Destroyed || item2.MapHeld != selectedMap || !item2.SpawnedOrAnyParentSpawned || item2.PositionHeld.Fogged(selectedMap) || pawnsOnly && item2 is not Pawn) continue;
                    if (raw["id"] != null) { if (item2.GetUniqueLoadID() != (string?)raw["id"]) continue; }
                    else { WorldNativeAccess.RequireNative(item2, "get_Label"); if (!string.Equals(item2.LabelShortCap, (string?)raw["name"], StringComparison.OrdinalIgnoreCase) && !string.Equals(item2.LabelCap, (string?)raw["name"], StringComparison.OrdinalIgnoreCase)) continue; }
                    matches.Add(item2); if (raw["id"] != null || matches.Count > 16) break;
                }
                if (matches.Count == 1) r.Value = matches[0]; else if (matches.Count > 1) { r.Candidates = matches.Take(16).ToList(); r.CandidatesTruncated = matches.Count > 16; }
            }
            if (r.Candidates != null) { result["resolution"] = new JObject { ["status"] = "ambiguous", ["reference"] = reference, ["candidates"] = new JArray(r.Candidates.OfType<Thing>().Select(WorldStateReader.Identity)), ["truncated"] = r.CandidatesTruncated }; throw ReadContext.Error("ambiguous_reference", "Choose a precise reference from candidates"); }
            if (r.Value is Thing t) { if (!t.SpawnedOrAnyParentSpawned || t.MapHeld == null) throw ReadContext.Error("object_unavailable", "Reference has no loaded position"); if (t.PositionHeld.Fogged(t.MapHeld)) throw ReadContext.Error("object_unavailable", "Reference is hidden by fog"); resolvedVersions.Add((t, t.MapHeld, t.PositionHeld)); set(new Target { Thing = t, Map = t.MapHeld, Cell = t.PositionHeld, Held = !t.Spawned }); }
            else if (r.Cell.HasValue && r.Map != null) { if (!r.Cell.Value.InBounds(r.Map)) throw ReadContext.Error("invalid_arguments", "Cell outside map"); if (r.Cell.Value.Fogged(r.Map)) throw ReadContext.Error("object_unavailable", "Cell hidden by fog"); set(new Target { Map = r.Map, Cell = r.Cell.Value }); }
            else throw ReadContext.Error("object_unavailable", "Reference not found in requested map");
        }
        private IEnumerable<int> Run()
        {
            WorldObservationContract.Validate(op, args);
            string signature = GameQueryEngine.Hash(op + ":" + Signature(args));
            if (args["cursor"] != null) { var token = args["cursor"]!.ToString().Split(':'); if (token.Length != 2 || !pages.TryGetValue(token[0], out var page) || page.Load != session.LoadEpoch || page.Signature != signature || page.Expires < DateTime.UtcNow || !int.TryParse(token[1], out var offset) || offset < 0 || offset >= page.Rows.Count) throw ReadContext.Error("invalid_arguments", "Expired or mismatched cursor; restart the scoped read"); Copy(page.Data, result); PutPage(page.Rows, offset, (int?)args["limit"] ?? 20, token[0]); result["continued_observation"] = true; yield break; }
            Target? actorRef = null, userRef = null;
            if (args["actor"] is JObject ar) foreach (var step in Resolve(ar, t => actorRef = t, true)) yield return step;
            if (args["user"] is JObject ur) foreach (var step in Resolve(ur, t => userRef = t, true)) yield return step;
            actorRef ??= userRef; Pawn? actor = actorRef?.Thing as Pawn; Pawn? user = userRef?.Thing as Pawn;
            bool details = (string?)args["detail"] == "details";
            if (op == "inspect_state")
            {
                var rows = new JArray(); result["objects"] = rows; var readers = new Dictionary<Map, WorldStateReader>();
                foreach (JObject target in (JArray)args["targets"]!)
                {
                    Target? resolved = null; Exception? resolutionError = null;
                    using (var resolving = Resolve(target, t => resolved = t).GetEnumerator())
                    {
                        while (true)
                        {
                            bool more = false; try { more = resolving.MoveNext(); } catch (ExecutionError e) when (e.Code != "world_changed") { resolutionError = e; }
                            if (resolutionError != null || !more) break; yield return resolving.Current;
                        }
                    }
                    if (resolutionError != null) { rows.Add(new JObject { ["requested_target"] = target.DeepClone(), ["status"] = "unavailable", ["reason"] = (resolutionError as ExecutionError)?.Code, ["message"] = resolutionError.Message, ["resolution"] = result["resolution"]?.DeepClone() }); result.Remove("resolution"); result["status"] = "partial"; continue; }
                    map = resolved!.Map; if (actorRef != null && actorRef.Map != map || userRef != null && userRef.Map != map) throw ReadContext.Error("invalid_arguments", "Contexts and target must be on the same map"); if (!readers.TryGetValue(map, out var reader)) readers[map] = reader = new WorldStateReader(map, context); if (resolved.Thing != null) rows.Add(reader.Read(resolved.Thing, actor, user, (args["stats"] as JArray)?.Values<string>().Select(s => s!) ?? Array.Empty<string>(), details)); else rows.Add(new JObject { ["target"] = WorldStateReader.Cell(map, resolved.Cell), ["environment"] = WorldEnvironmentReader.CellState(map, resolved.Cell, actor), ["reservations"] = reader.CellReservations(resolved.Cell) }); yield return 0;
                }
                result["meaning"] = "Current object state and named native predicates; no action, reservation, job test or universal usability verdict."; yield break;
            }
            var cells = new List<IntVec3>(); List<IntVec3>? pathCells = null; JObject geometry;
            if (op == "inspect_route")
            {
                if (actor == null || !actor.Spawned) throw ReadContext.Error("object_unavailable", "Route actor must be spawned"); map = actor.Map; Target? to = null, start = null;
                foreach (var step in Resolve((JObject)args["to"]!, t => to = t)) yield return step;
                if (args["start"] is JObject sr) foreach (var step in Resolve(sr, t => start = t)) yield return step;
                if (to!.Map != map || start != null && start.Map != map) throw ReadContext.Error("invalid_arguments", "Route cannot cross maps");
                if (to.Held) throw ReadContext.Error("invalid_arguments", "Held target has no own destination; select its holder explicitly");
                var from = start?.Cell ?? actor.Position; var target = to.Thing == null ? new LocalTargetInfo(to.Cell) : new LocalTargetInfo(to.Thing); var endMode = target.HasThing ? PathEndMode.ClosestTouch : PathEndMode.OnCell;
                var resolvedTarget = (LocalTargetInfo)GenPath.ResolvePathMode(actor, target.ToTargetInfo(map), ref endMode); var watch = Stopwatch.StartNew();
                using (var path = map.pathFinder.FindPathNow(from, resolvedTarget, TraverseParms.For(actor, Danger.Some, TraverseMode.ByPawn), PathFinderCostTuning.For(actor), endMode))
                {
                    if (!path.Found) { result["route"] = new JObject { ["status"] = "not_found" }; result["environment"] = new JObject { ["status"] = "not_computed", ["reason"] = "no_path" }; yield break; }
                    if (path.NodesReversed.Count > SpatialRouteSummary.MaxNodes) throw ReadContext.Error("route_node_limit", "Path exceeds bounded observation");
                    pathCells = path.NodesReversed.AsEnumerable().Reverse().ToList(); result["route"] = SpatialRouteSummary.Create(path.NodesReversed.Select(c => (c.x, c.z)).ToList(), path.TotalCost, map.GetUniqueLoadID());
                }
                context.RestartSlice(); result["route"]!["end_mode"] = endMode.ToString(); result["route"]!["path_tick"] = Find.TickManager.TicksGame; result["route"]!["read_ms"] = watch.Elapsed.TotalMilliseconds; result["route"]!["cooperative_budget_overrun"] = watch.Elapsed.TotalMilliseconds > 12; result["route"]!["hypothetical_start"] = start != null; result["route"]!["target"] = to.Thing == null ? WorldStateReader.Cell(map, to.Cell) : WorldStateReader.Identity(to.Thing); result["route"]!["actor"] = WorldStateReader.Identity(actor);
                var margin = (int?)args["margin"] ?? 2; var seen = new HashSet<IntVec3>(); foreach (var c in pathCells) { for (int x = -margin; x <= margin; x++) for (int z = -margin; z <= margin; z++) { if (x * x + z * z > margin * margin) continue; var p = c + new IntVec3(x, 0, z); if (p.InBounds(map) && seen.Add(p)) cells.Add(p); } if (cells.Count > 65536) throw ReadContext.Error("footprint_limit", "Path corridor exceeds 65536 cells"); yield return 0; }
                geometry = new JObject { ["kind"] = "route_corridor", ["margin"] = margin, ["path_cells"] = pathCells.Count, ["distinct_corridor_cells"] = cells.Count, ["meaning"] = "Path and corridor are sampled spatial footprints, not predicted actor movement." };
            }
            else
            {
                var r = (JObject)args["region"]!; map = SpatialQuery.ResolveMap((string?)r["map"]); geometry = (JObject)r.DeepClone();
                if (r["near"] is JObject near || r["room_of"] is JObject)
                {
                    Target? center = null; var rf = (JObject)(r["near"] ?? r["room_of"])!.DeepClone(); if (rf["map"] == null && r["map"] != null) rf["map"] = r["map"]!.DeepClone(); foreach (var step in Resolve(rf, t => center = t)) yield return step; if (r["map"] != null && map != center!.Map) throw ReadContext.Error("invalid_arguments", "Region map and center map differ"); map = center!.Map; geometry["resolved_center"] = WorldStateReader.Cell(map, center.Cell); geometry["center_position_kind"] = center.Held ? "holder" : "own";
                    if (r["room_of"] != null) { var room = center.Cell.GetRoom(map); if (room == null) throw ReadContext.Error("object_unavailable", "No room at center"); foreach (var c in room.Cells) { cells.Add(c); if ((cells.Count & 127) == 0) yield return 0; } geometry["room"] = WorldEnvironmentReader.RoomState(room); }
                    else { geometry["shape"]="circle";geometry["distance_metric"]="euclidean_map_cells"; int radius = (int?)r["radius"] ?? 20; for (int x = -radius; x <= radius; x++) { for (int z = -radius; z <= radius; z++) { var c = center.Cell + new IntVec3(x, 0, z); if (x * x + z * z <= radius * radius && c.InBounds(map)) cells.Add(c); } yield return 0; } }
                }
                else if (r["rect"] is JObject rect) { int x0 = Math.Max(0, (int)rect["min_x"]!), x1 = Math.Min(map!.Size.x - 1, (int)rect["max_x"]!), z0 = Math.Max(0, (int)rect["min_z"]!), z1 = Math.Min(map.Size.z - 1, (int)rect["max_z"]!); for (int x = x0; x <= x1; x++) { for (int z = z0; z <= z1; z++) cells.Add(new IntVec3(x, 0, z)); yield return 0; } }
                else if (r["area"] is JObject area) { var choices = new List<(string Id, string Name, string Kind, IEnumerable<IntVec3> Cells)>(); if ((string?)area["kind"] != "zone") choices.AddRange(map!.areaManager.AllAreas.Select(a => (a.GetUniqueLoadID(), a.Label, "area", a.ActiveCells))); if ((string?)area["kind"] != "area") choices.AddRange(map!.zoneManager.AllZones.Select(z => (z.GetUniqueLoadID(), z.label, "zone", (IEnumerable<IntVec3>)z.Cells))); var matches = choices.Where(c => area["id"] != null ? c.Id == (string?)area["id"] : string.Equals(c.Name, (string?)area["name"], StringComparison.OrdinalIgnoreCase)).ToList(); if (matches.Count != 1) { result["resolution"] = new JObject { ["status"] = matches.Count == 0 ? "not_found" : "ambiguous", ["candidates"] = new JArray(matches.Select(c => new JObject { ["id"] = c.Id, ["name"] = c.Name, ["kind"] = c.Kind })) }; throw ReadContext.Error("region_unresolved", "Select one area or zone"); } cells.AddRange(matches[0].Cells); geometry["resolved_id"] = matches[0].Id; }
                else { foreach (var c in map!.AllCells) { cells.Add(c); if ((cells.Count & 127) == 0) yield return 0; } }
            }
            if (actorRef != null && actorRef.Map != map) throw ReadContext.Error("invalid_arguments", "Actor and region map differ");
            if (userRef != null && userRef.Map != map) throw ReadContext.Error("invalid_arguments", "Recipient and region map differ");
            result["environment_contexts"] = new JArray(new[]{actor,user}.Where(p=>p!=null).Distinct().Select(p=>new JObject{["pawn"]=WorldStateReader.Identity(p!),["safe_temperature_range_c"]=new JObject{["min"]=p!.SafeTemperatureRange().min,["max"]=p!.SafeTemperatureRange().max},["concerned_by_vacuum"]=p!.ConcernedByVacuum,["harmed_by_vacuum"]=p!.HarmedByVacuum,["meaning"]="Current recipient/actor environmental traits. Compare with sampled conditions; no simulated carrying, exposure duration or survival forecast."}));
            cells = cells.Distinct().OrderBy(c => c.z).ThenBy(c => c.x).ToList(); if (cells.Count > 1000000) throw ReadContext.Error("footprint_limit", "Footprint exceeds one million cells");
            if (geometry["rect"] is JObject rectBounds) geometry["clipped_cells"] = ((decimal)(int)rectBounds["max_x"]! - (int)rectBounds["min_x"]! + 1) * ((decimal)(int)rectBounds["max_z"]! - (int)rectBounds["min_z"]! + 1) - cells.Count;
            if (geometry["near"] != null) { int radius = (int?)geometry["radius"] ?? 20; long requested = 0; for (int x = -radius; x <= radius; x++) for (int z = -radius; z <= radius; z++) if (x*x+z*z <= radius*radius) requested++; geometry["clipped_cells"] = requested-cells.Count; }
            geometry["bounds_policy"] = "Intersection with loaded map bounds; original requested footprint remains in geometry."; result["region"] = new JObject { ["map"] = map!.GetUniqueLoadID(), ["geometry"] = geometry, ["cell_count"] = cells.Count, ["resolved_tick"] = Find.TickManager.TicksGame };
            environment = new WorldEnvironmentReader(map, actor); var onPath = pathCells == null ? null : new HashSet<IntVec3>(pathCells); var pathEnvironment = pathCells == null ? null : new WorldEnvironmentReader(map, actor); if (pathCells != null) { environment.SetPath(pathCells); pathEnvironment!.SetPath(pathCells); }
            foreach (var c in cells) { environment.Observe(c); if (onPath?.Contains(c) == true) pathEnvironment!.Observe(c); yield return 0; }
            var threats = new JArray(); bool threatComplete = false; foreach (var step in environment.Threats(threats, v => threatComplete = v)) yield return step; result["environment"] = environment.Result(true, threats, threatComplete); if (pathEnvironment != null) result["path_environment"] = pathEnvironment.Result(true, new JArray(threats.Where(t => (t["path_indices"] as JArray)?.Count > 0 || (string?)t["status"] != "checked").Select(t => t.DeepClone())), threatComplete);
            var areaCoverage = new JArray(); var footprintSet = new HashSet<IntVec3>(cells);
            foreach (var area in map.areaManager.AllAreas) { int intersect = 0, total = 0; foreach (var c in area.ActiveCells) { total++; if (footprintSet.Contains(c)) intersect++; if ((total & 127) == 0) yield return 0; } if (intersect > 0) areaCoverage.Add(new JObject { ["kind"] = "area", ["id"] = area.GetUniqueLoadID(), ["name"] = area.Label, ["intersecting_cells"] = intersect, ["total_cells"] = total }); }
            foreach (var zone in map.zoneManager.AllZones) { int intersect = zone.Cells.Count(footprintSet.Contains); if (intersect > 0) areaCoverage.Add(new JObject { ["kind"] = "zone", ["id"] = zone.GetUniqueLoadID(), ["name"] = zone.label, ["intersecting_cells"] = intersect, ["total_cells"] = zone.Cells.Count }); yield return 0; }
            result["region"]!["area_zone_intersections"] = areaCoverage;
            var reader2 = new WorldStateReader(map, context); var objects = new HashSet<Thing>(environment.Things); var holders = new Queue<IThingHolder>(objects.OfType<IThingHolder>()); var visitedHolders = new HashSet<IThingHolder>(); bool heldComplete = true;
            while (holders.Count > 0) { var h = holders.Dequeue(); if (!visitedHolders.Add(h)) continue; if (visitedHolders.Count > 4096) { heldComplete = false; break; } if (!WorldNativeAccess.NativeMethod(h, "GetDirectlyHeldThings") || !WorldNativeAccess.NativeMethod(h, "GetChildHolders")) { heldComplete = false; continue; } var children = new List<IThingHolder>(); h.GetChildHolders(children); foreach (var childHolder in children) holders.Enqueue(childHolder); var direct = h.GetDirectlyHeldThings(); if (direct != null) foreach (var t in direct.ToArray()) { if (!t.Destroyed && (t.ParentHolder == null || ThingOwnerUtility.AreImmediateContentsReal(t.ParentHolder))) objects.Add(t); if (t is IThingHolder child) holders.Enqueue(child); } yield return 0; }
            bool Matches(Thing t) { if (args["select"] is not JObject sel) return true; return (sel["definitions"] is not JArray ds || ds.Values<string>().Contains(t.def.defName)) && (sel["capabilities"] is not JArray cs || WorldStateReader.Capabilities(t).Intersect(cs.Values<string>()).Any()); }
            var selected = objects.Where(t => !t.Destroyed && Matches(t)).OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
            result["objects_summary"] = new JObject { ["records"] = selected.Count, ["groups"] = new JArray(selected.GroupBy(t => t.def.defName).Select(g => new JObject { ["definition"] = g.Key, ["label"] = g.First().def.LabelCap.ToString(), ["records"] = g.Count(), ["stack_units"] = g.Sum(t => (long)t.stackCount) })), ["scan_complete"] = heldComplete && environment.CellsComplete, ["capabilities"] = new JArray(WorldObservationContract.Capabilities.Select(cap => new JObject { ["capability"] = cap, ["records"] = selected.Count(t => WorldStateReader.Capabilities(t).Contains(cap)) })), ["membership"] = "Any occupied cell intersects footprint; real held contents of observed holders included once. Map fog excludes unknown cells. Geometric inclusion is not reachability." };
            var footprint = new HashSet<IntVec3>(cells);
            IntVec3? centerCell = geometry["resolved_center"] is JObject centerJson ? new IntVec3((int)centerJson["x"]!, 0, (int)centerJson["z"]!) : null;
            var rows2 = new List<JObject>();
            foreach (var t in selected.Take(1000))
            {
                context.RestartSlice(); var row = reader2.Read(t, actor, user, Array.Empty<string>(), details);
                var occupied = t.Spawned ? t.OccupiedRect().Cells.ToArray() : new[] { t.PositionHeld };
                row["spatial_relation"] = new JObject { ["intersects"] = occupied.Any(footprint.Contains), ["fully_contained"] = occupied.All(footprint.Contains), ["position_basis"] = t.Spawned ? "occupied_cells" : "holder_position", ["geometric_distance"] = centerCell.HasValue ? (JToken)Math.Sqrt(occupied.Min(c => (double)(c - centerCell.Value).LengthHorizontalSquared)) : JValue.CreateNull(), ["distance_unit"] = "map_cell_widths" };
                if (onPath != null)
                {
                    var indices = pathCells!.Select((c, i) => (Cell: c, Index: i))
                        .Where(p => occupied.Contains(p.Cell)).Select(p => p.Index).ToArray();
                    row["spatial_relation"]!["relation"] = indices.Length > 0 ? "on_path" : "near_path";
                    row["spatial_relation"]!["path_indices"] = new JArray(indices);
                }
                else row["spatial_relation"]!["relation"] = "inside_region";
                rows2.Add(row); yield return 0;
            }
            result["objects_summary"]!["detail_retention_complete"] = selected.Count <= 1000;
            if (pathCells != null) { var segments = new JArray(); JObject? segment = null; string? last = null; for (int i = 0; i < pathCells.Count; i++) { var c = pathCells[i]; if (c.Fogged(map)) { last = null; continue; } var room = c.GetRoom(map); var door = c.GetEdifice(map) as Building_Door; var key = c.GetTerrain(map).defName + ":" + room?.ID + ":" + door?.GetUniqueLoadID(); if (key != last) { segment = new JObject { ["first_index"] = i, ["last_index"] = i, ["first_cell"] = WorldStateReader.Cell(map, c), ["last_cell"] = WorldStateReader.Cell(map, c), ["terrain"] = c.GetTerrain(map).defName, ["room_id"] = room?.ID, ["door"] = door == null ? null : reader2.Read(door, actor, user, Array.Empty<string>(), false) }; segments.Add(segment); last = key; } else { segment!["last_index"] = i; segment["last_cell"] = WorldStateReader.Cell(map, c); } yield return 0; } result["route"]!["segments"] = segments; result["route"]!["environment_meaning"] = "Path nodes and surrounding corridor inspected over reported interval; not a carry-job simulation, not all future hazards."; }
            string pageId = Guid.NewGuid().ToString("N"); cachedPageId = pageId; result["observation_started_tick"] = startTick; result["observation_finished_tick"] = Find.TickManager.TicksGame; result["sample"] = new JObject { ["load_epoch"] = session.LoadEpoch, ["snapshot_sequence"] = session.SnapshotSequence, ["started_tick"] = startTick, ["finished_tick"] = Find.TickManager.TicksGame, ["same_tick"] = startTick == Find.TickManager.TicksGame, ["atomic"] = false };
            WorldResultPaging.PutInitial(result,rows2,(int?)args["limit"]??20,pageId,()=>{
                foreach(var key in pages.Where(p=>p.Value.Expires<DateTime.UtcNow).Select(p=>p.Key).ToArray())pages.Remove(key);
                if(result.ToString(Formatting.None).Length+rows2.Sum(r=>r.ToString(Formatting.None).Length)>=4000000)return false;
                if(pages.Count>=8)pages.Remove(pages.Keys.First());
                pages[pageId]=new Page{Load=session.LoadEpoch,Signature=signature,Data=(JObject)result.DeepClone(),Rows=rows2,Expires=DateTime.UtcNow.AddMinutes(2)};
                return true;
            });
        }
        private void PutPage(List<JObject> rows, int offset, int limit, string id) => WorldResultPaging.Put(result,rows,offset,limit,pages.ContainsKey(id)?id:null);
        private static void Copy(JObject from,JObject to){foreach(var p in from.Properties())to[p.Name]=p.Value.DeepClone();}
        private static string Signature(JObject args) => GameQueryEngine.Hash(WorldResultPaging.Signature(args));
    }
}
