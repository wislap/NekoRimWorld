using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GraphQL;
using GraphQL.Execution;
using GraphQL.NewtonsoftJson;
using GraphQL.Resolvers;
using GraphQL.Types;
using GraphQLParser;
using GraphQLParser.AST;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>Same schema, validation and serial field resolvers for templates and exact queries.</summary>
internal sealed class GameQueryEngine
{
    private readonly Schema schema;
    private readonly SerialExecuter executer = new();
    private readonly GraphQLSerializer serializer = new();
    private readonly TemplateCatalog templates;
    private readonly SelectionConditions conditions;
    private readonly JObject catalog;
    private readonly double readBudgetMs;
    public GameQueryEngine(double readBudgetMs = 12)
    {
        this.readBudgetMs = readBudgetMs;
        schema = BuildSchema();
        SelectionConditions.Install(schema);
        schema.Initialize();
        conditions = new SelectionConditions(schema);
        templates = new TemplateCatalog(schema);
        var sdl = schema.Print();
        var specification = new JObject {
            ["schema"] = sdl,
            ["type_index"] = new JArray(schema.AllTypes.Where(t => !t.Name.StartsWith("__", StringComparison.Ordinal)).Select(t => t.Name).OrderBy(x => x)),
            ["query_roots"] = new JArray(schema.Query!.Fields.Select(f => new JObject { ["name"] = f.Name, ["type"] = TypeName(f.ResolvedType!), ["arguments"] = DescribeArguments(f.Arguments) })),
            ["templates"] = templates.Describe(),
            ["template_parameters"] = templates.InputSchema(),
            ["query_parameters"] = JObject.Parse(@"{
                'type':'object','properties':{
                    'document':{'type':'string','minLength':1,'maxLength':16384},
                    'variables':{'type':'object'}},'required':['document'],'additionalProperties':false}"),
            ["query_rules"] = "Read-only query; one operation. Aliases, variables and bounded inline type fragments supported; named fragments/directives/introspection/mutation unavailable. Native things/thing_defs/map targets expose selected original members, holders and storage groups. Thing group HaulableEver is not current hauling eligibility. Unknown component classes return metadata, not reflected fields. Exact native type/member descriptions are available using describe_game_schema. Target pawn by stable ID, then combine status/mood/health/equipment/profile. colonists locates spawned free colonists (not caravans/prisoners); first 1..50 default20. Nested slices use first 1..100 default20, offset 0..8192; offset continuation samples current data, not frozen history. Slice complete=false means selected fields failed; nextOffset is then null. Thought ref is observation-local. UI-visible thoughts/conditions are default; visibleOnly=false must be explicit. Discover StatDef names via pawn.statDefinitions(search), then request stats(defNames). Null without an issue can mean not applicable; observed empty lists have total=0. Limits: depth12, fields512, combined nested cost20000.",
        };
        specification["result_description_contract"] = new JObject { ["version"] = ResultEvidence.Version, ["coverage"] = "All public game query result types; metadata queries are not game observations." };
        specification["selection_contract"] = SelectionConditions.Contract();
        specification["resource_parameters"] = ResourceReports.Parameters();
        specification["resource_contract"] = ResourceReports.Contract();
        specification["schema_id"] = Hash(specification.ToString(Formatting.None));
        catalog = specification;
        // Validate every template against the actual executable schema before advertising it.
        foreach (var sample in templates.ValidationQueries())
        {
            var doc = Parser.Parse(sample);
            var validation = new GraphQL.Validation.DocumentValidator().ValidateAsync(new GraphQL.Validation.ValidationOptions {
                Schema = schema, Document = doc,
                Operation = (GraphQLOperationDefinition)doc.Definitions[0],
            }).GetAwaiter().GetResult();
            if (!validation.IsValid) throw new InvalidOperationException("Invalid built-in query template: " + string.Join("; ", validation.Errors.Select(x => x.Message)));
        }
    }

