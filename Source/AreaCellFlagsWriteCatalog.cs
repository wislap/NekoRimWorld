using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class AreaCellFlagsWriteCatalog
{
    internal static JObject[] Operations => new[] { CellFlags() };
    private static JObject CellFlags()
    {
        var cells = JObject.Parse("{'type':'array','minItems':1,'maxItems':64,'items':{'type':'object','additionalProperties':false,'properties':{'x':{'type':'integer'},'z':{'type':'integer'},'enabled':{'type':'boolean'}},'required':['x','z','enabled']}}");
        var value = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = new JObject { ["cells"] = cells }, ["required"] = new JArray("cells") };
        var expected = (JObject)value.DeepClone(); expected["properties"]!["native_instance"] = new JObject { ["type"] = "string", ["minLength"] = 1 }; ((JArray)expected["required"]!).Add("native_instance");
        expected["properties"]!["cells"]!["items"]!["properties"]!["manual_id"] = new JObject { ["type"] = new JArray("string", "null") };
        ((JArray)expected["properties"]!["cells"]!["items"]!["required"]!).Add("manual_id");
        return new JObject { ["op"] = "set_area_cell_flags", ["schema"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JObject { ["op"] = new JObject { ["const"] = "set_area_cell_flags" }, ["map"] = new JObject { ["type"] = "string", ["minLength"] = 1 }, ["area"] = new JObject { ["type"] = "string", ["minLength"] = 1 }, ["value"] = value }, ["required"] = new JArray("op", "map", "area", "value") }, ["expect_schema"] = expected,
            ["description"] = "Update at most 64 selected area cells. Every unselected cell is preserved. Each selected coordinate, including an unchanged requested flag, has a stable area_flag:x:z ledger field. Requires current-map native roof qualification and never clears the opposite roof instruction.",
            ["observation"] = new JObject {
                ["read"] = new JObject { ["tool"] = "inspect_areas", ["native_operation"] = "inspect_areas", ["arguments"] = new JObject { ["map"] = new JObject { ["arg"] = "map" }, ["ids"] = new JArray(new JObject { ["arg"] = "area" }), ["cells"] = new JObject { ["value_path"] = new JArray("cells") } } },
                ["extract"] = new JObject { ["kind"] = "field", ["object_id"] = new JObject { ["arg"] = "area" }, ["path"] = new JArray("write_flags"), ["allow_null"] = false, ["objects_path"] = new JArray("objects"), ["identity_path"] = new JArray("lookup_id"),
                    ["status_path"] = new JArray("status"), ["status_value"] = "observed", ["require"] = new JArray(new JObject { ["path"] = new JArray("manual_ids_complete"), ["value"] = true }), ["match"] = new JArray(new JObject { ["path"] = new JArray("map_id"), ["value"] = new JObject { ["arg"] = "map" } }) } } };
    }
}
