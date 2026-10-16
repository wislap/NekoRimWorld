using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Neko.RimWorld;

internal static class QuestReadings
{
    private static readonly FieldInfo UsedReward = typeof(global::RimWorld.Reward).GetField("usedOrCleanedUp", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo RememberedItems = typeof(Reward_Items).GetField("itemDefs", BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static JObject Read(JObject args, SessionDescriptor session)
    {
        var aspects = ColonyReadContract.Aspects(args, "identity");
        ColonyReadContract.RequireAspects(aspects, "identity", "requirements", "rewards", "deadlines");
        var quests = ColonyReadContract.Select(Find.QuestManager.QuestsListForReading.Where(q => !q.hidden && !q.hiddenInUI), args,
            q => q.GetUniqueLoadID(), q => q.name, q => !q.dismissed && q.State is QuestState.NotYetAccepted or QuestState.Ongoing);
        return ColonyReadContract.Page(args, quests.Select(q => ReadQuest(q, aspects)),
            ColonyReadContract.Targeted(args) ? "Selected public quest instances, including history." : "Public, non-dismissed offered and ongoing quests.", session);
    }
    private static JObject ReadQuest(Quest quest, HashSet<string> aspects)
    {
        var parts = quest.PartsListForReading;
        var row = new JObject { ["id"] = quest.GetUniqueLoadID(), ["name"] = quest.name, ["state"] = quest.State.ToString() };
        // Native public description stays available even when only a supported
        // subset of composable quest parts has structured interpretation.
        if (aspects.Contains("requirements")) {
            row["description"] = quest.description.ToString();
            row["root_def"] = quest.root?.defName; row["parent_quest_id"] = quest.parent?.GetUniqueLoadID();
            var requirements = new JArray();
            foreach (var part in parts) {
                if (part is QuestPart_InitiateTradeRequest trade) {
                    var request = trade.settlement?.GetComponent<TradeRequestComp>();
                    requirements.Add(new JObject { ["kind"] = "trade_request", ["part_index"] = part.Index,
                        ["item"] = Def(trade.requestedThingDef), ["required_units"] = trade.requestedCount,
                        ["destination"] = WorldObject(trade.settlement), ["request_duration_ticks"] = trade.requestDuration,
                        ["item_conditions"] = TradeRequestConditions(trade.requestedThingDef),
                        ["active"] = request != null && request.ActiveRequest && request.requestThingDef == trade.requestedThingDef,
                        ["phase"] = "delivery_at_settlement", ["source"] = "QuestPart_InitiateTradeRequest + TradeRequestComp" });
                } else if (part is QuestPart_RequiredShuttleThings shuttle) {
                    requirements.Add(new JObject { ["kind"] = "shuttle_colonists", ["part_index"] = part.Index, ["shuttle_id"] = shuttle.shuttle?.GetUniqueLoadID(),
                        ["require_all_colonists_on_map"] = shuttle.requireAllColonistsOnMap, ["required_colonist_count"] = shuttle.requiredColonistCount,
                        ["map_parent"] = WorldObject(shuttle.mapParent) });
                } else if (part is QuestPart_LendColonistsToFaction lend) {
                    requirements.Add(new JObject { ["kind"] = "lend_colonists", ["part_index"] = part.Index, ["state"] = lend.State.ToString(),
                        ["faction"] = Faction(lend.lendColonistsToFaction), ["return_after_ticks"] = lend.returnLentColonistsInTicks,
                        ["currently_lent"] = new JArray(lend.LentColonistsListForReading.Select(p => new JObject { ["id"] = p.GetUniqueLoadID(), ["name"] = p.LabelShortCap.ToString() })) });
                }
            }
            row["requirements"] = requirements;
            row["requirements_coverage"] = new JObject { ["status"] = "partial", ["supported"] = new JArray("trade requests", "shuttle colonist counts", "lent colonists"),
                ["notice"] = "Structured entries cover these native part types; the public description remains authoritative for other requirements. Entries describe their own phase, not proof that all conditions are met." };
        }
        if (aspects.Contains("rewards")) {
            row["reward_groups"] = new JArray(parts.OfType<QuestPart_Choice>().Select(choice => new JObject {
                ["part_index"] = choice.Index, ["selection_rule"] = "choose_one", ["choice_used"] = choice.choiceUsed,
                ["selected_option"] = JValue.CreateNull(), ["selected_option_status"] = choice.choiceUsed ? "not_retained_by_native_choice" : "not_selected",
                ["options"] = new JArray(choice.choices.Select((option, index) => new JObject { ["option"] = index + 1, ["rewards"] = new JArray(option.rewards.Select(Reward)) })) }));
            row["rewards_coverage"] = "Native choice groups; reward offers are not proof of payment. Unsupported reward types retain their type and unavailable status.";
        }
        if (aspects.Contains("deadlines")) {
            var deadlines = new JArray(); int now = Find.TickManager.TicksGame;
            if (quest.State == QuestState.NotYetAccepted && quest.acceptanceExpireTick >= 0) deadlines.Add(Deadline("acceptance", quest.acceptanceExpireTick, now));
            foreach (var delay in parts.OfType<QuestPart_Delay>().Where(d => d.State == QuestPartState.Enabled &&
                (!string.IsNullOrEmpty(d.expiryInfoPart) || !string.IsNullOrEmpty(d.alertLabel) || !string.IsNullOrEmpty(d.inspectString)))) {
                var timer = Deadline("stage_timer", delay.EnableTick + delay.delayTicks, now);
                timer["part_index"] = delay.Index; timer["label_template"] = delay.expiryInfoPart; timer["alert_label_template"] = delay.alertLabel;
                timer["effect"] = "Native stage delay completion; not necessarily quest completion or delivery expiry."; deadlines.Add(timer);
            }
            foreach (var trade in parts.OfType<QuestPart_InitiateTradeRequest>()) {
                var request = trade.settlement?.GetComponent<TradeRequestComp>();
                if (request == null || !request.ActiveRequest || request.requestThingDef != trade.requestedThingDef) continue;
                var deadline = Deadline("delivery", request.expiration, now); deadline["destination"] = WorldObject(trade.settlement); deadline["part_index"] = trade.Index; deadlines.Add(deadline);
            }
            row["deadlines"] = deadlines;
            row["deadline_coverage"] = "Acceptance expiry, enabled native delay timers and active settlement trade-request expiry. Public description may contain other timing conditions.";
        }
        return row;
    }
    private static JObject TradeRequestConditions(ThingDef? item)
    {
        // Native TradeRequestComp.PlayerCanGive applies these fixed goods
        // conditions; exposing them does not inspect or consume any cargo.
        var conditions = new JObject { ["rot_stage"] = "Fresh", ["source"] = "TradeRequestComp.PlayerCanGive" };
        if (item?.IsApparel == true) conditions["worn_by_corpse"] = false;
        if (item?.comps?.Any(comp => typeof(CompQuality).IsAssignableFrom(comp.compClass)) == true)
            conditions["minimum_quality"] = "Normal";
        return conditions;
    }
    private static JObject Deadline(string kind, int due, int now) => new() { ["kind"] = kind, ["due_game_tick"] = due,
        ["remaining_ticks"] = due - now, ["remaining_days"] = (double)(due - now) / GenDate.TicksPerDay, ["sample_tick"] = now };
    private static JObject? Def(Def? def) => def == null ? null : new JObject { ["def"] = def.defName, ["name"] = def.label };
    private static JObject? Faction(global::RimWorld.Faction? faction) => faction == null ? null : new JObject { ["id"] = faction.GetUniqueLoadID(), ["name"] = faction.Name };
    private static JObject? WorldObject(global::RimWorld.Planet.WorldObject? obj) => obj == null ? null : new JObject { ["id"] = obj.GetUniqueLoadID(), ["name"] = obj.Label,
        ["location"] = new JObject { ["tile_id"] = obj.Tile.tileId, ["layer_id"] = obj.Tile.Layer.LayerID } };
    private static JObject Reward(global::RimWorld.Reward reward)
    {
        var row = new JObject { ["type"] = reward.GetType().Name, ["status"] = "ok" };
        if (reward is Reward_Items items) {
            bool remembered = (bool)UsedReward.GetValue(reward)!;
            row["representation"] = remembered ? "native_remembered_offer" : "native_offer_items";
            row["items"] = remembered ? new JArray(((List<Reward_Items.RememberedItem>)RememberedItems.GetValue(items)!).Select(t => new JObject {
                ["def"] = t.thing.thing.defName, ["name"] = t.label, ["units"] = t.stackCount }))
                : new JArray(items.items.Select(t => new JObject { ["id"] = t.GetUniqueLoadID(), ["def"] = t.def.defName, ["name"] = t.LabelNoCount, ["units"] = t.stackCount }));
        }
        else if (reward is Reward_Goodwill goodwill) { row["faction"] = Faction(goodwill.faction); row["goodwill"] = goodwill.amount; }
        else if (reward is Reward_RoyalFavor favor) { row["faction"] = Faction(favor.faction); row["royal_favor"] = favor.amount; }
        else row["status"] = "unavailable";
        return row;
    }
}
