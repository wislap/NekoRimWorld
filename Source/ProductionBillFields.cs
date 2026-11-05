using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal sealed partial class VerseWriteFieldResolver
{
    private static readonly string[] ProductionSettingNames = { "suspended", "repeat_mode", "repeat_count", "target_count", "pause_when_satisfied", "unpause_when_you_have",
        "include_equipped", "include_tainted", "hp_range", "quality_range", "limit_to_allowed_stuff", "ingredient_search_radius", "allowed_skill_range", "worker", "store", "include_storage", "ingredient_filter" };

    private static JObject ProductionSettingsSchema()
    {
        JObject Text() => new() { ["type"] = "string", ["minLength"] = 1 };
        JObject Range(JObject value) => new() { ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JObject { ["min"] = value.DeepClone(), ["max"] = value.DeepClone() }, ["required"] = new JArray("min", "max") };
        var quality = new JObject { ["type"] = "string", ["enum"] = new JArray(Enum.GetNames(typeof(QualityCategory))) };
        var fraction = new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 };
        var properties = new JObject();
        foreach (var name in new[] { "suspended", "pause_when_satisfied", "include_equipped", "include_tainted", "limit_to_allowed_stuff" }) properties[name] = new JObject { ["type"] = "boolean" };
        foreach (var name in new[] { "repeat_count", "target_count", "unpause_when_you_have" }) properties[name] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = int.MaxValue };
        properties["repeat_mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray("Forever", "RepeatCount", "TargetCount") };
        properties["hp_range"] = Range(fraction); properties["quality_range"] = Range(quality);
        properties["ingredient_search_radius"] = new JObject { ["type"] = "number", ["minimum"] = 3, ["maximum"] = Bill.MaxIngredientSearchRadius };
        properties["allowed_skill_range"] = Range(new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 20 });
        properties["worker"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JObject { ["kind"] = new JObject { ["enum"] = new JArray("any", "pawn", "slaves", "mechs", "non_mechs") }, ["id"] = Text() }, ["required"] = new JArray("kind") };
        properties["store"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JObject { ["mode"] = new JObject { ["enum"] = new JArray("DropOnFloor", "BestStockpile", "SpecificStockpile") }, ["storage_id"] = new JObject { ["type"] = new JArray("string", "null") } }, ["required"] = new JArray("mode", "storage_id") };
        properties["include_storage"] = new JObject { ["type"] = new JArray("string", "null") };
        properties["ingredient_filter"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JObject {
                ["definitions"] = new JObject { ["type"] = "array", ["uniqueItems"] = true, ["items"] = Text() },
                ["special_filters"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
                    ["properties"] = new JObject { ["definition"] = Text(), ["allowed"] = new JObject { ["type"] = "boolean" } }, ["required"] = new JArray("definition", "allowed") } },
                ["hit_points"] = Range(fraction), ["mental_break_chance"] = Range(fraction), ["quality"] = Range(quality) }, ["required"] = new JArray("definitions", "special_filters", "hit_points", "mental_break_chance", "quality") };
        return new JObject { ["type"] = "object", ["minProperties"] = 1, ["additionalProperties"] = false, ["properties"] = properties };
    }

    private static Registration ProductionRegistration(string name, JObject valueSchema, string extract, Func<VerseWriteFieldResolver, JObject, IReadOnlyList<ResolvedWriteField>> resolve)
    {
        var properties = new JObject { ["op"] = new JObject { ["const"] = name }, ["worktable"] = new JObject { ["type"] = "string", ["minLength"] = 1 }, ["map"] = new JObject { ["type"] = "string", ["minLength"] = 1 }, ["value"] = valueSchema };
        var required = new JArray("op", "worktable", "map", "value");
        if (name != "reorder_production_bills") { properties["bill"] = new JObject { ["type"] = "string", ["minLength"] = 1 }; required.Add("bill"); }
        var extraction = JObject.Parse(extract);
        extraction["object_id"] = new JObject { ["arg"] = "worktable" }; extraction["objects_path"] = new JArray("objects");
        extraction["identity_path"] = new JArray("id"); extraction["status_path"] = new JArray("status"); extraction["status_value"] = "observed";
        extraction["match"] = new JArray(new JObject { ["value"] = new JObject { ["arg"] = "map" }, ["path"] = new JArray("map_id") });
        var spec = new JObject { ["op"] = name, ["schema"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties, ["required"] = required },
            ["observation"] = new JObject { ["read"] = new JObject { ["tool"] = "inspect_production", ["native_operation"] = "inspect_production", ["arguments"] = new JObject { ["map"] = new JObject { ["arg"] = "map" }, ["ids"] = new JArray(new JObject { ["arg"] = "worktable" }), ["limit"] = 100 } }, ["extract"] = extraction } };
        if (name == "reorder_production_bills") spec["expect_schema"] = valueSchema.DeepClone();
        else if (name == "copy_production_bill") spec["expect_schema"] = JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'bill_ids':{'type':'array','uniqueItems':true,'items':{'type':'string','minLength':1}},'source':{'type':'object','additionalProperties':false,'properties':{'bill_id':{'type':'string','minLength':1},'recipe':{'type':'string','minLength':1},'state_fingerprint':{'type':'string','minLength':1}},'required':['bill_id','recipe','state_fingerprint']}},'required':['bill_ids','source']}");
        else if (name == "remove_production_bill") spec["expect_schema"] = JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'bill_ids':{'type':'array','uniqueItems':true,'items':{'type':'string','minLength':1}},'bill_id':{'type':'string','minLength':1},'recipe':{'type':'string','minLength':1},'state_fingerprint':{'type':'string','minLength':1},'unfinished_id':{'type':['string','null']}},'required':['bill_ids','bill_id','recipe','state_fingerprint','unfinished_id']}");
        return new Registration(spec, resolve);
    }

    private static Registration[] ProductionRegistrations => new[] {
        ProductionRegistration("set_production_bill", ProductionSettingsSchema(), "{'kind':'object_fields','path':['bills'],'key_field':'bill_id','key':{'arg':'bill'},'row_require':{'bill_kind':'production','supported_standard_production':true},'fields_from_value':true}", (r,o) => r.ResolveProductionSettings(o)),
        ProductionRegistration("copy_production_bill", new JObject { ["type"] = "boolean", ["const"] = true }, "{'kind':'keyed_row','path':['bills'],'key_field':'bill_id','key':{'arg':'bill'},'value_field':'copy_guard','allow_null':false}", (r,o) => One(r.ResolveProductionCopy(o),o)),
        ProductionRegistration("reorder_production_bills", JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'bill_ids':{'type':'array','uniqueItems':true,'items':{'type':'string','minLength':1}}},'required':['bill_ids']}"), "{'kind':'ordered_ids','path':['bills'],'id_field':'bill_id','expect_field':'bill_ids','complete_path':['complete'],'complete_value':true}", (r,o) => One(r.ResolveProductionOrder(o),o)),
        ProductionRegistration("remove_production_bill", new JObject { ["type"] = "boolean", ["const"] = false }, "{'kind':'keyed_row','path':['bills'],'key_field':'bill_id','key':{'arg':'bill'},'value_field':'removal_guard','allow_null':false}", (r,o) => One(r.ResolveProductionRemoval(o),o))
    };

    private static Building_WorkTable ProductionTable(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Worktable identity required.");
        var table = Find.Maps.SelectMany(m => m.listerBuildings.allBuildingsColonist).OfType<Building_WorkTable>().SingleOrDefault(t => t.GetUniqueLoadID() == id);
        if (table == null || table.Destroyed || (table.GetType() != typeof(Building_WorkTable) && table.GetType() != typeof(Building_WorkTable_HeatPush))) throw new ArgumentException("Supported player worktable not found.");
        return table;
    }
    private static Bill_Production ProductionTarget(Building_WorkTable table, string? id)
    {
        var bill = table.BillStack.Bills.OfType<Bill_Production>().SingleOrDefault(b => b.GetUniqueLoadID() == id);
        if (bill == null || bill.deleted || !ProductionReadings.Standard(bill) || !Official(bill.recipe)) throw new ArgumentException("Supported official production bill not found.");
        return bill;
    }
    private static Building_WorkTable ProductionTable(JObject operation)
    {
        var table = ProductionTable((string?)operation["worktable"]);
        if (operation["map"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)operation["map"]) || (string?)operation["map"] != table.MapHeld?.GetUniqueLoadID()) throw new ArgumentException("Worktable does not belong to the requested map identity.");
        return table;
    }
    private static void ProductionKeys(JObject operation, params string[] extras)
    {
        var allowed = new HashSet<string>(new[] { "op", "worktable", "map", "bill", "expect", "value", "evidence" }.Concat(extras), StringComparer.Ordinal);
        if (operation.Properties().Any(p => !allowed.Contains(p.Name))) throw new ArgumentException("Unexpected production operation property.");
    }
    private IReadOnlyList<ResolvedWriteField> ResolveProductionSettings(JObject operation)
    {
        ProductionKeys(operation);
        var table = ProductionTable(operation); var bill = ProductionTarget(table, (string?)operation["bill"]);
        if (operation["value"] is not JObject value || !value.HasValues || value.Properties().Any(p => !ProductionSettingNames.Contains(p.Name))) throw new ArgumentException("Invalid production settings.");
        if (operation["expect"] is not JObject expected || !expected.Properties().Select(p => p.Name).OrderBy(n => n).SequenceEqual(value.Properties().Select(p => p.Name).OrderBy(n => n))) throw new ArgumentException("Production settings and expected keys must match.");
        return value.Properties().Select(p => new ResolvedWriteField(new ProductionSettingField(table, bill, p.Name), expected[p.Name]!, NormalizeProductionSetting(p.Name, p.Value))).ToArray();
    }
    private static JToken NormalizeProductionSetting(string name, JToken value)
    {
        if (name == "ingredient_search_radius" && (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)) return JsonValues.Number((float)value);
        if (name == "hp_range" && value is JObject range) { var copy = (JObject)range.DeepClone(); foreach (var key in new[] { "min", "max" }) if (copy[key]?.Type == JTokenType.Integer || copy[key]?.Type == JTokenType.Float) copy[key] = JsonValues.Number((float)copy[key]!); return copy; }
        if (name == "ingredient_filter" && value is JObject filter) {
            var copy = (JObject)filter.DeepClone();
            if (copy["definitions"] is JArray defs) copy["definitions"] = new JArray(defs.OrderBy(t => (string?)t, StringComparer.Ordinal));
            if (copy["special_filters"] is JArray specials) copy["special_filters"] = new JArray(specials.OrderBy(t => (string?)t["definition"], StringComparer.Ordinal));
            if (copy["hit_points"] != null) copy["hit_points"] = NormalizeProductionSetting("hp_range", copy["hit_points"]!);
            if (copy["mental_break_chance"] != null) copy["mental_break_chance"] = NormalizeProductionSetting("hp_range", copy["mental_break_chance"]!);
            return copy;
        }
        return value;
    }
    private IPreparedWriteField ResolveProductionOrder(JObject operation) { ProductionKeys(operation); return new ProductionOrderField(ProductionTable(operation)); }
    private IPreparedWriteField ResolveProductionRemoval(JObject operation) { ProductionKeys(operation); var table = ProductionTable(operation); return new ProductionRemovalField(table, ProductionTarget(table, (string?)operation["bill"])); }
    private IPreparedWriteField ResolveProductionCopy(JObject operation) { ProductionKeys(operation); var table = ProductionTable(operation); return new ProductionCopyField(table, ProductionTarget(table, (string?)operation["bill"])); }

    internal IPreparedWriteField? ResolveLedgerProductionField(string id, string field, object? target = null)
    {
        if (!field.StartsWith("bill:", StringComparison.Ordinal)) return null;
        var name = field.Substring(5); if (!ProductionSettingNames.Contains(name)) return null;
        var pair = Find.Maps.SelectMany(m => m.listerBuildings.allBuildingsColonist).OfType<Building_WorkTable>()
            .SelectMany(t => t.BillStack.Bills.OfType<Bill_Production>().Select(b => (table: t, bill: b))).SingleOrDefault(p => p.bill.GetUniqueLoadID() == id);
        if (pair.bill == null || (target != null && !ReferenceEquals(target, pair.bill))) return null;
        return new ProductionSettingField(pair.table, pair.bill, name);
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
    private static Pawn ProductionWorker(Building_WorkTable table, string? id) => table.MapHeld.mapPawns.AllPawns.SingleOrDefault(p => p.GetUniqueLoadID() == id && p.Faction == Faction.OfPlayer && !p.Destroyed)
        ?? throw new ArgumentException("Player pawn not found on the worktable map.");
    private static JObject Shape(JToken value, params string[] keys)
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
    private static void ValidateProductionSetting(Building_WorkTable table, RecipeDef recipe, string name, JToken value)
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
                foreach (var token in definitions) { var def = DefDatabase<ThingDef>.GetNamedSilentFail(token.Value<string>()!) ?? throw new ArgumentException("Ingredient definition not found."); if (!Official(def) || !recipe.IsIngredient(def)) throw new ArgumentException("Ingredient is not an official ingredient of this recipe."); }
                if (filter["special_filters"] is not JArray specials) throw new ArgumentException("Complete special-filter settings required.");
                var names = new List<string>(); foreach (var token in specials) { var special = Shape(token, "definition", "allowed"); if (special["definition"]?.Type != JTokenType.String || special["allowed"]?.Type != JTokenType.Boolean) throw new ArgumentException("Invalid special-filter entry."); names.Add((string)special["definition"]!); }
                if (!names.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.Select(d => d.defName).OrderBy(n => n, StringComparer.Ordinal))) throw new ArgumentException("Special-filter settings must cover every loaded filter exactly once.");
                ProductionFloatRange(filter["hit_points"]!); ProductionFloatRange(filter["mental_break_chance"]!); ProductionQualityRange(filter["quality"]!); return;
            default: throw new ArgumentException("Unknown production setting.");
        }
    }
    private static void WriteProductionSetting(Building_WorkTable table, Bill_Production bill, string name, JToken value)
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

    private sealed class ProductionSettingField : IPreparedWriteField, IWriteAggregateScope
    {
        private readonly Building_WorkTable table; private readonly Bill_Production bill; private readonly string name;
        internal ProductionSettingField(Building_WorkTable table, Bill_Production bill, string name) { this.table = table; this.bill = bill; this.name = name; }
        public string ObjectId => bill.GetUniqueLoadID(); public string Field => "bill:" + name;
        public string AggregateScope => table.GetUniqueLoadID(); public bool ChangesStructure => false;
        public object NativeTarget => bill; public bool IsDestroyed => table.Destroyed || bill.deleted || !table.BillStack.Bills.Contains(bill);
        public bool MayChangeByGame => name == "repeat_count" || name == "suspended";
        public JToken Read() => ProductionReadings.Settings(bill)[name]!;
        public void Validate(JToken value) { if (IsDestroyed || !ProductionReadings.Standard(bill)) throw new ArgumentException("Production bill unavailable."); ValidateProductionSetting(table, bill.recipe, name, value); }
        public void Write(JToken value) => WriteProductionSetting(table, bill, name, value);
        public void Restore(JToken value) => Write(value);
    }

    private sealed class ProductionOrderField : IPreparedWriteField, IWriteAggregateScope
    {
        private readonly Building_WorkTable table;
        internal ProductionOrderField(Building_WorkTable table) { this.table = table; }
        public string ObjectId => table.GetUniqueLoadID(); public string Field => "production_bills"; public object NativeTarget => table;
        public bool IsDestroyed => table.Destroyed; public bool MayChangeByGame => false; public string AggregateScope => ObjectId; public bool ChangesStructure => true;
        public JToken Read() => new JObject { ["bill_ids"] = new JArray(table.BillStack.Bills.Select(b => b.GetUniqueLoadID())) };
        public void Validate(JToken value) {
            if (IsDestroyed) throw new ArgumentException("Worktable unavailable."); var obj = Shape(value, "bill_ids");
            if (obj["bill_ids"] is not JArray ids || ids.Any(t => t.Type != JTokenType.String) || !ids.Select(t => t.Value<string>()!).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(table.BillStack.Bills.Select(b => b.GetUniqueLoadID()).OrderBy(n => n, StringComparer.Ordinal))) throw new ArgumentException("Bill order must be a complete permutation of current identities.");
        }
        public void Write(JToken value) {
            var ids = ((JArray)value["bill_ids"]!).Select(t => t.Value<string>()!).ToArray();
            for (var i = 0; i < ids.Length; i++) { var bill = table.BillStack.Bills.Single(b => b.GetUniqueLoadID() == ids[i]); table.BillStack.Reorder(bill, i - table.BillStack.Bills.IndexOf(bill)); }
        }
        public void Restore(JToken value) => Write(value);
    }

    private static bool ProductionReferenced(Bill bill) => Find.Maps.SelectMany(m => m.mapPawns.AllPawns).Concat(Find.WorldPawns.AllPawnsAliveOrDead)
        .Any(p => p.jobs?.curJob?.bill == bill || p.jobs?.jobQueue.Any(j => j.job?.bill == bill) == true);
    internal static JObject ProductionRemovalGuard(Building_WorkTable table, Bill_Production bill) => new() {
        ["bill_ids"] = new JArray(table.BillStack.Bills.Select(b => b.GetUniqueLoadID())), ["bill_id"] = bill.GetUniqueLoadID(), ["recipe"] = bill.recipe.defName,
        ["state_fingerprint"] = new BillState().Fingerprint(bill), ["unfinished_id"] = ProductionReadings.Text((bill as Bill_ProductionWithUft)?.BoundUft?.GetUniqueLoadID())
    };
    internal static JObject ProductionCopyGuard(Building_WorkTable table, Bill_Production bill) => new() {
        ["bill_ids"] = new JArray(table.BillStack.Bills.Select(b => b.GetUniqueLoadID())),
        ["source"] = ProductionCopySource(bill)
    };
    private static JObject ProductionCopySource(Bill_Production bill) => new() { ["bill_id"] = bill.GetUniqueLoadID(), ["recipe"] = bill.recipe.defName, ["state_fingerprint"] = new BillState().Fingerprint(bill) };

    private sealed class ProductionCopyField : IPreparedStructuralWrite, IWriteAggregateScope
    {
        private readonly Building_WorkTable table; private readonly Bill_Production source; private readonly ProductionBillAddition addition;
        private bool applied;
        internal ProductionCopyField(Building_WorkTable table, Bill_Production source) { this.table = table; this.source = source; addition = new ProductionBillAddition(table, source); }
        public string ObjectId => table.GetUniqueLoadID(); public string Field => "production_bills"; public object NativeTarget => table;
        public string AggregateScope => ObjectId; public bool ChangesStructure => true; public bool IsDestroyed => table.Destroyed || source.deleted; public bool MayChangeByGame => false;
        public JToken Read() => applied ? addition.Read() : ProductionCopyGuard(table, source);
        private JObject Request() => new() { ["recipe"] = source.recipe.defName, ["settings"] = ProductionReadings.Settings(source) };
        public void Validate(JToken value) {
            if (IsDestroyed || !table.BillStack.Bills.Contains(source) || value.Type != JTokenType.Boolean || !(bool)value) throw new ArgumentException("Bill copy requires an existing standard source and value true.");
            addition.Validate(Request());
        }
        public void Write(JToken value) { applied = true; addition.Write(Request()); }
        public void Restore(JToken value) { addition.Restore(value); applied = false; }
        public JToken AppliedValue() => addition.AppliedValue();
        public JToken UndoExpected() { var expected = (JObject)addition.UndoExpected().DeepClone(); expected["source"] = ProductionCopySource(source); return expected; }
        public IPreparedWriteField CreateUndo() => new CopyUndo(this, addition.CreateUndo());
        private sealed class CopyUndo : IPreparedWriteField, IWriteAggregateScope {
            private readonly ProductionCopyField copy; private readonly IPreparedWriteField inner;
            internal CopyUndo(ProductionCopyField copy, IPreparedWriteField inner) { this.copy = copy; this.inner = inner; }
            public string ObjectId => copy.ObjectId; public string Field => copy.Field; public object NativeTarget => copy.table;
            public string AggregateScope => ObjectId; public bool ChangesStructure => true; public bool IsDestroyed => copy.IsDestroyed; public bool MayChangeByGame => false;
            public JToken Read() { var state = (JObject)inner.Read().DeepClone(); state["source"] = ProductionCopySource(copy.source); return state; }
            public void Validate(JToken value) { if (copy.source.deleted || !copy.table.BillStack.Bills.Contains(copy.source)) throw new ArgumentException("Copy source unavailable."); inner.Validate(value); }
            public void Write(JToken value) => inner.Write(value);
            public void Restore(JToken value) => inner.Restore(value);
        }
    }

    private sealed class ProductionRemovalField : IPreparedStructuralWrite, IWriteAggregateScope
    {
        private readonly Building_WorkTable table; private readonly Bill_Production bill; private int index = -1; private bool removed;
        private JObject? result;
        internal ProductionRemovalField(Building_WorkTable table, Bill_Production bill) { this.table = table; this.bill = bill; }
        public string ObjectId => table.GetUniqueLoadID(); public string Field => "production_bills"; public object NativeTarget => table;
        public bool IsDestroyed => table.Destroyed; public bool MayChangeByGame => false; public string AggregateScope => ObjectId; public bool ChangesStructure => true;
        public JToken Read() => removed ? new JObject { ["bill_ids"] = new JArray(table.BillStack.Bills.Select(b => b.GetUniqueLoadID())), ["removed"] = bill.GetUniqueLoadID() } : ProductionRemovalGuard(table, bill);
        public void Validate(JToken value) {
            if (IsDestroyed || bill.deleted || !table.BillStack.Bills.Contains(bill) || value.Type != JTokenType.Boolean || (bool)value) throw new ArgumentException("Bill removal requires an existing bill and value false.");
            if (bill is Bill_ProductionWithUft uft && uft.BoundUft != null) throw new ArgumentException("Suspend a bill with bound unfinished work; removal does not cancel or reclaim its materials.");
            if (ProductionReferenced(bill)) throw new ArgumentException("Bill is referenced by a current or queued Job.");
        }
        public void Write(JToken value) { index = table.BillStack.Bills.IndexOf(bill); removed = true; table.BillStack.Delete(bill); result = new JObject { ["bill_ids"] = new JArray(table.BillStack.Bills.Select(b => b.GetUniqueLoadID())), ["removed"] = bill.GetUniqueLoadID() }; }
        public void Restore(JToken value) { if (!removed) return; bill.deleted = false; bill.billStack = table.BillStack; if (!table.BillStack.Bills.Contains(bill)) table.BillStack.Bills.Insert(index, bill); removed = false; }
        public JToken AppliedValue() => result ?? throw new InvalidOperationException("Bill was not removed.");
        public JToken UndoExpected() => AppliedValue();
        public IPreparedWriteField CreateUndo() => new ProductionRestoreField(this);
        private sealed class ProductionRestoreField : IPreparedWriteField, IWriteAggregateScope {
            private readonly ProductionRemovalField removal;
            internal ProductionRestoreField(ProductionRemovalField removal) { this.removal = removal; }
            public string ObjectId => removal.ObjectId; public string Field => removal.Field; public object NativeTarget => removal.table;
            public bool IsDestroyed => removal.IsDestroyed; public bool MayChangeByGame => false; public string AggregateScope => ObjectId; public bool ChangesStructure => true;
            public JToken Read() => removal.removed ? new JObject { ["bill_ids"] = new JArray(removal.table.BillStack.Bills.Select(b => b.GetUniqueLoadID())), ["removed"] = removal.bill.GetUniqueLoadID() } : removal.Read();
            public void Validate(JToken value) { if (IsDestroyed || removal.table.BillStack.Count >= BillStack.MaxCount || removal.table.BillStack.Bills.Contains(removal.bill) || !removal.bill.deleted) throw new ArgumentException("Removed bill cannot be restored."); }
            public void Write(JToken value) => removal.Restore(value);
            public void Restore(JToken value) => removal.Write(new JValue(false));
        }
    }
}
