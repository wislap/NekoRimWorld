using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

// Quantities by observed source, not an ownership or immediate usability verdict.
// Traversal and existing trader inventories are the same ones used by other reads.
internal static class ResourceReadings
{
    internal static IEnumerable<int> ReadSteps(JObject args, SessionDescriptor session, ReadContext context, Action<JObject> publish)
    {
        var requested = ColonyReadContract.Texts(args, "items");
        if (requested.Length == 0) throw ReadContext.Error("invalid_arguments", "Resources require requested item definitions or exact labels.");
        var aspects = ColonyReadContract.Aspects(args, "quantity", "distribution");
        ColonyReadContract.RequireAspects(aspects, "quantity", "distribution");
        var map = GameWorldReferences.ResolveMap((string?)args["map"] ?? "current");
        var resolved = new Dictionary<string, ThingDef[]>(StringComparer.OrdinalIgnoreCase);
        var matches = new List<ThingDef>();
        foreach (var step in DefinitionCandidates.ExactSteps(DefDatabase<ThingDef>.AllDefsListForReading, requested, def => def.defName, def => def.label, context.Check))
        {
            if (step is ThingDef def) matches.Add(def);
            yield return 0;
        }
        foreach (var name in requested)
        {
            var candidates = matches.Where(def => string.Equals(def.defName, name, StringComparison.OrdinalIgnoreCase) || string.Equals(def.label, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 0) throw ReadContext.Error("unknown_definition", "No loaded resource definition matches " + name);
            if (candidates.Length > 1) throw ReadContext.Error("ambiguous_reference", "Several resource definitions match " + name + "; use an exact def name. Candidates: " + string.Join(", ", candidates.Select(def => def.defName)));
            resolved[name] = candidates;
        }
        var definitions = resolved.Values.SelectMany(value => value).Distinct().ToArray();
        var selected = new HashSet<ThingDef>(definitions);
        var stocks = TradeReadings.ExistingMapStocks(map).ToArray();
        var sellerByThing = new Dictionary<Thing, (string Id, string Name, string Channel)>();
        foreach (var stock in stocks)
            foreach (var item in stock.Stock)
            {
                if (selected.Contains(item.def)) sellerByThing[item] = (stock.Id, stock.Name, stock.Channel);
                yield return 0;
            }
        var quantities = definitions.ToDictionary(def => def, _ => new Dictionary<string, JObject>(StringComparer.Ordinal));
        var seen = new HashSet<Thing>();
        void Add(Thing item)
        {
            if (!selected.Contains(item.def) || !seen.Add(item)) return;
            string source, sourceId; JObject? identity = null;
            if (sellerByThing.TryGetValue(item, out var seller))
            {
                source = "trader_stock"; sourceId = seller.Id;
                identity = new JObject { ["id"] = seller.Id, ["name"] = seller.Name, ["channel"] = seller.Channel };
            }
            else if (item.Spawned) { source = "spawned_ground"; sourceId = map.GetUniqueLoadID(); }
            else if (ThingData.HeldByPawn(item) is Pawn pawn && pawn.Faction == Faction.OfPlayer)
            {
                source = "player_pawn_held"; sourceId = pawn.GetUniqueLoadID();
                identity = new JObject { ["id"] = sourceId, ["name"] = pawn.LabelShort };
            }
            else { source = "other_held"; sourceId = "unknown_ownership"; }
            string key = source + ":" + sourceId;
            if (!quantities[item.def].TryGetValue(key, out var bucket))
            {
                bucket = new JObject { ["source"] = source, ["units"] = 0L, ["records"] = 0 };
                if (identity != null) bucket["holder"] = identity;
                quantities[item.def].Add(key, bucket);
            }
            bucket["units"] = checked((long)bucket["units"]! + item.stackCount);
            bucket["records"] = (int)bucket["records"]! + 1;
        }
        var query = new Dictionary<string, object?> { ["mapId"] = map.GetUniqueLoadID(), ["defNames"] = definitions.Select(def => def.defName).ToArray(), ["includeHeld"] = true, ["includeUnreal"] = false, ["visibleOnly"] = true };
        foreach (var node in ThingData.QueryThings(query, context))
        {
            if (node.Handle is Thing item) Add(item);
            yield return 0;
        }
        // Public announced stock can have no spawned parent; include existing goods once.
        foreach (var stock in stocks) foreach (var item in stock.Stock) { Add(item); yield return 0; }
        var rows = definitions.Select(def => {
            var row = new JObject { ["def_name"] = def.defName, ["label"] = def.label, ["coverage"] = "complete_in_scope" };
            var buckets = quantities[def].Values.OrderBy(bucket => (string?)bucket["source"], StringComparer.Ordinal).ThenBy(bucket => (string?)bucket["holder"]?["id"], StringComparer.Ordinal).ToArray();
            if (aspects.Contains("quantity")) row["observed_units"] = buckets.Sum(bucket => (long)bucket["units"]!);
            // Keep the source partition beside any quantity; aggregate is not player stock.
            row["sources"] = new JArray(buckets);
            return row;
        });
        var scope = new JObject { ["map_id"] = map.GetUniqueLoadID(), ["population"] = "visible_spawned_map_and_real_holder_contents_plus_existing_public_map_trader_stock",
            ["ownership"] = "spawned_ground_and_other_held_do_not_establish_player_ownership_or_usability; trader_stock_is_seller_goods",
            ["excluded"] = "fogged_map_contents,unreal_offer_items,off_map_caravans,settlement_inventories", ["deduplicated"] = true };
        publish(ColonyReadContract.Page(args, rows, scope, session));
    }
}
