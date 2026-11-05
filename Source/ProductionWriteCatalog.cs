using System;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal static class ProductionWriteCatalog
{
    internal static JObject SettingsSchema()
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

    private static JObject Operation(string name, JObject valueSchema, string extract)
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
        return spec;
    }

    internal static JObject[] Operations => new[] {
        Operation("set_production_bill", SettingsSchema(), "{'kind':'object_fields','path':['bills'],'key_field':'bill_id','key':{'arg':'bill'},'row_require':{'bill_kind':'production','supported_standard_production':true},'fields_from_value':true}"),
        Operation("copy_production_bill", new JObject { ["type"] = "boolean", ["const"] = true }, "{'kind':'keyed_row','path':['bills'],'key_field':'bill_id','key':{'arg':'bill'},'value_field':'copy_guard','allow_null':false}"),
        Operation("reorder_production_bills", JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'bill_ids':{'type':'array','uniqueItems':true,'items':{'type':'string','minLength':1}}},'required':['bill_ids']}"), "{'kind':'ordered_ids','path':['bills'],'id_field':'bill_id','expect_field':'bill_ids','complete_path':['complete'],'complete_value':true}"),
        Operation("remove_production_bill", new JObject { ["type"] = "boolean", ["const"] = false }, "{'kind':'keyed_row','path':['bills'],'key_field':'bill_id','key':{'arg':'bill'},'value_field':'removal_guard','allow_null':false}")
    };
}
