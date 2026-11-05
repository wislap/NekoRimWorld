using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

// Production policy and native counters are observations. No Job is created and
// ShouldDoNow is deliberately not called: it updates the bill's paused state.
internal static class ProductionReadings
{
    private static readonly FieldInfo? IngredientGetterClass = typeof(RecipeDef).GetField("ingredientValueGetterClass", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly FieldInfo? CustomName = typeof(Bill_Production).GetField("playerCustomName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    internal static JToken Text(string? value) => JsonValues.Text(value);
    internal static JObject Presentation(Bill_Production bill) => new() {
        ["custom_name"] = Text(CustomName?.GetValue(bill) as string), ["style"] = Text(bill.style?.defName), ["precept_id"] = Text(bill.precept?.GetUniqueLoadID()),
        ["global_style"] = bill.globalStyle, ["graphic_index"] = bill.graphicIndexOverride
    };
    internal static void CopyPresentation(Bill_Production source, Bill_Production target) {
        if (CustomName == null) throw new ArgumentException("Native custom bill name layout unavailable.");
        target.style = source.style; target.precept = source.precept; target.globalStyle = source.globalStyle; target.graphicIndexOverride = source.graphicIndexOverride;
        CustomName.SetValue(target, CustomName.GetValue(source));
    }
    internal static bool Standard(Bill_Production bill) => bill.GetType() == typeof(Bill_Production) || bill.GetType() == typeof(Bill_ProductionWithUft);
    internal static bool SurgeryReadOnly(RecipeDef recipe) => DefDatabase<ThingDef>.AllDefsListForReading.Any(d => d.category == ThingCategory.Pawn && ResearchReadings.UsesRecipe(d, recipe));
    internal static string? StorageKey(ISlotGroup? group) => group switch {
        SlotGroup slot when slot.parent is ILoadReferenceable reference => reference.GetUniqueLoadID(),
        ILoadReferenceable reference => reference.GetUniqueLoadID(),
        null => null,
        _ => throw new ArgumentException("This native storage group has no supported stable identity.")
    };
    internal static JObject FloatRangeValue(FloatRange range) => new() { ["min"] = JsonValues.Number(range.min), ["max"] = JsonValues.Number(range.max) };
    internal static JObject QualityRangeValue(QualityRange range) => new() { ["min"] = range.min.ToString(), ["max"] = range.max.ToString() };
    internal static JObject Filter(ThingFilter filter) => new() {
        ["definitions"] = new JArray(filter.AllowedThingDefs.Select(d => d.defName).OrderBy(n => n, StringComparer.Ordinal)),
        ["special_filters"] = new JArray(DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.OrderBy(d => d.defName, StringComparer.Ordinal)
            .Select(d => new JObject { ["definition"] = d.defName, ["allowed"] = filter.Allows(d) })),
        ["hit_points"] = FloatRangeValue(filter.AllowedHitPointsPercents),
        ["mental_break_chance"] = FloatRangeValue(filter.AllowedMentalBreakChance),
        ["quality"] = QualityRangeValue(filter.AllowedQualityLevels)
    };
    internal static JObject Settings(Bill_Production bill) => new() {
        ["suspended"] = bill.suspended, ["repeat_mode"] = bill.repeatMode?.defName,
        ["repeat_count"] = bill.repeatCount, ["target_count"] = bill.targetCount,
        ["pause_when_satisfied"] = bill.pauseWhenSatisfied, ["unpause_when_you_have"] = bill.unpauseWhenYouHave,
        ["include_equipped"] = bill.includeEquipped, ["include_tainted"] = bill.includeTainted,
        ["hp_range"] = FloatRangeValue(bill.hpRange), ["quality_range"] = QualityRangeValue(bill.qualityRange),
        ["limit_to_allowed_stuff"] = bill.limitToAllowedStuff,
        ["ingredient_search_radius"] = JsonValues.Number(bill.ingredientSearchRadius),
        ["allowed_skill_range"] = new JObject { ["min"] = bill.allowedSkillRange.min, ["max"] = bill.allowedSkillRange.max },
        ["worker"] = bill.PawnRestriction != null ? new JObject { ["kind"] = "pawn", ["id"] = bill.PawnRestriction.GetUniqueLoadID() }
            : new JObject { ["kind"] = bill.SlavesOnly ? "slaves" : bill.MechsOnly ? "mechs" : bill.NonMechsOnly ? "non_mechs" : "any" },
        ["store"] = new JObject { ["mode"] = Text(bill.GetStoreMode()?.defName), ["storage_id"] = Text(StorageKey(bill.GetSlotGroup())) },
        ["include_storage"] = Text(StorageKey(bill.GetIncludeSlotGroup())), ["ingredient_filter"] = Filter(bill.ingredientFilter)
    };

    internal static JObject Read(Building_WorkTable table, int limit = 100)
    {
        if (limit < 1 || limit > 100) throw new ArgumentException("Production bill limit must be 1-100.");
        WorldNativeAccess.RequireNative(table, "get_BillStack");
        var all = table.BillStack.Bills;
        return new JObject {
            ["id"] = table.GetUniqueLoadID(), ["map_id"] = table.MapHeld?.GetUniqueLoadID(), ["status"] = "observed",
            ["supported_bill_creation"] = table.GetType() == typeof(Building_WorkTable) || table.GetType() == typeof(Building_WorkTable_HeatPush),
            ["bill_owner"] = new JObject { ["id"] = table.GetUniqueLoadID(), ["role"] = "worktable", ["native_interface"] = "IBillGiver" },
            ["bill_ids"] = new JArray(all.Select(b => b.GetUniqueLoadID())),
            ["bills"] = new JArray(all.Take(limit).Select(ReadBill)), ["complete"] = all.Count <= limit,
            ["available_recipes"] = new JArray(DefDatabase<RecipeDef>.AllDefsListForReading.Where(r => ResearchReadings.UsesRecipe(table.def, r)).Select(r => new JObject {
                ["recipe"] = r.defName, ["available_now"] = ResearchReadings.RecipeAvailableReadOnly(r),
                ["supported_standard_production"] = !SurgeryReadOnly(r) && !r.mechResurrection && r.gestationCycles <= 0 && r.formingTicks <= 0,
                ["uses_unfinished_thing"] = r.UsesUnfinishedThing })),
            ["meaning"] = "Policy, recipe requirements and native target counter. Ingredient candidates are definitions, not available quantities or a runnable Job. Native counters use the game's count scope and may include equipped/carried items as configured."
        };
    }

    internal static JObject ReadBill(Bill bill)
    {
        WorldNativeAccess.RequireNative(bill, "get_Label", "GetUniqueLoadID");
        var row = new JObject { ["bill_id"] = bill.GetUniqueLoadID(), ["bill_kind"] = bill is Bill_Production ? "production" : bill is Bill_Medical ? "medical" : "other",
            ["runtime_type"] = bill.GetType().FullName, ["recipe"] = bill.recipe?.defName, ["label"] = bill.Label,
            ["recipe_available_now"] = bill.recipe != null && ResearchReadings.RecipeAvailableReadOnly(bill.recipe) is bool available ? new JValue(available) : JValue.CreateNull() };
        if (bill is not Bill_Production production || bill.recipe == null) return row;
        row["supported_standard_production"] = Standard(production);
        if (!Standard(production)) { row["suspended"] = bill.suspended; row["repeat_mode"] = production.repeatMode?.defName; row["repeat_count"] = production.repeatCount; row["target_count"] = production.targetCount; row["details_status"] = "unsupported_bill_type"; return row; }
        row.Merge(Settings(production));
        row["presentation"] = Presentation(production);
        if (bill.billStack?.billGiver is Building_WorkTable table) { row["removal_guard"] = VerseWriteFieldResolver.ProductionRemovalGuard(table, production); row["copy_guard"] = VerseWriteFieldResolver.ProductionCopyGuard(table, production); }
        row["paused"] = production.paused;
        var ingredients = bill.recipe.ingredients;
        row["ingredients"] = new JArray(ingredients.Select((ingredient, index) => {
            var candidates = ingredient.filter.AllowedThingDefs.OrderBy(d => d.defName, StringComparer.Ordinal).ToArray();
            return new JObject { ["index"] = index, ["base_count"] = ingredient.GetBaseCount(), ["fixed"] = ingredient.IsFixedIngredient,
                ["candidates"] = new JArray(candidates.Select(def => new JObject { ["definition"] = def.defName,
                    ["bill_allows"] = bill.ingredientFilter.Allows(def), ["recipe_fixed_filter_allows"] = bill.recipe.fixedIngredientFilter.Allows(def),
                    ["effective_definition_allowed"] = bill.IsFixedOrAllowedIngredient(def),
                    ["required_count"] = RequiredCount(ingredient, def, bill.recipe), ["required_count_source"] = "standard_native_ingredient_value" })), ["complete"] = true };
        }));
        row["products"] = new JArray(bill.recipe.products.Select(p => new JObject { ["definition"] = p.thingDef.defName, ["count"] = p.count }));
        row["allow_mixing_ingredients"] = bill.recipe.allowMixingIngredients;
        row["take_entire_stacks"] = bill.recipe.ignoreIngredientCountTakeEntireStacks;
        row["base_work_amount"] = bill.recipe.workAmount; row["work_skill"] = bill.recipe.workSkill?.defName;
        row["skill_requirements"] = new JArray(bill.recipe.skillRequirements?.Select(s => new JObject { ["skill"] = s.skill.defName, ["min_level"] = s.minLevel }) ?? Enumerable.Empty<JObject>());
        row["native_count"] = NativeCount(production);
        if (production is Bill_ProductionWithUft withUft) {
            var uft = withUft.BoundUft;
            row["unfinished"] = uft == null ? JValue.CreateNull() : new JObject { ["id"] = uft.GetUniqueLoadID(), ["definition"] = uft.def.defName,
                ["work_left"] = uft.workLeft, ["destroyed"] = uft.Destroyed, ["spawned"] = uft.Spawned,
                ["map_id"] = uft.MapHeld?.GetUniqueLoadID(), ["creator_id"] = uft.Creator?.GetUniqueLoadID(),
                ["continuation_creator"] = uft.Creator == null ? JValue.CreateNull() : new JObject { ["id"] = uft.Creator.GetUniqueLoadID(),
                    ["spawned"] = uft.Creator.Spawned, ["downed"] = uft.Creator.Downed, ["destroyed"] = uft.Creator.Destroyed,
                    ["host_faction"] = uft.Creator.HostFaction?.GetUniqueLoadID() }, ["bound_bill_id"] = uft.BoundBill?.GetUniqueLoadID(),
                ["ingredients"] = new JArray(uft.ingredients.Select(t => new JObject { ["id"] = t.GetUniqueLoadID(), ["definition"] = t.def.defName, ["count"] = t.stackCount })) };
        } else row["unfinished"] = JValue.CreateNull();
        return row;
    }

    private static JToken RequiredCount(IngredientCount ingredient, ThingDef def, RecipeDef recipe)
    {
        // CountRequiredOfFor would initialize live RecipeDef worker caches. All
        // standard nonmedical native workers inherit this base-count method.
        var workerMethod = recipe.workerClass?.GetMethod("GetIngredientCount");
        if (workerMethod?.DeclaringType != typeof(RecipeWorker)) return JValue.CreateNull();
        IngredientValueGetter getter;
        var getterClass = IngredientGetterClass?.GetValue(recipe) as Type;
        if (getterClass == typeof(IngredientValueGetter_Volume)) getter = new IngredientValueGetter_Volume();
        else if (getterClass == typeof(IngredientValueGetter_Nutrition)) getter = new IngredientValueGetter_Nutrition();
        else return JValue.CreateNull();
        var perUnit = getter.ValuePerUnitOf(def);
        if (perUnit <= 0 || float.IsNaN(perUnit) || float.IsInfinity(perUnit)) return JValue.CreateNull();
        return new JValue(UnityEngine.Mathf.CeilToInt(ingredient.GetBaseCount() / perUnit));
    }

    private static JObject NativeCount(Bill_Production bill)
    {
        // Only the verified standard native counter is called. Other counters
        // have recipe-specific behavior and require their own explicit support.
        if (bill.recipe.workerCounterClass != null && bill.recipe.workerCounterClass != typeof(RecipeWorkerCounter))
            return new JObject { ["status"] = "unknown", ["reason"] = "unsupported_counter", ["runtime_type"] = bill.recipe.workerCounterClass.FullName };
        var counter = new RecipeWorkerCounter { recipe = bill.recipe };
        WorldNativeAccess.RequireNative(counter, "CanCountProducts", "CountProducts");
        if (!counter.CanCountProducts(bill)) return new JObject { ["status"] = "unavailable", ["reason"] = "recipe_not_countable" };
        return new JObject { ["status"] = "observed", ["count"] = counter.CountProducts(bill), ["source"] = "RecipeWorkerCounter.CountProducts" };
    }
}
