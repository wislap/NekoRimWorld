using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

// Will anyone freeze, overheat or sleep outside?
internal static class ColonyVitalsShelter
{
    internal static IEnumerable<int> Read(Map map, Pawn[] colonists, JObject domain)
    {
        var rows = new List<(int rank, double gap, JObject row)>();
        int beds = 0, owned = 0, unroofedBeds = 0;
        foreach (var bed in map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>().ToArray()) {
            if (!bed.def.building.bed_humanlike || bed.Medical || bed.ForPrisoners) continue;
            beds++;
            var owners = bed.OwnersForReading;
            if (owners.Count > 0) owned++;
            var open = bed.OccupiedRect().Cells.Count(c => !map.roofGrid.Roofed(c));
            if (open > 0) {
                unroofedBeds++;
                var row = ColonyVitals.ThingRow(bed);
                row["why"] = new JArray("unroofed_bed"); row["unroofed_cells"] = open;
                row["owners"] = new JArray(owners.Select(p => p.GetUniqueLoadID()));
                rows.Add((1, 0, row));
            }
            yield return 0;
        }
        int withoutBed = 0, uncomfortable = 0;
        double worstGap = 0;
        foreach (var pawn in colonists) {
            if (pawn.ownership?.OwnedBed == null) {
                withoutBed++;
                var row = ColonyVitals.PawnRow(pawn); row["why"] = new JArray("no_bed");
                rows.Add((0, 0, row));
            }
            float temperature = pawn.AmbientTemperature;
            var comfort = pawn.ComfortableTemperatureRange();
            double gap = temperature < comfort.min ? temperature - comfort.min : temperature > comfort.max ? temperature - comfort.max : 0;
            if (gap != 0) {
                uncomfortable++;
                if (System.Math.Abs(gap) > System.Math.Abs(worstGap)) worstGap = gap;
                var row = ColonyVitals.PawnRow(pawn);
                row["why"] = new JArray(gap < 0 ? "too_cold" : "too_hot");
                row["temperature"] = System.Math.Round(temperature, 1); row["comfortable_min"] = System.Math.Round(comfort.min, 1);
                row["comfortable_max"] = System.Math.Round(comfort.max, 1); row["gap"] = System.Math.Round(gap, 1);
                rows.Add((2, System.Math.Abs(gap), row));
            }
            yield return 0;
        }
        domain["numbers"] = new JObject { ["colonist_beds"] = beds, ["owned_beds"] = owned, ["without_bed"] = withoutBed,
            ["beds_unroofed"] = unroofedBeds, ["uncomfortable_temperature"] = uncomfortable, ["worst_temperature_gap"] = System.Math.Round(worstGap, 1) };
        domain["method"] = new JObject {
            ["colonist_beds"] = "Player Building_Bed with humanlike beds, not medical, not for prisoners.",
            ["without_bed"] = "Colonists whose native Pawn_Ownership.OwnedBed is null.",
            ["beds_unroofed"] = "Colonist beds with any occupied cell lacking a roof (map.roofGrid.Roofed). Says nothing about the rest of the room.",
            ["worst_temperature_gap"] = "Native AmbientTemperature minus the nearer bound of ComfortableTemperatureRange() (ComfyTemperatureMin/Max stats, apparel included); negative is too cold, 0 is comfortable.",
            ["worst"] = "No bed, unroofed bed, then temperature gap by magnitude." };
        ColonyVitalsContract.SetWorst(domain, rows.OrderBy(r => r.rank).ThenByDescending(r => r.gap).Select(r => r.row));
    }
}
