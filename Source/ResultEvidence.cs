using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GraphQL.Types;
using GraphQLParser.AST;
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
    private static string Kind(JToken value) => value is JObject ? "object" : value is JArray ? "collection" : "scalar";
    public void Tree(JToken value, string path, string name, string type, string? native = null)
    {
        Nodes[path] = new JObject { ["kind"] = Kind(value), ["name"] = name, ["type"] = type, ["native"] = native ?? name };
        if (value is JObject obj) foreach (var field in obj.Properties()) Tree(field.Value, Path(path, field.Name), field.Name, field.Value.Type.ToString(), name + "." + field.Name);
        if (value is JArray array) for (var i = 0; i < array.Count; i++) Tree(array[i], Path(path, i.ToString()), "item", array[i].Type.ToString(), native ?? name);
    }
    public void Reading(string path, JObject reading, string name, JObject context, string unit, string? lower = null, string? state = null, string? present = null)
    {
        var description = (JObject)Nodes[path]!;
        description["name"] = name;
        description["kind"] = "reading";
        description["context"] = context.DeepClone();
        description["reading"] = new JObject { ["value"] = Pattern(Path(path, "units")), ["unit"] = unit,
            ["lower"] = lower == null ? null : Pattern(Path(path, lower)), ["state"] = state == null ? null : Pattern(Path(path, state)),
            ["present"] = present == null ? null : Pattern(Path(path, present)) };
        foreach (var field in reading.Properties())
            if (Nodes[Path(path, field.Name)] is JObject child) {
                if (field.Name == "item_count") {
                    child["kind"] = "reading"; child["context"] = context.DeepClone();
                    child["reading"] = new JObject { ["value"] = Pattern(Path(path, field.Name)), ["unit"] = "item_records", ["state"] = state == null ? null : Pattern(Path(path, state)) };
                } else if (field.Name is "units" or "known_units" or "status" or "complete" or "entry_present") child["bound_to"] = Pattern(path);
            }
    }

    public static JObject Query(JObject data, GraphQLSelectionSet selection, IComplexGraphType root, ISchema schema, JObject variables, QueryIssue[] issues, IReadOnlyDictionary<string, JObject> resolvedFields)
    {
        var evidence = new ResultEvidence();
        evidence.Tree(data, "", "Query", "Query");
        void Visit(JToken value, GraphQLSelectionSet selections, IGraphType type, string path, JObject context)
        {
            foreach (var item in selections.Selections)
            {
                if (item is GraphQLInlineFragment fragment)
                {
                    var concrete = fragment.TypeCondition == null ? type : schema.AllTypes[fragment.TypeCondition.Type.Name.StringValue];
                    if (concrete != null) Visit(value, fragment.SelectionSet, concrete, path, context);
                    continue;
                }
                if (item is not GraphQLField field || value is not JObject obj) continue;
                var key = field.Alias?.Name.StringValue ?? field.Name.StringValue;
                var definition = (type as IComplexGraphType)?.GetField(field.Name.StringValue);
                if (definition == null || obj[key] == null) continue;
                var location = Path(path, key);
                resolvedFields.TryGetValue(location, out var trace);
                var actual = (string?)trace?["native"];
                if (actual != null && (actual.Split('.').Last() != field.Name.StringValue || type is IObjectGraphType && actual.Split('.')[0] != type.Name)) continue;
                var description = (JObject)evidence.Nodes[location]!;
                description["name"] = field.Name.StringValue;
                description["native"] = actual ?? type.Name + "." + field.Name.StringValue;
                description["type"] = definition.ResolvedType!.Name;
                var local = (JObject)context.DeepClone();
                var effective = trace?["arguments"] as JObject;
                if (effective?.Count > 0) local[actual ?? type.Name + "." + field.Name.StringValue] = effective.DeepClone();
                if (trace?["subject"] is JObject subject) { evidence.Subjects[path] = subject.DeepClone(); local["subject"] = new JObject { ["source"] = Pattern(path) }; }
                if (trace?["window"] is JObject window) {
                    var coverage = (JObject)window.DeepClone();
                    var containerPath = location.Substring(0, location.LastIndexOf('/'));
                    coverage["selected_fields_complete"] = !issues.Any(i => IssueBelow(i, containerPath));
                    local["window"] = coverage;
                }
                // Include only identities already selected; no additional getters are invoked.
                var identity = new JObject();
                foreach (var selected in selections.Selections.OfType<GraphQLField>())
                    if (selected.Name.StringValue is "id" or "defName" or "ref" or "mapId" or "key")
                    {
                        var identityValue = obj[selected.Alias?.Name.StringValue ?? selected.Name.StringValue];
                        if (identityValue is JValue && identityValue.Type != JTokenType.Null) identity[selected.Name.StringValue] = new JObject { ["path"] = Pattern(Path(path, selected.Alias?.Name.StringValue ?? selected.Name.StringValue)) };
                    }
                if (identity.Count > 0) local[type.Name] = identity;
                description["context"] = local;

                IGraphType child = definition.ResolvedType!;
                while (child is NonNullGraphType nonNull) child = nonNull.ResolvedType!;
                if (child is ListGraphType list) child = SelectionConditions.Bare(list.ResolvedType!);
                if (field.SelectionSet == null || child is not (IComplexGraphType or IAbstractGraphType)) continue;
                if (obj[key] is JArray array)
                    for (var i = 0; i < array.Count; i++) Visit(array[i], field.SelectionSet, child, Path(location, i.ToString()), local);
                else Visit(obj[key]!, field.SelectionSet, child, location, local);
            }
        }
        Visit(data, selection, root, "", new JObject());
        return evidence.Export();
    }
    private static bool IssueBelow(QueryIssue issue, string path)
    {
        var issuePath = ""; foreach (var part in issue.Path) issuePath = Path(issuePath, part.ToString());
        return issuePath == path || issuePath.StartsWith(path + "/", StringComparison.Ordinal) || path.StartsWith(issuePath + "/", StringComparison.Ordinal);
    }
}
