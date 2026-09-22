using System;
using System.Collections.Generic;
using System.Linq;
using GraphQL;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace Neko.RimWorld;

/// <summary>
/// V2.1-A: one-shot, current-tick native reachability evidence.
/// This deliberately does not expose routes, Rooms, Regions or WorkGivers.
/// </summary>
internal static class SpatialQuery
{
    private const double ReadBudgetMilliseconds = 12;
    private const int MaxCandidates = 16;

    internal sealed class Operation : IDisposable
    {
        private readonly SessionDescriptor session;
        private readonly ReadContext context;
        private readonly JObject result;
        private readonly IEnumerator<int> work;
        private readonly System.Diagnostics.Stopwatch lifetime = System.Diagnostics.Stopwatch.StartNew();
        internal QueryOutcome? Outcome { get; private set; }
        internal Operation(JObject args, SessionDescriptor session)
        {
            this.session = session;
            context = new ReadContext(new VerseGameData(), session, ReadBudgetMilliseconds);
            result = new JObject {
                ["answer"] = "unknown",
                ["sample"] = new JObject { ["load_epoch"] = session.LoadEpoch, ["game_tick"] = session.GameTick, ["snapshot_sequence"] = session.SnapshotSequence },
                ["lookup_started_tick"] = session.GameTick,
                ["checked"] = new JObject { ["actor_resolved"] = false, ["target_resolved"] = false, ["same_map"] = null, ["native_reachability"] = false },
                ["stage"] = "validation",
                ["policy"] = new JObject { ["danger"] = "Some", ["traverse_mode"] = "ByPawn", ["can_bash_doors"] = false },
                ["meaning"] = "Native reachability only. No reservation, work eligibility, scheduling, route length or arrival-time check. The blocking cause is not identified. Policy parameters describe the check, not observed obstacles or an explanation for its outcome."
            };
            result["lookup_consistency"] = "Each reference scans the object membership captured at its lookup start; names sampled over the reported tick interval. Objects created afterwards are outside that lookup. Reachability checks the resolved IDs at the final tick.";
            work = Run(args).GetEnumerator();
        }
        internal bool Advance()
        {
            if (Outcome != null) return true;
            context.RestartSlice();
            var slice = System.Diagnostics.Stopwatch.StartNew();
            try {
                if (lifetime.Elapsed.TotalSeconds > 2) throw ReadContext.Error("spatial_deadline", "Spatial request exceeded its total lifetime.");
                do {
                    if (!work.MoveNext()) { Dispose(); return true; }
                } while (slice.Elapsed.TotalMilliseconds < 2);
            } catch (ExecutionError error) {
                if (error.Code == "invalid_arguments" || error.Code == "object_unavailable")
                    Outcome = QueryOutcome.Failure(error.Code, error.Message);
                else {
                    result["answer"] = "unknown";
                    result["explanation"] = "Spatial " + result["stage"] + " did not complete. Absence and reachability remain unknown; repeating the same selector is not a narrower query.";
                    Outcome = new QueryOutcome { Status = ProtocolStatuses.Partial, Data = result,
                        Issues = new[] { new QueryIssue { Code = error.Code ?? "spatial_unavailable", Message = error.Code == "deadline_exceeded" ? "Spatial work slice exceeded its cooperative budget; repeating identical selectors does not narrow the lookup." : error.Message, Path = new JArray() } } };
                }
                Dispose();
            } catch (Exception error) when (ReadContext.Recoverable(error)) {
                result["answer"] = "unknown";
                result["explanation"] = "A native spatial read was unavailable; no reachability conclusion was produced.";
                Outcome = new QueryOutcome { Status = ProtocolStatuses.Partial, Data = result,
                    Issues = new[] { new QueryIssue { Code = "spatial_unavailable", Message = "Native spatial read failed.", Path = new JArray() } } };
                Dispose();
            }
            if (Outcome != null) {
                session.GameTick = Find.TickManager.TicksGame;
                result["sample"]!["game_tick"] = session.GameTick;
                result["lookup_finished_tick"] = session.GameTick;
            }
            return Outcome != null;
        }
        private IEnumerable<int> Run(JObject args)
        {
            SpatialContract.Validate(args); // Both references validated before any live read.
            args = (JObject)args.DeepClone();
            foreach (var key in new[] { "from", "to" }) {
                var value = (JObject)args[key]!;
                value["map"] = ResolveMap((string?)value["map"])!.GetUniqueLoadID();
            }
            result["request"] = args.DeepClone();
            var from = new Reference(); var to = new Reference();
            result["stage"] = "actor_lookup";
            foreach (var step in Resolve((JObject)args["from"]!, from, context, true)) yield return step;
            result["checked"]!["actor_resolved"] = from.Value is Pawn;
            if (from.Value is Thing actor) result["from"] = DescribeThing(actor);
            result["stage"] = "target_lookup";
            foreach (var step in Resolve((JObject)args["to"]!, to, context, false)) yield return step;
            result["checked"]!["target_resolved"] = to.Value is Thing || to.Cell.HasValue;
            if (to.Value is Thing target) result["target"] = DescribeThing(target);
            else if (to.Cell.HasValue) result["target"] = DescribeCell(to.Map!, to.Cell.Value);
            session.GameTick = Find.TickManager.TicksGame;
            result["sample"]!["game_tick"] = session.GameTick;
            result["lookup_finished_tick"] = session.GameTick;
            result["lookup_consistency"] = "Each reference scans the object membership captured at its lookup start; names sampled over the reported tick interval. Objects created afterwards are outside that lookup. Reachability checks the resolved IDs at the final tick.";
            Outcome = Compute(from, to, result, context);
        }
        public void Dispose() => work.Dispose();
    }

