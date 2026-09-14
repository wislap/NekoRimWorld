using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GraphQL;
using GraphQL.Execution;
using GraphQL.Resolvers;
using GraphQL.Types;
using GraphQLParser.AST;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>One typed predicate language over the executable public schema. No domain field registry.</summary>
internal sealed class SelectionConditions
{
    internal const int MaxDepth = 12, MaxNodes = 512;
    private readonly ISchema schema;
    internal delegate bool? Predicate(object? value, ReadContext context);
    public SelectionConditions(ISchema schema) {
        this.schema = schema;
        // New public fields inherit type behavior. A new scalar family must be
        // implemented before any partial predicate capability is advertised.
        foreach (var type in schema.AllTypes.Where(t => !t.Name.StartsWith("__", StringComparison.Ordinal)).OfType<IComplexGraphType>())
            foreach (var field in type.Fields) AssertType(field.ResolvedType!, type.Name + "." + field.Name);
    }
    private static void AssertType(IGraphType type, string field)
    {
        type = Bare(type);
        if (type is ListGraphType list) { AssertType(list.ResolvedType!, field); return; }
        if (type is IComplexGraphType or IAbstractGraphType or BooleanGraphType or IntGraphType or FloatGraphType or StringGraphType or IdGraphType or EnumerationGraphType) return;
        throw new InvalidOperationException("Public predicate type has no complete operator implementation: " + field + " (" + type.Name + ")");
    }

    internal static IGraphType Bare(IGraphType type) => type is NonNullGraphType n ? Bare(n.ResolvedType!) : type;
    internal static IGraphType? Element(FieldType field)
    {
        if (field.Metadata.TryGetValue("collection_element", out var item)) return (IGraphType)item!;
        return Bare(field.ResolvedType!) is ListGraphType list && !field.Metadata.ContainsKey("collection_view") ? Bare(list.ResolvedType!) : null;
    }
    internal static void Install(Schema schema)
    {
        var visited = new HashSet<IGraphType>();
        var predicate = new ConditionGraphType();
        void Visit(IGraphType type)
        {
            type = Bare(type);
            if (type is ListGraphType list) { Visit(list.ResolvedType!); return; }
            if (!visited.Add(type)) return;
            if (type is IAbstractGraphType abstractType) foreach (var concrete in abstractType.PossibleTypes) Visit(concrete);
            if (type is not IComplexGraphType complex) return;
            foreach (var field in complex.Fields)
            {
                var element = Element(field);
                if (element != null)
                {
                    field.Arguments ??= new QueryArguments();
                    field.Arguments.Add(GameArguments.Create("where", predicate,
                        "Predicate over " + element.Name + "; evaluated before pagination. fields/all/any/not; field operations follow public types. Unavailable membership is not false. See selection_contract and describe_schema."));
                    field.Metadata["collection_element"] = element;
                    // Slice and cursor producers apply the predicate before constructing their window.
                    if (!field.Metadata.ContainsKey("slice") && !field.Metadata.ContainsKey("cursor_page"))
                    {
                        var original = field.Resolver!;
                        field.Resolver = new FuncFieldResolver<object?>(c => {
                            var task = original.ResolveAsync(c);
                            if (!task.IsCompleted) throw new InvalidOperationException("Asynchronous game resolver.");
                            var value = task.GetAwaiter().GetResult();
                            if (value == null) return null;
                            var state = (ReadContext)c.UserContext["read"]!;
                            var where = c.Arguments != null && c.Arguments.TryGetValue("where", out var argument) ? argument.Value : null;
                            return state.Filter((IEnumerable)value, element.Name, where).ToArray();
                        });
                    }
                }
                Visit(field.ResolvedType!);
            }
        }
        Visit(schema.Query!);
        schema.RegisterType(predicate);
    }

