using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
            result["events"] = Events(result);
            ReadAlerts(result);
        } catch (ExecutionError error) when (error.Code is "invalid_arguments" or "object_unavailable" or "unsupported_game_implementation") {
            return QueryOutcome.Failure(error.Code!, error.Message);
        }
        result["scope"] = new JObject {
            ["conditions"] = "Active GameConditions of the selected map and of the world; one entry per condition.",
            ["threat_groups"] = "One entry per Lord whose faction is hostile to the player and that has visible spawned members on the selected map. Fogged members are not listed. Hostile pawns outside a Lord, such as manhunter animals, are not threat groups; use inspect_threats for current hostile targets.",
            ["events"] = "Game archive entries created within the last " + SituationContract.EventWindowTicks + " ticks: every letter, and messages of threat types only. Older entries remain only with a direct NotYetAccepted/Ongoing Quest reference. Native Quest state is not an inferred incident resolution. The game culls old unpinned archive entries; culled entries are unavailable, not absent.",
            ["alerts"] = "Global game UI active-alert cache (all maps/caravans as defined by each alert), not a selected-map scan or a fresh evaluation. Cache updates are staggered across frames; no report, explanation, culprit computation or refresh is invoked. A unique direct QuestPart cached-alert reference may attach its visible Quest; shared labels/targets are not relations. Missing alerts do not establish absence of problems. No stable native alert ID or start time exists in this cache.",
            ["subjects"] = "Humanlike pawns are listed by ID. Other things are grouped by definition with a count; a group of one carries its ID. Cells and world objects are listed directly."
        };
        var finished = Find.TickManager.TicksGame;
        session.GameTick = finished;
        var partial = result["threat_groups_unavailable"] != null || result["alerts_unavailable"] != null || result["quest_state_unavailable"] != null;
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
                    ["id"] = condition.GetUniqueLoadID(),
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
                ["id"] = lord.GetUniqueLoadID(),
                ["lord"] = lord.GetUniqueLoadID(),
                ["faction"] = new JObject { ["id"] = lord.faction.GetUniqueLoadID(), ["name"] = lord.faction.Name, ["def"] = lord.faction.def?.defName },
                ["lord_job"] = lord.LordJob?.GetType().Name,
                ["toil"] = lord.CurLordToil?.GetType().Name,
                ["members"] = Subjects(visible),
            });
        }
        result["threat_groups"] = rows;
    }

    private static JArray Events(JObject result)
    {
        var archive = Find.Archive;
        var now = Find.TickManager.TicksGame;
        var rows = new JArray();
        foreach (var entry in archive.ArchivablesListForReading) {
            if (entry == null) continue;
            var tick = entry.CreatedTicksGame;
            string kind, def;
            if (entry is Letter letter) { kind = "letter"; def = letter.def?.defName ?? "unknown"; }
            else if (entry is Message message && (message.def == MessageTypeDefOf.ThreatBig || message.def == MessageTypeDefOf.ThreatSmall)) { kind = "message"; def = message.def.defName; }
            else continue;
            var quest = entry is ChoiceLetter choice ? choice.quest : (entry as Message)?.quest;
            var questInfo = quest != null ? VisibleQuest(quest, result) : null;
            if (!SituationContract.RetainEvent(now, tick, (string?)questInfo?["state"])) continue;
            var row = new JObject {
                ["id"] = ((ILoadReferenceable)entry).GetUniqueLoadID(),
                ["kind"] = kind, ["def"] = def, ["label"] = Text(entry.ArchivedLabel), ["tick"] = tick,
                ["pinned"] = archive.IsPinned(entry), ["subjects"] = Targets(entry.LookTargets),
            };
            if (questInfo != null) row["quest"] = questInfo;
            rows.Add(row);
        }
        return rows;
    }

    // Field reads intentionally bypass arbitrary Mod getters/GetReport. The audited
    // Alert.Recalculate caches only active/label, not a generic culprit report.
    private static void ReadAlerts(JObject result)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var readout = Find.Alerts;
        var activeField = typeof(AlertsReadout).GetField("activeAlerts", fields);
        var cachedActive = typeof(Alert).GetField("cachedActive", fields);
        var cachedLabel = typeof(Alert).GetField("cachedLabel", fields);
        var priority = typeof(Alert).GetField("defaultPriority", fields);
        var partAlert = typeof(QuestPartActivable).GetField("cachedAlert", fields);
        if (readout == null || activeField == null || cachedActive == null || cachedLabel == null || priority == null) {
            result["alerts"] = null;
            result["alerts_unavailable"] = new JObject { ["reason"] = "alert_cache_unavailable" };
            return;
        }
        if (Math.Max(Find.TickManager.TicksGame, Find.TutorialState.endTick) < 600) {
            result["alerts"] = null;
            result["alerts_unavailable"] = new JObject { ["reason"] = "alert_cache_start_delay", ["message"] = "The game has not reached the alert cache update threshold (600 ticks)." };
            return;
        }
        if (Find.Storyteller.def.disableAlerts) {
            result["alerts"] = null;
            result["alerts_unavailable"] = new JObject { ["reason"] = "alerts_disabled_by_storyteller" };
            return;
        }
        if (activeField.GetValue(readout) is not List<Alert> active) {
            result["alerts"] = null;
            result["alerts_unavailable"] = new JObject { ["reason"] = "alert_cache_unavailable" };
            return;
        }
        var rows = new JArray();
        foreach (var alert in active) {
            if (alert == null || !(bool)cachedActive.GetValue(alert)) continue;
            var row = new JObject {
                ["type"] = alert.GetType().FullName,
                ["label"] = Text((string?)cachedLabel.GetValue(alert)),
                ["active"] = true,
            };
            // Vanilla inherits this field-only getter; arbitrary overrides are
            // not invoked and their default field must not masquerade as priority.
            if (alert.GetType().GetProperty(nameof(Alert.Priority))?.GetMethod?.DeclaringType == typeof(Alert))
                row["priority"] = priority.GetValue(alert).ToString();
            else row["priority_unavailable"] = "overridden_priority_not_evaluated";
            var quest = OwningQuest(alert, partAlert);
            var questInfo = quest != null ? VisibleQuest(quest, result) : null;
            if (questInfo != null) row["quest"] = questInfo;
            rows.Add(row);
        }
        result["alerts"] = rows;
    }

    private static Quest? OwningQuest(Alert alert, FieldInfo? partAlert)
    {
        if (partAlert == null || Find.QuestManager == null) return null;
        Quest? owner = null;
        foreach (var quest in Find.QuestManager.QuestsListForReading) {
            foreach (var part in quest.PartsListForReading) {
                // Never invoke QuestPartActivable.CachedAlert: its getter
                // computes reports and may create/replace the cached Alert.
                if (part is not QuestPartActivable || !ReferenceEquals(partAlert.GetValue(part), alert)) continue;
                if (owner != null && !ReferenceEquals(owner, quest)) return null;
                owner = quest;
            }
        }
        return owner;
    }

    private static JObject? VisibleQuest(Quest quest, JObject result)
    {
        if (quest.hidden || quest.hiddenInUI) return null;
        // Use native state and existing dependency/patch guards; never recreate
        // expiry/outcome rules. A failure stays explicit for the whole read.
        if (result["quest_state_unavailable"] == null) {
            try { GameImplementation.RequireInspected((typeof(Quest), "get_State")); }
            catch (ExecutionError error) {
                result["quest_state_unavailable"] = new JObject { ["reason"] = error.Code, ["message"] = error.Message };
            }
        }
        var row = new JObject { ["id"] = quest.GetUniqueLoadID(), ["state"] = null };
        if (result["quest_state_unavailable"] is JObject unavailable) row["state_unavailable"] = unavailable.DeepClone();
        else row["state"] = quest.State.ToString();
        return row;
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
