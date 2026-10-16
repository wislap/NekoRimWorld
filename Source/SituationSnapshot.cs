using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using GraphQL;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace Neko.RimWorld;

internal sealed class SituationSnapshot : FrameQueryOperation
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo ActiveAlerts = typeof(AlertsReadout).GetField("activeAlerts", Fields)!;
    private static readonly FieldInfo CachedQuestAlert = typeof(QuestPartActivable).GetField("cachedAlert", Fields)!;
    private static readonly FieldInfo DefaultPriority = typeof(Alert).GetField("defaultPriority", Fields)!;
    private readonly Game game = Current.Game;
    private readonly SessionDescriptor session;
    private readonly JObject args;
    private readonly int startTick = Find.TickManager.TicksGame;
    private readonly JObject result = new() { ["format"] = SituationSnapshotContract.Version };
    private readonly JObject sections = new();
    private Map? map;
    private bool partial;

    internal SituationSnapshot(JObject args, SessionDescriptor session)
    {
        this.args = (JObject)args.DeepClone(); this.session = session;
        result["sections"] = sections;
        InitializeWork(Read().GetEnumerator());
    }
    protected override void RestartReadSlice() { }
    protected override void ValidateFrame()
    {
        if (Current.Game != game || map?.Disposed == true) throw ReadContext.Error("world_changed", "Loaded game or selected map changed during situation read.");
        EnsureLifetime(8, "situation_deadline", "Situation read exceeded its lifetime.");
    }
    protected override void Complete() => Finish();
    protected override void HandleError(Exception error)
    {
        if (error is ExecutionError execution && execution.Code is "invalid_arguments" or "world_changed" or "object_unavailable" or "unsupported_game_implementation")
            Outcome = QueryOutcome.Failure(execution.Code!, execution.Message);
        else { partial = true; result["read_error"] = Error(error); Finish(); }
    }
    private void Finish()
    {
        session.GameTick = Find.TickManager.TicksGame;
        result["status"] = partial ? "partial" : "ok";
        result["sample"] = new JObject { ["colony_id"] = session.ColonyId, ["load_epoch"] = session.LoadEpoch,
            ["map_id"] = map?.GetUniqueLoadID(), ["started_tick"] = startTick, ["finished_tick"] = session.GameTick,
            ["snapshot_sequence"] = session.SnapshotSequence, ["atomic"] = false };
        result["read_ms"] = ElapsedMilliseconds;
        if (Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > ProtocolCodec.MaxFrameBytes - 8192) { Outcome = QueryOutcome.Failure("situation_result_too_large", "Native situation exceeds the bridge delivery bound; no silent truncation."); return; }
        Outcome = new QueryOutcome { Status = partial ? ProtocolStatuses.Partial : ProtocolStatuses.Ok, Data = result,
            Issues = partial ? new[] { new QueryIssue { Code = "situation_partial", Message = "Inspect individual section availability and source samples.", Path = new JArray() } } : Array.Empty<QueryIssue>() };
    }
    private IEnumerable<int> Read()
    {
        SituationSnapshotContract.Validate(args);
        GameImplementation.RequireKnownBinary();
        map = GameWorldReferences.ResolveMap((string?)args["map"]);
        result["clock"] = new JObject { ["game_tick"] = startTick, ["absolute_tick"] = Find.TickManager.TicksAbs,
            ["ticks_per_day"] = GenDate.TicksPerDay, ["source"] = "Verse.TickManager" };
        result["map"] = new JObject { ["id"] = map.GetUniqueLoadID(), ["tile"] = map.Tile.ToString(), ["is_player_home"] = map.IsPlayerHome,
            ["biome"] = map.Biome?.defName, ["source"] = "Verse.Map" };
        foreach (var section in new[] { "conditions", "threats", "alerts", "quests", "events" }) {
            var row = new JObject { ["status"] = "reading", ["source"] = Source(section), ["sample_tick"] = Find.TickManager.TicksGame, ["items"] = new JArray() };
            sections[section] = row;
            using var work = ReadSection(section, row).GetEnumerator();
            while (true) {
                bool more = false; Exception? failure = null;
                try { more = work.MoveNext(); } catch (Exception error) when (ReadContext.Recoverable(error)) { failure = error; }
                if (failure != null) { row["status"] = ((JArray)row["items"]!).Count > 0 ? "partial" : "unavailable"; row["error"] = Error(failure); partial = true; break; }
                if (!more) { if ((string?)row["status"] == "reading") row["status"] = "ok"; break; }
                yield return work.Current;
            }
            row["finished_tick"] = Find.TickManager.TicksGame;
            Sort(section, row);
            if ((string?)row["status"] != "ok") partial = true;
            yield return 0;
        }
    }
    private IEnumerable<int> ReadSection(string name, JObject section)
    {
        var rows = (JArray)section["items"]!;
        switch (name) {
            case "conditions":
                foreach (var manager in new[] { map!.gameConditionManager, Find.World.gameConditionManager }.Distinct()) {
                    foreach (var condition in manager.ActiveConditions.ToArray()) {
                        var row = SituationConditionPresentation.Read(condition);
                        row.Merge(new JObject { ["id"] = condition.GetUniqueLoadID(), ["def"] = condition.def.defName,
                            ["scope"] = manager == map.gameConditionManager ? "map" : "world", ["map_id"] = manager == map.gameConditionManager ? map.GetUniqueLoadID() : null,
                            ["start_tick"] = condition.startTick, ["permanent"] = condition.Permanent,
                            ["duration_ticks"] = condition.Permanent ? null : (JToken)condition.Duration,
                            ["quest_id"] = condition.quest?.GetUniqueLoadID(),
                            ["source"] = "RimWorld.GameConditionManager.ActiveConditions + native condition instance presentation" });
                        rows.Add(row);
                        if ((string?)row["presentation"]?["status"] != "ok") Partial(section, "condition_presentation_partial");
                        yield return 0;
                    }
                }
                break;
            case "threats":
                // Hostility membership is already maintained by the native
                // attack-target cache. Do not call active-threat behavior predicates.
                var hostiles = WorldNativeAccess.HostileTargetSnapshot(map!, Faction.OfPlayer);
                var visible = new HashSet<Thing>();
                foreach (var target in hostiles) {
                    if (target.GetType().Assembly != typeof(Pawn).Assembly) { Partial(section, "unsupported_mod_attack_target"); continue; }
                    var thing = target.Thing;
                    if (!thing.Destroyed && thing.Spawned && thing.Map == map && !thing.Position.Fogged(map)) visible.Add(thing);
                    yield return 0;
                }
                section["scope"] = "Visible spawned members of native player-hostile attack-target cache on selected map; not an active-attack or safety classification.";
                var grouped = new HashSet<Thing>();
                foreach (var lord in map!.lordManager.lords.ToArray()) {
                    var members = lord.ownedPawns.Where(p => visible.Contains(p)).ToArray();
                    if (members.Length == 0) continue;
                    foreach (var member in members) grouped.Add(member);
                    rows.Add(new JObject { ["id"] = lord.GetUniqueLoadID(), ["kind"] = "hostile_group",
                        ["label"] = lord.LordJob?.GetType().Name ?? "Hostile group", ["job_type"] = lord.LordJob?.GetType().FullName,
                        ["toil_type"] = lord.CurLordToil?.GetType().FullName, ["faction_id"] = lord.faction?.GetUniqueLoadID(), ["faction_def"] = lord.faction?.def?.defName,
                        ["visible_hostile_count"] = members.Length, ["members"] = new JArray(members.Select(Identity)),
                        ["source"] = "Verse.AI.Group.LordManager.lords/ownedPawns + Verse.AttackTargetsCache" });
                    yield return 0;
                }
                foreach (var thing in visible.Where(t => !grouped.Contains(t)).OrderBy(t => t.thingIDNumber)) {
                    var pawn = thing as Pawn;
                    var row = Identity(thing); row["kind"] = "hostile_target"; row["mental_state"] = pawn?.MentalStateDef?.defName;
                    row["source"] = "Verse.AttackTargetsCache.targetsHostileToFaction"; rows.Add(row); yield return 0;
                }
                break;
            case "alerts":
                section["scope"] = "Native global UI active-alert cache, including cross-map alerts; associated targets retain their map ids.";
                section["report_hook_matched"] = SituationAlertObservations.HookMatched;
                section["observer_metrics"] = SituationAlertObservations.Metrics();
                if (Math.Max(Find.TickManager.TicksGame, Find.TutorialState.endTick) < 600) { section["status"] = "unavailable"; section["reason"] = "native_alert_cache_not_started"; break; }
                if (ActiveAlerts?.GetValue(Find.Alerts) is not List<Alert> alerts) throw ReadContext.Error("unsupported_native_layout", "Native active-alert cache unavailable.");
                var questOwners = new Dictionary<Alert, Quest>();
                foreach (var quest in Find.QuestManager.QuestsListForReading)
                    foreach (var part in quest.PartsListForReading.OfType<QuestPartActivable>())
                        if (CachedQuestAlert?.GetValue(part) is Alert owned) questOwners[owned] = quest;
                foreach (var alert in alerts.ToArray()) {
                    if (!alert.Active) continue;
                    var report = SituationAlertObservations.Read(alert);
                    var priorityGetter = alert.GetType().GetProperty(nameof(Alert.Priority))?.GetMethod;
                    // Supported native binary has no Priority overrides (see
                    // metadata audit). Do not invoke an unknown virtual getter.
                    var priority = priorityGetter?.DeclaringType == typeof(Alert) ? (AlertPriority)DefaultPriority.GetValue(alert)!
                        : (AlertPriority?)null;
                    var row = new JObject { ["id"] = report["id"]?.DeepClone(), ["runtime_type"] = alert.GetType().FullName,
                        ["label"] = alert.Label, ["priority"] = priority?.ToString(), ["priority_value"] = priority.HasValue ? (JToken)(int)priority.Value : null,
                        ["quest_id"] = questOwners.TryGetValue(alert, out var owner) ? owner.GetUniqueLoadID() : null,
                        ["id_scope"] = "load_epoch/observer_instance (native Alert has no persistent ID)",
                        ["report"] = report, ["source"] = "RimWorld.AlertsReadout.activeAlerts/Alert.cachedLabel" };
                    rows.Add(row);
                    if ((string?)report["status"] == "unavailable") Partial(section, (string?)report["reason"] ?? "native_report_unavailable");
                    yield return 0;
                }
                break;
            case "quests":
                section["scope"] = "Visible, non-dismissed native quests in NotYetAccepted or Ongoing state, colony-wide.";
                foreach (var quest in Find.QuestManager.QuestsListForReading.ToArray()) {
                    if (quest.hidden || quest.hiddenInUI || quest.dismissed || quest.State is not (QuestState.NotYetAccepted or QuestState.Ongoing)) continue;
                    rows.Add(new JObject { ["id"] = quest.GetUniqueLoadID(), ["label"] = quest.name, ["description"] = quest.description.ToString(),
                        ["state"] = quest.State.ToString(), ["root_def"] = quest.root?.defName, ["appearance_tick"] = quest.appearanceTick,
                        ["acceptance_tick"] = quest.acceptanceTick, ["acceptance_expire_tick"] = quest.acceptanceExpireTick,
                        ["parent_quest_id"] = quest.parent?.GetUniqueLoadID(), ["source"] = "RimWorld.QuestManager.QuestsListForReading/Quest.State" });
                    yield return 0;
                }
                break;
            case "events":
                section["scope"] = "Native archive letters and messages from the last game day; older letters retained only for directly linked currently actionable quests.";
                section["since_tick"] = startTick - GenDate.TicksPerDay;
                foreach (var item in Find.Archive.ArchivablesListForReading.ToArray()) {
                    var letter = item as Letter; var message = item as Message;
                    if (letter == null && message == null) continue;
                    var quest = (letter as ChoiceLetter)?.quest ?? message?.quest;
                    bool liveQuest = quest != null && !quest.hidden && !quest.hiddenInUI && !quest.dismissed && quest.State is QuestState.NotYetAccepted or QuestState.Ongoing;
                    if (item.CreatedTicksGame < startTick - GenDate.TicksPerDay && !(letter != null && liveQuest)) continue;
                    rows.Add(new JObject { ["id"] = letter?.GetUniqueLoadID() ?? message!.GetUniqueLoadID(), ["kind"] = letter != null ? "letter" : "message",
                        ["label"] = letter?.Label.ToString() ?? message!.text, ["description"] = (letter as ChoiceLetter)?.Text.ToString(),
                        ["def"] = letter?.def?.defName ?? message?.def?.defName, ["created_tick"] = item.CreatedTicksGame,
                        ["quest_id"] = quest?.GetUniqueLoadID(), ["quest_state"] = quest?.State.ToString(),
                        ["visible_letter"] = letter != null && Find.LetterStack.LettersListForReading.Contains(letter),
                        ["targets"] = SituationAlertObservations.Targets(letter?.lookTargets?.targets ?? message?.lookTargets?.targets),
                        ["source"] = "RimWorld.Archive.ArchivablesListForReading" });
                    yield return 0;
                }
                break;
        }
    }
    private static JObject Identity(Thing thing) => new() { ["id"] = thing.GetUniqueLoadID(), ["def"] = thing.def.defName,
        ["label"] = thing.def.label, ["map_id"] = thing.Map?.GetUniqueLoadID(), ["x"] = thing.Position.x, ["z"] = thing.Position.z,
        ["dead"] = thing is Pawn pawn ? (JToken)pawn.Dead : null, ["downed"] = thing is Pawn p ? (JToken)p.Downed : null };
    private static string Source(string section) => section switch { "conditions" => "RimWorld.GameConditionManager", "threats" => "Verse.AttackTargetsCache + Verse.AI.Group.LordManager", "alerts" => "RimWorld.AlertsReadout", "quests" => "RimWorld.QuestManager", _ => "RimWorld.Archive" };
    private static JObject Error(Exception error) => new() { ["code"] = (error as ExecutionError)?.Code ?? error.GetType().Name, ["message"] = error.Message };
    private static void Partial(JObject section, string reason) { section["status"] = "partial"; var reasons = section["reasons"] as JArray ?? new JArray(); section["reasons"] = reasons; if (!reasons.Any(r => (string?)r == reason)) reasons.Add(reason); }
    private static void Sort(string name, JObject section)
    {
        var rows = ((JArray)section["items"]!).OfType<JObject>();
        var ordered = name switch {
            "alerts" => rows.OrderByDescending(r => (int?)r["priority_value"] ?? -1).ThenBy(r => (string?)r["id"], StringComparer.Ordinal),
            "events" => rows.OrderByDescending(r => (int?)r["created_tick"] ?? -1).ThenBy(r => (string?)r["id"], StringComparer.Ordinal),
            "quests" => rows.OrderBy(r => (string?)r["state"] == "Ongoing" ? 0 : 1).ThenByDescending(r => (int?)r["appearance_tick"] ?? -1).ThenBy(r => (string?)r["id"], StringComparer.Ordinal),
            "conditions" => rows.OrderByDescending(r => (int?)r["start_tick"] ?? -1).ThenBy(r => (string?)r["id"], StringComparer.Ordinal),
            _ => rows.OrderBy(r => (string?)r["id"], StringComparer.Ordinal)
        };
        section["items"] = new JArray(ordered.ToArray());
        section["order"] = name switch { "alerts" => "native priority descending, id", "events" => "created_tick descending, id", "quests" => "ongoing before offers, appearance_tick descending, id", "conditions" => "start_tick descending, id", _ => "native id" };
    }
}
