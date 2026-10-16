using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Neko.RimWorld;

internal static class PlanetReadings
{
    internal static IEnumerable<WorldObject> KnownObjects() => Find.WorldObjects.AllWorldObjects
        .Where(item => item != null && !item.Destroyed && item.Tile.Valid && item.def.selectable &&
            (item.Faction == null || !item.Faction.Hidden || item.Faction.IsPlayer));

    internal static IEnumerable<Caravan> PlayerCaravans() => KnownObjects().OfType<Caravan>().Where(item => item.IsPlayerControlled);

    internal static JObject Location(PlanetTile tile) => new()
    {
        ["layer_id"] = tile.Layer.LayerID, ["layer"] = tile.Layer.Def.defName, ["tile_id"] = tile.tileId
    };

    internal static string Kind(WorldObject value) => value is Caravan ? "caravan" : value is Settlement ? "settlement" : value is Site ? "site" : "world_object";

    internal static JObject Identity(WorldObject value) => new()
    {
        ["id"] = value.GetUniqueLoadID(), ["name"] = value.Label, ["kind"] = Kind(value), ["def_name"] = value.def.defName
    };

    internal static WorldObject Resolve(JObject selector, IEnumerable<WorldObject>? population = null)
    {
        var args = new JObject();
        if (selector["id"] != null) args["ids"] = new JArray(selector["id"]!.DeepClone());
        else if (selector["name"] != null) args["names"] = new JArray(selector["name"]!.DeepClone());
        else throw ReadContext.Error("invalid_arguments", "A world object requires id or name.");
        var matches = ColonyReadContract.Select(population ?? KnownObjects(), args, value => value.GetUniqueLoadID(), value => value.Label).ToArray();
        if (matches.Length != 1) throw ReadContext.Error("object_unavailable", "No matching known world object.");
        return matches[0];
    }

    internal static JObject Read(JObject args, SessionDescriptor session)
    {
        var kind = (string?)args["kind"] ?? "world";
        if (kind != "world" && kind != "world_objects" && kind != "caravans") throw ReadContext.Error("invalid_arguments", "Unknown planet object kind.");
        var aspects = ColonyReadContract.Aspects(args, kind == "caravans" ? new[] { "identity", "location", "route" } : new[] { "identity", "location" });
        IEnumerable<WorldObject> population = kind == "caravans" ? PlayerCaravans() : KnownObjects();
        var category = (string?)args["category"];
        if (category != null) population = population.Where(item => string.Equals(Kind(item), category.TrimEnd('s'), StringComparison.OrdinalIgnoreCase) || string.Equals(item.def.defName, category, StringComparison.OrdinalIgnoreCase));
        if (args["faction"] != null)
        {
            string reference = (string)args["faction"]!;
            var faction = ColonyReadContract.Select(Find.FactionManager.AllFactionsListForReading.Where(f => !f.Hidden), new JObject { ["names"] = new JArray(reference) }, f => f.GetUniqueLoadID(), f => f.Name).Single();
            population = population.Where(item => item.Faction == faction);
        }
        bool targeted = args["ids"] != null || args["names"] != null;
        if (kind == "world" && !targeted && category == null)
            population = population.Where(item => item.Faction == Faction.OfPlayer || item.questTags?.Count > 0);
        ColonyReadContract.RequireAspects(aspects, "identity", "location", "places", "route", "supplies");
        IEnumerable<WorldObject> selected = ColonyReadContract.Select(population, args, value => value.GetUniqueLoadID(), value => value.Label);
        PlanetTile? origin = null;
        if (args["near"] is JObject near)
            origin = (string?)near["kind"] == "map" ? GameWorldReferences.ResolveMap((string?)near["map"]).Tile : Resolve(near).Tile;
        if (origin.HasValue)
        {
            var from = origin.Value;
            selected = selected.OrderBy(item => item.Tile.Layer == from.Layer ? Find.WorldGrid.ApproxDistanceInTiles(from, item.Tile) : float.MaxValue);
        }
        // Select/count first; expensive route/supply aspects are read only for the delivered page.
        var all = selected.ToArray();
        int offset = (int?)args["offset"] ?? 0, limit = (int?)args["limit"] ?? 20;
        var rows = all.Skip(offset).Take(limit).Select(item => Describe(item, aspects, origin)).ToArray();
        var scope = new JObject { ["kind"] = kind, ["population"] = kind == "caravans" ? "existing_player_caravans" : "known_selectable_world_objects", ["category"] = category, ["faction"] = (string?)args["faction"] };
        if (origin.HasValue) scope["near"] = Location(origin.Value);
        if (kind == "world" && !targeted && category == null) scope["view"] = "player_objects_and_quest_related_places";
        var result = ColonyReadContract.Page(new JObject { ["limit"] = limit }, rows, scope, session);
        result["total"] = all.Length;
        result["page"] = new JObject { ["offset"] = offset, ["limit"] = limit, ["returned"] = rows.Length, ["has_more"] = offset + rows.Length < all.Length,
            ["next_offset"] = offset + rows.Length < all.Length ? (JToken)(offset + rows.Length) : null };
        return result;
    }

