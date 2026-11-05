using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class PreparationWriteCatalog
{
    private static JObject Operation(string kind)
    {
        var op = "set_" + kind;
        var schema = new JObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JObject
            {
                ["op"] = new JObject { ["const"] = op },
                ["map"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["thing"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["value"] = new JObject { ["type"] = "boolean" }
            },
            ["required"] = new JArray("op", "map", "thing", "value")
        };
        return new JObject
        {
            ["op"] = op,
            ["schema"] = schema,
            ["description"] = "A normal preparation designation on the exact visible native object on the player's current map. Does not complete work, clear competing instructions or enable god mode. False removes only this designation.",
            ["observation"] = new JObject
            {
                ["read"] = new JObject { ["tool"] = "look", ["arguments"] = new JObject { ["object"] = new JObject { ["kind"] = "thing", ["id"] = new JObject { ["arg"] = "thing" }, ["map"] = new JObject { ["arg"] = "map" } }, ["aspects"] = new JArray("state"), ["focus"] = new JObject { ["state"] = new JArray("condition") } }, ["native_operation"] = "inspect_state" },
                ["extract"] = new JObject
                {
                    ["kind"] = "field",
                    ["object_id"] = new JObject { ["arg"] = "thing" },
                    ["path"] = new JArray("facets", "condition", "construction_designations", kind),
                    ["allow_null"] = false,
                    ["objects_path"] = new JArray("objects"),
                    ["identity_path"] = new JArray("target", "id"),
                    ["status_path"] = new JArray("facets", "condition", "status"),
                    ["status_value"] = "observed",
                    ["match"] = new JArray(new JObject { ["path"] = new JArray("target", "map"), ["value"] = new JObject { ["arg"] = "map" } })
                }
            }
        };
    }
    internal static readonly string[] Kinds = { "deconstruct", "mine", "cut_plant" };
    internal static JObject[] Operations => Kinds.Select(Operation).ToArray();
}
