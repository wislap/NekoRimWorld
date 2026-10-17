using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Neko.RimWorld;

internal static class TradeReadings
{
    private sealed class Partner
    {
        internal ITrader Trader = null!;
        internal string Id = "";
        internal string Name = "";
        internal string Channel = "";
        internal Map? Map;
        internal Settlement? Settlement;
        internal JObject Identity() => new() { ["id"] = Id, ["name"] = Name, ["channel"] = Channel, ["map_id"] = Map?.GetUniqueLoadID(),
            ["faction"] = Trader.Faction?.Name, ["trader_kind"] = Trader.TraderKind?.defName, ["currency"] = Trader.TradeCurrency.ToString() };
    }

    private static IEnumerable<Partner> Partners(string? mapSelector)
    {
        var maps = mapSelector == null ? Find.Maps.Where(map => !map.Disposed) : new[] { GameWorldReferences.ResolveMap(mapSelector) };
        foreach (var map in maps)
        {
            foreach (var pawn in map.mapPawns.AllPawnsSpawned.Where(pawn => pawn.trader?.traderKind != null && pawn.Faction != Faction.OfPlayer && !pawn.Position.Fogged(map)))
                yield return new Partner { Trader = pawn, Id = pawn.GetUniqueLoadID(), Name = pawn.LabelShort, Channel = "map_trader", Map = map };
            foreach (var ship in map.passingShipManager.passingShips.OfType<TradeShip>().Where(ship => ship.WasAnnounced && !ship.Departed))
                yield return new Partner { Trader = ship, Id = ship.GetUniqueLoadID(), Name = ship.TraderName, Channel = "orbital", Map = map };
        }
        foreach (var settlement in PlanetReadings.KnownObjects().OfType<Settlement>().Where(item => item.Faction != null && !item.Faction.IsPlayer && item.TraderKind != null && item.EverVisited))
            yield return new Partner { Trader = settlement, Id = settlement.GetUniqueLoadID(), Name = settlement.Label, Channel = "settlement", Settlement = settlement };
    }

    private static Thing[] ExistingStock(Partner partner, out bool materialized)
    {
        if (partner.Settlement != null)
        {
            // Settlement.Goods/StockListForReading can generate inventory, even for an empty stock.
            // The audited holder method only returns the existing backing owner.
            var holder = partner.Settlement.trader.GetDirectlyHeldThings();
            materialized = holder != null;
            return holder?.Cast<Thing>().Where(item => !item.Destroyed).ToArray() ?? Array.Empty<Thing>();
        }
        WorldNativeAccess.RequireNative(partner.Trader, "get_Goods", "get_CanTradeNow", "ColonyThingsWillingToBuy", "get_TradePriceImprovementOffsetForPlayer");
        materialized = true;
        return partner.Trader.Goods.Where(item => !item.Destroyed).Distinct().ToArray();
    }

    internal static IEnumerable<(ITrader Trader, string Id, string Name, string Channel, Map? Map, Thing[] Stock)> ExistingMapStocks(Map map)
    {
        foreach (var partner in Partners(map.GetUniqueLoadID()).Where(p => p.Map == map))
        {
            var stock = ExistingStock(partner, out bool materialized);
            if (materialized) yield return (partner.Trader, partner.Id, partner.Name, partner.Channel, partner.Map, stock);
        }
    }