    private static JObject Describe(WorldObject value, HashSet<string> aspects, PlanetTile? origin)
    {
        var row = Identity(value);
        if (aspects.Contains("location") || aspects.Contains("places"))
        {
            row["location"] = Location(value.Tile);
            row["terrain"] = new JObject { ["biome"] = value.Tile.Tile.PrimaryBiome?.defName, ["hilliness"] = value.Tile.Tile.hilliness.ToString() };
            if (origin.HasValue && origin.Value.Layer == value.Tile.Layer)
                row["approx_distance_tiles"] = Find.WorldGrid.ApproxDistanceInTiles(origin.Value, value.Tile);
        }
        if (value.Faction != null) row["faction"] = new JObject { ["id"] = value.Faction.GetUniqueLoadID(), ["name"] = value.Faction.Name };
        if (value is Caravan caravan)
        {
            // A caravan's current location is part of its identity, including route-only reads.
            row["location"] = Location(caravan.Tile);
            row["members"] = new JArray(caravan.PawnsListForReading.Select(pawn => new JObject { ["id"] = pawn.GetUniqueLoadID(), ["name"] = pawn.LabelShort }));
            if (aspects.Contains("route"))
            {
                var route = new JObject { ["moving"] = caravan.pather.Moving, ["paused"] = caravan.pather.Paused, ["night_resting"] = caravan.NightResting,
                    ["mass_usage"] = caravan.MassUsage, ["mass_capacity"] = caravan.MassCapacity, ["immobilized_by_mass"] = caravan.MassUsage > caravan.MassCapacity };
                if (caravan.pather.Moving && caravan.pather.Destination.Valid)
                {
                    route["destination"] = Location(caravan.pather.Destination);
                    if (caravan.pather.curPath != null && caravan.pather.curPath.Found)
                    {
                        var ticks = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(caravan, false);
                        route["travel_ticks_estimate"] = ticks; route["travel_days_estimate"] = ticks / 60000d;
                    }
                }
                row["route"] = route;
            }
            if (aspects.Contains("supplies")) row["supplies"] = Supplies(caravan);
        }
        return row;
    }

    internal static JObject Supplies(Caravan caravan)
    {
        return new JObject {
            ["food_days_estimate"] = DaysWorthOfFoodCalculator.ApproxDaysWorthOfFood(caravan),
            ["basis"] = "Native approximation for current caravan and current route; grazing/foraging and changing conditions can affect this estimate.",
            ["source"] = "DaysWorthOfFoodCalculator.ApproxDaysWorthOfFood",
            ["carried_inventory"] = CarriedInventory(caravan)
        };
    }

    private static JObject CarriedInventory(Caravan caravan)
    {
        var inventory = CaravanInventoryUtility.AllInventoryItems(caravan).Where(item => item != null && !item.Destroyed).ToArray();
        var groups = inventory.GroupBy(item => item.GetInnerIfMinified().def).OrderByDescending(group => group.Key.IsNutritionGivingIngestible || group.Key == ThingDefOf.Silver).ThenBy(group => group.Key.defName, StringComparer.Ordinal).ToArray();
        return new JObject {
            ["grouping"] = "definition_totals; variants combined, pawn equipment and apparel excluded", ["total_definitions"] = groups.Length,
            ["rows"] = new JArray(groups.Take(50).Select(group => new JObject { ["def_name"] = group.Key.defName, ["name"] = group.Key.label, ["units"] = group.Sum(item => item.stackCount) })),
            ["omitted_definitions"] = Math.Max(0, groups.Length - 50)
        };
    }

    internal static JObject Supplies(Caravan caravan, WorldPath route)
    {
        var transferables = caravan.PawnsListForReading.Select(pawn =>
        {
            var value = new TransferableOneWay(); value.things.Add(pawn); value.ForceTo(1); return value;
        }).ToList();
        return new JObject {
            ["food_days_estimate"] = DaysWorthOfFoodCalculator.ApproxDaysWorthOfFood(transferables, caravan.Tile, IgnorePawnsInventoryMode.DontIgnore, caravan.Faction, route, 0f, caravan.TicksPerMove),
            ["basis"] = "Native approximation using current party, carried inventories and this proposed route; includes native grazing/foraging assumptions.",
            ["source"] = "DaysWorthOfFoodCalculator.ApproxDaysWorthOfFood",
            ["carried_inventory"] = CarriedInventory(caravan)
        };
    }
}
