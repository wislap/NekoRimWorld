using System;
using System.Collections.Generic;
using System.Linq;
using GraphQL;
using GraphQL.Types;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class SelectionTests
{
    private static readonly SessionDescriptor Session = new(new string('2', 32), new string('3', 32), 100, 7);
    private static void Require(bool value, string reason) { if (!value) throw new Exception("Selection: " + reason); }
    public static void Run()
    {
        var engine = new GameQueryEngine(1000); var data = new Data();
        QueryOutcome Query(string document) => engine.Execute("graphql", new JObject { ["document"] = document }, data, Session);
        foreach (var pair in new[] { ("IsMedicine", "herb"), ("IsWeapon", "rifle"), ("IsIngestible", "rice"), ("CountAsResource", "steel") })
        {
            var result = Query("{thing_defs(first:1,where:{fields:{" + pair.Item1 + ":{eq:true}}}){nodes{defName} pageInfo{hasNextPage}}}");
            Require(result.Status == "ok" && (string?)result.Data!["thing_defs"]!["nodes"]![0]!["defName"] == pair.Item2, pair.Item1 + " did not filter before paging: " + result.Message + string.Join(";", result.Issues.Select(i => i.Message)));
        }
        var numeric = Query("{thing_defs(where:{fields:{stackLimit:{gte:75},label:{contains:\"MEDICINE\",case_sensitive:false}}}){nodes{defName}}}");
        Require(numeric.Status == "ok" && (string?)numeric.Data!["thing_defs"]!["nodes"]![0]!["defName"] == "steel", "name match acquired classification meaning");
        var noMedicine = Query("{thing_defs(where:{fields:{IsMedicine:{eq:false}}}){nodes{defName}}}");
        Require(((JArray)noMedicine.Data!["thing_defs"]!["nodes"]!).Count == 3, "false became omitted");
        var nested = Query("{thing_defs(where:{fields:{ingestible:{fields:{HumanEdible:{eq:true}}}}}){nodes{defName}}}");
        Require(nested.Status == "ok" && (string?)nested.Data!["thing_defs"]!["nodes"]![0]!["defName"] == "rice", "nested nullable selection failed");
        var relation = Query("{thing_defs(where:{fields:{thingCategories:{some:{reach:{field:\"parent\",include_self:true,some:{fields:{defName:{eq:\"ModRoot\"}}}}}}}}){nodes{defName}}}");
        Require(relation.Status == "ok" && (string?)relation.Data!["thing_defs"]!["nodes"]![0]!["defName"] == "rifle", "arbitrary category ancestry failed");
        var full = Query("{thing_defs(where:{fields:{stackLimit:{gte:1}}},first:1){nodes{defName} pageInfo{endCursor}}}");
        var cursor = (string)full.Data!["thing_defs"]!["pageInfo"]!["endCursor"]!;
        data.Reads = 0;
        var changed = engine.Execute("graphql", JObject.FromObject(new { document = "query($c:String){thing_defs(first:1,after:$c,where:{fields:{stackLimit:{gte:2}}}){nodes{defName}}}", variables = new { c = cursor } }), data, Session);
        Require(changed.Status == "failed" && data.Reads == 0, "cursor crossed predicate boundary");
        foreach (var bad in new[] {
            "{thing_defs(where:{fields:{IsMedicine:{eq:1}}}){nodes{defName}}}",
            "{thing_defs(where:{fields:{stackLimit:{eq:true}}}){nodes{defName}}}",
            "{thing_defs(where:{fields:{Invented:{eq:true}}}){nodes{defName}}}",
            "{thing_defs(isMedicine:true){nodes{defName}}}",
            "{thing_defs(where:{fields:{stats:{eq:true}}}){nodes{defName}}}",
        }) { data.Reads = 0; var result = Query(bad); Require(result.Status == "failed" && data.Reads == 0, "invalid predicate performed reads: " + bad); }
        data.FailField = "IsMedicine";
        var failed = Query("{thing_defs(where:{fields:{IsMedicine:{eq:true}}}){nodes{defName}}}");
        Require(failed.Status == "partial" && failed.Data!["thing_defs"]!.Type == JTokenType.Null, "unknown membership became empty");
        var certain = Query("{thing_defs(where:{any:[{fields:{IsMedicine:{eq:true}}},{fields:{stackLimit:{gt:0}}}]}){nodes{defName}}}");
        Require(certain.Status == "ok" && ((JArray)certain.Data!["thing_defs"]!["nodes"]!).Count == 4, "three-valued OR lost a proven match");
        data.FailField = null;
        var slice = Query("{thing_def(defName:\"rifle\"){thingCategories(first:1,where:{fields:{defName:{eq:\"ModChild\"}}}){items{defName} total returned complete}}}");
        Require(slice.Status == "ok" && (int?)slice.Data!["thing_def"]!["thingCategories"]!["total"] == 1, "slice predicate was applied after its window");
        var scalarTypes = new IGraphType[] { new BooleanGraphType(), new IntGraphType(), new FloatGraphType(), new StringGraphType(), new IdGraphType(), new EnumerationGraphType { Name = "TestEnum" } };
        ((EnumerationGraphType)scalarTypes.Last()).Add("Alpha", 1);
        var custom = new ObjectGraphType { Name = "NovelObject" };
        foreach (var type in scalarTypes) PawnSchema.Add(custom, "new" + type.Name, type, c => ((Dictionary<string, object?>)c.Source!)[c.FieldDefinition.Name]);
        PawnSchema.Add(custom, "children", new ListGraphType(custom), c => ((Dictionary<string, object?>)c.Source!)["children"]);
        var root = new ObjectGraphType { Name = "Query" }; PawnSchema.Add(root, "objects", new ListGraphType(custom), _ => Array.Empty<object>());
        var schema = new Schema { Query = root }; SelectionConditions.Install(schema); schema.Initialize();
        var conditions = new SelectionConditions(schema); var context = new ReadContext(data, Session, 1000) { Conditions = conditions };
        var values = new Dictionary<string, object?> { ["newBoolean"] = true, ["newInt"] = 42, ["newFloat"] = 1.5, ["newString"] = "Hello", ["newID"] = "id", ["newTestEnum"] = 1, ["children"] = Array.Empty<object>() };
        foreach (var pair in values.Where(p => p.Key != "children")) {
            var expected = pair.Key == "newTestEnum" ? new JValue("Alpha") : JToken.FromObject(pair.Value!);
            Require(conditions.Compile("NovelObject", new JObject { ["fields"] = new JObject { [pair.Key] = new JObject { ["eq"] = expected } } })(values, context) == true, "new same-type field required a compiler branch: " + pair.Key);
        }
        Require(conditions.Compile("NovelObject", JObject.Parse("{'fields':{'children':{'every':{'fields':{'newBoolean':{'eq':true}}}}}}"))(values, context) == true, "empty every is not true");
        Require(conditions.Compile("NovelObject", JObject.Parse("{'fields':{'children':{'some':{'fields':{'newBoolean':{'eq':true}}}}}}"))(values, context) == false, "empty some is not false");
        Console.WriteLine("Selection tests passed: shared native booleans, numeric/text/null/collection/ancestry conditions, unknown logic, new schema fields, paging and pre-read rejection.");
    }

    private sealed class Data : IGameData, IThingData
    {
        public int Reads; public string? FailField;
        private readonly GameNode[] definitions;
        private static GameNode Node(string kind, string key, Dictionary<string, object?> fields) => new(kind, key, new GameEntity(key, "map", fields), fields);
        public Data()
        {
            var parent = Node("ThingCategoryDef", "root", new() { ["defName"] = "ModRoot", ["parent"] = null });
            var category = Node("ThingCategoryDef", "child", new() { ["defName"] = "ModChild", ["parent"] = parent });
            definitions = new[] { "herb", "rifle", "rice", "steel" }.Select(name => Node("ThingDef", name, new() {
                ["defName"] = name, ["label"] = name == "steel" ? "Fake medicine" : name, ["stackLimit"] = name == "rifle" ? 1 : 75,
                ["IsMedicine"] = name == "herb", ["IsWeapon"] = name == "rifle", ["IsIngestible"] = name == "rice", ["CountAsResource"] = name == "steel",
                ["ingestible"] = name == "rice" ? Node("IngestibleProperties", "edible", new() { ["HumanEdible"] = true }) : null,
                ["thingCategories"] = name == "rifle" ? new[] { category } : Array.Empty<GameNode>(),
            })).ToArray();
        }
        public IEnumerable<GameEntity> Maps() => Array.Empty<GameEntity>();
        public IEnumerable<GameEntity> Colonists() => Array.Empty<GameEntity>();
        public object? GameField(string field) { Reads++; return false; }
        public object? MapField(GameEntity map, string field) => null;
        public object? ColonistField(GameEntity pawn, string field) => null;
        public object? NodeField(GameNode node, string field, IReadOnlyDictionary<string, object?> arguments) { Reads++; if (field == FailField) throw ReadContext.Error("field_unavailable", "Injected missing field"); return ((Dictionary<string, object?>)node.Handle!)[field]; }
        public object? NativeField(GameNode node, string field, IReadOnlyDictionary<string, object?> args, ReadContext context) => NodeField(node, field, args);
        public object? NativeRoot(string root, IReadOnlyDictionary<string, object?> args, ReadContext context) { Reads++; return root == "thing_def" ? definitions.First(n => n.Key == (string)args["defName"]!) : ThingPaging.Page(definitions, args, context); }
    }
}
