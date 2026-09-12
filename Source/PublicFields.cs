using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GraphQL;
using GraphQL.Types;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>One public vocabulary over the executable schema, shared by selection, conditions and aggregation.</summary>
internal sealed class PublicFields
{
    private readonly Schema schema;
    private readonly SelectionConditions conditions;
    private readonly Dictionary<string, Dictionary<string, FieldType>> fields = new();
    public PublicFields(Schema schema, SelectionConditions conditions)
    {
        this.schema = schema; this.conditions = conditions;
        foreach (var type in schema.AllTypes.OfType<IComplexGraphType>().Where(t => !t.Name.StartsWith("__", StringComparison.Ordinal)))
        {
            var mapped = new Dictionary<string, FieldType>(StringComparer.Ordinal);
            foreach (var field in type.Fields)
            {
                if (field.Metadata.ContainsKey("public_hidden")) continue;
                var key = field.Metadata.TryGetValue("public_name",out var publicName) ? (string)publicName! : Name(field.Name);
                if (mapped.ContainsKey(key)) throw new InvalidOperationException("Public name collision: " + type.Name + "." + key);
                mapped.Add(key, field);
            }
            fields.Add(type.Name, mapped);
        }
    }
    internal static string Name(string native) => native == "def" ? "definition" : Regex.Replace(Regex.Replace(native, "([A-Z]+)([A-Z][a-z])", "$1_$2"), "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
    internal IGraphType Type(string name) => schema.AllTypes[name] ?? throw Error("Unknown type: " + name);
    internal IReadOnlyDictionary<string, FieldType> Fields(string type) => fields.TryGetValue(type, out var found) ? found : throw Error("Not an object type: " + type);
    internal FieldType Field(string type, string name) => Fields(type).TryGetValue(name, out var field) ? field : throw Error("Unknown field " + type + "." + name + ". Available: " + string.Join(", ", Fields(type).Keys));
    internal static IGraphType Bare(IGraphType type) => type is NonNullGraphType n ? Bare(n.ResolvedType!) : type;
    internal static IGraphType ValueType(FieldType field) => field.Metadata.TryGetValue("collection_element", out var item) && item is IGraphType graph ? graph : Bare(field.ResolvedType!) is ListGraphType list ? Bare(list.ResolvedType!) : Bare(field.ResolvedType!);
    internal static bool IsList(FieldType field) => field.Metadata.ContainsKey("collection_element") || Bare(field.ResolvedType!) is ListGraphType;
    internal static bool IsScalar(FieldType field) => ValueType(field) is ScalarGraphType && !IsList(field);
    internal static bool CanSum(string type, FieldType field) => type == "Thing" && field.Name == "stackCount";
    internal Dictionary<string, object?> Arguments(FieldType field, JToken? token, string path)
    {
        var supplied = token as JObject ?? (token == null ? new JObject() : throw Error(path + ".args must be an object."));
        var translated = new JObject();
        foreach (var prop in supplied.Properties())
        {
            var argument = field.Arguments?.FirstOrDefault(a => Name(a.Name) == prop.Name);
            if (argument == null) throw Error(path + ": unknown argument " + prop.Name);
            translated[argument.Name] = prop.Value.DeepClone();
        }
        return SelectionConditions.Arguments(field, translated, path);
    }
    internal object? Read(string type, object value, FieldType field, IReadOnlyDictionary<string, object?> args, ReadContext context)
        => conditions.ReadField((IComplexGraphType)Type(type),field,value,args,context);

    internal JObject CompactDirectory(IEnumerable<string> roots)
    {
        var queue = new Queue<string>(roots); var result = new JObject();
        while (queue.Count > 0) {
            var name = queue.Dequeue(); if (result[name] != null || Type(name) is not IComplexGraphType) continue;
            var entry = new JObject(); result[name] = entry;
            foreach (var field in Fields(name)) {
                var target = ValueType(field.Value);
                var arguments = (field.Value.Arguments?.ToArray() ?? Array.Empty<QueryArgument>()).Where(a => a.Name is not ("where" or "first" or "after" or "offset")).Select(a => Name(a.Name) + (a.ResolvedType is NonNullGraphType ? "!" : "")).ToArray();
                entry[field.Key] = (IsList(field.Value) ? "[" + target.Name + "]" : target.Name) + (arguments.Length > 0 ? " args:" + string.Join(",",arguments) : "");
                if (target is IComplexGraphType) queue.Enqueue(target.Name);
            }
            if (Type(name) is IAbstractGraphType abstractType) foreach (var concrete in abstractType.PossibleTypes) queue.Enqueue(concrete.Name);
        }
        return result;
    }
    internal static JObject ArgumentSchema(QueryArgument argument)
    {
        JObject Convert(IGraphType t) {
            t = Bare(t);
            if (t is ListGraphType list) return new JObject { ["type"] = "array", ["items"] = Convert(list.ResolvedType!) };
            var result = new JObject { ["type"] = t is BooleanGraphType ? "boolean" : t is IntGraphType ? "integer" : t is FloatGraphType ? "number" : "string" };
            if (t is EnumerationGraphType e) result["enum"] = new JArray(e.Values.Select(v => v.Name));
            return result;
        }
        var schema = Convert(argument.ResolvedType!);
        if (argument.Metadata.TryGetValue("json_limits",out var raw) && raw is JObject limits) foreach (var p in limits.Properties()) schema[p.Name] = p.Value.DeepClone();
        schema["description"] = argument.Description;
        return schema;
    }

    internal JObject Predicate(string type, JToken token)
    {
        if (token is not JObject input || input.Count == 0) throw Error("where must be a nonempty object.");
        var output = new JObject(); var selected = new JObject(); var wrappers = new JArray();
        foreach (var p in input.Properties())
        {
            if (p.Name == "where") { wrappers.Add(Predicate(type,p.Value)); continue; }
            if (p.Name is "all" or "any")
            {
                if (p.Value is not JArray array || array.Count == 0) throw Error(p.Name + " requires a nonempty condition array.");
                output[p.Name] = new JArray(array.Select(x => Predicate(type, x))); continue;
            }
            if (p.Name == "not") { output[p.Name] = Predicate(type,p.Value); continue; }
            if (p.Name == "reach") {
                if (p.Value is not JObject relation || relation["field"]?.Type != JTokenType.String) throw Error("reach requires field and some/every/none.");
                var field = Field(type,(string)relation["field"]!); var converted = new JObject { ["field"] = field.Name };
                foreach (var part in relation.Properties().Where(x => x.Name != "field")) {
                    if (part.Name is "some" or "every" or "none") converted[part.Name] = Predicate(type,part.Value);
                    else if (part.Name == "args") converted["arguments"] = JObject.FromObject(Arguments(field,part.Value,"reach"));
                    else if (part.Name == "include_self") converted[part.Name] = part.Value.DeepClone();
                    else throw Error("Unsupported reach option " + part.Name);
                }
                output["reach"] = converted; continue;
            }
            if (p.Name == "on") {
                if (Type(type) is not IAbstractGraphType abstractType || p.Value is not JObject choices || choices.Count == 0) throw Error("on requires concrete types of an abstract object.");
                if (choices.Properties().Any(c => !abstractType.PossibleTypes.Any(t => t.Name == c.Name))) throw Error("Unsupported concrete type in on.");
                output["types"] = new JObject(choices.Properties().Select(c => new JProperty(c.Name,Predicate(c.Name,c.Value)))); continue;
            }
            AddPath(type, selected, p.Name.Split('.'), 0, p.Value);
        }
        if (selected.Count > 0) output["fields"] = selected;
        return wrappers.Count == 0 ? output : new JObject { ["all"] = new JArray(new[] { output }.Where(o => o.Count > 0).Cast<JToken>().Concat(wrappers)) };
    }
    private void AddPath(string type, JObject output, string[] path, int index, JToken value)
    {
        var field = Field(type,path[index]); var childType = ValueType(field); JObject predicate;
        if (index < path.Length - 1)
        {
            if (IsList(field) || childType is not IComplexGraphType) throw Error("Path " + string.Join(".",path) + " crosses a collection or scalar; use some/every/none for collections.");
            predicate = output[field.Name] as JObject ?? new JObject();
            var child = predicate["fields"] as JObject ?? new JObject();
            AddPath(childType.Name,child,path,index+1,value); predicate["fields"] = child;
        }
        else
        {
            if (value is not JObject condition) throw Error("Condition for " + field.Name + " must be an operator object.");
            predicate = new JObject();
            foreach (var op in condition.Properties())
            {
                if (op.Name == "args") { predicate["arguments"] = JObject.FromObject(Arguments(field,op.Value,field.Name)); continue; }
                if (op.Name is "some" or "every" or "none")
                {
                    if (!IsList(field)) throw Error(field.Name + " is not a collection.");
                    predicate[op.Name] = childType is IComplexGraphType ? Predicate(childType.Name,op.Value) : op.Value.DeepClone();
                }
                else if (op.Name == "where" && childType is IComplexGraphType && !IsList(field))
                {
                    var nested = Predicate(childType.Name,op.Value);
                    foreach (var part in nested.Properties()) predicate[part.Name] = part.Value.DeepClone();
                }
                else if (op.Name == "on" && childType is IAbstractGraphType)
                {
                    if (op.Value is not JObject choices) throw Error("on requires concrete type conditions.");
                    predicate["types"] = new JObject(choices.Properties().Select(c => new JProperty(c.Name,Predicate(c.Name,c.Value))));
                }
                else if (op.Name == "is_null" || childType is ScalarGraphType && !IsList(field) && new[] { "eq","ne","in","gt","gte","lt","lte","contains","starts_with","ends_with","case_sensitive" }.Contains(op.Name)) predicate[op.Name] = op.Value.DeepClone();
                else throw Error("Unsupported public operator " + op.Name + " for " + type + "." + path[index]);
            }
            if (output[field.Name] != null) throw Error("Duplicate/overlapping predicate path; use all to combine conditions.");
        }
        output[field.Name] = predicate;
    }
    internal JArray Describe(IEnumerable<string> names)
    {
        return new JArray(names.Select(name => {
            var type = Type(name);
            var description = new JObject { ["type"] = name, ["description"] = type.Description };
            if (type is IComplexGraphType)
                description["fields"] = new JArray(Fields(name).Select(p => new JObject {
                    ["name"] = p.Key,
                    ["type"] = ValueType(p.Value).Name, ["collection"] = IsList(p.Value),
                    ["default_reference"] = p.Value.Metadata.ContainsKey("default_reference"),
                    ["description"] = p.Value.Description ?? "Selected game field. Null without issue means not applicable; failed reads are reported separately.",
                    ["aggregate"] = CanSum(name,p.Value) ? new JArray("sum","min","max") : IsScalar(p.Value) && ValueType(p.Value) is IntGraphType or FloatGraphType ? new JArray("min","max") : new JArray(),
                    ["args"] = new JArray((p.Value.Arguments?.ToArray() ?? Array.Empty<QueryArgument>()).Where(a => a.Name is not ("where" or "first" or "after" or "offset")).Select(a => new JObject { ["name"] = Name(a.Name), ["type"] = a.ResolvedType!.ToString(), ["default"] = a.DefaultValue == null ? JValue.CreateNull() : JToken.FromObject(a.DefaultValue), ["required"] = a.ResolvedType is NonNullGraphType }))
                }));
            if (type is IAbstractGraphType abstractType) description["on"] = new JArray(abstractType.PossibleTypes.Select(t => t.Name));
            return description;
        }));
    }
    internal static ExecutionError Error(string message) => ReadContext.Error("invalid_query",message);
}