    internal static JObject Read(JObject args, SessionDescriptor session)
    {
        var aspects = ColonyReadContract.Aspects(args, "identity");
        ColonyReadContract.RequireAspects(aspects, "identity", "stock");
        var selected = ColonyReadContract.Select(Partners((string?)args["map"] ?? (ColonyReadContract.Targeted(args) ? null : "current")), args, item => item.Id, item => item.Name);
        bool targetedStock = ColonyReadContract.Targeted(args) && aspects.Contains("stock");
        int offset = (int?)args["offset"] ?? 0, limit = (int?)args["limit"] ?? 20;
        // Directory offset pages partners. Selecting a trader makes offset page that stock,
        // retaining the trader row on every page instead of applying the same offset twice.
        var delivered = targetedStock ? selected : selected.Skip(offset).Take(limit).ToArray();
        var requested = ColonyReadContract.Texts(args, "items");
        var rows = new List<JObject>();
        foreach (var partner in delivered)
        {
            WorldNativeAccess.RequireNative(partner.Trader, "get_CanTradeNow", "get_TradePriceImprovementOffsetForPlayer");
            var row = partner.Identity();
            row["can_trade_now"] = partner.Trader.CanTradeNow;
            row["availability_basis"] = "Trader availability only; negotiator ability, reachable channel and currency are checked by evaluate_trade.";
            if (partner.Settlement != null) row["location"] = PlanetReadings.Location(partner.Settlement.Tile);
            if (aspects.Contains("stock"))
            {
                var stock = ExistingStock(partner, out bool materialized);
                if (requested.Length != 0) stock = stock.Where(item => requested.Any(name => MatchesItem(item, name))).ToArray();
                var groups = Group(stock);
                int stockOffset = targetedStock ? offset : 0;
                var stockRows = groups.Skip(stockOffset).Take(limit).Select(group => Item(group[0], group.Sum(item => item.stackCount))).ToArray();
                row["stock"] = new JObject { ["status"] = materialized ? "ok" : "not_materialized", ["source"] = "existing_inventory_only", ["total"] = groups.Count,
                    ["price_basis"] = "market_value_reference_not_negotiated_quote",
                    ["rows"] = new JArray(stockRows),
                    ["page"] = new JObject { ["offset"] = stockOffset, ["limit"] = limit, ["returned"] = stockRows.Length, ["has_more"] = stockOffset + stockRows.Length < groups.Count,
                        ["next_offset"] = stockOffset + stockRows.Length < groups.Count ? (JToken)(stockOffset + stockRows.Length) : null } };
            }
            rows.Add(row);
        }
        var scope = new JObject { ["population"] = "visible_map_traders_announced_ships_and_previously_visited_settlements", ["map"] = (string?)args["map"] ?? "current_for_unselected_directory",
            ["paging"] = targetedStock ? "offset_and_limit_apply_to_each_selected_trader_stock; selected_trader_rows_are_retained" : "offset_and_limit_page_partners; stock_starts_at_zero; select_trader_to_page_stock" };
        var result = ColonyReadContract.Page(new JObject { ["limit"] = Math.Max(1, rows.Count) }, rows, scope, session);
        result["total"] = selected.Length;
        if (!targetedStock) result["page"] = new JObject { ["offset"] = offset, ["limit"] = limit, ["returned"] = rows.Count, ["has_more"] = offset + rows.Count < selected.Length,
            ["next_offset"] = offset + rows.Count < selected.Length ? (JToken)(offset + rows.Count) : null };
        return result;
    }

    private static bool MatchesItem(Thing item, string selector) => string.Equals(item.GetUniqueLoadID(), selector, StringComparison.Ordinal)
        || string.Equals(item.GetInnerIfMinified().def.defName, selector, StringComparison.OrdinalIgnoreCase)
        || string.Equals(item.GetInnerIfMinified().def.label, selector, StringComparison.OrdinalIgnoreCase)
        || string.Equals(item.LabelNoCount, selector, StringComparison.OrdinalIgnoreCase);

    private static List<List<Thing>> Group(IEnumerable<Thing> things)
    {
        var groups = new List<List<Thing>>();
        foreach (var item in things)
        {
            var group = groups.FirstOrDefault(existing => TransferableUtility.TransferAsOne(item, existing[0], TransferAsOneMode.Normal));
            if (group == null) groups.Add(new List<Thing> { item }); else group.Add(item);
        }
        return groups;
    }

    private static JObject Item(Thing thing, int count) => new()
    {
        ["id"] = thing.GetUniqueLoadID(), ["name"] = thing.LabelNoCount, ["def_name"] = thing.GetInnerIfMinified().def.defName,
        ["units"] = count, ["market_value_reference"] = thing.GetInnerIfMinified().MarketValue
    };

