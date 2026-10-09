using System;
using System.Collections.Generic;
using System.Linq;
using GraphQL;
using Newtonsoft.Json.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace Neko.RimWorld;

internal static class SituationObservation
{
    internal static QueryOutcome Execute(JObject arguments, SessionDescriptor session)
    {
        var started = Find.TickManager.TicksGame;
        var result = new JObject();
        try {
            SituationContract.Validate(arguments);
            GameImplementation.RequireKnownBinary();
            if (SituationContract.EventWindowTicks != GenDate.TicksPerDay) throw ReadContext.Error("unsupported_game_implementation", "Situation event window no longer matches one game day");
            var map = SpatialQuery.ResolveMap((string?)arguments["map"])!;
            result["map"] = new JObject { ["id"] = map.GetUniqueLoadID(), ["is_player_home"] = map.IsPlayerHome };
            result["clock"] = Clock(map);
            result["conditions"] = Conditions(map);
            ReadThreatGroups(map, result);
            result["events"] = Events();
        } catch (ExecutionError error) when (error.Code is "invalid_arguments" or "object_unavailable" or "unsupported_game_implementation") {
            return QueryOutcome.Failure(error.Code!, error.Message);
        }
        result["scope"] = new JObject {
            ["conditions"] = "Active GameConditions of the selected map and of the world; one entry per condition.",
            ["threat_groups"] = "One entry per Lord whose faction is hostile to the player and that has visible spawned members on the selected map. Fogged members are not listed. Hostile pawns outside a Lord, such as manhunter animals, are not threat groups; use inspect_threats for current hostile targets.",
            ["events"] = "Game archive entries created within the last " + SituationContract.EventWindowTicks + " ticks: every letter, and messages of threat types only. The game culls old unpinned archive entries; culled entries are unavailable, not absent.",
            ["subjects"] = "Humanlike pawns are listed by ID. Other things are grouped by definition with a count; a group of one carries its ID. Cells and world objects are listed directly."
        };
        var finished = Find.TickManager.TicksGame;
        session.GameTick = finished;
        var partial = result["threat_groups_unavailable"] != null;
        result["status"] = partial ? "partial" : "ok";
        result["read_only"] = true;
        result["sample"] = new JObject { ["load_epoch"] = session.LoadEpoch, ["started_tick"] = started, ["finished_tick"] = finished, ["same_tick"] = started == finished, ["atomic"] = false };
        return partial
            ? QueryOutcome.Partial(result, "situation_partial", "Some situation sections could not be read; inspect the *_unavailable fields.")
            : new QueryOutcome { Status = ProtocolStatuses.Ok, Data = result };
    }

    private static JObject Clock(Map map)
    {
        long ticks = Find.TickManager.TicksAbs;
        var longLat = Find.WorldGrid.LongLatOf(map.Tile);
        return new JObject {
            ["ticks_game"] = Find.TickManager.TicksGame,
            ["paused"] = Find.TickManager.Paused,
            ["year"] = GenDate.Year(ticks, longLat.x),
            ["quadrum"] = GenDate.Quadrum(ticks, longLat.x).ToString(),
            ["day_of_quadrum"] = GenDate.DayOfQuadrum(ticks, longLat.x) + 1,
            ["hour"] = GenDate.HourOfDay(ticks, longLat.x),
            ["season"] = GenLocalDate.Season(map).ToString(),
            ["date_text"] = GenDate.DateFullStringWithHourAt(ticks, longLat),
        };
    }

    private static JArray Conditions(Map map)
    {
        var rows = new JArray();
        void Add(GameConditionManager? manager, string scope) {
            if (manager == null) return;
            foreach (var condition in manager.ActiveConditions) {
                if (condition?.def == null) continue;
                rows.Add(new JObject {
                    ["def"] = condition.def.defName,
                    ["label"] = Text(condition.LabelCap),
                    ["scope"] = scope,
                    ["since_tick"] = condition.startTick,
                    ["permanent"] = condition.Permanent,
                    ["ticks_left"] = condition.Permanent ? null : (JToken)condition.TicksLeft,
                });
            }
        }
        Add(map.gameConditionManager, "map");
        Add(Find.World?.gameConditionManager, "world");
        return rows;
    }