    internal static QueryOutcome Execute(JObject arguments, SessionDescriptor session)
    {
        using var operation = new Operation(arguments, session);
        while (!operation.Advance()) { } // Paused developer capture only; live pump advances once per frame.
        return operation.Outcome!;
    }

    private static QueryOutcome Compute(Reference from, Reference to, JObject result, ReadContext context)
    {
            if (from.Candidates != null || to.Candidates != null)
            {
                result["answer"] = "ambiguous";
                result["candidates"] = new JObject {
                    ["from"] = from.Candidates == null ? null : new JArray(from.Candidates.Select(Candidate)),
                    ["to"] = to.Candidates == null ? null : new JArray(to.Candidates.Select(Candidate)),
                };
                result["candidates_truncated"] = (from.CandidatesTruncated || to.CandidatesTruncated);
                result["explanation"] = "A reference matched more than one loaded object; no object was selected.";
                return Ok(result);
            }
            if (from.Value == null)
            {
                result["answer"] = "not_found";
                result["explanation"] = "No actor matched in the selected map membership and lookup interval reported in request/sample.";
                return Ok(result);
            }
            if (to.Value == null && !to.Cell.HasValue)
            {
                result["answer"] = "not_found";
                result["explanation"] = "No target matched in the selected map membership and lookup interval reported in request/sample.";
                return Ok(result);
            }

            foreach (var pair in new[] { ("from", from), ("to", to) }) {
                if (pair.Item2.Value is not Thing member) continue;
                var selector = (JObject)result["request"]![pair.Item1]!;
                var name = (string?)selector["name"];
                if (member.Destroyed || member.MapHeld != pair.Item2.Map ||
                    name != null && !string.Equals(member.LabelShortCap, name, StringComparison.OrdinalIgnoreCase) && !string.Equals(member.LabelCap, name, StringComparison.OrdinalIgnoreCase)) {
                    result["answer"] = "unknown";
                    result["explanation"] = "A resolved object changed map, name or lifetime during lookup; no reachability check was made.";
                    return new QueryOutcome { Status = ProtocolStatuses.Partial, Data = result,
                        Issues = new[] { new QueryIssue { Code = "spatial_object_changed", Message = "Resolved identity changed during the lookup interval.", Path = new JArray(pair.Item1) } } };
                }
            }
            var pawn = (Pawn)from.Value;
            var map = pawn.Map;
            result["from"] = DescribeThing(pawn);
            if (map == null || map.Disposed || !pawn.Spawned)
            {
                result["answer"] = "unsupported";
                result["explanation"] = "The actor is not a spawned Pawn on a loaded map.";
                return Ok(result);
            }

            LocalTargetInfo target;
            if (to.Value is Thing thing)
            {
                if (!thing.Spawned || thing.Map == null || thing.Map.Disposed)
                {
                    result["answer"] = "unsupported";
                    result["explanation"] = "The selected instance is not spawned on a loaded map, so direct reachability to that instance is unsupported. This does not say that its spawned container is unreachable.";
                    return Ok(result);
                }
                if (!ReferenceEquals(map, thing.Map))
                {
                    result["explanation"] = "The actor and target are on different loaded maps.";
                    return Ok(SetMapResult(result, false));
                }
                target = new LocalTargetInfo(thing);
                result["target"] = DescribeThing(thing);
            }
            else
            {
                var cell = to.Cell!.Value;
                if (!cell.InBounds(to.Map!))
                {
                    result["answer"] = "not_found";
                    result["explanation"] = "The target cell is outside the selected map.";
                    return Ok(result);
                }
                if (!ReferenceEquals(map, to.Map))
                {
                    result["explanation"] = "The actor and target are on different loaded maps.";
                    return Ok(SetMapResult(result, false));
                }
                target = new LocalTargetInfo(cell);
                result["target"] = DescribeCell(to.Map!, cell);
            }

            var checkedFields = (JObject)result["checked"]!;
            checkedFields["same_map"] = true;
            result["stage"] = "reachability";
            result["policy"]!["end_mode"] = target.HasThing ? "ClosestTouch" : "OnCell";
            context.Check();
            var reachable = map.reachability.CanReach(
                pawn.Position,
                target,
                target.HasThing ? PathEndMode.ClosestTouch : PathEndMode.OnCell,
                TraverseParms.For(pawn, Danger.Some, TraverseMode.ByPawn));
            context.Check();
            checkedFields["native_reachability"] = true;
            result["answer"] = reachable ? "reachable" : "unreachable";
            result["explanation"] = reachable
                ? "Native reachability found a valid current path under this Pawn's movement parameters."
                : "Native reachability found no valid current path under this Pawn's movement parameters.";
            return Ok(result);
    }