    internal object? ReadPublicField(GameNode node, string field, IReadOnlyDictionary<string, object?> arguments, ReadContext context) =>
        conditions.ReadMember(node.Kind, node.Kind == "Pawn" ? node.Owner : node, field, arguments, context);

    public QueryOutcome Execute(string operation, JObject arguments, IGameData source, SessionDescriptor session)
    {
        try
        {
            if (operation == "resource_report") return ResourceReports.Execute(arguments, source, session, readBudgetMs, catalog["schema_id"]!.ToString(), conditions);
            if (operation == "query_catalog")
            {
                ExactKeys(arguments, Array.Empty<string>());
                return new QueryOutcome { Data = (JObject)catalog.DeepClone() };
            }
            if (operation == "describe_schema")
            {
                ExactKeys(arguments, new[] { "types", "include_templates" });
                if (arguments["types"] is not JArray names || names.Count < 1 || names.Count > 8 || names.Any(x => x.Type != JTokenType.String || x.ToString().Length > 128))
                    throw ReadContext.Error("invalid_arguments", "types must contain 1..8 supported type names.");
                if (arguments["include_templates"] != null && arguments["include_templates"]!.Type != JTokenType.Boolean)
                    throw ReadContext.Error("invalid_arguments", "include_templates must be boolean.");
                var described = new JArray();
                foreach (var name in names.Values<string>().Distinct())
                {
                    var type = schema.AllTypes[name!];
                    if (type == null || name!.StartsWith("__", StringComparison.Ordinal)) throw ReadContext.Error("unknown_type", "Unknown supported type: " + name);
                    var row = new JObject { ["name"] = type.Name, ["description"] = type.Description };
                    if (type is IComplexGraphType complex) row["fields"] = new JArray(complex.Fields.Select(f => new JObject { ["name"] = f.Name, ["type"] = TypeName(f.ResolvedType!), ["description"] = f.Description, ["arguments"] = DescribeArguments(f.Arguments), ["selection"] = SelectionConditions.DescribeField(f) }));
                    if (type is IInterfaceGraphType iface) row["possible_types"] = new JArray(iface.PossibleTypes.Select(t => t.Name));
                    described.Add(row);
                }
                var schemaData = new JObject { ["schema_id"] = catalog["schema_id"], ["types"] = described, ["metadata_only"] = true };
                if ((bool?)arguments["include_templates"] == true) schemaData["templates"] = templates.Describe();
                return new QueryOutcome { Data = schemaData };
            }
            string document;
            JObject variables;
            JArray? bindings = null;
            if (operation == "template_query")
            {
                ExactKeys(arguments, new[] { "requests" });
                var expanded = templates.Expand(arguments["requests"]);
                document = expanded.Document; variables = expanded.Variables; bindings = expanded.Bindings;
            }
            else if (operation == "graphql")
            {
                ExactKeys(arguments, new[] { "document", "variables" });
                if (arguments["document"]?.Type != JTokenType.String)
                    throw ReadContext.Error("invalid_arguments", "document must be a string.");
                document = (string)arguments["document"]!;
                variables = arguments["variables"] == null ? new JObject() :
                    arguments["variables"] as JObject ?? throw ReadContext.Error("invalid_arguments", "variables must be an object.");
            }
            else return QueryOutcome.Failure("unsupported_operation", "Use query_catalog, describe_schema, template_query or graphql.", ProtocolStatuses.Unsupported);

            var parsed = CheckDocument(document, variables, session);
            var context = new ReadContext(source, session, readBudgetMs) { Conditions = conditions };
            var task = executer.ExecuteAsync(new ExecutionOptions {
                Schema = schema, Document = parsed, Query = document,
                Variables = serializer.Deserialize<Inputs>(variables.ToString(Formatting.None)),
                UserContext = new Dictionary<string, object?> { ["read"] = context },
                ThrowOnUnhandledException = false,
            });
            // All resolvers and validation are synchronous; never continue Verse reads on a pool thread.
            if (!task.IsCompleted) throw new InvalidOperationException("The game query unexpectedly yielded asynchronously.");
            var execution = task.GetAwaiter().GetResult();
            var result = JObject.Parse(serializer.Serialize(execution));
            var issues = new List<QueryIssue>();
            if (result["errors"] is JArray errors)
                foreach (var error in errors.OfType<JObject>())
                    issues.Add(new QueryIssue {
                        Code = (string?)error["extensions"]?["code"] ?? "query_error",
                        Path = error["path"] as JArray ?? new JArray(),
                        Message = Short((string?)error["message"] ?? "Query failed.", 512),
                    });
            var data = result["data"] as JObject;
            if (!execution.Executed || data == null)
                return new QueryOutcome { Status = ProtocolStatuses.Failed, ErrorCode = "query_rejected",
                    Message = issues.Count == 0 ? "Query could not execute." : issues[0].Message,
                    Issues = issues.Take(256).ToArray() };
            MarkIncompletePages(data, ((GraphQLOperationDefinition)parsed.Definitions[0]).SelectionSet, schema.Query!, issues, new List<object>());
            // Metadata records exactly what the template expanded to; no extra gameplay facts are inserted.
            var metadata = new JObject { ["document"] = document, ["variables"] = variables.DeepClone(), ["schema_id"] = catalog["schema_id"] };
            if (bindings != null) metadata["bindings"] = bindings;
            metadata["evidence"] = ResultEvidence.Query(data, ((GraphQLOperationDefinition)parsed.Definitions[0]).SelectionSet, schema.Query!, schema, variables, issues.ToArray(), context.ResolvedFields);
            var outcome = new QueryOutcome {
                Status = issues.Count == 0 ? ProtocolStatuses.Ok : ProtocolStatuses.Partial,
                Data = data, Issues = issues.Take(256).ToArray(), Execution = metadata,
            };
            if (data.ToString(Formatting.None).Length + metadata.ToString(Formatting.None).Length > 96_000)
                return QueryOutcome.Failure("query_result_too_large", "Request fewer rows or fields.");
            return outcome;
        }
        catch (ExecutionError error) { return QueryOutcome.Failure(error.Code ?? "invalid_query", error.Message); }
        catch (GraphQLParser.Exceptions.GraphQLMaxDepthExceededException) { return QueryOutcome.Failure("query_limit", "The query exceeds parser depth limits."); }
        catch (Exception error) when (error is FormatException || error is OverflowException || error is InvalidCastException || error is ArgumentException) { return QueryOutcome.Failure("invalid_arguments", "Query arguments have invalid types or values; inspect the supplied schema."); }
        catch (GraphQLParser.Exceptions.GraphQLSyntaxErrorException) { return QueryOutcome.Failure("invalid_query", "Invalid GraphQL syntax."); }
    }

