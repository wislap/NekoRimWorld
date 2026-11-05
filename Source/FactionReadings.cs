using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal static class FactionReadings
{
    private static readonly FieldInfo GoodwillCache = typeof(GoodwillSituationManager).GetField("cachedData", BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static JObject Read(JObject args, SessionDescriptor session)
    {
        var aspects = ColonyReadContract.Aspects(args, "identity", "relations");
        ColonyReadContract.RequireAspects(aspects, "identity", "relations", "settlements");
        var factions = ColonyReadContract.Select(Find.FactionManager.AllFactionsListForReading.Where(f => !f.Hidden), args, f => f.GetUniqueLoadID(), f => f.Name);
        var rows = factions.Select(f => {
            var row = new JObject { ["id"] = f.GetUniqueLoadID(), ["name"] = f.Name, ["def"] = f.def.defName, ["is_player"] = f.IsPlayer };
            if (aspects.Contains("identity")) { row["defeated"] = f.defeated; row["temporary"] = f.temporary; row["leader"] = f.leader == null ? null : new JObject { ["id"] = f.leader.GetUniqueLoadID(), ["name"] = f.leader.LabelShortCap.ToString() }; }
            if (aspects.Contains("relations")) {
                var relation = f.IsPlayer ? null : f.RelationWith(Faction.OfPlayer, allowNull: true);
                bool goodwillApplies = !f.IsPlayer && f.HasGoodwill && Faction.OfPlayer.HasGoodwill;
                var cache = (Dictionary<Faction, List<GoodwillSituationManager.CachedSituation>>)GoodwillCache.GetValue(Find.GoodwillSituationManager)!;
                int? goodwill = null;
                if (goodwillApplies && relation != null && cache.TryGetValue(f, out var situations)) goodwill = System.Math.Min(relation.baseGoodwill, situations.Aggregate(100, (value, situation) => System.Math.Min(value, situation.maxGoodwill)));
                row["relation_to_player"] = new JObject { ["kind"] = f.IsPlayer ? "self" : relation?.kind.ToString(),
                    ["status"] = f.IsPlayer || relation != null ? "ok" : "unavailable", ["goodwill_applicable"] = goodwillApplies,
                    ["base_goodwill"] = goodwillApplies && relation != null ? (JToken)relation.baseGoodwill : null,
                    ["goodwill"] = goodwill.HasValue ? (JToken)goodwill.Value : null,
                    ["goodwill_status"] = !goodwillApplies ? "not_applicable" : goodwill.HasValue ? "ok" : "native_situation_cache_not_ready",
                    ["permanent_enemy"] = f.def.permanentEnemy,
                    ["goodwill_locked_by_quest"] = !f.IsPlayer && QuestUtility.IsGoodwillLockedByQuest(f, Faction.OfPlayer),
                    ["kind_thresholds"] = goodwillApplies ? new JObject { ["become_hostile_at_or_below"] = -75, ["become_ally_at_or_above"] = 75,
                        ["hostile_returns_neutral_at_or_above"] = 0, ["ally_returns_neutral_at_or_below"] = 0,
                        ["source"] = "FactionRelation.CheckKindThresholds; thresholds use effective goodwill, not a prediction that an offered reward is paid or applies unchanged." } : null };
            }
            if (aspects.Contains("settlements"))
            {
                var settlements = Find.WorldObjects.Settlements.Where(s => s.Faction == f && !s.Destroyed).ToArray();
                row["settlements"] = new JObject { ["total"] = settlements.Length, ["rows"] = new JArray(settlements.Take(5).Select(s => new JObject {
                    ["id"] = s.GetUniqueLoadID(), ["name"] = s.Label, ["location"] = PlanetReadings.Location(s.Tile) })),
                    ["omitted"] = System.Math.Max(0, settlements.Length - 5),
                    ["read_more"] = new JObject { ["object"] = new JObject { ["kind"] = "world_objects", ["category"] = "settlement" }, ["focus"] = new JObject { ["faction"] = new JObject { ["id"] = f.GetUniqueLoadID() } }, ["offset"] = 0, ["limit"] = 20 } };
            }
            return row;
        });
        return ColonyReadContract.Page(args, rows, "Public faction instances; relations are relative to the player; settlements are existing world objects, not stock observations.", session);
    }
}
