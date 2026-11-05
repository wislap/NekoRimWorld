using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class ConstructionWriteCatalog
{
    internal static JObject[] Operations => new[] { Placement(), Cancellation(), Restart() };
    private static JObject Placement() {
        var schema = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = new JObject { ["op"] = new JObject { ["const"] = "place_blueprint" }, ["map"] = new JObject { ["type"] = "string", ["minLength"] = 1 }, ["value"] = ColonyManagementContract.ElementSchema() }, ["required"] = new JArray("op", "map", "value") };
        return new JObject { ["op"] = "place_blueprint", ["schema"] = schema, ["expect_schema"] = JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'element_id':{'type':'string'},'map_id':{'type':'string'},'element':{'type':'object'},'stage':{'const':'absent'},'site':{'type':'array','items':{'type':'object'}}},'required':['element_id','map_id','element','stage','site']}"), ["description"] = "Place one normally qualified visible building or floor blueprint. No object clearing, replacement, instant construction or roof creation. Undo is limited to the exact unchanged unstarted blueprint.",
            ["verification_read"] = IntentRead(true),
            ["observation"] = new JObject { ["read"] = new JObject { ["tool"] = "look", ["arguments"] = new JObject { ["object"] = new JObject { ["kind"] = "construction_site", ["map"] = new JObject { ["arg"] = "map" } }, ["aspects"] = new JArray("state"), ["elements"] = new JArray(new JObject { ["value_path"] = new JArray() }) }, ["native_operation"] = "preview_construction" },
                ["extract"] = new JObject { ["kind"] = "field", ["object_id"] = new JObject { ["value_path"] = new JArray("element_id") }, ["path"] = new JArray("state"), ["allow_null"] = false, ["objects_path"] = new JArray("elements"), ["identity_path"] = new JArray("element_id"), ["status_path"] = new JArray("status"), ["status_value"] = "eligible", ["match"] = new JArray(new JObject { ["path"] = new JArray("element"), ["value"] = new JObject { ["value_path"] = new JArray() } }, new JObject { ["path"] = new JArray("map_id"), ["value"] = new JObject { ["arg"] = "map" } }) } } };
    }
    private static JObject IntentRead(bool valueTarget) => new() { ["tool"] = "inspect_construction_intents", ["native_operation"] = "inspect_construction_intents", ["arguments"] = new JObject { ["map"] = new JObject { ["arg"] = "map" }, ["plan_id"] = valueTarget ? new JObject { ["value_path"] = new JArray("plan_id") } : new JObject { ["arg"] = "plan_id" }, ["ids"] = new JArray(valueTarget ? new JObject { ["value_path"] = new JArray("element_id") } : new JObject { ["arg"] = "element_id" }) } };
    private static JObject Cancellation() => new() { ["op"] = "cancel_blueprint", ["schema"] = JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'op':{'const':'cancel_blueprint'},'map':{'type':'string','minLength':1},'plan_id':{'type':'string','minLength':1},'element_id':{'type':'string','minLength':1},'value':{'type':'boolean','const':false}},'required':['op','map','plan_id','element_id','value']}"), ["expect_schema"] = new JObject { ["type"] = "object" }, ["verification_read"] = IntentRead(false), ["observation"] = new JObject { ["read"] = IntentRead(false), ["extract"] = new JObject { ["kind"] = "field", ["object_id"] = new JObject { ["arg"] = "element_id" }, ["path"] = new JArray("cancel_guard"), ["allow_null"] = false, ["objects_path"] = new JArray("objects"), ["identity_path"] = new JArray("element_id"), ["status_path"] = new JArray("stage"), ["status_value"] = "blueprint", ["match"] = new JArray(new JObject { ["path"] = new JArray("plan_id"), ["value"] = new JObject { ["arg"] = "plan_id" } }, new JObject { ["path"] = new JArray("map_id"), ["value"] = new JObject { ["arg"] = "map" } }) } } };
    private static JObject Restart() {
        var spec = Placement(); spec["op"] = "restart_blueprint"; spec["schema"]!["properties"]!["op"]!["const"] = "restart_blueprint";
        spec["expect_schema"] = JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'intent':{'type':'object'},'site':{'type':'object'}},'required':['intent','site']}");
        spec["observation"]!["read"] = IntentRead(true); var extract = (JObject)spec["observation"]!["extract"]!; extract["path"] = new JArray("restart_guard"); extract["objects_path"] = new JArray("objects"); extract["status_path"] = new JArray("status"); extract["status_value"] = "observed";
        return spec;
    }
}