    private static string TypeName(IGraphType type) => type switch { NonNullGraphType n => TypeName(n.ResolvedType!) + "!", ListGraphType l => "[" + TypeName(l.ResolvedType!) + "]", _ => type.Name };
    private static JArray DescribeArguments(QueryArguments? arguments) => arguments == null ? new JArray() : new JArray(arguments.Select(a => new JObject { ["name"] = a.Name, ["type"] = TypeName(a.ResolvedType!), ["description"] = a.Description, ["default"] = a.DefaultValue == null ? JValue.CreateNull() : JToken.FromObject(a.DefaultValue), ["limits"] = a.Metadata.TryGetValue("json_limits", out var limits) && limits is JObject obj ? obj.DeepClone() : new JObject() }));

    private static Schema BuildSchema()
    {
        var game = new ObjectGraphType { Name = "Game", Description = "Active game's directly observed state." };
        DataField(game, "paused", new BooleanGraphType(), "Whether the game is paused.");
        DataField(game, "currentMapId", new IdGraphType(), "Current viewed map; null if none.");
        var map = new ObjectGraphType { Name = "Map", Description = "An active, loaded map." };
        DataField(map, "id", new NonNullGraphType(new IdGraphType()), "Stable map ID within the save.");
        DataField(map, "name", new StringGraphType(), "Display name; not a unique identifier.");
        DataField(map, "isCurrent", new BooleanGraphType(), "Whether this is the currently viewed map.");
        DataField(map, "isPlayerHome", new BooleanGraphType(), "Game's player-home map flag.");
        DataField(map, "colonistCount", new IntGraphType(), "Number of spawned free colonists; excludes caravans/prisoners.");
        DataField(map, "hasVisibleThreat", new BooleanGraphType(), "Game-provided active visible hostile threat signal, not an overall safety assessment.");
        var native = new ThingSchema();
        var pawn = PawnSchema.Build(native);
        var pageInfo = new ObjectGraphType { Name = "PageInfo", Description = "A cursor applies to the same load and filters; partial pages return no advancing cursor." };
        Field(pageInfo, "hasNextPage", new NonNullGraphType(new BooleanGraphType()), c => ((ColonistPage)c.Source!).HasNextPage);
        Field(pageInfo, "endCursor", new StringGraphType(), c => ((ColonistPage)c.Source!).EndCursor);
        var page = new ObjectGraphType { Name = "ColonistPage" };
        Field(page, "nodes", new ListGraphType(pawn), c => ((ColonistPage)c.Source!).Nodes);
        page.GetField("nodes")!.Metadata["collection_view"] = true;
        Field(page, "pageInfo", pageInfo, c => c.Source);
        var root = new ObjectGraphType { Name = "Query" };
        Field(root, "game", game, c => State(c));
        Field(root, "maps", new ListGraphType(map), c => {
            var ids = c.GetArgument<string[]?>("ids");
            return State(c).Maps().Where(x => ids == null || ids.Contains(x.Id)).ToArray();
        }, new QueryArguments(GameArguments.Ids("ids")));
        Field(root, "pawn", pawn, c => State(c).Colonists().FirstOrDefault(x => x.Id == c.GetArgument<string>("id")),
            new QueryArguments(new QueryArgument(new NonNullGraphType(new IdGraphType())) { Name = "id", Description = "Exact pawn ID." }));
        Field(root, "colonists", page, c => ReadPage(c), ColonistArguments());
        root.GetField("colonists")!.Metadata["cursor_page"] = true;
        root.GetField("colonists")!.Metadata["collection_element"] = pawn;
        root.GetField("colonists")!.Metadata["list_default"] = GameArguments.DefaultPageSize;
        root.GetField("colonists")!.Metadata["list_max"] = GameArguments.MaxPageSize;
        root.GetField("maps")!.Metadata["list_default"] = 32;
        root.GetField("maps")!.Metadata["list_max"] = 32;
        var colony = new ObjectGraphType { Name = "Colony", Description = "Game and loaded-map overview." };
        Field(colony, "game", game, c => State(c));
        Field(colony, "maps", new ListGraphType(map), c => State(c).Maps());
        colony.GetField("maps")!.Metadata["list_default"] = 32;
        colony.GetField("maps")!.Metadata["list_max"] = 32;
        Field(root, "colony", colony, c => State(c));
        native.AddRoots(root);
        var resultSchema = new Schema { Query = root, NameConverter = new GraphQL.Conversion.DefaultNameConverter() };
        foreach (var component in native.ComponentTypes) resultSchema.RegisterType(component);
        return resultSchema;
    }
    private static QueryArguments ColonistArguments() => new(
        GameArguments.Ids("ids"),
        GameArguments.Create("mapId", new IdGraphType(), "Restrict to one loaded map."),
        GameArguments.Create("name", new StringGraphType(), "Case-insensitive display-name substring, at most " + GameArguments.MaxNameLength + " characters.", new JObject { ["maxLength"] = GameArguments.MaxNameLength }),
        GameArguments.Create("first", new IntGraphType(), "Page size 1.." + GameArguments.MaxPageSize + ", default " + GameArguments.DefaultPageSize + ".", new JObject { ["minimum"] = 1, ["maximum"] = GameArguments.MaxPageSize }, GameArguments.DefaultPageSize),
        GameArguments.Create("after", new StringGraphType(), "Cursor from the previous complete page, same load and filters.", new JObject { ["maxLength"] = GameArguments.MaxCursorLength }));
    private static void DataField(ObjectGraphType type, string name, IGraphType graphType, string description) =>
        Field(type, name, graphType, c => State(c).Read(type.Name, c.Source as GameEntity, name), description: description);
    private static void Field(ObjectGraphType type, string name, IGraphType graphType,
        Func<IResolveFieldContext, object?> resolve, QueryArguments? arguments = null, string? description = null) =>
        type.AddField(new FieldType { Name = name, Description = description, ResolvedType = graphType,
            Arguments = arguments, Resolver = new FuncFieldResolver<object?>(c => { State(c).TraceField(c); return resolve(c); }) });
    private static ReadContext State(IResolveFieldContext c) => (ReadContext)c.UserContext["read"]!;

