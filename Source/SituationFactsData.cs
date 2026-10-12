using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI.Group;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>Lazy native situation facts. All native references are query-local.</summary>
internal static class SituationFactsData
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo? ActiveAlerts = typeof(AlertsReadout).GetField("activeAlerts", Fields);
    private static readonly FieldInfo? CachedActive = typeof(Alert).GetField("cachedActive", Fields);
    private static readonly FieldInfo? CachedLabel = typeof(Alert).GetField("cachedLabel", Fields);
    private static readonly FieldInfo? DefaultPriority = typeof(Alert).GetField("defaultPriority", Fields);
    private static readonly FieldInfo? PartAlert = typeof(QuestPartActivable).GetField("cachedAlert", Fields);
    private sealed class State
    {
        internal readonly Map Map;
        internal readonly Dictionary<Quest, string> QuestStates = new();
        internal Dictionary<Alert, Quest?>? AlertOwners;
        internal State(Map map) { Map = map; }
        internal string QuestState(Quest quest)
        {
            if (QuestStates.TryGetValue(quest, out var state)) return state;
            GameImplementation.RequireInspected((typeof(Quest), "get_State"));
            state = quest.State.ToString(); QuestStates[quest] = state; return state;
        }
    }
    private sealed class ConditionRef { internal GameCondition Value = null!; internal string Scope = ""; }
    private sealed class EventRef { internal IArchivable Value = null!; internal State State = null!; }
    private sealed class AlertRef { internal Alert Value = null!; internal State State = null!; }
    private sealed class QuestRef { internal Quest Value = null!; internal State State = null!; }
    private sealed class Subject
    {
        internal string? Id, Def, Map, Label;
        internal string Kind = "";
        internal int? Count, X, Z;
    }
    internal static bool Supports(GameNode node) => node.Kind.StartsWith("ObjectSituation", StringComparison.Ordinal);
    internal static object Root(IReadOnlyDictionary<string, object?> args, ReadContext context)
    {
        context.Check(); GameImplementation.RequireKnownBinary();
        if (SituationContract.EventWindowTicks != GenDate.TicksPerDay)
            throw ReadContext.Error("unsupported_game_implementation", "Situation window differs from one game day.");
        var map = SpatialQuery.ResolveMap(args.TryGetValue("mapId", out var value) ? value as string : "current")!;
        var owner = new GameEntity(map.GetUniqueLoadID(), map.GetUniqueLoadID(), map);
        return new GameNode("ObjectSituation", owner.Id + "/situation", owner, new State(map));
    }
    private static GameNode Child(GameNode parent, string kind, string key, object value) => new(kind, parent.Key + "/" + key, parent.Owner, value,
        value is AlertRef alert ? alert.Value : value is EventRef entry ? entry.Value : value is ConditionRef condition ? condition.Value : value is QuestRef quest ? quest.Value : null);
    private static Quest? DirectQuest(IArchivable entry) => entry is ChoiceLetter letter ? letter.quest : (entry as Message)?.quest;
    private static GameNode? QuestNode(GameNode parent, Quest? quest, State state) => quest == null || quest.hidden || quest.hiddenInUI
        ? null : Child(parent, "ObjectSituationQuest", quest.GetUniqueLoadID(), new QuestRef { Value = quest, State = state });
    internal static object? Read(GameNode node, string field, IReadOnlyDictionary<string, object?> args, ReadContext context)
    {
        context.Check();
        switch (node.Kind)
        {
            case "ObjectSituation":
                var state = (State)node.Handle!;
                return field switch {
                    "clock" => Child(node, "ObjectSituationClock", "clock", state),
                    "conditions" => Conditions(node, state, context),
                    "threatGroups" => Groups(node, state, context),
                    "events" => Events(node, state, context),
                    "alerts" => Alerts(node, state, context),
                    "comparison" => Child(node,"ObjectSituationComparison","comparison",state),
                    _ => Unknown(node, field),
                };
            case "ObjectSituationClock": return Clock((State)node.Handle!, field);
            case "ObjectSituationComparison": return field switch {
                "available"=>false,"reason"=>"plugin_derived","meaning"=>"Comparison requires the selected saved source observations.",
                "baselineObservationId" or "baselineTick"=>null,"comparedSections" or "excludedSections" or "newItemIds" or "newSourceIds"=>Array.Empty<string>(),
                _=>Unknown(node,field)
            };
            case "ObjectSituationCondition":
                var condition = (ConditionRef)node.Handle!; var c = condition.Value;
                return field switch {
                    "id" => c.GetUniqueLoadID(), "defName" => c.def.defName, "label" => Text(c.LabelCap),
                    "scope" => condition.Scope, "sinceTick" => c.startTick, "permanent" => c.Permanent,
                    "ticksLeft" => c.Permanent ? null : c.TicksLeft, _ => Unknown(node, field),
                };
            case "ObjectSituationThreatGroup":
                var lord = (Lord)node.Handle!;
                return field switch {
                    "id" => lord.GetUniqueLoadID(), "factionId" => lord.faction?.GetUniqueLoadID(),
                    "factionName" => lord.faction?.Name, "factionDef" => lord.faction?.def?.defName,
                    "lordJob" => lord.LordJob?.GetType().Name, "toil" => lord.CurLordToil?.GetType().Name,
                    "members" => Subjects(node, VisibleMembers(lord), context), _ => Unknown(node, field),
                };
            case "ObjectSituationEvent":
                var e = (EventRef)node.Handle!; var entry = e.Value;
                return field switch {
                    "id" => ((ILoadReferenceable)entry).GetUniqueLoadID(), "kind" => entry is Letter ? "letter" : "message",
                    "defName" => entry is Letter l ? l.def?.defName : (entry as Message)?.def?.defName,
                    "label" => Text(entry.ArchivedLabel), "tick" => entry.CreatedTicksGame,
                    "pinned" => Find.Archive.IsPinned(entry), "quest" => QuestNode(node, DirectQuest(entry), e.State),
                    "subjects" => Targets(node, entry.LookTargets, context), _ => Unknown(node, field),
                };
            case "ObjectSituationAlert":
                var alert = (AlertRef)node.Handle!;
                return field switch {
                    "type" => alert.Value.GetType().FullName, "label" => Text((string?)CachedLabel!.GetValue(alert.Value)),
                    "active" => (bool)CachedActive!.GetValue(alert.Value), "priority" => Priority(alert.Value),
                    "quest" => QuestNode(node, AlertOwner(alert.Value, alert.State, context), alert.State),
                    _ => Unknown(node, field),
                };
            case "ObjectSituationQuest":
                var q = (QuestRef)node.Handle!;
                return field switch { "id" => q.Value.GetUniqueLoadID(), "state" => q.State.QuestState(q.Value), _ => Unknown(node, field) };
            case "ObjectSituationSubject":
                var s = (Subject)node.Handle!;
                return field switch { "id" => s.Id, "kind" => s.Kind, "defName" => s.Def, "count" => s.Count,
                    "mapId" => s.Map, "x" => s.X, "z" => s.Z, "label" => s.Label, _ => Unknown(node, field) };
            default: return Unknown(node, field);
        }
    }
    private static object? Clock(State state, string field)
    {
        if (field == "ticksGame") return Find.TickManager.TicksGame;
        if (field == "paused") return Find.TickManager.Paused;
        var ticks = Find.TickManager.TicksAbs; var longLat = Find.WorldGrid.LongLatOf(state.Map.Tile);
        return field switch { "year" => GenDate.Year(ticks, longLat.x), "quadrum" => GenDate.Quadrum(ticks, longLat.x).ToString(),
            "dayOfQuadrum" => GenDate.DayOfQuadrum(ticks, longLat.x) + 1, "hour" => GenDate.HourOfDay(ticks, longLat.x),
            "season" => GenLocalDate.Season(state.Map).ToString(), "dateText" => GenDate.DateFullStringWithHourAt(ticks, longLat),
            _ => throw ReadContext.Error("unknown_field", "Unknown clock field.") };
    }
    private static IEnumerable<GameNode> Conditions(GameNode parent, State state, ReadContext context)
    {
        foreach (var source in new[] { (state.Map.gameConditionManager, "map"), (Find.World.gameConditionManager, "world") })
            if (source.Item1 != null) foreach (var condition in source.Item1.ActiveConditions) {
                context.Check(); if (condition?.def == null) continue;
                yield return Child(parent, "ObjectSituationCondition", source.Item2 + "/" + condition.GetUniqueLoadID(), new ConditionRef { Value = condition, Scope = source.Item2 });
            }
    }
    private static IEnumerable<Thing> VisibleMembers(Lord lord) => lord.ownedPawns.OfType<Thing>().Concat(lord.ownedBuildings)
        .Where(t => t.Spawned && !t.Fogged());
    private static IEnumerable<GameNode> Groups(GameNode parent, State state, ReadContext context)
    {
        GameImplementation.RequireInspected((typeof(FactionUtility), nameof(FactionUtility.HostileTo)));
        foreach (var lord in state.Map.lordManager.lords) {
            context.Check(); if (lord?.faction == null || !lord.faction.HostileTo(Faction.OfPlayer) || !VisibleMembers(lord).Any()) continue;
            yield return Child(parent, "ObjectSituationThreatGroup", lord.GetUniqueLoadID(), lord);
        }
    }
    private static IEnumerable<GameNode> Events(GameNode parent, State state, ReadContext context)
    {
        var now = Find.TickManager.TicksGame;
        foreach (var entry in Find.Archive.ArchivablesListForReading) {
            context.Check();
            if (entry is not Letter && (entry is not Message m || m.def != MessageTypeDefOf.ThreatBig && m.def != MessageTypeDefOf.ThreatSmall)) continue;
            var quest = DirectQuest(entry); string? questState = null;
            if (now - entry.CreatedTicksGame > SituationContract.EventWindowTicks && quest != null && !quest.hidden && !quest.hiddenInUI)
                questState = state.QuestState(quest);
            if (!SituationContract.RetainEvent(now, entry.CreatedTicksGame, questState)) continue;
            yield return Child(parent, "ObjectSituationEvent", ((ILoadReferenceable)entry).GetUniqueLoadID(), new EventRef { Value = entry, State = state });
        }
    }
    private static IEnumerable<GameNode> Alerts(GameNode parent, State state, ReadContext context)
    {
        var readout = Find.Alerts;
        if (readout == null || ActiveAlerts == null || CachedActive == null || CachedLabel == null || DefaultPriority == null)
            throw ReadContext.Error("alert_cache_unavailable", "The native alert cache is unavailable.");
        if (Math.Max(Find.TickManager.TicksGame, Find.TutorialState.endTick) < 600)
            throw ReadContext.Error("alert_cache_start_delay", "The game has not reached its alert cache update threshold.");
        if (Find.Storyteller.def.disableAlerts) throw ReadContext.Error("alerts_disabled_by_storyteller", "The storyteller disables alerts.");
        if (ActiveAlerts.GetValue(readout) is not List<Alert> active) throw ReadContext.Error("alert_cache_unavailable", "Invalid native alert cache.");
        for (var i = 0; i < active.Count; i++) {
            context.Check(); var alert = active[i]; if (alert == null || !(bool)CachedActive.GetValue(alert)) continue;
            yield return Child(parent, "ObjectSituationAlert", "alert:" + i, new AlertRef { Value = alert, State = state });
        }
    }
    private static object Priority(Alert alert)
    {
        if (alert.GetType().GetProperty(nameof(Alert.Priority))?.GetMethod?.DeclaringType != typeof(Alert))
            throw ReadContext.Error("priority_unavailable", "An overridden priority getter was not evaluated.");
        return DefaultPriority!.GetValue(alert).ToString()!;
    }
    private static Quest? AlertOwner(Alert alert, State state, ReadContext context)
    {
        if (PartAlert == null) throw ReadContext.Error("quest_relation_unavailable", "The cached Quest alert relation is unavailable.");
        if (state.AlertOwners == null) {
            state.AlertOwners = new Dictionary<Alert, Quest?>();
            if (Find.QuestManager != null) foreach (var quest in Find.QuestManager.QuestsListForReading)
                foreach (var part in quest.PartsListForReading) {
                    context.Check();
                    if (part is not QuestPartActivable || PartAlert.GetValue(part) is not Alert cached) continue;
                    if (state.AlertOwners.TryGetValue(cached, out var prior) && !ReferenceEquals(prior, quest)) state.AlertOwners[cached] = null;
                    else state.AlertOwners[cached] = quest;
                }
        }
        if (!state.AlertOwners.TryGetValue(alert, out var owner)) return null;
        if (owner == null) throw ReadContext.Error("quest_relation_ambiguous", "The cached alert belongs to multiple tasks.");
        return owner;
    }
    private static IEnumerable<GameNode> Subjects(GameNode parent, IEnumerable<Thing> values, ReadContext context)
    {
        var groups = new Dictionary<string, List<Thing>>(StringComparer.Ordinal);
        foreach (var thing in values.Distinct()) {
            context.Check();
            if (thing is Pawn pawn && pawn.RaceProps?.Humanlike == true) {
                yield return Child(parent, "ObjectSituationSubject", thing.GetUniqueLoadID(), new Subject { Id = thing.GetUniqueLoadID(), Kind = "humanlike" });
            } else {
                var def = thing.def?.defName ?? "unknown";
                if (!groups.TryGetValue(def, out var group)) groups[def] = group = new List<Thing>();
                group.Add(thing);
            }
        }
        foreach (var group in groups) yield return Child(parent, "ObjectSituationSubject", group.Key, new Subject {
            Kind = "definition_group", Def = group.Key, Count = group.Value.Count, Id = group.Value.Count == 1 ? group.Value[0].GetUniqueLoadID() : null });
    }
    private static IEnumerable<GameNode> Targets(GameNode parent, LookTargets? targets, ReadContext context)
    {
        var things = new List<Thing>(); var other = new List<Subject>();
        if (targets?.targets != null) foreach (var target in targets.targets) {
            context.Check(); if (!target.IsValid) continue;
            if (target.HasThing) things.Add(target.Thing);
            else if (target.HasWorldObject) other.Add(new Subject { Kind = "world_object", Id = target.WorldObject.GetUniqueLoadID(), Label = Text(target.WorldObject.Label) });
            else if (target.Map != null) other.Add(new Subject { Kind = target.Cell.IsValid ? "cell" : "map", Map = target.Map.GetUniqueLoadID(),
                X = target.Cell.IsValid ? target.Cell.x : null, Z = target.Cell.IsValid ? target.Cell.z : null });
        }
        foreach (var subject in Subjects(parent, things, context)) yield return subject;
        for (var i = 0; i < other.Count; i++) yield return Child(parent, "ObjectSituationSubject", "target:" + i, other[i]);
    }
    private static string Text(string? value) => value == null ? "" : ColoredText.StripTags(value).Trim();
    private static object Unknown(GameNode node, string field) => throw ReadContext.Error("unknown_field", "Unknown situation fact " + node.Kind + "." + field);
}