    private static void ReadThreatGroups(Map map, JObject result)
    {
        try {
            GameImplementation.RequireInspected((typeof(FactionUtility), nameof(FactionUtility.HostileTo)));
        } catch (ExecutionError error) {
            result["threat_groups"] = null;
            result["threat_groups_unavailable"] = new JObject { ["reason"] = error.Code, ["message"] = error.Message };
            return;
        }
        var rows = new JArray();
        foreach (var lord in map.lordManager.lords) {
            if (lord?.faction == null || !lord.faction.HostileTo(Faction.OfPlayer)) continue;
            var visible = lord.ownedPawns.Where(p => p != null && p.Spawned && p.Map == map && !p.Fogged()).Cast<Thing>()
                .Concat(lord.ownedBuildings.Where(b => b != null && b.Spawned && b.Map == map && !b.Fogged())).ToList();
            if (visible.Count == 0) continue;
            rows.Add(new JObject {
                ["lord"] = lord.GetUniqueLoadID(),
                ["faction"] = new JObject { ["id"] = lord.faction.GetUniqueLoadID(), ["name"] = lord.faction.Name, ["def"] = lord.faction.def?.defName },
                ["lord_job"] = lord.LordJob?.GetType().Name,
                ["toil"] = lord.CurLordToil?.GetType().Name,
                ["members"] = Subjects(visible),
            });
        }
        result["threat_groups"] = rows;
    }

    private static JArray Events()
    {
        var archive = Find.Archive;
        var now = Find.TickManager.TicksGame;
        var rows = new JArray();
        foreach (var entry in archive.ArchivablesListForReading) {
            if (entry == null) continue;
            var tick = entry.CreatedTicksGame;
            if (now - tick > SituationContract.EventWindowTicks) continue;
            string kind, def;
            if (entry is Letter letter) { kind = "letter"; def = letter.def?.defName ?? "unknown"; }
            else if (entry is Message message && (message.def == MessageTypeDefOf.ThreatBig || message.def == MessageTypeDefOf.ThreatSmall)) { kind = "message"; def = message.def.defName; }
            else continue;
            rows.Add(new JObject {
                ["kind"] = kind, ["def"] = def, ["label"] = Text(entry.ArchivedLabel), ["tick"] = tick,
                ["pinned"] = archive.IsPinned(entry), ["subjects"] = Targets(entry.LookTargets),
            });
        }
        return rows;
    }

    private static JArray Targets(LookTargets? targets)
    {
        var things = new List<Thing>(); var others = new JArray();
        if (targets?.targets != null) foreach (var target in targets.targets) {
            if (!target.IsValid) continue;
            if (target.HasThing) things.Add(target.Thing);
            else if (target.HasWorldObject) others.Add(new JObject { ["world_object"] = target.WorldObject.GetUniqueLoadID(), ["label"] = Text(target.WorldObject.Label) });
            else if (target.Map != null && target.Cell.IsValid) others.Add(new JObject { ["map"] = target.Map.GetUniqueLoadID(), ["x"] = target.Cell.x, ["z"] = target.Cell.z });
            else if (target.Map != null) others.Add(new JObject { ["map"] = target.Map.GetUniqueLoadID() });
        }
        var rows = Subjects(things);
        foreach (var other in others) rows.Add(other);
        return rows;
    }

    private static JArray Subjects(IEnumerable<Thing> things)
    {
        var rows = new JArray();
        var groups = new List<(string Def, List<Thing> Members)>();
        foreach (var thing in things.Distinct()) {
            if (thing is Pawn pawn && pawn.RaceProps?.Humanlike == true) { rows.Add(pawn.GetUniqueLoadID()); continue; }
            var def = thing.def?.defName ?? "unknown";
            var index = groups.FindIndex(g => g.Def == def);
            if (index < 0) groups.Add((def, new List<Thing> { thing })); else groups[index].Members.Add(thing);
        }
        foreach (var (def, members) in groups) {
            var row = new JObject { ["def"] = def, ["count"] = members.Count };
            if (members.Count == 1) row["id"] = members[0].GetUniqueLoadID();
            rows.Add(row);
        }
        return rows;
    }

    private static string Text(string? value) => value == null ? "" : ColoredText.StripTags(value).Trim();
}
