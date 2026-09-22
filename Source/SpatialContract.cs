using System;
using System.Linq;
using Newtonsoft.Json.Linq;
namespace Neko.RimWorld;

// One wire contract shared by the game catalog, validator and offline tests.
internal static class SpatialContract
{
    internal static JObject Catalog() => new JObject {
        ["version"] = "spatial_query.v1", ["operation"] = "spatial_query",
        ["description"] = "Bounded, frame-sliced identity resolution followed by native Pawn reachability at the final tick. Lookup time interval is reported. No action or ETA inference.",
        ["route_summary"] = "native_path_summary.v1",
        ["parameters"] = new JObject {
            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("from", "to"),
            ["properties"] = new JObject { ["from"] = Reference("pawn"), ["to"] = Reference("thing", true), ["detail"] = new JObject { ["type"] = "string", ["enum"] = new JArray("summary", "route"), ["default"] = "summary", ["description"] = "summary checks reachability only; route also computes a native path in the same call. No ETA." } }
        }
    };
    private static JObject Reference(string kind, bool cell = false)
    {
        JObject Text() => new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 256 };
        var props = new JObject { ["kind"] = new JObject { ["type"] = "string", ["enum"] = cell ? new JArray(kind, "cell") : new JArray(kind) }, ["id"] = Text(), ["name"] = Text(), ["map"] = Text() };
        var noCoordinates = new JObject { ["not"] = new JObject { ["anyOf"] = new JArray(new JObject { ["required"] = new JArray("x") }, new JObject { ["required"] = new JArray("z") }) } };
        var forms = new JArray();
        foreach (var key in new[] { "id", "name" }) forms.Add(new JObject {
            ["required"] = new JArray(key), ["properties"] = new JObject { ["kind"] = new JObject { ["const"] = kind } },
            ["not"] = new JObject { ["required"] = new JArray(key == "id" ? "name" : "id") },
            ["allOf"] = new JArray(noCoordinates.DeepClone())
        });
        if (cell) {
            foreach (var axis in new[] { "x", "z" }) props[axis] = new JObject { ["type"] = "integer", ["minimum"] = int.MinValue, ["maximum"] = int.MaxValue };
            forms.Add(new JObject {
                ["required"] = new JArray("x", "z"), ["properties"] = new JObject { ["kind"] = new JObject { ["const"] = "cell" } },
                ["not"] = new JObject { ["anyOf"] = new JArray(new JObject { ["required"] = new JArray("id") }, new JObject { ["required"] = new JArray("name") }) }
            });
        }
        return new JObject { ["type"] = "object", ["properties"] = props, ["required"] = new JArray("kind"), ["additionalProperties"] = false, ["oneOf"] = forms };
    }

    internal static void Validate(JObject args)
    {
        CheckKeys(args, "spatial_query", "from", "to", "detail");
        if (args.TryGetValue("detail", out var detail) && (detail.Type != JTokenType.String || detail.ToString() is not ("summary" or "route")))
            throw ReadContext.Error("invalid_arguments", "detail must be summary or route.");
        foreach (var key in new[] { "from", "to" }) {
            if (args[key] is not JObject value) throw ReadContext.Error("invalid_arguments", key + " must be an object.");
            CheckKeys(value, key, "kind", "id", "name", "map", "x", "z");
            foreach (var field in new[] { "kind", "id", "name", "map" })
                if (value.TryGetValue(field, out var token) && (token.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)token) || token.ToString().Length > 256))
                    throw ReadContext.Error("invalid_arguments", key + "." + field + " must be nonempty text (at most 256 characters).");
            var kind = (string?)value["kind"];
            if (key == "from" ? kind != "pawn" : kind is not ("thing" or "cell")) throw ReadContext.Error("invalid_arguments", "Invalid " + key + ".kind.");
            if (kind == "cell") {
                foreach (var axis in new[] { "x", "z" })
                    if (value[axis]?.Type != JTokenType.Integer || !int.TryParse(value[axis]!.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
                        throw ReadContext.Error("invalid_arguments", "Cell x/z must be 32-bit integers.");
                if (value["id"] != null || value["name"] != null) throw ReadContext.Error("invalid_arguments", "Cell does not accept id/name.");
            } else if ((value["id"] != null) == (value["name"] != null) || value["x"] != null || value["z"] != null)
                throw ReadContext.Error("invalid_arguments", key + " requires exactly one of id/name, without coordinates.");
        }
    }

    private static void CheckKeys(JObject value, string path, params string[] allowed)
    {
        var unknown = value.Properties().FirstOrDefault(property => !allowed.Contains(property.Name, StringComparer.Ordinal));
        if (unknown != null) throw ReadContext.Error("invalid_arguments", path + " contains unsupported field " + unknown.Name + ".");
    }

}
