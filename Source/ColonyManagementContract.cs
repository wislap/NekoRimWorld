using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class ColonyManagementContract
{
    internal const string Version = "colony_management.v1";
    internal static readonly string[] Operations = { "inspect_production", "inspect_food_policies", "construction_catalog", "inspect_construction", "preview_construction", "inspect_construction_intents", "inspect_areas" };
    internal static JObject Catalog() => new() { ["version"] = Version, ["operations"] = new JArray(Operations.Select(op => new JObject { ["operation"] = op, ["parameters"] = Parameters(op) })) };
    private static JObject Text() => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 256 };
    internal static JObject Parameters(string operation) {
        var p = new JObject(); var required = new JArray();
        if (operation != "inspect_food_policies" && operation != "construction_catalog") { p["map"] = Text(); required.Add("map"); }
        if (operation == "inspect_production" || operation == "inspect_food_policies" || operation == "inspect_construction_intents" || operation == "inspect_areas") p["ids"] = new JObject { ["type"] = "array", ["items"] = Text(), ["minItems"] = 1, ["maxItems"] = 100 };
        if (operation == "inspect_construction_intents") p["plan_id"] = Text();
        if (operation == "inspect_areas") p["cells"] = JObject.Parse("{'type':'array','minItems':1,'maxItems':64,'description':'Read only these existing area cells. Optional enabled values are ignored selectors, never write instructions. Requires exactly one area ID.','items':{'type':'object','additionalProperties':false,'properties':{'x':{'type':'integer'},'z':{'type':'integer'},'enabled':{'type':'boolean'}},'required':['x','z']}}");
        if (operation == "construction_catalog") p["names"] = new JObject { ["type"] = "array", ["items"] = Text(), ["minItems"] = 1, ["maxItems"] = 100 };
        if (operation == "inspect_construction") { p["bounds"] = JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'min_x':{'type':'integer'},'min_z':{'type':'integer'},'width':{'type':'integer','minimum':1,'maximum':64},'height':{'type':'integer','minimum':1,'maximum':64}},'required':['min_x','min_z','width','height']}"); required.Add("bounds"); }
        else if (operation == "preview_construction") { p["elements"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 64, ["items"] = ElementSchema() }; required.Add("elements"); }
        else { p["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0 }; p["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 }; }
        return new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = p, ["required"] = required };
    }
    internal static JObject ElementSchema() => JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'plan_id':{'type':'string','minLength':1,'maxLength':128},'element_id':{'type':'string','minLength':1,'maxLength':128},'definition':{'type':'string','minLength':1},'stuff':{'type':['string','null']},'x':{'type':'integer'},'z':{'type':'integer'},'rotation':{'type':'integer','minimum':0,'maximum':3},'kind':{'type':'string','enum':['building','floor']}},'required':['plan_id','element_id','definition','x','z','rotation','kind']}");
    internal static void Validate(string operation, JObject args) {
        if (!Operations.Contains(operation)) throw ReadContext.Error("invalid_arguments", "Unknown colony-management operation.");
        var schema = Parameters(operation); var props = (JObject)schema["properties"]!;
        if (args.Properties().Any(p => props[p.Name] == null) || ((JArray)schema["required"]!).Values<string>().Any(k => args[k!] == null)) throw ReadContext.Error("invalid_arguments", "Missing or unknown colony-management argument.");
        foreach (var p in args.Properties()) { var type = (string?)props[p.Name]!["type"]; if (type == "string" && (p.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)p.Value)) || type == "array" && p.Value is not JArray || type == "object" && p.Value is not JObject || type == "integer" && p.Value.Type != JTokenType.Integer) throw ReadContext.Error("invalid_arguments", "Invalid " + p.Name + "."); }
        foreach (string k in new[] { "ids", "names" }) if (args[k] != null && (ColonyReadContract.Texts(args, k).Length > 100)) throw ReadContext.Error("invalid_arguments", "At most 100 selectors are allowed.");
        if (args["offset"] != null && ((long)args["offset"]! < 0 || (long)args["offset"]! > int.MaxValue) || args["limit"] != null && ((long)args["limit"]! < 1 || (long)args["limit"]! > 100)) throw ReadContext.Error("invalid_arguments", "Invalid page.");
        if (args["bounds"] is JObject b && (b.Properties().Count() != 4 || new[] { "min_x", "min_z", "width", "height" }.Any(k => b[k]?.Type != JTokenType.Integer) || (long)b["width"]! < 1 || (long)b["width"]! > 64 || (long)b["height"]! < 1 || (long)b["height"]! > 64)) throw ReadContext.Error("invalid_arguments", "Bounds must be a rectangle of at most 64 by 64 cells.");
        if (args["elements"] is JArray e) { if (e.Count < 1 || e.Count > 64) throw ReadContext.Error("invalid_arguments", "Select 1..64 elements."); foreach (var row in e) ValidateElement(row as JObject); if (e.Select(x => (string?)x["element_id"]).Distinct(StringComparer.Ordinal).Count() != e.Count) throw ReadContext.Error("invalid_arguments", "Element IDs must be unique."); }
    }
    internal static void ValidateElement(JObject? e) {
        if (e == null || e.Properties().Any(p => !new[] { "plan_id", "element_id", "definition", "stuff", "x", "z", "rotation", "kind" }.Contains(p.Name)) || new[] { "plan_id", "element_id", "definition", "kind" }.Any(k => e[k]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)e[k])) || new[] { "x", "z", "rotation" }.Any(k => e[k]?.Type != JTokenType.Integer) || (long)e["rotation"]! < 0 || (long)e["rotation"]! > 3 || ((string?)e["kind"] != "building" && (string?)e["kind"] != "floor") || (e["stuff"] != null && e["stuff"]!.Type != JTokenType.Null && e["stuff"]!.Type != JTokenType.String)) throw ReadContext.Error("invalid_arguments", "Invalid construction element.");
    }
}