    public static JObject Contract() => new() {
        ["version"] = "schema_conditions.v1", ["max_depth"] = MaxDepth, ["max_nodes"] = MaxNodes,
        ["rules"] = "All supplied clauses are AND. all/any accept nonempty condition arrays; not accepts one condition. Object: fields:{nativeField:condition}, is_null, match, types:{publicConcreteType:condition}. Field arguments go in arguments and use its existing schema/defaults. Boolean:eq/ne. Number:eq/ne/in/gt/gte/lt/lte. String/ID:eq/ne/in/contains/starts_with/ends_with, ordinal case-sensitive (case_sensitive:false opts into ordinal ignore-case). Enum:eq/ne/in over all declared values. Native flags also support has_all/has_any over comma-separated declared names. Nullable:is_null; null differs from failed read. Collection:some/every/none over its complete source before display paging. Empty every/none are true; a null collection does not satisfy a quantifier. Unknown reads use three-valued logic; undecidable membership makes the collection unavailable, never an observed empty set. reach:{field, some/every/none, include_self?, arguments?} traverses any public same-type reference/collection with shared cycle/depth/node limits; use parent for ancestors or a declared child collection for descendants. Wrapper items/nodes are display windows: apply where at their source field, not to a returned page. Exact selectors and where intersect. No purpose/category defaults.",
    };

    internal static JObject DescribeField(FieldType field) => new() {
        ["value_type"] = Bare(field.ResolvedType!).Name,
        ["element_type"] = Element(field)?.Name,
        ["display_window"] = field.Metadata.ContainsKey("collection_view"),
        ["value_domain"] = NativeEnumScalar.For(field)?.Describe(),
    };

    public Predicate Compile(string typeName, object? raw)
    {
        var type = schema.AllTypes[typeName] ?? throw Error("Unknown predicate type: " + typeName);
        if (raw == null) return (_, _) => true;
        var value = raw as JToken ?? JToken.FromObject(raw);
        var nodes = 0;
        return Build(type, value, typeName, 0, ref nodes);
    }

    private Predicate Build(IGraphType type, JToken token, string path, int depth, ref int nodes)
    {
        if (depth > MaxDepth || ++nodes > MaxNodes) throw Error("Condition exceeds depth/node limit at " + path);
        if (token is not JObject obj || obj.Count == 0) throw Error("Expected nonempty condition at " + path);
        type = Bare(type);
        var parts = new List<Predicate>();
        foreach (var property in obj.Properties())
        {
            var key = property.Name; var arg = property.Value; var at = path + "." + key;
            if (key is "all" or "any")
            {
                if (arg is not JArray list || list.Count == 0 || list.Count > MaxNodes) throw Error(at + " requires a bounded nonempty array.");
                var children = new List<Predicate>();
                foreach (var child in list) children.Add(Build(type, child, at, depth + 1, ref nodes));
                parts.Add((v, c) => Combine(children.Select(p => Evaluate(p, v, c)), key == "all"));
            }
            else if (key == "not")
            {
                var child = Build(type, arg, at, depth + 1, ref nodes);
                parts.Add((v, c) => !Evaluate(child, v, c));
            }
            else if (key == "is_null")
            {
                if (arg.Type != JTokenType.Boolean) throw Error(at + " must be boolean.");
                var wanted = (bool)arg; parts.Add((v, _) => (v == null) == wanted);
            }
            else if (key == "match" && type is IComplexGraphType)
            {
                var child = Build(type, arg, at, depth + 1, ref nodes);
                parts.Add((v, c) => v == null ? false : Evaluate(child, v, c));
            }
            else if (key == "fields" && type is IComplexGraphType complex)
            {
                if (arg is not JObject fields || fields.Count == 0) throw Error(at + " must contain public fields.");
                foreach (var fieldCondition in fields.Properties())
                {
                    var field = complex.GetField(fieldCondition.Name) ?? throw Error("Unknown field " + type.Name + "." + fieldCondition.Name);
                    if (fieldCondition.Value is not JObject input) throw Error(at + "." + field.Name + " must be an object.");
                    var condition = (JObject)input.DeepClone(); var arguments = Arguments(field, condition["arguments"], at + "." + field.Name);
                    condition.Remove("arguments");
                    var element = Element(field);
                    var childType = element == null ? (IGraphType?)NativeEnumScalar.For(field) ?? field.ResolvedType! : new ListGraphType((IGraphType?)NativeEnumScalar.For(field) ?? element);
                    var child = Build(childType, condition, at + "." + field.Name, depth + 1, ref nodes);
                    parts.Add((v, c) => v == null ? false : Evaluate(child, ReadField(complex, field, v, arguments, c), c));
                }
            }
            else if (key == "types" && type is IAbstractGraphType abstractType)
            {
                if (arg is not JObject choices || choices.Count == 0) throw Error(at + " requires concrete type conditions.");
                var compiled = new Dictionary<string, Predicate>();
                foreach (var choice in choices.Properties())
                {
                    var concrete = abstractType.PossibleTypes.FirstOrDefault(t => t.Name == choice.Name) ?? throw Error(at + ": type is not a public possible type: " + choice.Name);
                    compiled[choice.Name] = Build(concrete, choice.Value, at, depth + 1, ref nodes);
                }
                parts.Add((v, c) => v != null && compiled.TryGetValue(abstractType.ResolveType!(v)!.Name, out var selected) ? Evaluate(selected, v, c) : false);
            }
            else if (key is "some" or "every" or "none" && type is ListGraphType collection)
            {
                var child = Build(collection.ResolvedType!, arg, at, depth + 1, ref nodes);
                parts.Add((v, c) => v == null ? false : Quantify((IEnumerable)v, child, key, c));
            }
            else if (key == "reach" && type is IComplexGraphType owner)
            {
                if (arg is not JObject walk) throw Error(at + " must be an object.");
                GameQueryEngine.ExactKeys(walk, new[] { "field", "some", "every", "none", "include_self", "arguments" });
                var link = owner.GetField((string?)walk["field"] ?? "") ?? throw Error(at + " requires a public relation field.");
                var target = Element(link) ?? Bare(link.ResolvedType!);
                if (target != type) throw Error(at + " requires a same-type relation, not a guessed reverse relationship.");
                var mode = new[] { "some", "every", "none" }.Where(walk.ContainsKey).ToArray();
                if (mode.Length != 1 || walk["include_self"] != null && walk["include_self"]!.Type != JTokenType.Boolean) throw Error(at + " requires one quantifier and optional boolean include_self.");
                var arguments = Arguments(link, walk["arguments"], at);
                var child = Build(type, walk[mode[0]]!, at, depth + 1, ref nodes);
                var includeSelf = (bool?)walk["include_self"] ?? false;
                parts.Add((v, c) => v == null ? false : Quantify(Walk(owner, link, v, arguments, includeSelf, c), child, mode[0], c));
            }
            else if (key == "case_sensitive" && type is StringGraphType or IdGraphType)
            {
                if (arg.Type != JTokenType.Boolean) throw Error(at + " must be boolean.");
                if (!obj.Properties().Any(p => p.Name is "eq" or "ne" or "in" or "contains" or "starts_with" or "ends_with" or "has_all" or "has_any")) throw Error(at + " requires a string operation.");
            }
            else if (type is ScalarGraphType scalar)
                parts.Add(Scalar(scalar, key, arg, (bool?)obj["case_sensitive"] ?? true, at));
            else throw Error("Unsupported operator " + at + " for " + type.Name);
        }
        return (v, c) => Combine(parts.Select(p => Evaluate(p, v, c)), true);
    }

