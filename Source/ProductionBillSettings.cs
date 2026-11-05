using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

// Validation, normalization and native application of production bill settings.
internal static class ProductionBillSettings
{
    internal static readonly string[] Names = { "suspended", "repeat_mode", "repeat_count", "target_count", "pause_when_satisfied", "unpause_when_you_have",
        "include_equipped", "include_tainted", "hp_range", "quality_range", "limit_to_allowed_stuff", "ingredient_search_radius", "allowed_skill_range", "worker", "store", "include_storage", "ingredient_filter" };

    internal static JToken Normalize(string name, JToken value)
    {
        if (name == "ingredient_search_radius" && (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)) return JsonValues.Number((float)value);
        if (name == "hp_range" && value is JObject range) { var copy = (JObject)range.DeepClone(); foreach (var key in new[] { "min", "max" }) if (copy[key]?.Type == JTokenType.Integer || copy[key]?.Type == JTokenType.Float) copy[key] = JsonValues.Number((float)copy[key]!); return copy; }
        if (name == "ingredient_filter" && value is JObject filter) {
            var copy = (JObject)filter.DeepClone();
            if (copy["definitions"] is JArray defs) copy["definitions"] = new JArray(defs.OrderBy(t => (string?)t, StringComparer.Ordinal));
            if (copy["special_filters"] is JArray specials) copy["special_filters"] = new JArray(specials.OrderBy(t => (string?)t["definition"], StringComparer.Ordinal));
            if (copy["hit_points"] != null) copy["hit_points"] = Normalize("hp_range", copy["hit_points"]!);
            if (copy["mental_break_chance"] != null) copy["mental_break_chance"] = Normalize("hp_range", copy["mental_break_chance"]!);
            return copy;
        }
        return value;
    }
    private static ISlotGroup? ProductionStorage(Building_WorkTable table, JToken value)
    {
        if (value.Type == JTokenType.Null) return null;
        if (value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)value)) throw new ArgumentException("Storage identity must be a nonempty string or null.");
        var id = value.Value<string>()!;
        var slots = table.MapHeld.haulDestinationManager.AllGroupsListForReading;
        var matches = slots.Cast<ISlotGroup>().Concat(slots.Where(s => s.StorageGroup != null).Select(s => (ISlotGroup)s.StorageGroup)).Distinct()
            .Where(s => ProductionReadings.StorageKey(s) == id).ToArray();
        if (matches.Length != 1) throw new ArgumentException("Storage group not found on the worktable map.");
        return matches[0];
    }
    private static Pawn ProductionWorker(Building_WorkTable table, string? id) => table.MapHeld.mapPawns.AllPawns.SingleOrDefault(p => p.GetUniqueLoadID() == id && NativeFactions.IsPlayer(p.Faction) && !p.Destroyed)
        ?? throw new ArgumentException("Player pawn not found on the worktable map.");
    internal static JObject Shape(JToken value, params string[] keys)
    {
        if (value is not JObject obj || !obj.Properties().Select(p => p.Name).OrderBy(n => n).SequenceEqual(keys.OrderBy(n => n))) throw new ArgumentException("Invalid production setting shape.");
        return obj;
    }
    private static double Number(JToken? value, double min, double max, bool integer = false)
    {
        if (value == null || (integer ? value.Type != JTokenType.Integer : value.Type != JTokenType.Integer && value.Type != JTokenType.Float)) throw new ArgumentException("Invalid production number.");
        var number = (double)value;
        if (double.IsNaN(number) || double.IsInfinity(number) || number < min || number > max) throw new ArgumentException("Production number outside supported bounds.");
        return number;
    }
    private static FloatRange ProductionFloatRange(JToken value)
    {
        var obj = Shape(value, "min", "max"); var min = Number(obj["min"], 0, 1); var max = Number(obj["max"], min, 1); return new FloatRange((float)min, (float)max);
    }
    private static QualityRange ProductionQualityRange(JToken value)
    {
        var obj = Shape(value, "min", "max");
        QualityCategory Read(string key) { if (obj[key]?.Type != JTokenType.String || !Enum.TryParse((string?)obj[key], false, out QualityCategory quality) || !Enum.IsDefined(typeof(QualityCategory), quality)) throw new ArgumentException("Unknown quality."); return quality; }
        var min = Read("min"); var max = Read("max"); if (min > max) throw new ArgumentException("Inverted quality range."); return new QualityRange(min, max);
    }
    internal static void Validate(Building_WorkTable table, RecipeDef recipe, string name, JToken value)
    {
        switch (name) {
            case "suspended": case "pause_when_satisfied": case "include_equipped": case "include_tainted": case "limit_to_allowed_stuff":
                if (value.Type != JTokenType.Boolean) throw new ArgumentException("Production setting must be boolean."); return;
            case "repeat_count": case "target_count": case "unpause_when_you_have": Number(value, 0, int.MaxValue, true); return;
            case "repeat_mode":
                if (value.Type != JTokenType.String || !new[] { "Forever", "RepeatCount", "TargetCount" }.Contains((string?)value) || DefDatabase<BillRepeatModeDef>.GetNamedSilentFail(value.Value<string>()!) == null) throw new ArgumentException("Unknown repeat mode."); return;
            case "hp_range": ProductionFloatRange(value); return;
            case "quality_range": ProductionQualityRange(value); return;
            case "ingredient_search_radius": Number(value, 3, Bill.MaxIngredientSearchRadius); return;
            case "allowed_skill_range": var range = Shape(value, "min", "max"); Number(range["max"], Number(range["min"], 0, 20, true), 20, true); return;
            case "worker":
                if (value is not JObject worker || worker["kind"]?.Type != JTokenType.String) throw new ArgumentException("Invalid worker restriction.");
                var kind = (string)worker["kind"]!;
                Shape(value, kind == "pawn" ? new[] { "kind", "id" } : new[] { "kind" });
                if (kind == "pawn") { if (worker["id"]?.Type != JTokenType.String) throw new ArgumentException("Pawn identity required."); ProductionWorker(table, (string?)worker["id"]); }
                else if (!new[] { "any", "slaves", "mechs", "non_mechs" }.Contains(kind)) throw new ArgumentException("Unknown worker restriction."); return;
            case "store":
                var store = Shape(value, "mode", "storage_id"); var mode = (string?)store["mode"];
                if (store["mode"]?.Type != JTokenType.String || !new[] { "DropOnFloor", "BestStockpile", "SpecificStockpile" }.Contains(mode)) throw new ArgumentException("Unknown product store mode.");
                var group = ProductionStorage(table, store["storage_id"]!);
                if ((mode == "SpecificStockpile") != (group != null)) throw new ArgumentException("SpecificStockpile requires a storage group; other modes require null.");
                if (DefDatabase<BillStoreModeDef>.GetNamedSilentFail(mode!) == null) throw new ArgumentException("Store mode unavailable."); return;
            case "include_storage": ProductionStorage(table, value); return;
            case "ingredient_filter":
                var filter = Shape(value, "definitions", "special_filters", "hit_points", "mental_break_chance", "quality");
                if (filter["definitions"] is not JArray definitions || definitions.Any(d => d.Type != JTokenType.String) || definitions.Select(d => d.Value<string>()!).Distinct(StringComparer.Ordinal).Count() != definitions.Count) throw new ArgumentException("Unique ingredient definitions required.");
                foreach (var token in definitions) { var def = DefDatabase<ThingDef>.GetNamedSilentFail(token.Value<string>()!) ?? throw new ArgumentException("Ingredient definition not found."); if (!OfficialContent.Contains(def) || !recipe.IsIngredient(def)) throw new ArgumentException("Ingredient is not an official ingredient of this recipe."); }
                if (filter["special_filters"] is not JArray specials) throw new ArgumentException("Complete special-filter settings required.");
                var names = new List<string>(); foreach (var token in specials) { var special = Shape(token, "definition", "allowed"); if (special["definition"]?.Type != JTokenType.String || special["allowed"]?.Type != JTokenType.Boolean) throw new ArgumentException("Invalid special-filter entry."); names.Add((string)special["definition"]!); }
                if (!names.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.Select(d => d.defName).OrderBy(n => n, StringComparer.Ordinal))) throw new ArgumentException("Special-filter settings must cover every loaded filter exactly once.");
                ProductionFloatRange(filter["hit_points"]!); ProductionFloatRange(filter["mental_break_chance"]!); ProductionQualityRange(filter["quality"]!); return;
            default: throw new ArgumentException("Unknown production setting.");
        }
    }
    internal static void Write(Building_WorkTable table, Bill_Production bill, string name, JToken value)
    {
        switch (name) {
            case "suspended": bill.suspended = (bool)value; break;
            case "repeat_mode": bill.repeatMode = DefDatabase<BillRepeatModeDef>.GetNamed(value.Value<string>()!); break;
            case "repeat_count": bill.repeatCount = (int)value; break;
            case "target_count": bill.targetCount = (int)value; break;
            case "pause_when_satisfied": bill.pauseWhenSatisfied = (bool)value; break;
            case "unpause_when_you_have": bill.unpauseWhenYouHave = (int)value; break;
            case "include_equipped": bill.includeEquipped = (bool)value; break;
            case "include_tainted": bill.includeTainted = (bool)value; break;
            case "limit_to_allowed_stuff": bill.limitToAllowedStuff = (bool)value; break;
            case "ingredient_search_radius": bill.ingredientSearchRadius = (float)value; break;
            case "hp_range": bill.hpRange = ProductionFloatRange(value); break;
            case "quality_range": bill.qualityRange = ProductionQualityRange(value); break;
            case "allowed_skill_range": bill.allowedSkillRange = new IntRange((int)value["min"]!, (int)value["max"]!); break;
            case "worker": switch ((string?)value["kind"]) { case "pawn": bill.SetPawnRestriction(ProductionWorker(table, (string?)value["id"])); break; case "slaves": bill.SetAnySlaveRestriction(); break; case "mechs": bill.SetAnyMechRestriction(); break; case "non_mechs": bill.SetAnyNonMechRestriction(); break; default: bill.SetAnyPawnRestriction(); break; } break;
            case "store": bill.SetStoreMode(DefDatabase<BillStoreModeDef>.GetNamed((string)value["mode"]!), ProductionStorage(table, value["storage_id"]!)); break;
            case "include_storage": bill.SetIncludeGroup(ProductionStorage(table, value)); break;
            case "ingredient_filter":
                var filter = bill.ingredientFilter;
                foreach (var def in filter.AllowedThingDefs.ToArray()) filter.SetAllow(def, false);
                foreach (var token in (JArray)value["definitions"]!) filter.SetAllow(DefDatabase<ThingDef>.GetNamed(token.Value<string>()!), true);
                foreach (var token in (JArray)value["special_filters"]!) filter.SetAllow(DefDatabase<SpecialThingFilterDef>.GetNamed((string)token["definition"]!), (bool)token["allowed"]!);
                filter.AllowedHitPointsPercents = ProductionFloatRange(value["hit_points"]!); filter.AllowedMentalBreakChance = ProductionFloatRange(value["mental_break_chance"]!); filter.AllowedQualityLevels = ProductionQualityRange(value["quality"]!); break;
            default: throw new ArgumentException("Unknown production setting.");
        }
    }
}
