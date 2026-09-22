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

    internal static QueryOutcome Execute(JObject arguments, SessionDescriptor session)
    {
        try
        {
            CheckKeys(arguments, "spatial_query", "from", "to");
            var context = new ReadContext(new VerseGameData(), session, ReadBudgetMilliseconds);
            var from = ParseReference(arguments, "from", context, requirePawn: true);
            var to = ParseReference(arguments, "to", context, requirePawn: false);
            var result = new JObject
            {
                ["answer"] = "unknown",
                ["sample"] = new JObject
                {
                    ["load_epoch"] = session.LoadEpoch,
                    ["game_tick"] = session.GameTick,
                    ["snapshot_sequence"] = session.SnapshotSequence,
                },
                ["checked"] = new JObject
                {
                    ["actor_resolved"] = from.Value is Pawn,
                    ["target_resolved"] = to.Value is Thing || to.Cell.HasValue,
                    ["same_map"] = false,
                    ["native_reachability"] = false,
                },
            };

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
                result["explanation"] = "The actor could not be resolved in the loaded maps.";
                return Ok(result);
            }
            if (to.Value == null && !to.Cell.HasValue)
            {
                result["answer"] = "not_found";
                result["explanation"] = "The target could not be resolved in the loaded maps.";
                return Ok(result);
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
                    result["explanation"] = "The selected Thing is not spawned on a loaded map; containment is not a walk target.";
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
        catch (ExecutionError error)
        {
            if (error.Code is "deadline_exceeded" or "wrong_thread")
            {
                var data = new JObject
                {
                    ["answer"] = "unknown",
                    ["explanation"] = error.Message,
                };
                return new QueryOutcome
                {
                    Status = ProtocolStatuses.Partial,
                    Data = data,
                    Issues = new[] { new QueryIssue { Code = error.Code ?? "spatial_unavailable", Message = error.Message, Path = new JArray() } },
                };
            }
            return QueryOutcome.Failure(error.Code ?? "invalid_arguments", error.Message);
        }
        catch (Exception exception) when (ReadContext.Recoverable(exception))
        {
            return QueryOutcome.Failure("spatial_unavailable", "The native spatial read could not complete.", ProtocolStatuses.Failed);
        }
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

    private static Reference ParseReference(JObject arguments, string key, ReadContext context, bool requirePawn)
    {
        if (arguments[key] is not JObject raw) throw ReadContext.Error("invalid_arguments", key + " must be an object.");
        CheckKeys(raw, key, "kind", "id", "name", "map", "x", "z");
        var kind = (string?)raw["kind"];
        if (requirePawn && kind != "pawn") throw ReadContext.Error("invalid_arguments", "from.kind must be pawn.");
        if (!requirePawn && kind is not ("thing" or "cell")) throw ReadContext.Error("invalid_arguments", "to.kind must be thing or cell.");
        var map = ResolveMap((string?)raw["map"]);
        if (kind == "cell")
        {
            if (raw["id"] != null || raw["name"] != null) throw ReadContext.Error("invalid_arguments", key + ".cell cannot include id or name.");
            if (map == null) return new Reference();
            if (raw["x"]?.Type != JTokenType.Integer || raw["z"]?.Type != JTokenType.Integer)
                throw ReadContext.Error("invalid_arguments", "cell references require integer x and z.");
            return new Reference { Map = map, Cell = new IntVec3((int)raw["x"]!, 0, (int)raw["z"]!) };
        }
        var id = (string?)raw["id"];
        var name = (string?)raw["name"];
        if (raw["x"] != null || raw["z"] != null) throw ReadContext.Error("invalid_arguments", key + " coordinates require kind=cell.");
        if (id != null && name != null) throw ReadContext.Error("invalid_arguments", key + " accepts either id or name, not both.");
        if (id == null && name == null) throw ReadContext.Error("invalid_arguments", key + " requires id, name, or cell coordinates.");
        var matches = new List<object>();
        var queryArgs = new Dictionary<string, object?> { ["includeHeld"] = true, ["visibleOnly"] = false };
        if (map != null) queryArgs["mapId"] = map.GetUniqueLoadID();
        var group = kind == "pawn" ? ThingRequestGroup.Pawn : ThingRequestGroup.Everything;
        foreach (var node in ThingData.QueryThings(queryArgs, context, requestGroup: group))
        {
            if (node.Handle is not Thing thing) continue;
            if (id != null && !string.Equals(thing.GetUniqueLoadID(), id, StringComparison.Ordinal)) continue;
            if (name != null && !string.Equals(thing.LabelShortCap, name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(thing.LabelCap, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (kind == "pawn" && thing is not Pawn) continue;
            matches.Add(thing);
            if (id != null) break;
            if (matches.Count >= MaxCandidates) break;
        }
        if (matches.Count == 0) return new Reference();
        if (id == null && matches.Count > 1) return new Reference { Candidates = matches, CandidatesTruncated = matches.Count >= MaxCandidates };
        var selected = matches[0];
        return new Reference { Value = selected, Map = (selected as Thing)?.MapHeld };
    }

    private static Map? ResolveMap(string? requested)
    {
        if (requested == null || requested == "current") return Find.CurrentMap;
        var map = Find.Maps.FirstOrDefault(item => item != null && !item.Disposed && item.GetUniqueLoadID() == requested);
        if (map == null) throw ReadContext.Error("object_unavailable", "Selected map is not loaded: " + requested);
        return map;
    }

    private static void CheckKeys(JObject value, string path, params string[] allowed)
    {
        var unknown = value.Properties().FirstOrDefault(property => !allowed.Contains(property.Name, StringComparer.Ordinal));
        if (unknown != null) throw ReadContext.Error("invalid_arguments", path + " contains unsupported field " + unknown.Name + ".");
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
