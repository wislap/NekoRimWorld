using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld.Planet;
using Verse;

namespace Neko.RimWorld;

internal static class JourneyEvaluation
{
    internal static JObject Read(JObject args, SessionDescriptor session)
    {
        var caravan = PlanetReadings.Resolve((JObject)args["caravan"]!, PlanetReadings.PlayerCaravans()) as Caravan
            ?? throw ReadContext.Error("object_unavailable", "Select an existing player caravan.");
        var destinations = (JArray)args["destinations"]!;
        var rows = new JArray();
        foreach (var selector in destinations.Cast<JObject>())
        {
            var destination = PlanetReadings.Resolve(selector);
            var row = new JObject { ["destination"] = PlanetReadings.Identity(destination), ["from"] = PlanetReadings.Location(caravan.Tile), ["to"] = PlanetReadings.Location(destination.Tile) };
            if (caravan.Tile.Layer != destination.Tile.Layer || !caravan.Tile.LayerDef.SurfaceTiles || caravan.Shuttle != null)
            {
                row["status"] = "unavailable"; row["reason"] = "Surface walking-caravan travel is supported; this trip uses a different layer or travel mode.";
            }
            else
            {
                using var path = caravan.Tile.Layer.Pather.FindPath(caravan.Tile, destination.Tile, caravan);
                if (!path.Found) { row["status"] = "no_path"; }
                else
                {
                    int ticks = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(caravan.Tile, destination.Tile, path, 0f, caravan.TicksPerMove, Find.TickManager.TicksAbs);
                    row["status"] = "ok"; row["travel_ticks_estimate"] = ticks; row["travel_days_estimate"] = ticks / 60000d;
                    row["mass_usage"] = caravan.MassUsage; row["mass_capacity"] = caravan.MassCapacity;
                    row["immobilized_by_mass"] = caravan.MassUsage > caravan.MassCapacity;
                    row["currently_paused"] = caravan.pather.Paused;
                    if ((args["include"] as JArray)?.Any(token => (string?)token == "supplies") == true) row["supplies"] = PlanetReadings.Supplies(caravan, path);
                }
            }
            rows.Add(row);
        }
        var result = ColonyReadContract.Page(new JObject { ["limit"] = 20 }, rows.Cast<JObject>(), new JObject { ["caravan"] = PlanetReadings.Identity(caravan) }, session);
        result["assumptions"] = new JArray("Existing party and cargo, current absolute departure time, native walking-caravan path and night-rest estimate; no travel orders issued. Pauses, immobilization, encounters and later changes are not predicted.");
        return result;
    }
}