    private static QueryOutcome Ok(JObject data) => new() { Status = ProtocolStatuses.Ok, Data = data };

    private static JObject SetMapResult(JObject result, bool sameMap)
    {
        result["checked"]!["same_map"] = sameMap;
        result["answer"] = "unsupported";
        return result;
    }

    private sealed class Reference
    {
        internal object? Value;
        internal Map? Map;
        internal IntVec3? Cell;
        internal List<object>? Candidates;
        internal bool CandidatesTruncated;
    }

    private static IEnumerable<int> Resolve(JObject raw, Reference resolved, ReadContext context, bool requirePawn)
    {
        var map = ResolveMap((string?)raw["map"])!;
        if ((string?)raw["kind"] == "cell") {
            resolved.Map = map; resolved.Cell = new IntVec3((int)raw["x"]!, 0, (int)raw["z"]!);
            yield break;
        }
        var id = (string?)raw["id"]; var name = (string?)raw["name"];
        // Capture only identities, not a world mirror or label cache. No iterator over
        // mutable game collections survives a frame boundary.
        var members = new List<Thing>();
        foreach (var thing in Candidates(map, requirePawn, context)) {
            if (members.Count >= 100000) throw ReadContext.Error("identity_scan_limit", "Identity membership exceeds 100000 objects.");
            members.Add(thing);
        }
        var matches = new List<object>();
        for (var i = 0; i < members.Count; i++) {
            if ((i & 63) == 0) { context.Check(); yield return 0; }
            if (map.Disposed) throw ReadContext.Error("object_unavailable", "Selected map was unloaded during lookup.");
            var thing = members[i];
            if (thing.Destroyed || thing.MapHeld != map) continue;
            if (id != null && !string.Equals(thing.GetUniqueLoadID(), id, StringComparison.Ordinal)) continue;
            if (name != null && !string.Equals(thing.LabelShortCap, name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(thing.LabelCap, name, StringComparison.OrdinalIgnoreCase)) continue;
            matches.Add(thing);
            if (id != null || matches.Count > MaxCandidates) break;
        }
        context.Check();
        if (matches.Count > 1) { resolved.Candidates = matches.Take(MaxCandidates).ToList(); resolved.CandidatesTruncated = matches.Count > MaxCandidates; }
        else if (matches.Count == 1) { resolved.Value = matches[0]; resolved.Map = (matches[0] as Thing)?.MapHeld; }
    }

    // Enumerate spawned identities once, then real held contents. Do not traverse
    // the map's ThingOwner again (it contains all spawned trees/rocks/plants).
    private static IEnumerable<Thing> Candidates(Map map, bool pawnsOnly, ReadContext context)
    {
        foreach (var thing in map.listerThings.ThingsInGroup(pawnsOnly ? ThingRequestGroup.Pawn : ThingRequestGroup.Everything))
            yield return thing;
        var visited = new HashSet<IThingHolder>();
        var seen = new HashSet<Thing>();
        var roots = new List<IThingHolder>();
        map.GetChildHolders(roots);
        IEnumerable<Thing> Held(IThingHolder holder, int depth)
        {
            context.Check();
            if (!visited.Add(holder)) yield break;
            if (depth > 24 || visited.Count > 16384) throw ReadContext.Error("holder_scan_limit", "Holder enumeration limit reached; identity remains unknown.");
            var contents = holder.GetDirectlyHeldThings();
            if (contents != null)
                foreach (Thing thing in contents)
                    if (!thing.Spawned && (!pawnsOnly || thing is Pawn) && seen.Add(thing) &&
                        (thing.ParentHolder == null || ThingOwnerUtility.AreImmediateContentsReal(thing.ParentHolder)))
                        yield return thing;
            var children = new List<IThingHolder>();
            holder.GetChildHolders(children);
            foreach (var child in children)
                foreach (var thing in Held(child, depth + 1)) yield return thing;
        }
        foreach (var root in roots)
            foreach (var thing in Held(root, 0)) yield return thing;
    }

    private static Map? ResolveMap(string? requested)
    {
        if (requested == null || requested == "current")
            return Find.CurrentMap ?? throw ReadContext.Error("object_unavailable", "There is no current loaded map.");
        var map = Find.Maps.FirstOrDefault(item => item != null && !item.Disposed && item.GetUniqueLoadID() == requested);
        if (map == null) throw ReadContext.Error("object_unavailable", "Selected map is not loaded: " + requested);
        return map;
    }

    private static JObject Candidate(object value) => value switch
    {
        Pawn pawn => new JObject { ["kind"] = "pawn", ["id"] = pawn.GetUniqueLoadID(), ["name"] = pawn.LabelShortCap, ["map"] = pawn.MapHeld?.GetUniqueLoadID() },
        Thing thing => DescribeThing(thing),
        _ => new JObject(),
    };

    private static JObject DescribeThing(Thing thing) => new()
    {
        ["kind"] = thing is Pawn ? "pawn" : "thing",
        ["id"] = thing.GetUniqueLoadID(),
        ["label"] = thing.LabelCap,
        ["def_name"] = thing.def.defName,
        ["map"] = thing.MapHeld?.GetUniqueLoadID(),
    };

    private static JObject DescribeCell(Map map, IntVec3 cell) => new()
    {
        ["kind"] = "cell",
        ["map"] = map.GetUniqueLoadID(),
        ["x"] = cell.x,
        ["z"] = cell.z,
    };
}