    private static Predicate Scalar(ScalarGraphType type, string op, JToken arg, bool sensitive, string path)
    {
        var numeric = type is IntGraphType or FloatGraphType;
        var text = type is StringGraphType or IdGraphType;
        var nativeEnum = type as NativeEnumScalar;
        if (nativeEnum != null && op is not ("eq" or "ne" or "in") && !(nativeEnum.Flags && op is "has_all" or "has_any")) throw Error(path + " requires " + string.Join("/", nativeEnum.Describe()["operators"]!) + " for " + nativeEnum.NativeType + ". Declared names: " + string.Join(", ", nativeEnum.Describe()["values"]!));
        if (op is not ("eq" or "ne" or "in") && !(nativeEnum?.Flags == true && op is "has_all" or "has_any") && !(numeric && op is "gt" or "gte" or "lt" or "lte") && !(text && op is "contains" or "starts_with" or "ends_with")) throw Error("Unsupported scalar operation " + path);
        if (type is BooleanGraphType && op == "in") throw Error("Boolean supports eq/ne: " + path);
        var values = op == "in" ? arg as JArray ?? throw Error(path + " must be an array.") : new JArray(arg.DeepClone());
        if (values.Count > 128) throw Error(path + " exceeds 128 values.");
        foreach (var value in values) ValidateScalar(type, value, path, sensitive);
        var comparison = sensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        bool Equal(object value, JToken other) => nativeEnum != null ? nativeEnum.Equal(value, other, sensitive) : numeric ? Convert.ToDouble(value, CultureInfo.InvariantCulture) == (double)other : text ? string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), (string?)other, comparison) : JToken.DeepEquals(JToken.FromObject(type.Serialize(value)!), other);
        return (v, _) => {
            if (v == null) return false;
            if (nativeEnum != null && op is "has_all" or "has_any") return nativeEnum.Has(v, arg, sensitive, op == "has_all");
            if (op == "in") return values.Any(x => Equal(v, x));
            if (op == "eq") return Equal(v, arg);
            if (op == "ne") return !Equal(v, arg);
            if (numeric) { var n = Convert.ToDouble(v, CultureInfo.InvariantCulture); var b = (double)arg; return op switch { "gt" => n > b, "gte" => n >= b, "lt" => n < b, _ => n <= b }; }
            var str = Convert.ToString(v, CultureInfo.InvariantCulture)!; var term = (string)arg!;
            return op switch { "contains" => str.IndexOf(term, comparison) >= 0, "starts_with" => str.StartsWith(term, comparison), _ => str.EndsWith(term, comparison) };
        };
    }
    private static void ValidateScalar(ScalarGraphType type, JToken value, string path, bool sensitive = true)
    {
        var valid = type switch {
            NativeEnumScalar nativeEnum => nativeEnum.Valid(value, sensitive),
            BooleanGraphType => value.Type == JTokenType.Boolean,
            IntGraphType => value.Type == JTokenType.Integer && (long)value >= int.MinValue && (long)value <= int.MaxValue,
            FloatGraphType => value.Type is JTokenType.Integer or JTokenType.Float && !double.IsNaN((double)value) && !double.IsInfinity((double)value),
            EnumerationGraphType enumeration => value.Type == JTokenType.String && enumeration.Values.Any(x => x.Name == (string?)value),
            StringGraphType or IdGraphType => value.Type == JTokenType.String && value.ToString().Length <= 4096,
            _ => false,
        };
        if (!valid) throw Error(path + " has an invalid " + type.Name + " value; use is_null for null." + (type is NativeEnumScalar domain ? " Declared names: " + string.Join(", ", domain.Describe()["values"]!) : ""));
    }
    internal static Dictionary<string, object?> Arguments(FieldType field, JToken? supplied, string path)
    {
        if (supplied != null && supplied is not JObject) throw Error(path + ".arguments must be an object.");
        var obj = supplied as JObject ?? new JObject();
        var definitions = field.Arguments?.ToArray() ?? Array.Empty<QueryArgument>();
        if (obj.Properties().Any(p => definitions.All(a => a.Name != p.Name) || p.Name is "where" or "first" or "after" or "offset")) throw Error(path + ": predicate field arguments cannot page/filter their input or name unknown arguments.");
        var output = new Dictionary<string, object?>();
        foreach (var argument in definitions.Where(a => a.Name is not ("where" or "first" or "after" or "offset")))
        {
            var value = obj[argument.Name];
            if (value == null && argument.DefaultValue != null) value = JToken.FromObject(argument.DefaultValue);
            if (value == null || value.Type == JTokenType.Null)
            {
                if (argument.ResolvedType is NonNullGraphType) throw Error(path + " requires argument " + argument.Name);
                continue;
            }
            object? ConvertValue(IGraphType type, JToken token)
            {
                type = Bare(type);
                if (type is ListGraphType list) return token is JArray array ? array.Select(v => ConvertValue(list.ResolvedType!, v)).ToArray() : throw Error(path + " requires a list.");
                if (type is not ScalarGraphType scalar) throw Error(path + ": unsupported public argument type " + type.Name);
                ValidateScalar(scalar, token, path);
                return scalar.ParseValue(((JValue)token).Value);
            }
            var converted = ConvertValue(argument.ResolvedType!, value);
            try { argument.Validator?.Invoke(converted!); } catch (ArgumentException error) { throw Error(path + ": " + error.Message); }
            if (argument.Metadata.TryGetValue("json_limits", out var raw) && raw is JObject limits && limits["enum"] is JArray choices && !choices.Any(x => JToken.DeepEquals(x, value))) throw Error(path + ": invalid argument choice.");
            output[argument.Name] = converted;
        }
        return output;
    }
    internal object? ReadMember(string type, object value, string field, IReadOnlyDictionary<string, object?> args, ReadContext context)
    {
        var owner = schema.AllTypes[type] as IComplexGraphType ?? throw Error("Unknown public object type " + type);
        var definition = owner.GetField(field) ?? throw Error("Unknown public field " + type + "." + field);
        return ReadField(owner, definition, value, args, context);
    }
    internal object? ReadField(IComplexGraphType owner, FieldType field, object value, IReadOnlyDictionary<string, object?> arguments, ReadContext state)
    {
        state.Check();
        if (field.Metadata.TryGetValue("projection_path",out var projection) && projection is string[] path) {
            var node = value is GameEntity entity ? new GameNode(owner.Name,"pawn",entity,entity.Handle) : (GameNode)value;
            return state.ProjectContents(node,path,(bool)field.Metadata["projection_single"]!);
        }
        if (field.Metadata.ContainsKey("slice"))
        {
            var node = value is GameEntity entity ? new GameNode(owner.Name, "pawn", entity, entity.Handle) : (GameNode)value;
            return state.ReadNode(node, field.Name, arguments);
        }
        var actual = owner is IAbstractGraphType abstractType ? abstractType.ResolveType!(value)! : (IObjectGraphType)owner;
        var definition = owner is IAbstractGraphType ? actual.GetField(field.Name)! : field;
        if (definition.Metadata.ContainsKey("node_member")) {
            var node = value is GameEntity entity ? new GameNode(owner.Name,"pawn",entity,entity.Handle) : (GameNode)value;
            return state.ReadNode(node,definition.Name,arguments);
        }
        var context = new ResolveFieldContext {
            Source = value, FieldDefinition = definition, ParentType = actual, Schema = schema,
            Arguments = arguments.ToDictionary(p => p.Key, p => new ArgumentValue(p.Value, ArgumentSource.Literal)),
            UserContext = new Dictionary<string, object?> { ["read"] = state },
        };
        var task = definition.Resolver!.ResolveAsync(context);
        if (!task.IsCompleted) throw new InvalidOperationException("Predicate resolver yielded.");
        return task.GetAwaiter().GetResult();
    }
    private IEnumerable Walk(IComplexGraphType owner, FieldType field, object start, Dictionary<string, object?> arguments, bool includeSelf, ReadContext context)
    {
        var pending = new Queue<(object Value, int Depth)>(); pending.Enqueue((start, 0));
        var seen = new HashSet<object>(); var nodes = 0;
        while (pending.Count > 0)
        {
            context.Check(); var entry = pending.Dequeue();
            var identity = entry.Value is GameNode n ? n.Handle ?? n : entry.Value;
            if (!seen.Add(identity)) continue;
            if (++nodes > MaxNodes || entry.Depth > MaxDepth) throw ReadContext.Error("filter_limit", "Transitive relation exceeds condition limits.");
            if (includeSelf || entry.Depth > 0) yield return entry.Value;
            var next = ReadField(owner, field, entry.Value, arguments, context);
            if (next == null) continue;
            if (Element(field) != null) foreach (var child in (IEnumerable)next) { if (child != null) pending.Enqueue((child, entry.Depth + 1)); }
            else pending.Enqueue((next, entry.Depth + 1));
        }
    }
    private static bool? Quantify(IEnumerable values, Predicate predicate, string mode, ReadContext context)
    {
        IEnumerable<bool?> Results() { var count = 0; foreach (var value in values) { context.Check(); if (++count > 8192) throw ReadContext.Error("filter_limit", "Predicate collection exceeds 8192 elements."); yield return Evaluate(predicate, value, context); } }
        var result = Combine(Results(), mode == "every");
        return mode == "none" ? !result : result;
    }
    private static bool? Combine(IEnumerable<bool?> results, bool all)
    {
        var unknown = false;
        foreach (var result in results) { if (result == !all) return !all; if (result == null) unknown = true; }
        return unknown ? null : all;
    }
    private static bool? Evaluate(Predicate predicate, object? value, ReadContext context)
    {
        context.Check();
        try { return predicate(value, context); }
        catch (ExecutionError error) when (error.Code is not ("deadline_exceeded" or "wrong_thread" or "filter_limit")) { return null; }
    }
    private static ExecutionError Error(string message) => ReadContext.Error("invalid_condition", message);
}

internal sealed class ConditionGraphType : ScalarGraphType
{
    public ConditionGraphType() { Name = "SelectionPredicate"; Description = "Schema-validated structured condition; selection_contract defines the shared operators."; }
    public override object? ParseValue(object? value) => value == null ? null : value is JObject obj ? obj : JObject.FromObject(value);
    public override object? Serialize(object? value) => value;
    public override object? ParseLiteral(GraphQLValue value) => value is GraphQLNullValue ? null : GameQueryEngine.Value(value, new JObject());
}
