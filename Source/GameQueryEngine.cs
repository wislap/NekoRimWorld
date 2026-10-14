using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GraphQL;
using GraphQL.Resolvers;
using GraphQL.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>The current public-query vocabulary and typed native field schema.</summary>
internal sealed class GameQueryEngine
{
    private readonly Schema schema;
    private readonly SelectionConditions conditions;
    private readonly PublicQuery publicQuery;
    private readonly JObject catalog;
    private readonly double readBudgetMs;

    public GameQueryEngine(double readBudgetMs = 12)
    {
        this.readBudgetMs = readBudgetMs;
        schema = BuildSchema();
        SelectionConditions.Install(schema);
        schema.Initialize();
        conditions = new SelectionConditions(schema);
        publicQuery = new PublicQuery(schema, conditions);
        catalog = new JObject {
            ["selection_contract"] = SelectionConditions.Contract(),
            ["spatial_query"] = SpatialContract.Catalog(),
            ["world_observation"] = WorldObservationContract.Catalog(),
            ["public_query"] = publicQuery.Catalog(),
        };
        catalog["schema_id"] = Hash(catalog.ToString(Formatting.None));
    }

    internal object? ReadPublicField(GameNode node, string field, IReadOnlyDictionary<string, object?> arguments, ReadContext context) =>
        conditions.ReadMember(node.Kind, node.Kind == "Pawn" ? node.Owner : node, field, arguments, context);

    // Capture traverses the same declared getters as public query. This is an
    // in-process typed adapter contract, not a second wire schema operation.
    internal IReadOnlyList<FieldType> NativeFields(string kind) =>
        schema.AllTypes[kind] is IComplexGraphType type ? type.Fields.ToArray()
            : throw new ArgumentException("Unknown native node kind: " + kind);

    public QueryOutcome Execute(string operation, JObject arguments, IGameData source, SessionDescriptor session)
    {
        try
        {
            if (operation == "query") return publicQuery.Execute(arguments, source, session, readBudgetMs, catalog["schema_id"]!.ToString());
            if (operation == "query_catalog")
            {
                ExactKeys(arguments, Array.Empty<string>());
                return new QueryOutcome { Data = (JObject)catalog.DeepClone() };
            }
            if (operation == "describe_query")
            {
                ExactKeys(arguments, new[] { "types" });
                if (arguments["types"] is not JArray types || types.Count is < 1 or > 8 || types.Any(t => t.Type != JTokenType.String))
                    throw ReadContext.Error("invalid_arguments", "types needs 1..8 public type names.");
                return new QueryOutcome { Data = new JObject { ["metadata_only"] = true, ["schema_id"] = catalog["schema_id"], ["types"] = publicQuery.Describe(types.Values<string>().Select(t => t!)) } };
            }
            return QueryOutcome.Failure("unsupported_operation", "Use query_catalog, describe_query or query.", ProtocolStatuses.Unsupported);
        }
        catch (ExecutionError error) {
            var failure = QueryOutcome.Failure(error.Code ?? "invalid_query", error.Message);
            if (error.Data["input_path"] is string path)
                failure.Issues = new[] { new QueryIssue { Code=error.Code ?? "invalid_query", Message=error.Message,
                    Path=new JArray(path.Split('/').Skip(1).Select(p=>p.Replace("~1","/").Replace("~0","~"))) } };
            return failure;
        }
        catch (Exception error) when (error is FormatException || error is OverflowException || error is InvalidCastException || error is ArgumentException) {
            return QueryOutcome.Failure("invalid_arguments", "Query arguments have invalid types or values; inspect the supplied schema.");
        }
    }

    internal IFrameQueryOperation BeginPublicQuery(JObject arguments,IGameData source,SessionDescriptor session,Func<bool> current,Func<long> tick)
        =>new CooperativeQuery(publicQuery,arguments,source,session,readBudgetMs,catalog["schema_id"]!.ToString(),current,tick);

