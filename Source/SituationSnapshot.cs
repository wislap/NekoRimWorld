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
        result["attention_snapshot"] = AttentionFacts.SnapshotFromSections(sections, (JObject)result["sample"]!);
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
            ["ticks_per_day"] = GenDate.TicksPerDay, ["source"] = "Verse.TickManager",
            ["paused"] = Find.TickManager.Paused, ["speed_paused"] = Find.TickManager.CurTimeSpeed == TimeSpeed.Paused,
            ["force_paused"] = Find.TickManager.ForcePaused, ["windows_force_paused"] = Find.WindowStack.WindowsForcePause,
            ["force_pause_window"] = Find.WindowStack.Windows.FirstOrDefault(w => w.forcePause)?.GetType().FullName,
            ["pause_windows"] = NativePauseWindows.Capture(),
            ["pause_state"] = game.GetComponent<NekoGameComponent>()?.PauseEvidence() ?? new JObject { ["status"] = "unavailable" },
            ["automatic_pause_mode"] = Prefs.AutomaticPauseMode.ToString(),
            ["pause_meaning"] = "paused is native Paused: speed_paused or force_paused. force_paused also covers long events, tile picking and gravship cutscenes besides force-pause windows. Letter auto-pause sets the speed to paused, so state alone cannot tell it from a user pause; a letter whose pauses_game is true paused the game on arrival." };
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
            case "threats":
            case "alerts":
                foreach (var step in SituationNativeFacts.Read(name, map!, section)) yield return step;
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
                        ["auto_pause_mode"] = letter?.def?.pauseMode.ToString(), ["pauses_game"] = letter == null ? null : (JToken)ObservationPolicies.LetterPauses((int)Prefs.AutomaticPauseMode, (int)letter.def.pauseMode),
                        ["quest_id"] = quest?.GetUniqueLoadID(), ["quest_state"] = quest?.State.ToString(),
                        ["visible_letter"] = letter != null && Find.LetterStack.LettersListForReading.Contains(letter),
                        ["targets"] = SituationAlertObservations.Targets(letter?.lookTargets?.targets ?? message?.lookTargets?.targets),
                        ["source"] = "RimWorld.Archive.ArchivablesListForReading" });
                    yield return 0;
                }
                break;
        }
    }
    private static string Source(string section) => section switch { "conditions" => "RimWorld.GameConditionManager", "threats" => "Verse.AttackTargetsCache + Verse.AI.Group.LordManager", "alerts" => "RimWorld.AlertsReadout", "quests" => "RimWorld.QuestManager", _ => "RimWorld.Archive" };
    private static JObject Error(Exception error) => new() { ["code"] = (error as ExecutionError)?.Code ?? error.GetType().Name, ["message"] = error.Message };
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
