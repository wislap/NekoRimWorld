using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal static class ColonyObservationReadings
{
    internal static void Qualify(JObject spec) {
        var ids = ((JArray)spec["target_ids"]!).Values<string>().Select(x => x!).ToArray();
        if ((string?)spec["kind"] == "food_policy") { foreach (var id in ids) PolicyReadings.Resolve(id); return; }
        var map = GameWorldReferences.ResolveMap((string?)spec["map_id"]); if (map.GetUniqueLoadID() != (string?)spec["map_id"]) throw new ArgumentException("Observation map_id must be canonical.");
        if ((string?)spec["kind"] == "resources") { foreach (var id in ids) if (DefDatabase<ThingDef>.GetNamedSilentFail(id) == null) throw new ArgumentException("Resource target must be an exact loaded ThingDef."); return; }
        if ((string?)spec["kind"] == "construction") { var registry = Current.Game.GetComponent<NekoGameComponent>().Save.ConstructionIntents; foreach (var id in ids) if (registry.Find(map, ConstructionIntents.Key(new JObject { ["plan_id"] = spec["plan_id"]!.DeepClone(), ["element_id"] = id })) == null) throw new ArgumentException("Construction intent is not registered."); return; }
        var tables = map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>().Where(t => ids.Contains(t.GetUniqueLoadID(), StringComparer.Ordinal)).ToArray(); if (tables.Length != ids.Length) throw new ArgumentException("A production target is unavailable.");
    }
    internal static JObject Sample(JObject spec) {
        var ids = ((JArray)spec["target_ids"]!).Values<string>().Select(x => x!).ToArray();
        if ((string?)spec["kind"] == "food_policy") return new JObject { ["objects"] = new JArray(ids.Select(id => { var p = PolicyReadings.Resolve(id); var content = PolicyReadings.Content(p); return new JObject { ["id"] = PolicyReadings.Id(p), ["policy_id"] = PolicyReadings.Id(p), ["native_instance"] = PolicyReadings.Instance(p), ["label"] = p.label, ["users"] = content["users"]!.DeepClone(), ["users_complete"] = true, ["content_fingerprint"] = GameQueryEngine.Hash(content.ToString(Newtonsoft.Json.Formatting.None)) }; })), ["complete"] = true };
        var map = GameWorldReferences.ResolveMap((string?)spec["map_id"]);
        if ((string?)spec["kind"] == "construction") { var result = Current.Game.GetComponent<NekoGameComponent>().Save.ConstructionIntents.Read(new JObject { ["map"] = map.GetUniqueLoadID(), ["plan_id"] = spec["plan_id"]!.DeepClone(), ["ids"] = new JArray(ids), ["limit"] = 100 }); if (((JArray)result["missing_ids"]!).Count > 0) throw new ArgumentException("Construction intent is not registered."); return result; }
        var tables = map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>().Where(t => ids.Contains(t.GetUniqueLoadID(), StringComparer.Ordinal)).ToArray(); if (tables.Length != ids.Length) throw new ArgumentException("A production target is unavailable.");
        return new JObject { ["objects"] = new JArray(tables.OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).Select(t => new JObject { ["id"] = t.GetUniqueLoadID(), ["map_id"] = map.GetUniqueLoadID(), ["status"] = "observed", ["bill_ids"] = new JArray(t.BillStack.Bills.Select(b => b.GetUniqueLoadID())), ["bills"] = new JArray(t.BillStack.Bills.Select(b => { var row = new JObject { ["bill_id"] = b.GetUniqueLoadID(), ["recipe"] = b.recipe?.defName, ["suspended"] = b.suspended }; if (b is Bill_Production production && ProductionReadings.Standard(production)) { row["repeat_count"] = production.repeatCount; row["target_count"] = production.targetCount; row["settings_fingerprint"] = GameQueryEngine.Hash(ProductionReadings.Settings(production).ToString(Newtonsoft.Json.Formatting.None)); if (production is Bill_ProductionWithUft uft) { row["unfinished_id"] = uft.BoundUft?.GetUniqueLoadID(); row["unfinished_work_left"] = uft.BoundUft == null ? JValue.CreateNull() : (JToken)uft.BoundUft.workLeft; } } return row; })), ["complete"] = true, ["coverage"] = "bill configuration, order and standard unfinished-work progress; inventory product counts require inspect_production" })), ["complete"] = true };
    }
}
