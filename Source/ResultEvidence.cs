using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>Descriptions of existing returned values, never an additional game read or computation.</summary>
internal sealed class ResultEvidence
{
    public const string Version = "observed_values.v1";
    public readonly JObject Nodes = new();
    public readonly JObject Subjects = new();
    public JObject Export()
    {
        var compact = new JObject(); var contexts = new JObject(); var contextIds = new Dictionary<string, string>();
        var normalized = new JObject();
        foreach (var property in Nodes.Properties()) {
            var node = (JObject)property.Value.DeepClone();
            if (node["context"] is JObject context) {
                var key = context.ToString(Newtonsoft.Json.Formatting.None);
                if (!contextIds.TryGetValue(key, out var id)) { id = "c" + contextIds.Count; contextIds[key] = id; contexts[id] = context; }
                node.Remove("context"); node["context_ref"] = id;
            }
            normalized[property.Name] = node;
        }
        foreach (var group in normalized.Properties().GroupBy(p => Pattern(p.Name))) {
            var first = group.First().Value;
            if (group.All(p => JToken.DeepEquals(p.Value, first))) compact[group.Key] = first.DeepClone();
            else foreach (var node in group) compact[node.Name] = node.Value.DeepClone();
        }
        return new JObject { ["version"] = Version, ["nodes"] = compact, ["contexts"] = contexts, ["subjects"] = Subjects.DeepClone() };
    }
    public static string Pattern(string path) => Regex.Replace(path, @"/\d+(?=/|$)", "/~2");
    public static string Path(string parent, string key) => parent + "/" + key.Replace("~", "~0").Replace("/", "~1");

    /// <summary>
    /// Descriptions for a <c>public_query.v2</c> result. The query operation builds its own
    /// result shape and walks the returned document. These are descriptions
    /// only: nothing here reads the game or computes a new value.
    /// </summary>
    public static JObject PublicQuery(JObject data)
    {
        var evidence = new ResultEvidence();
        evidence.Tree(data, "", "Query", Version);
        if (data["results"] is not JArray results) return evidence.Export();
        for (var index = 0; index < results.Count; index++)
        {
            if (results[index] is not JObject result) continue;
            var basePath = "/results/" + index;
            var scope = result["scope"] as JObject;
            var context = new JObject {
                ["selection"] = scope?["args"]?.DeepClone() ?? new JObject(),
                ["scope"] = scope?["description"]?.DeepClone() ?? JValue.CreateNull(),
                ["where"] = scope?["where"]?.DeepClone() ?? new JObject(),
                // The display window belongs in the context so every rendered fact from
                // this reading carries its own coverage: returned/has_more is exactly what
                // "全部/唯一" claims need in order to be checkable.
                ["window"] = result["page"]?.DeepClone() ?? JValue.CreateNull(),
            };
            if (result["aggregates"] is JObject aggregates)
                foreach (var measure in aggregates.Properties())
                {
                    if (measure.Value is not JObject row) continue;
                    if (row["unit"]?.Type != JTokenType.String) continue;
                    var unit = (string)row["unit"]!;
                    if (string.IsNullOrEmpty(unit)) continue;
                    evidence.Measure(Path(basePath + "/aggregates", measure.Name), measure.Name,
                        context, unit, "value", "known_value", "complete");
                }
            evidence.Subjects[basePath] = new JObject {
                ["from"] = result["from"]?.DeepClone() ?? JValue.CreateNull(),
                ["selection"] = scope?["args"]?.DeepClone() ?? new JObject(),
            };
        }
        return evidence.Export();
    }
    private static string Kind(JToken value) => value is JObject ? "object" : value is JArray ? "collection" : "scalar";
    public void Tree(JToken value, string path, string name, string type, string? native = null)
    {
        Nodes[path] = new JObject { ["kind"] = Kind(value), ["name"] = name, ["type"] = type, ["native"] = native ?? name };
        if (value is JObject obj) foreach (var field in obj.Properties()) Tree(field.Value, Path(path, field.Name), field.Name, field.Value.Type.ToString(), name + "." + field.Name);
        if (value is JArray array) for (var i = 0; i < array.Count; i++) Tree(array[i], Path(path, i.ToString()), "item", array[i].Type.ToString(), native ?? name);
    }
    /// <summary>
    /// Record an already-returned aggregate as a reading. Public-query measures report
    /// <c>value</c>/<c>known_value</c>/<c>complete</c>/<c>unit</c>, which is a different shape
    /// from native scalar fields.
    /// The unit is inlined because the presentation contract requires a literal string.
    /// No additional game read or computation happens here.
    /// </summary>
    public void Measure(string path, string name, JObject context, string unit, string value,
        string? lower = null, string? state = null)
    {
        if (Nodes[path] is not JObject description) return;
        description["kind"] = "reading";
        description["name"] = name;
        description["context"] = context.DeepClone();
        // Only name a companion path that the result actually carried. `known_value`
        // is present only when the measure is incomplete, and an exact aggregate
        // produces no such field; naming it anyway makes the plugin's renderer raise
        // "Field was not returned" and rejects the whole submission.
        // `Tree()` walked the real document, so a missing node means a missing field.
        // Return a JToken, not a string: assigning `(string?)null` into a JObject picks
        // the string overload and yields a String-typed token holding null, which
        // renders as `null` but is not a JSON null and breaks a strict consumer.
        JToken Existing(string? field) =>
            field != null && Nodes.ContainsKey(Path(path, field))
                ? new JValue(Pattern(Path(path, field)))
                : JValue.CreateNull();
        description["reading"] = new JObject {
            ["value"] = Existing(value),
            ["unit"] = unit,
            ["lower"] = Existing(lower),
            ["state"] = Existing(state),
            ["present"] = null,
        };
        foreach (var field in new[] { value, lower, state })
            if (field != null && Nodes[Path(path, field)] is JObject child) child["bound_to"] = Pattern(path);
    }

}