    private static ColonistPage ReadPage(IResolveFieldContext c)
    {
        var state = State(c);
        var ids = c.GetArgument<string[]?>("ids");
        var mapId = c.GetArgument<string?>("mapId");
        var name = c.GetArgument<string?>("name");
        var first = c.GetArgument<int?>("first") ?? GameArguments.DefaultPageSize;
        var after = c.GetArgument<string?>("after");
        var where = c.Arguments != null && c.Arguments.TryGetValue("where", out var predicate) ? predicate.Value : null;
        var filter = FilterHash(ids, mapId, name, where);
        var lastId = DecodeCursor(after, state.Session.LoadEpoch, filter);
        var found = new List<GameEntity>();
        var matches = state.Matcher("Pawn", where);
        foreach (var entity in state.Colonists())
        {
            state.Check();
            if (lastId != null && StringComparer.Ordinal.Compare(entity.Id, lastId) <= 0) continue;
            if (ids != null && !ids.Contains(entity.Id) || mapId != null && entity.MapId != mapId) continue;
            if (name != null && ((string?)state.Read("Colonist", entity, "name"))?.IndexOf(name, StringComparison.OrdinalIgnoreCase) is not >= 0) continue;
            if (!matches(entity)) continue;
            found.Add(entity);
            if (found.Count > first) break;
        }
        var page = new ColonistPage { Nodes = found.Take(first).ToList(), HasNextPage = found.Count > first };
        if (page.Nodes.Count > 0) page.EndCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            state.Session.LoadEpoch + "\n" + filter + "\n" + page.Nodes.Last().Id));
        return page;
    }
    private static string FilterHash(string[]? ids, string? map, string? name, object? where = null) => Hash(new JObject {
        ["ids"] = ids == null ? JValue.CreateNull() : new JArray(ids.OrderBy(x => x, StringComparer.Ordinal)),
        ["mapId"] = map, ["name"] = name?.ToUpperInvariant(), ["where"] = where == null ? JValue.CreateNull() : JToken.FromObject(where),
    }.ToString(Formatting.None));
    private static string? DecodeCursor(string? cursor, string epoch, string filter)
    {
        if (cursor == null) return null;
        try {
            if (cursor.Length > GameArguments.MaxCursorLength) throw new FormatException();
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('\n');
            if (parts.Length != 3 || parts[2].Length == 0) throw new FormatException();
            if (parts[0] != epoch || parts[1] != filter) throw ReadContext.Error("stale_cursor", "Cursor belongs to another load or filter; restart pagination.");
            return parts[2];
        } catch (FormatException) { throw ReadContext.Error("invalid_cursor", "Invalid paging cursor."); }
    }
    internal static string Hash(string text)
    { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
    internal static void ExactKeys(JObject args, string[] allowed)
    { if (args.Properties().Any(p => !allowed.Contains(p.Name))) throw ReadContext.Error("invalid_arguments", "Unexpected argument; allowed: " + string.Join(", ", allowed)); }
    private static string Short(string text, int max) => text.Length <= max ? text : text.Substring(0, max);

    private GraphQLDocument CheckDocument(string document, JObject suppliedVariables, SessionDescriptor session)
    {
        if (string.IsNullOrWhiteSpace(document) || document.Length > 16_384) throw ReadContext.Error("query_limit", "document must contain 1..16384 characters.");
        var doc = Parser.Parse(document, new ParserOptions { MaxDepth = 48 });
        if (doc.Definitions.Count != 1 || doc.Definitions[0] is not GraphQLOperationDefinition op || op.Operation != OperationType.Query)
            throw ReadContext.Error("query_only", "Use exactly one read-only query; named fragments are not supported.");
        if (op.Directives?.Count > 0) throw ReadContext.Error("unsupported_directive", "Directives are not supported.");
        var variables = (JObject)suppliedVariables.DeepClone();
        if (op.Variables != null)
            foreach (var variable in op.Variables)
                if (!variables.ContainsKey(variable.Variable.Name.StringValue) && variable.DefaultValue != null)
                    variables[variable.Variable.Name.StringValue] = Value(variable.DefaultValue, variables);
        var fields = 0; long cost = 0;
        void Visit(GraphQLSelectionSet set, IComplexGraphType type, int depth, long multiplier)
        {
            if (depth > 12) throw ReadContext.Error("query_limit", "Maximum selection depth is 12.");
            foreach (var selection in set.Selections)
            {
                if (selection is GraphQLInlineFragment fragment)
                {
                    if (fragment.Directives?.Count > 0) throw ReadContext.Error("unsupported_directive", "Directives are not supported.");
                    if (++fields > 512 || (cost += multiplier) > 20000) throw ReadContext.Error("query_limit", "Inline selections exceed the query budget.");
                    var fragmentType = fragment.TypeCondition == null ? type : schema.AllTypes[fragment.TypeCondition.Type.Name.StringValue] as IComplexGraphType;
                    if (fragmentType != null) Visit(fragment.SelectionSet, fragmentType, depth + 1, multiplier);
                    continue;
                }
                if (selection is not GraphQLField field) throw ReadContext.Error("unsupported_fragment", "Named fragments are not supported.");
                if (field.Name.StringValue.StartsWith("__", StringComparison.Ordinal)) throw ReadContext.Error("introspection_disabled", "Use the supplied schema description.");
                if (field.Directives?.Count > 0) throw ReadContext.Error("unsupported_directive", "Directives are not supported.");
                if (++fields > 512 || (cost += multiplier) > 20_000) throw ReadContext.Error("query_limit", "Combined query exceeds 512 fields or cost 20000; narrow objects, sections or nested lists.");
                var definition = type.GetField(field.Name.StringValue);
                if (definition == null) continue; // GraphQL validation reports the actual unknown-field error before execution.
                var args = new JObject();
                if (field.Arguments != null) foreach (var argument in field.Arguments) args[argument.Name.StringValue] = Value(argument.Value, variables);
                if (definition.Arguments != null) foreach (var argument in definition.Arguments)
                {
                    if (!args.ContainsKey(argument.Name) && argument.DefaultValue != null) args[argument.Name] = JToken.FromObject(argument.DefaultValue);
                    var value = args[argument.Name];
                    if (value == null || value.Type == JTokenType.Null) continue;
                    if (argument.Metadata.TryGetValue("json_limits", out var rawLimits) && rawLimits is JObject limits)
                    {
                        if (limits["maxLength"] != null && value.Type == JTokenType.String && value.ToString().Length > (int)limits["maxLength"]! ||
                            limits["minLength"] != null && value.Type == JTokenType.String && value.ToString().Length < (int)limits["minLength"]! ||
                            limits["maxItems"] != null && value is JArray listMax && listMax.Count > (int)limits["maxItems"]! ||
                            limits["minItems"] != null && value is JArray listMin && listMin.Count < (int)limits["minItems"]! ||
                            limits["enum"] is JArray choices && !choices.Any(x => JToken.DeepEquals(x, value)))
                            throw ReadContext.Error("invalid_arguments", type.Name + "." + field.Name + ": " + argument.Name + " is outside its declared limits.");
                        if (limits["minimum"] != null && value.Type == JTokenType.Integer && (long)value < (long)limits["minimum"]! ||
                            limits["maximum"] != null && value.Type == JTokenType.Integer && (long)value > (long)limits["maximum"]!)
                            throw ReadContext.Error("invalid_arguments", type.Name + "." + field.Name + ": " + argument.Name + " is outside its declared range.");
                    }
                }
                if (type == schema.Query && field.Name.StringValue == "colonists")
                {
                    var ids = args["ids"] == null || args["ids"]!.Type == JTokenType.Null ? null : args["ids"] is JArray array ? array.Values<string>().ToArray() : new[] { (string)args["ids"]! };
                    DecodeCursor((string?)args["after"], session.LoadEpoch, FilterHash(ids!, (string?)args["mapId"], (string?)args["name"], args["where"]));
                }
                if (type == schema.Query && field.Name.StringValue is "things" or "thing_defs") ThingPaging.ValidateCursor(args, session.LoadEpoch);
                if (args["where"] != null && args["where"]!.Type != JTokenType.Null && SelectionConditions.Element(definition) is IGraphType element) conditions.Compile(element.Name, args["where"]);
                var childMultiplier = multiplier;
                if (definition.Metadata.TryGetValue("list_default", out var defaultSize))
                {
                    var count = args["first"]?.Type == JTokenType.Integer ? (long)args["first"]! : Convert.ToInt64(defaultSize);
                    childMultiplier *= count;
                    if (childMultiplier > 20_000) throw ReadContext.Error("query_limit", "Nested collection expansion exceeds query budget.");
                }
                if (field.SelectionSet != null && Unwrap(definition.ResolvedType!) is IComplexGraphType child)
                    Visit(field.SelectionSet, child, depth + 1, childMultiplier);
            }
        }
        Visit(op.SelectionSet, schema.Query!, 1, 1);
        return doc;
    }
    private static IGraphType Unwrap(IGraphType type) => type switch {
        NonNullGraphType n => Unwrap(n.ResolvedType!), ListGraphType l => Unwrap(l.ResolvedType!), _ => type,
    };
    // Page/Slice wrappers are concrete validated types. Inline selections have
    // the same coverage semantics as fields written directly on those wrappers.
    private static IEnumerable<GraphQLField> PageFields(GraphQLSelectionSet selection)
    {
        foreach (var item in selection.Selections)
        {
            if (item is GraphQLField field) yield return field;
            else if (item is GraphQLInlineFragment fragment)
                foreach (var nested in PageFields(fragment.SelectionSet)) yield return nested;
        }
    }
    private void MarkIncompletePages(JToken data, GraphQLSelectionSet selection, IComplexGraphType type, List<QueryIssue> issues, List<object> path)
    {
        foreach (var fragment in selection.Selections.OfType<GraphQLInlineFragment>())
        {
            var fragmentType = fragment.TypeCondition == null ? type : schema.AllTypes[fragment.TypeCondition.Type.Name.StringValue] as IComplexGraphType;
            if (fragmentType != null) MarkIncompletePages(data, fragment.SelectionSet, fragmentType, issues, path);
        }
        foreach (var field in selection.Selections.OfType<GraphQLField>())
        {
            var definition = type.GetField(field.Name.StringValue);
            var key = field.Alias?.Name.StringValue ?? field.Name.StringValue;
            if (definition == null || field.SelectionSet == null || data is not JObject obj || obj[key] == null || obj[key]!.Type == JTokenType.Null) continue;
            var value = obj[key]!;
            var childPath = path.Concat(new object[] { key }).ToList();
            var childType = Unwrap(definition.ResolvedType!) as IComplexGraphType;
            if (childType == null) continue;
            var hasErrors = issues.Any(issue => issue.Path.Count >= childPath.Count && !childPath.Where((part, i) => issue.Path[i].ToString() != part.ToString()).Any());
            var fields = PageFields(field.SelectionSet).ToArray();
            if (hasErrors && value is JObject page)
            {
                if (definition.Metadata.ContainsKey("slice"))
                {
                    foreach (var metadata in fields)
                    {
                        var resultKey = metadata.Alias?.Name.StringValue ?? metadata.Name.StringValue;
                        if (!page.ContainsKey(resultKey)) continue;
                        if (metadata.Name.StringValue == "nextOffset") page[resultKey] = JValue.CreateNull();
                        if (metadata.Name.StringValue == "complete") page[resultKey] = false;
                    }
                }
                if (definition.ResolvedType?.Name == "ColonistPage" || definition.Metadata.ContainsKey("cursor_page"))
                    foreach (var info in fields.Where(x => x.Name.StringValue == "pageInfo"))
                    {
                        var infoKey = info.Alias?.Name.StringValue ?? info.Name.StringValue;
                        if (page[infoKey] is not JObject pageInfo || info.SelectionSet == null) continue;
                        foreach (var metadata in PageFields(info.SelectionSet))
                        {
                            var resultKey = metadata.Alias?.Name.StringValue ?? metadata.Name.StringValue;
                            if (!pageInfo.ContainsKey(resultKey)) continue;
                            if (metadata.Name.StringValue == "endCursor") pageInfo[resultKey] = JValue.CreateNull();
                            // A detail failure disables advancement; it does not
                            // change whether identity discovery found more rows.
                        }
                    }
            }
            if (value is JArray array)
            {
                for (var i = 0; i < array.Count; i++)
                    if (array[i] is JObject)
                        MarkIncompletePages(array[i], field.SelectionSet, childType, issues, childPath.Concat(new object[] { i }).ToList());
            }
            else if (value is JObject) MarkIncompletePages(value, field.SelectionSet, childType, issues, childPath);
        }
    }
    internal static JToken Value(GraphQLValue value, JObject variables) => value switch {
        GraphQLVariable v => variables[v.Name.StringValue]?.DeepClone() ?? JValue.CreateNull(),
        GraphQLBooleanValue v => new JValue(v.BoolValue),
        GraphQLIntValue v => new JValue(long.Parse(v.Value.ToString(), CultureInfo.InvariantCulture)),
        GraphQLFloatValue v => new JValue(double.Parse(v.Value.ToString(), CultureInfo.InvariantCulture)),
        GraphQLEnumValue v => new JValue(v.Name.StringValue),
        GraphQLObjectValue v => v.Fields == null ? new JObject() : new JObject(v.Fields.Select(f => new JProperty(f.Name.StringValue, Value(f.Value, variables)))),
        GraphQLStringValue v => new JValue(v.Value.ToString()),
        GraphQLNullValue => JValue.CreateNull(),
        GraphQLListValue v => v.Values is null
            ? new JArray()
            : new JArray(v.Values.Select(x => Value(x, variables))),
        _ => throw ReadContext.Error("invalid_arguments", "Unsupported argument value for this schema."),
    };
    private sealed class SerialExecuter : DocumentExecuter
    {
        protected override IExecutionStrategy SelectExecutionStrategy(GraphQL.Execution.ExecutionContext context) => SerialExecutionStrategy.Instance;
    }
}
