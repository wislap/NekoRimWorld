using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

// Will anyone starve? Edible scope follows the native ResourceCounter.TotalHumanEdibleNutrition
// definition, but counts every visible spawned stack, not only stored ones.
internal static class ColonyVitalsFood
{
    private const int RotSoonTicks = 2 * GenDate.TicksPerDay;

    internal static IEnumerable<int> Read(Map map, Pawn[] colonists, JObject domain)
    {
        double ready = 0, raw = 0, forbidden = 0, unroofed = 0;
        int forbiddenUnits = 0, stacks = 0;
        var flagged = new List<(double nutrition, double hp, JObject row)>();
        foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree).ToArray()) {
            if (!Edible(thing, map)) continue;
            stacks++;
            double nutrition = thing.def.GetStatValueAbstract(StatDefOf.Nutrition) * thing.stackCount;
            bool isForbidden = NativeThingState.Forbidden(thing) == true, roofed = map.roofGrid.Roofed(thing.Position);
            double hp = thing.def.useHitPoints && thing.MaxHitPoints > 0 ? (double)thing.HitPoints / thing.MaxHitPoints : 1.0;
            int? rotTicks = thing.TryGetComp<CompRottable>()?.TicksUntilRotAtCurrentTemp;
            if (isForbidden) { forbidden += nutrition; forbiddenUnits += thing.stackCount; }
            else {
                if (thing.def.ingestible.IsMeal) ready += nutrition; else raw += nutrition;
                if (!roofed) unroofed += nutrition;
            }
            var why = new JArray();
            if (isForbidden) why.Add("forbidden");
            if (!roofed && hp < 1.0) why.Add("deteriorating_unroofed");
            if (rotTicks < RotSoonTicks) why.Add("rots_soon");
            if (why.Count > 0) {
                var row = ColonyVitals.ThingRow(thing);
                row["why"] = why; row["nutrition"] = System.Math.Round(nutrition, 2); row["roofed"] = roofed;
                row["hit_points_ratio"] = System.Math.Round(hp, 2);
                row["days_to_rot"] = rotTicks < 30 * GenDate.TicksPerDay ? System.Math.Round(rotTicks.Value / (double)GenDate.TicksPerDay, 2) : null;
                flagged.Add((nutrition, hp, row));
            }
            yield return 0;
        }
        double need = colonists.Sum(p => (double?)p.needs?.food?.FoodFallPerTickAssumingCategory(HungerCategory.Fed) ?? 0.0) * GenDate.TicksPerDay;
        var numbers = new JObject {
            ["days_edible"] = ColonyVitalsContract.Days(ready + raw, need), ["days_ready"] = ColonyVitalsContract.Days(ready, need),
            ["ready_nutrition"] = System.Math.Round(ready, 2), ["raw_nutrition"] = System.Math.Round(raw, 2),
            ["unroofed_nutrition"] = System.Math.Round(unroofed, 2),
            ["forbidden_nutrition"] = System.Math.Round(forbidden, 2), ["forbidden_units"] = forbiddenUnits,
            ["daily_need"] = System.Math.Round(need, 3), ["stacks"] = stacks };
        domain["numbers"] = numbers;
        domain["method"] = new JObject {
            ["scope"] = "Spawned, unfogged items on the selected map with native IsNutritionGivingIngestible and HumanEdible, not IsNotFresh. Corpses and items held by pawns or inside containers are excluded.",
            ["nutrition"] = "Native ThingDef Nutrition stat x stack count, as ResourceCounter.TotalHumanEdibleNutrition.",
            ["forbidden"] = "CompForbiddable.Forbidden (NativeThingState.Forbidden), the definition the count tool splits on. Forbidden food is excluded from every other number.",
            ["ready"] = "ingestible.IsMeal; raw is every other edible item.",
            ["daily_need"] = "Sum over colonists of native Need_Food.FoodFallPerTickAssumingCategory(Fed) x ticks per day.",
            ["days_*"] = "unforbidden nutrition / daily_need; no reachability, hauling or cooking loss. Null when daily_need is 0.",
            ["unroofed_nutrition"] = "Unforbidden edible nutrition on cells without a roof (map.roofGrid.Roofed), where items deteriorate. No time-to-loss estimate: weather changes the rate.",
            ["worst"] = "Forbidden, deteriorating unroofed or rotting within 2 days; nutrition at stake descending, then hit points ascending." };
        ColonyVitalsContract.SetWorst(domain, flagged.OrderByDescending(f => f.nutrition).ThenBy(f => f.hp).Select(f => f.row));
        foreach (var step in Crops(map, numbers, domain)) yield return step;
    }

    private static bool Edible(Thing thing, Map map) =>
        thing.Spawned && thing.def.category == ThingCategory.Item && thing is not Corpse && !thing.Position.Fogged(map)
        && thing.def.IsNutritionGivingIngestible && thing.def.ingestible.HumanEdible && !thing.IsNotFresh();

    private static IEnumerable<int> Crops(Map map, JObject numbers, JObject domain)
    {
        int zones = 0, plants = 0, harvestable = 0;
        Plant? closest = null;
        foreach (var zone in map.zoneManager.AllZones.OfType<Zone_Growing>().ToArray()) {
            zones++;
            var sown = zone.GetPlantDefToGrow();
            foreach (var cell in zone.Cells.ToArray()) {
                var plant = map.thingGrid.ThingAt<Plant>(cell);
                if (plant == null || plant.def != sown || !plant.def.plant.Harvestable) continue;
                plants++;
                if (plant.HarvestableNow) harvestable++;
                else if (closest == null || plant.Growth > closest.Growth) closest = plant;
            }
            yield return 0;
        }
        numbers["crops"] = new JObject { ["growing_zones"] = zones, ["plants"] = plants, ["harvestable_now"] = harvestable,
            ["closest_def"] = closest?.def.defName, ["closest_growth"] = closest == null ? null : System.Math.Round(closest.Growth, 3),
            ["closest_days_at_full_rate"] = closest == null ? null : System.Math.Round((1.0 - closest.Growth) * closest.def.plant.growDays, 2),
            ["closest_growth_rate_now"] = closest == null ? null : System.Math.Round(closest.GrowthRate, 3) };
        ((JObject)domain["method"]!)["crops"] = "Plants of each growing zone's sown def. closest_days_at_full_rate = (1 - Growth) x growDays, ignoring rest, season, light and fertility; closest_growth_rate_now is native Plant.GrowthRate (0 outside growing season).";
    }
}