    private static Schema BuildSchema()
    {
        var game = new ObjectGraphType { Name = "Game", Description = "Active game's directly observed state." };
        DataField(game, "paused", new BooleanGraphType(), "Simulation ticks are paused. The player can still inspect, place plans and issue orders while paused; pause does not disable consultation or establish Pawn task incapability. Recommendations can be planned now and run when simulation resumes.");
        DataField(game, "currentMapId", new IdGraphType(), "Current viewed map; null if none.");
        var map = new ObjectGraphType { Name = "Map", Description = "An active, loaded map." };
        DataField(map, "id", new NonNullGraphType(new IdGraphType()), "Stable map ID within the save.");
        DataField(map, "name", new StringGraphType(), "Display name; not a unique identifier.");
        DataField(map, "isCurrent", new BooleanGraphType(), "Whether this is the currently viewed map.");
        DataField(map, "isPlayerHome", new BooleanGraphType(), "Game's player-home map flag.");
        DataField(map, "colonistCount", new IntGraphType(), "Number of spawned free colonists; excludes caravans/prisoners.");
        DataField(map, "hasVisibleThreat", new BooleanGraphType(), "Game-provided active visible hostile threat signal, not an overall safety assessment.");
        var native = new ThingSchema();
        // The list view and a map reference expose the same facts. Resolver storage handles may differ.
        foreach (var name in new[] { "haulDestinationManager", "resourceCounter" }) {
            var field = native.Map.GetField(name)!;
            Field(map,name,field.ResolvedType!,c => {
                var node = (GameNode)State(c).NativeRoot("map",new Dictionary<string,object?> { ["id"] = ((GameEntity)c.Source!).Id })!;
                return State(c).ReadNode(node,name);
            });
        }
        var pawn = PawnSchema.Build(native);
        var root = new ObjectGraphType { Name = "Query" };
        Field(root, "game", game, c => State(c));
        Field(root, "maps", new ListGraphType(map), c => {
            var ids = c.GetArgument<string[]?>("ids");
            return State(c).Maps().Where(x => ids == null || ids.Contains(x.Id)).ToArray();
        }, new QueryArguments(GameArguments.Ids("ids")));
        Field(root, "pawn", pawn, c => State(c).Colonists().FirstOrDefault(x => x.Id == c.GetArgument<string>("id")),
            new QueryArguments(new QueryArgument(new NonNullGraphType(new IdGraphType())) { Name = "id", Description = "Exact pawn ID." }));
        Field(root, "colonists", new ListGraphType(pawn), c => State(c).Colonists(), ColonistArguments());
        root.GetField("colonists")!.Metadata["collection_element"] = pawn;
        var colony = new ObjectGraphType { Name = "Colony", Description = "Game and loaded-map overview." };
        Field(colony, "game", game, c => State(c));
        Field(colony, "maps", new ListGraphType(map), c => State(c).Maps());
        Field(root, "colony", colony, c => State(c));
        native.AddRoots(root);
        var resultSchema = new Schema { Query = root, NameConverter = new GraphQL.Conversion.DefaultNameConverter() };
        foreach (var component in native.ComponentTypes) resultSchema.RegisterType(component);
        return resultSchema;
    }
    private static QueryArguments ColonistArguments() => new(
        GameArguments.Ids("ids"),
        GameArguments.Create("mapId", new IdGraphType(), "Restrict to one loaded map."),
        GameArguments.Create("name", new StringGraphType(), "Case-insensitive display-name substring, at most " + GameArguments.MaxNameLength + " characters.", new JObject { ["maxLength"] = GameArguments.MaxNameLength }));
    private static void DataField(ObjectGraphType type, string name, IGraphType graphType, string description) =>
        Field(type, name, graphType, c => State(c).Read(type.Name, c.Source as GameEntity, name), description: description);
    private static void Field(ObjectGraphType type, string name, IGraphType graphType,
        Func<IResolveFieldContext, object?> resolve, QueryArguments? arguments = null, string? description = null) =>
        type.AddField(new FieldType { Name = name, Description = description, ResolvedType = graphType,
            Arguments = arguments, Resolver = new FuncFieldResolver<object?>(c => { State(c).TraceField(c); return resolve(c); }) });
    private static ReadContext State(IResolveFieldContext c) => (ReadContext)c.UserContext["read"]!;

    internal static string Hash(string text)
    { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
    internal static void ExactKeys(JObject args, string[] allowed)
    { if (args.Properties().Any(p => !allowed.Contains(p.Name))) throw ReadContext.Error("invalid_arguments", "Unexpected argument; allowed: " + string.Join(", ", allowed)); }
}
