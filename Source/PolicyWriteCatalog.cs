using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class PolicyWriteCatalog
{
    private static JObject FoodOperation(string op, string properties, string required, string path, string? policyArg = null)
    {
        var schema = JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'op':{'const':'" + op + "'}," + properties + "},'required':['op'," + required + "]}");
        var arguments = new JObject { ["object"] = new JObject { ["kind"] = "food_policy" }, ["aspects"] = new JArray("content", "users") };
        if (policyArg != null) arguments["object"]!["id"] = new JObject { ["arg"] = policyArg };
        var extract = new JObject
        {
            ["kind"] = "field",
            ["object_id"] = policyArg == null ? JValue.CreateNull() : new JObject { ["arg"] = policyArg },
            ["path"] = new JArray(path),
            ["allow_null"] = false,
            ["objects_path"] = policyArg == null ? new JArray() : new JArray("objects"),
            ["identity_path"] = policyArg == null ? new JArray() : new JArray("id"),
            ["status_path"] = new JArray("status"),
            ["status_value"] = policyArg == null ? "ok" : "observed"
        };
        var contentSchema = JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'native_instance':{'type':'string','minLength':1},'label':{'type':'string'},'filter':{'type':'object'},'users':{'type':'array','items':{'type':'string','minLength':1}},'users_complete':{'const':true}},'required':['native_instance','label','filter','users','users_complete']}");
        JObject expected;
        if (op == "set_food_policy_content") expected = contentSchema;
        else
        {
            expected = JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'policy_ids':{'type':'array','items':{'type':'string','minLength':1}}},'required':['policy_ids']}");
            if (policyArg != null)
            {
                var name = op == "copy_food_policy" ? "source" : "policy";
                expected["properties"]![name + "_id"] = JObject.Parse("{'type':'string','minLength':1}");
                expected["properties"]![name] = contentSchema;
                ((JArray)expected["required"]!).Add(name + "_id"); ((JArray)expected["required"]!).Add(name);
            }
        }
        return new JObject
        {
            ["op"] = op,
            ["schema"] = schema,
            ["expect_schema"] = expected,
            ["verification_read"] = new JObject { ["tool"] = "inspect_food_policies", ["native_operation"] = "inspect_food_policies", ["arguments"] = new JObject() },
            ["observation"] = new JObject { ["read"] = new JObject { ["tool"] = "look", ["arguments"] = arguments, ["native_operation"] = "inspect_food_policies" }, ["extract"] = extract }
        };
    }
    internal static readonly JObject[] Operations = {
        FoodOperation("create_food_policy", "'policy':{'const':'new'},'value':{'type':'object','additionalProperties':false,'properties':{'label':{'type':'string','minLength':1,'maxLength':128}},'required':['label']}", "'policy','value'", "structure"),
        FoodOperation("copy_food_policy", "'policy':{'type':'string','minLength':1},'value':{'type':'object','additionalProperties':false,'properties':{'label':{'type':'string','minLength':1,'maxLength':128}},'required':['label']}", "'policy','value'", "copy_expect", "policy"),
        FoodOperation("set_food_policy_content", "'policy':{'type':'string','minLength':1},'value':{'type':'object','additionalProperties':false,'properties':{'label':{'type':'string','minLength':1,'maxLength':128},'filter':{'type':'object'},'impact':{'type':'object'}},'required':['label','filter','impact']}", "'policy','value'", "write_content", "policy"),
        FoodOperation("delete_food_policy", "'policy':{'type':'string','minLength':1},'value':{'type':'object','additionalProperties':false,'properties':{'impact':{'type':'object'}},'required':['impact']}", "'policy','value'", "delete_expect", "policy"),
        JObject.Parse(@"{'op':'set_food_policy','schema':{'type':'object','additionalProperties':false,'properties':{'op':{'const':'set_food_policy'},'pawn':{'type':'string','minLength':1},'value':{'type':'object','additionalProperties':false,'properties':{'policy_id':{'type':'string','minLength':1},'native_instance':{'type':'string','minLength':1}},'required':['policy_id','native_instance']}},'required':['op','pawn','value']},'observation':{'read':{'tool':'look','arguments':{'object':{'kind':'pawn','id':{'arg':'pawn'}},'aspects':['work']},'native_operation':'inspect_state'},'extract':{'kind':'field','object_id':{'arg':'pawn'},'path':['facets','work','food_policy_key'],'allow_null':false,'objects_path':['objects'],'identity_path':['target','id'],'status_path':['facets','work','status'],'status_value':'observed'}}}")
    };
}
