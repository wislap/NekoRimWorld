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
        QueryOutcome Query(string condition,int limit=100,int offset=0) => engine.Execute("query",new JObject { ["from"]="thing_defs",["where"]=JObject.Parse(condition),["select"]=new JArray("def_name"),["limit"]=limit,["offset"]=offset },data,Session);
        JToken? Rows(QueryOutcome result) => result.Data?["results"]?[0]?["rows"];
        foreach(var pair in new[]{("is_medicine","herb"),("is_weapon","rifle"),("is_ingestible","rice"),("count_as_resource","steel")}) {
            var result=Query("{"+pair.Item1+":{eq:true}}",1);
            Require(result.Status=="ok" && (string?)Rows(result)?[0]?["def_name"]==pair.Item2,"Native classification filter changed: "+pair.Item1);
        }
        var numeric=Query("{stack_limit:{gte:75},label:{contains:'MEDICINE',case_sensitive:false}}");
        Require(numeric.Status=="ok" && (string?)Rows(numeric)?[0]?["def_name"]=="steel","Text label acquired classification meaning");
        Require(((JArray)Rows(Query("{is_medicine:{eq:false}}"))!).Count==3,"False condition omitted");
        var nested=Query("{ingestible:{human_edible:{eq:true}}}");
        Require(nested.Status=="ok" && (string?)Rows(nested)?[0]?["def_name"]=="rice","Nullable nested predicate failed");
        var relation=Query("{thing_categories:{some:{reach:{field:'parent',include_self:true,some:{def_name:{eq:'ModRoot'}}}}}}");
        Require(relation.Status=="ok" && (string?)Rows(relation)?[0]?["def_name"]=="rifle","Category ancestry failed");
        var full=Query("{stack_limit:{gte:1}}",1);
        var continuation=Query("{stack_limit:{gte:1}}",1,1);
        Require((bool?)full.Data?["results"]?[0]?["page"]?["has_more"]==true && (string?)Rows(continuation)?[0]?["def_name"]=="rifle","Current offset continuation changed");
        foreach(var bad in new[]{"{is_medicine:{eq:1}}","{stack_limit:{eq:true}}","{invented:{eq:true}}","{stats:{eq:true}}"}) {
            data.Reads=0;var result=Query(bad);Require(result.Status=="failed" && data.Reads==0,"Invalid condition performed reads");
        }
        data.FailField="IsMedicine";
        var failed=Query("{is_medicine:{eq:true}}");
        Require(failed.Status=="partial" && failed.Data?["results"]?[0]?["matched_count"]?.Type==JTokenType.Null,"Unknown membership became empty");
        var certain=Query("{any:[{is_medicine:{eq:true}},{stack_limit:{gt:0}}]}");
        Require(certain.Status=="ok" && ((JArray)Rows(certain)!).Count==4,"Three-valued OR lost proven match");
        data.FailField=null;
        var slice=engine.Execute("query",JObject.Parse("{from:'thing_def',args:{def_name:'rifle'},select:{thing_categories:{select:['def_name'],where:{def_name:{eq:'ModChild'}},limit:1}}}"),data,Session);
        Require(slice.Status=="ok" && (int?)slice.Data?["results"]?[0]?["rows"]?[0]?["thing_categories"]?["matched_count"]==1,"Nested predicate applied after window");
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
        var literalDocument=GraphQLParser.Parser.Parse("{objects(where:{fields:{newInt:{eq:42},newBoolean:{eq:true},newString:{in:[Alpha,null,1.5]}}}){newInt}}");
        var literalOperation=(GraphQLParser.AST.GraphQLOperationDefinition)literalDocument.Definitions[0];
        var literalField=(GraphQLParser.AST.GraphQLField)literalOperation.SelectionSet.Selections[0];
        var literal=new ConditionGraphType().ParseLiteral(literalField.Arguments![0].Value);
        Require(JToken.DeepEquals((JToken)literal!,JObject.Parse("{fields:{newInt:{eq:42},newBoolean:{eq:true},newString:{in:['Alpha',null,1.5]}}}")),"Typed predicate literal conversion changed");
        var exactDefs = engine.Execute("query", JObject.Parse("{'from':'thing_defs','args':{'exact_names':['STEEL','Fake medicine']},'select':['def_name','label'],'where':{'any':[{'def_name':{'eq':'steel','case_sensitive':false}},{'label':{'eq':'Fake medicine','case_sensitive':false}}]}}"), new Data(), Session);
        Require(exactDefs.Status == "ok" && (string?)exactDefs.Data?["results"]?[0]?["rows"]?[0]?["def_name"] == "steel", "exact_names was not accepted/projected by real public-query schema: " + exactDefs.Message + exactDefs.Data);
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
        public object? NativeRoot(string root, IReadOnlyDictionary<string, object?> args, ReadContext context) {
            Reads++; if (root == "thing_def") return definitions.First(n => n.Key == (string)args["defName"]!);
            var selected = args.TryGetValue("exactNames", out var exact) && exact != null
                ? DefinitionCandidates.Exact(definitions, ((System.Collections.IEnumerable)exact).Cast<string>(), n => n.Key, n => (string?)((Dictionary<string, object?>)n.Handle!)["label"], context.Check) : definitions;
            return selected;
        }
    }
}