    internal static JObject Evaluate(JObject args, SessionDescriptor session)
    {
        var selector = (JObject)args["trader"]!;
        var select = new JObject();
        if (selector["id"] != null) select["ids"] = new JArray(selector["id"]!.DeepClone());
        else if (selector["name"] != null) select["names"] = new JArray(selector["name"]!.DeepClone());
        else throw ReadContext.Error("invalid_arguments", "A trader requires id or name.");
        var partner = ColonyReadContract.Select(Partners((string?)selector["map"]), select, item => item.Id, item => item.Name).Single();
        WorldNativeAccess.RequireNative(partner.Trader, "get_CanTradeNow", "get_TradePriceImprovementOffsetForPlayer", "ColonyThingsWillingToBuy");
        var negotiatorSelector = (JObject)args["negotiator"]!;
        var negotiatorArgs = new JObject();
        if (negotiatorSelector["id"] != null) negotiatorArgs["ids"] = new JArray(negotiatorSelector["id"]!.DeepClone());
        else if (negotiatorSelector["name"] != null) negotiatorArgs["names"] = new JArray(negotiatorSelector["name"]!.DeepClone());
        else throw ReadContext.Error("invalid_arguments", "A negotiator requires id or name.");
        IEnumerable<Pawn> people = Find.Maps.SelectMany(map => map.mapPawns.AllPawnsSpawned).Concat(PlanetReadings.PlayerCaravans().SelectMany(caravan => caravan.PawnsListForReading));
        if (negotiatorSelector["map"] != null) { var map = GameWorldReferences.ResolveMap((string?)negotiatorSelector["map"]); people = people.Where(pawn => pawn.Map == map); }
        var negotiator = ColonyReadContract.Select(people.Where(pawn => pawn.Faction == Faction.OfPlayer).Distinct(), negotiatorArgs, pawn => pawn.GetUniqueLoadID(), pawn => pawn.LabelShort).Single();
        var stock = ExistingStock(partner, out bool materialized);
        if (!(args["buy"] is JArray buys && buys.Count > 0) && !(args["sell"] is JArray sells && sells.Count > 0)) throw ReadContext.Error("invalid_arguments", "Specify a buy or sell basket.");
        foreach (string direction in new[] { "buy", "sell" })
            if (args[direction] is JArray basket && basket.Any(item => item is not JObject || item["item"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)item["item"]) || item["quantity"]?.Type != JTokenType.Integer || (long)item["quantity"]! < 1 || (long)item["quantity"]! > int.MaxValue))
                throw ReadContext.Error("invalid_arguments", "Basket lines need an item and positive integer quantity.");
        var blockers = ContextBlockers(partner, negotiator);
        if (!materialized) blockers.Add("Trader inventory is not currently materialized; no inventory was generated.");
        var scope = new JObject { ["trader"] = partner.Identity(), ["negotiator"] = new JObject { ["id"] = negotiator.GetUniqueLoadID(), ["name"] = negotiator.LabelShort } };
        var result = ColonyReadContract.Page(new JObject(), Array.Empty<JObject>(), scope, session);
        result["currency"] = partner.Trader.TradeCurrency.ToString();
        if (blockers.Count > 0) { result["status"] = "unavailable"; result["blockers"] = blockers; return result; }
        var colony = partner.Trader.ColonyThingsWillingToBuy(negotiator).Where(item => TradeUtility.PlayerSellableNow(item, partner.Trader) && (negotiator.IsWorldPawn() || InSellablePosition(item))).Distinct().ToArray();
        var lines = new JArray(); float net = 0f; bool complete = true;
        foreach (string direction in new[] { "buy", "sell" })
        {
            if (args[direction] is not JArray basket) continue;
            // Repeated identical selectors form one quantity, preventing duplicate availability claims.
            foreach (var request in basket.Cast<JObject>().GroupBy(item => (string)item["item"]!, StringComparer.OrdinalIgnoreCase))
            {
                string name = request.Key;
                int quantity = checked(request.Sum(item => (int)item["quantity"]!));
                var population = direction == "buy" ? stock : colony;
                var matching = population.Where(item => MatchesItem(item, name)).ToArray();
                var groups = Group(matching);
                var line = new JObject { ["item"] = name, ["player_direction"] = direction, ["requested_units"] = quantity };
                if (groups.Count == 0) { line["status"] = "not_available"; complete = false; }
                else if (groups.Count > 1) { line["status"] = "choose_variant"; line["variants"] = new JArray(groups.Select(group => Item(group[0], group.Sum(item => item.stackCount)))); complete = false; }
                else
                {
                    var group = groups[0]; var thing = group[0].GetInnerIfMinified();
                    line["object"] = Item(group[0], group.Sum(item => item.stackCount));
                    int available = group.Sum(item => item.stackCount);
                    bool accepted = partner.Trader.TraderKind.WillTrade(thing.def) && (direction == "buy" ? thing.def.tradeability.TraderCanSell() : thing.def.tradeability.PlayerCanSell());
                    line["available_units"] = available;
                    if (thing.def == ThingDefOf.Silver) { line["status"] = "currency_is_settlement_not_basket_item"; complete = false; }
                    else if (!accepted) { line["status"] = "trader_will_not_trade"; complete = false; }
                    else if (quantity > available) { line["status"] = "insufficient_units"; complete = false; }
                    else if (partner.Trader.TradeCurrency == TradeCurrency.Favor && direction == "buy") { line["status"] = "favor_trader_only_accepts_player_sales"; complete = false; }
                    else
                    {
                        float price = Price(thing, partner.Trader, negotiator, direction == "buy");
                        line["status"] = "ok"; line["unit_price"] = price; line["line_total_unrounded"] = price * quantity;
                        net += (direction == "buy" ? 1 : -1) * price * quantity;
                    }
                }
                lines.Add(line);
            }
        }
        result["rows"] = lines; result["total"] = lines.Count;
        result["page"] = new JObject { ["offset"] = 0, ["limit"] = lines.Count, ["returned"] = lines.Count, ["has_more"] = false };
        result["status"] = complete ? "ok" : "incomplete";
        result["quote_basis"] = "Native explicit-context TradeUtility prices with Tradeable dependency factors, sell-price clamp and aggregate currency rounding; no trade session or orders created.";
        if (complete)
        {
            int settlement = partner.Trader.TradeCurrency == TradeCurrency.Favor ? Mathf.CeilToInt(net) : Mathf.RoundToInt(net);
            int playerSilver = colony.Where(item => item.def == ThingDefOf.Silver).Sum(item => item.stackCount);
            int traderSilver = stock.Where(item => item.def == ThingDefOf.Silver).Sum(item => item.stackCount);
            result["settlement"] = new JObject { ["player_pays"] = settlement, ["rounding"] = partner.Trader.TradeCurrency == TradeCurrency.Favor ? "ceil_aggregate" : "nearest_integer_aggregate",
                ["player_silver_available"] = playerSilver, ["trader_silver_available"] = traderSilver,
                ["funds_scope"] = "Only goods currently accepted and reachable through this trader/negotiator channel; not total colony wealth.",
                ["trader_id"] = partner.Id, ["negotiator_id"] = negotiator.GetUniqueLoadID(),
                ["player_funds_sufficient"] = settlement <= playerSilver,
                ["trader_funds_sufficient"] = partner.Trader.TradeCurrency == TradeCurrency.Favor || -settlement <= traderSilver };
        }
        // A useful partial purchase for a one-item silver quote, using the same price,
        // native whole-transaction rounding and current channel funds. No optimization.
        if (complete && partner.Trader.TradeCurrency == TradeCurrency.Silver && lines.Count == 1 && (string?)lines[0]!["player_direction"] == "buy")
        {
            var line = (JObject)lines[0]!;
            float price = (float)line["unit_price"]!;
            int available = (int)line["available_units"]!, requested = (int)line["requested_units"]!;
            int funds = (int)result["settlement"]!["player_silver_available"]!;
            int low = 0, high = Math.Min(available, requested);
            while (low < high)
            {
                int middle = low + (high - low + 1) / 2;
                if (Mathf.RoundToInt(price * middle) <= funds) low = middle; else high = middle - 1;
            }
            result["affordable_purchase"] = new JObject { ["item"] = line["item"]!.DeepClone(), ["units"] = low, ["player_pays"] = Mathf.RoundToInt(price * low),
                ["basis"] = "Subset of this requested one-item lot at its quoted unit price, current channel silver and native aggregate rounding; no purchases or sales executed." };
        }
        return result;
    }

    private static JArray ContextBlockers(Partner partner, Pawn pawn)
    {
        var blockers = new JArray();
        if (!partner.Trader.CanTradeNow) blockers.Add("Trader is not trading now.");
        var acceptance = pawn.CanTradeWith(partner.Trader.Faction, partner.Trader.TraderKind);
        if (!acceptance.Accepted) blockers.Add(acceptance.Reason ?? "Negotiator cannot trade with this faction or trader.");
        if (pawn.Dead || pawn.Downed || pawn.InMentalState || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Talking)) blockers.Add("Negotiator is not currently able to negotiate.");
        if (partner.Settlement != null)
        {
            var caravan = pawn.GetCaravan();
            if (caravan == null || CaravanVisitUtility.SettlementVisitedNow(caravan) != partner.Settlement) blockers.Add("Negotiator's caravan is not currently visiting this settlement.");
        }
        else if (pawn.Map != partner.Map || !pawn.Spawned) blockers.Add("Negotiator is not on the trader's map.");
        else if (partner.Trader is Pawn trader && !pawn.CanReach(trader, PathEndMode.Touch, Danger.Some)) blockers.Add("Negotiator cannot reach the trader.");
        else if (partner.Trader is TradeShip)
        {
            if (!partner.Map!.listerBuildings.AllBuildingsColonistOfClass<Building_CommsConsole>().Any(console => console.CanUseCommsNow && pawn.CanReach(console, PathEndMode.InteractionCell, Danger.Some))) blockers.Add("No usable reachable communications console for this negotiator.");
            if (!Building_OrbitalTradeBeacon.AllPowered(partner.Map).Any()) blockers.Add("No powered orbital trade beacon.");
        }
        return blockers;
    }

    private static float Price(Thing thing, ITrader trader, Pawn negotiator, bool buy)
    {
        // Mirrors Tradeable.InitPriceDataIfNeeded's explicit factors; never touches TradeSession.
        float gain = negotiator.GetStatValue(StatDefOf.TradePriceImprovement);
        float joinAs = 1f, humanSell = 1f;
        if (ModsConfig.IdeologyActive && thing is Pawn pawn && pawn.RaceProps.Humanlike && pawn.guest != null)
        { if (pawn.guest.joinStatus == JoinStatus.JoinAsSlave) joinAs = 0.6f; humanSell = 0.6f; }
        float buyPrice = TradeUtility.GetPricePlayerBuy(thing, trader.TraderKind.PriceTypeFor(thing.def, TradeAction.PlayerBuys).PriceMultiplier(), joinAs, gain, trader.TradePriceImprovementOffsetForPlayer);
        if (buy) return buyPrice;
        float drug = thing.def.IsNonMedicalDrug ? negotiator.GetStatValue(StatDefOf.DrugSellPriceImprovement) : 0f;
        float produce = ModsConfig.IdeologyActive && (thing.def.IsLeather || thing.def.IsMeat || thing.def.IsWool) ? negotiator.GetStatValue(StatDefOf.AnimalProductsSellImprovement) : 0f;
        return Mathf.Min(buyPrice, TradeUtility.GetPricePlayerSell(thing, trader.TraderKind.PriceTypeFor(thing.def, TradeAction.PlayerSells).PriceMultiplier(), humanSell, gain, trader.TradePriceImprovementOffsetForPlayer, drug, produce, trader.TradeCurrency));
    }

    private static bool InSellablePosition(Thing thing)
    {
        // The native TradeDeal helper is private and its constructor opens the global session.
        // Keep its short position-only predicate here; all trader channel enumeration remains native.
        if (!thing.Spawned && (!ModsConfig.BiotechActive || thing.def != ThingDefOf.Genepack || thing.ParentHolder is not CompGenepackContainer) && (thing is not Book || thing.ParentHolder is not Building_Bookcase) && thing.ParentHolder is not Building_OutfitStand) return false;
        var position = thing.PositionHeld; var map = thing.MapHeld;
        if (map == null || position.Fogged(map)) return false;
        var room = thing.SpawnedParentOrMe.GetRoom();
        if (room != null)
            for (int i = 0; i < GenRadial.NumCellsInRadius(6.9f); i++)
            {
                var cell = position + GenRadial.RadialPattern[i];
                if (cell.InBounds(map) && cell.GetRoom(map) == room && cell.GetThingList(map).Any(item => item.PreventPlayerSellingThingsNearby(out _))) return false;
            }
        return true;
    }
}
