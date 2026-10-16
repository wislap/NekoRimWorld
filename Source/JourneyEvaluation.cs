using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
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
        bool changed = args["cargo_add"] != null || args["cargo_remove"] != null;
        var cargo = changed ? CargoScenario(caravan, args) : null;
        float loadedMass = cargo == null ? caravan.MassUsage : (float)cargo["mass_usage"]!;
        int loadedTicks = changed ? CaravanTicksPerMoveUtility.GetTicksPerMove(caravan.PawnsListForReading, loadedMass, caravan.MassCapacity) : caravan.TicksPerMove;
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
                    if (changed)
                    {
                        bool immobilized = loadedMass > caravan.MassCapacity;
                        var scenario = new JObject { ["status"] = immobilized ? "immobilized_by_mass" : "ok", ["mass_usage"] = loadedMass,
                            ["mass_capacity"] = caravan.MassCapacity, ["immobilized_by_mass"] = immobilized, ["ticks_per_move"] = loadedTicks };
                        if (!immobilized)
                        {
                            int loadedEta = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(caravan.Tile, destination.Tile, path, 0f, loadedTicks, Find.TickManager.TicksAbs);
                            scenario["travel_ticks_estimate"] = loadedEta; scenario["travel_days_estimate"] = loadedEta / 60000d;
                        }
                        scenario["food_basis"] = "Food estimates in supplies describe unchanged current inventory; cargo additions/removals are not included in food consumption, grazing or foraging estimates.";
                        row["cargo_scenario"] = scenario;
                    }
                    if ((args["include"] as JArray)?.Any(token => (string?)token == "supplies") == true) row["supplies"] = PlanetReadings.Supplies(caravan, path);
                }
            }
            rows.Add(row);
        }
        var result = ColonyReadContract.Page(new JObject { ["limit"] = 20 }, rows.Cast<JObject>(), new JObject { ["caravan"] = PlanetReadings.Identity(caravan) }, session);
        if (cargo != null) result["cargo_plan"] = cargo;
        result["assumptions"] = new JArray("Existing party and cargo, current absolute departure time, native walking-caravan path and night-rest estimate; no travel orders issued. Pauses, immobilization, encounters and later changes are not predicted.");
        if (changed) ((JArray)result["assumptions"]!).Add("Top-level row estimates describe current cargo; cargo_scenario describes the requested numeric cargo changes for the same party and departure time, excluding time to obtain/load goods.");
        return result;
    }

    // Definition additions and actual carried-stack removals change only numeric mass.
    // No Things, transferables, inventories or world objects are created or altered.
    private static JObject CargoScenario(Caravan caravan, JObject args)
    {
        var inventory = CaravanInventoryUtility.AllInventoryItems(caravan).Where(t => t != null && !t.Destroyed).Distinct().ToArray();
        var lines = new JArray(); float added = 0, removed = 0;
        foreach (string direction in new[] { "cargo_remove", "cargo_add" })
        {
            if (args[direction] is not JArray requests) continue;
            var grouped = new Dictionary<ThingDef, int>();
            foreach (var request in requests.Cast<JObject>())
            {
                string name = (string)request["item"]!;
                var definition = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                if (definition == null)
                {
                    var matches = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => string.Equals(d.label, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length != 1) throw ReadContext.Error(matches.Length > 1 ? "ambiguous_reference" : "object_unavailable", "Cargo item must identify one loaded item definition: " + name);
                    definition = matches[0];
                }
                if (definition.category != ThingCategory.Item) throw ReadContext.Error("invalid_arguments", "Cargo changes support item definitions, not pawns or buildings: " + definition.defName);
                grouped[definition] = checked((grouped.TryGetValue(definition, out int count) ? count : 0) + (int)request["quantity"]!);
            }
            foreach (var entry in grouped)
            {
                var definition = entry.Key; int quantity = entry.Value;
                float mass = 0;
                if (direction == "cargo_remove")
                {
                    var matching = inventory.Where(t => t.def == definition).OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
                    int available = matching.Sum(t => t.stackCount);
                    if (quantity > available) throw ReadContext.Error("insufficient_units", "Cannot remove " + quantity + " " + definition.defName + "; current carried inventory has " + available + ".");
                    int remaining = quantity;
                    foreach (var thing in matching)
                    {
                        int take = Math.Min(remaining, thing.stackCount); mass += thing.GetStatValue(StatDefOf.Mass) * take; remaining -= take;
                        if (remaining == 0) break;
                    }
                    removed += mass;
                }
                else
                {
                    if (definition.MadeFromStuff) throw ReadContext.Error("invalid_arguments", "Added cargo needs a fixed-mass item definition; material-dependent items require an actual variant and are not supported: " + definition.defName);
                    mass = definition.GetStatValueAbstract(StatDefOf.Mass) * quantity; added += mass;
                }
                lines.Add(new JObject { ["change"] = direction == "cargo_add" ? "add" : "remove", ["item"] = definition.defName, ["name"] = definition.label,
                    ["units"] = quantity, ["mass"] = mass, ["mass_basis"] = direction == "cargo_add" ? "native_definition_Mass" : "native_actual_carried_stacks_Mass" });
            }
        }
        return new JObject { ["rows"] = lines, ["current_mass_usage"] = caravan.MassUsage, ["added_mass"] = added, ["removed_mass"] = removed,
            ["mass_usage"] = Math.Max(0, caravan.MassUsage + added - removed),
            ["basis"] = "Existing party/capacity, abstract native Mass for added fixed-material item definitions and native Mass of actual removed carried stacks. Availability of added goods and time to obtain/load them are not assumed. No cargo is changed." };
    }
}
