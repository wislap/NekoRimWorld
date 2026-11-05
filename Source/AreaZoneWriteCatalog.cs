using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class AreaZoneWriteCatalog
{
    private static JObject CellSchema() => JObject.Parse("{'type':'array','maxItems':1024,'items':{'type':'object','additionalProperties':false,'properties':{'x':{'type':'integer'},'z':{'type':'integer'}},'required':['x','z']}}");
    private static JObject Operation(string op, string? target)
    {
        var properties = new JObject { ["op"] = new JObject { ["const"] = op }, ["map"] = new JObject { ["type"] = "string", ["minLength"] = 1 } };
        var required = new JArray("op", "map", "value");
        var valueProperties = new JObject { ["cells"] = CellSchema() }; var valueRequired = new JArray("cells");
        if (target != null) { properties[target] = new JObject { ["type"] = "string", ["minLength"] = 1 }; required.Add(target); }
        else { if (op == "create_zone") { valueProperties["kind"] = new JObject { ["enum"] = new JArray("stockpile", "growing") }; valueRequired.Add("kind"); } valueProperties["label"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 }; }
        properties["value"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = valueProperties, ["required"] = valueRequired };
        var expected = target == null ? JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'map_id':{'type':'string','minLength':1},'zone_ids':{'type':'array','items':{'type':'string'}},'allowed_area_ids':{'type':'array','items':{'type':'string'}}},'required':['map_id','zone_ids','allowed_area_ids']}")
            : new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = new JObject { ["native_instance"] = new JObject { ["type"] = "string", ["minLength"] = 1 }, ["cells"] = JObject.Parse("{'type':'array','items':{'type':'object','additionalProperties':false,'properties':{'x':{'type':'integer'},'z':{'type':'integer'}},'required':['x','z']}}") }, ["required"] = new JArray("native_instance", "cells") };
        if (op == "set_area_cells") { expected["properties"]!["manual_revision"] = new JObject { ["type"] = "string" }; ((JArray)expected["required"]!).Add("manual_revision"); }
        var arguments = new JObject { ["map"] = new JObject { ["arg"] = "map" }, ["limit"] = 100 }; if (target != null) arguments["ids"] = new JArray(new JObject { ["arg"] = target });
        var extract = new JObject
        {
            ["kind"] = "field",
            ["object_id"] = target == null ? JValue.CreateNull() : new JObject { ["arg"] = target },
            ["path"] = new JArray(target == null ? "structure" : "write_cells"),
            ["allow_null"] = false,
            ["objects_path"] = target == null ? new JArray() : new JArray("objects"),
            ["identity_path"] = target == null ? new JArray() : new JArray("lookup_id"),
            ["status_path"] = new JArray("status"),
            ["status_value"] = target == null ? "ok" : "observed"
        };
        if (op == "set_area_cells") extract["require"] = new JArray(new JObject { ["path"] = new JArray("manual_ids_complete"), ["value"] = true });
        extract["match"] = new JArray(new JObject { ["path"] = new JArray("map_id"), ["value"] = new JObject { ["arg"] = "map" } });
        return new JObject
        {
            ["op"] = op,
            ["schema"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties, ["required"] = required },
            ["expect_schema"] = expected,
            ["description"] = "Current player map only, with native designator qualification. Zone edits preserve Home, cannot overlap another zone and require one connected nonempty region. Roof areas cannot clear the opposite roof instruction implicitly.",
            ["observation"] = new JObject { ["read"] = new JObject { ["tool"] = "inspect_areas", ["arguments"] = arguments, ["native_operation"] = "inspect_areas" }, ["extract"] = extract }
        };
    }
    internal static JObject[] Operations => new[] { Operation("create_zone", null), Operation("create_allowed_area", null), Operation("set_zone_cells", "zone"), Operation("set_area_cells", "area"), Removal(true), Removal(false) };
    private static JObject Removal(bool zone)
    {
        var op = zone ? "remove_zone" : "remove_allowed_area"; var target = zone ? "zone" : "area";
        var expected = JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'map_id':{'type':'string','minLength':1},'zone_ids':{'type':'array','items':{'type':'string'}},'allowed_area_ids':{'type':'array','items':{'type':'string'}},'removed_id':{'type':'string','minLength':1},'removed':{'type':'object'}},'required':['map_id','zone_ids','allowed_area_ids','removed_id','removed']}");
        var properties = new JObject
        {
            ["op"] = new JObject { ["const"] = op },
            ["map"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            [target] = new JObject { ["type"] = "string", ["minLength"] = 1 },
            ["value"] = new JObject { ["const"] = false }
        };
        return new JObject
        {
            ["op"] = op,
            ["schema"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties, ["required"] = new JArray("op", "map", target, "value") },
            ["expect_schema"] = expected,
            ["description"] = "Remove an ordinary unreferenced zone or allowed area. Ground inventory and plants are retained. Undo retains the original native object and requires its cells, configuration, references and map structure to remain compatible.",
            ["observation"] = new JObject
            {
                ["read"] = new JObject { ["tool"] = "inspect_areas", ["arguments"] = new JObject { ["map"] = new JObject { ["arg"] = "map" }, ["ids"] = new JArray(new JObject { ["arg"] = target }), ["limit"] = 100 }, ["native_operation"] = "inspect_areas" },
                ["extract"] = new JObject
                {
                    ["kind"] = "field",
                    ["object_id"] = new JObject { ["arg"] = target },
                    ["path"] = new JArray("delete_guard"),
                    ["allow_null"] = false,
                    ["objects_path"] = new JArray("objects"),
                    ["identity_path"] = new JArray("id"),
                    ["status_path"] = new JArray("status"),
                    ["status_value"] = "observed",
                    ["match"] = new JArray(new JObject { ["path"] = new JArray("map_id"), ["value"] = new JObject { ["arg"] = "map" } })
                }
            }
        };
    }
}
