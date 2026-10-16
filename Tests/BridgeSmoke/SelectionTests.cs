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
        Require((int?)exactDefs.Data?["results"]?[0]?["matched_count"] == 1 && ((JArray)exactDefs.Data!["results"]![0]!["rows"]!).Count == 1, "Scheduler progress became a definition candidate/evidence row");
        JObject Union(params string[] names) => new() { ["any"] = new JArray(names.SelectMany(name => new[] {
            new JObject { ["def_name"] = new JObject { ["eq"] = name, ["case_sensitive"] = false } },
            new JObject { ["label"] = new JObject { ["eq"] = name, ["case_sensitive"] = false } }
        })) };
        QueryOutcome Named(Data source, string[] names, JObject where) => engine.Execute("query", new JObject {
            ["from"] = "thing_defs", ["args"] = new JObject { ["exact_names"] = new JArray(names) },
            ["where"] = where, ["select"] = new JArray(), ["limit"] = 100
        }, source, Session);
        var aliasSource = new Data(); aliasSource.SetLabel("herb", "STEEL");
        var equivalent = Named(aliasSource, new[] { "steel" }, Union("StEeL"));
        Require(equivalent.Status == "ok" && (int?)equivalent.Data?["results"]?[0]?["matched_count"] == 2 && aliasSource.DefinitionFieldReads == 0, "Equivalent exact union reread metadata or lost canonical/label collision");
        var projectedSource = new Data(); projectedSource.SetLabel("herb", "STEEL");
        var projectedWhere = Union("StEeL");
        var projectedUnion = engine.Execute("query", new JObject { ["from"] = "thing_defs", ["args"] = new JObject { ["exact_names"] = new JArray("steel") }, ["where"] = projectedWhere, ["select"] = new JArray("def_name", "label") }, projectedSource, Session);
        var projectedRows = (JArray)projectedUnion.Data!["results"]![0]!["rows"]!;
        Require(projectedUnion.Status == "ok" && projectedRows.Select(row => (string)row["def_name"]!).OrderBy(value => value).SequenceEqual(new[] { "herb", "steel" }) && projectedSource.DefinitionFieldReads == 4 && JToken.DeepEquals(projectedUnion.Data["results"]![0]!["scope"]!["where"], projectedWhere), "Optimized exact union lost normal projections or original evidence scope");
        var normalSource = new Data(); normalSource.SetLabel("herb", "STEEL");
        var normalUnion = Named(normalSource, new[] { "steel" }, new JObject { ["all"] = new JArray(Union("StEeL")) });
        Require(normalUnion.Status == "ok" && (int?)normalUnion.Data?["results"]?[0]?["matched_count"] == 2 && normalSource.DefinitionFieldReads > 0, "Unoptimized exact union changed set or silently bypassed arbitrary wrapper");
        var duplicateSource = new Data(); duplicateSource.SetLabel("herb", "STEEL");
        var duplicateUnion = Named(duplicateSource, new[] { "steel", "STEEL", "rice" }, Union("STEEL", "steel", "rice"));
        Require(duplicateUnion.Status == "ok" && (int?)duplicateUnion.Data?["results"]?[0]?["matched_count"] == 3 && duplicateSource.DefinitionFieldReads == 0, "Casefold duplicate/multiple exact names changed union or duplicated rows");
        var missingUnion = Named(new Data(), new[] { "NotLoaded" }, Union("NOTLOADED"));
        Require(missingUnion.Status == "ok" && (int?)missingUnion.Data?["results"]?[0]?["matched_count"] == 0 && (bool?)missingUnion.Data?["results"]?[0]?["scan_complete"] == true, "Missing exact name became unknown, present or incomplete");
        foreach (var fallbackWhere in new[] {
            new JObject { ["all"] = new JArray(Union("steel"), JObject.Parse("{is_medicine:{eq:true}}")) },
            JObject.Parse("{any:[{def_name:{eq:'steel',case_sensitive:false}}]}"),
            JObject.Parse("{any:[{def_name:{eq:'steel',case_sensitive:true}},{label:{eq:'steel',case_sensitive:true}}]}")
        }) {
            var fallbackSource = new Data(); fallbackSource.SetLabel("herb", "STEEL");
            var fallback = Named(fallbackSource, new[] { "steel" }, fallbackWhere);
            Require(fallback.Status == "ok" && (int?)fallback.Data?["results"]?[0]?["matched_count"] == 1 && fallbackSource.DefinitionFieldReads > 0, "Additional AND, incomplete union or case-sensitive comparison was incorrectly removed: " + fallbackWhere);
        }
        var capturedSource = new Data { IdentityDelayMilliseconds = 40 }; capturedSource.SetLabel("herb", "STEEL"); capturedSource.CaptureDefinitionNames();
        var capturedNode = capturedSource.DefinitionNode("steel");
        var expiredCapturedContext = new ReadContext(capturedSource, Session, 0);
        Require((string?)engine.ReadPublicField(capturedNode, "defName", new Dictionary<string, object?>(), expiredCapturedContext) == "steel" && (string?)engine.ReadPublicField(capturedNode, "label", new Dictionary<string, object?>(), expiredCapturedContext) == "Fake medicine" && capturedSource.DefinitionFieldReads == 0, "Captured native identity attempted another getter/read-budget check");
        bool uncapturedStopped = false;
        try { engine.ReadPublicField(capturedNode, "stackLimit", new Dictionary<string, object?>(), expiredCapturedContext); } catch (ExecutionError error) when (error.Code == "deadline_exceeded") { uncapturedStopped = true; }
        Require(uncapturedStopped, "Captured identity disabled the budget for unobserved native fields");
        var capturedQuery = engine.Execute("query", new JObject { ["from"] = "thing_defs", ["args"] = new JObject { ["exact_names"] = new JArray("steel") }, ["where"] = Union("steel"), ["select"] = new JArray("def_name", "label") }, capturedSource, Session);
        Require(capturedQuery.Status == "ok" && (int?)capturedQuery.Data?["results"]?[0]?["matched_count"] == 2 && capturedSource.DefinitionFieldReads == 0, "Captured alias/canonical union lost normal twofield projection or invoked slow getters");
        var capturedAnd = engine.Execute("query", new JObject { ["from"] = "thing_defs", ["args"] = new JObject { ["exact_names"] = new JArray("steel") }, ["where"] = new JObject { ["all"] = new JArray(Union("steel"), JObject.Parse("{is_medicine:{eq:true}}")) }, ["select"] = new JArray("def_name", "label") }, capturedSource, Session);
        Require(capturedAnd.Status == "ok" && (int?)capturedAnd.Data?["results"]?[0]?["matched_count"] == 1 && (string?)capturedAnd.Data?["results"]?[0]?["rows"]?[0]?["def_name"] == "herb" && capturedSource.DefinitionFieldReads > 0, "Captured identity bypassed arbitrary additional native predicate");
        var failedDefinition = engine.Execute("query", JObject.Parse("{from:'thing_defs',args:{exact_names:['steel']},select:['def_name']}"), new Data { FailRootDeadline = true }, Session);
        Require(failedDefinition.Status == "partial" && failedDefinition.Issues.Any(issue => issue.Code == "deadline_exceeded" && issue.Message?.EndsWith("Read stage: source_iteration.", StringComparison.Ordinal) == true && issue.Path.Values<string>().SequenceEqual(new[] { "results", "0" })), "Exact-read deadline lost reason/path or failed to identify actual source iteration stage");
        var directDefinitions = new Data();
        var repeatedDefinitions = engine.Execute("query", JObject.Parse("{requests:[{from:'thing_defs',args:{exact_names:['steel','herb']},where:{is_medicine:{eq:true}},select:['def_name','label']},{from:'thing_defs',args:{exact_names:['steel','herb']},where:{is_medicine:{eq:true}},select:['def_name','label']}] }"), directDefinitions, Session);
        Require(repeatedDefinitions.Status == "ok" && directDefinitions.NativeRootReads == 2 && directDefinitions.GameReads == 0 && directDefinitions.MapReads == 0 && repeatedDefinitions.Data!["results"]!.All(r => (int?)r["matched_count"] == 1 && (string?)r["rows"]?[0]?["def_name"] == "herb"), "Definition roots used physical/cache setup or bypassed ordinary predicate/projection: " + repeatedDefinitions.Data);
        var delayedRoot = new Data { RootDelayMilliseconds = 40 };
        var slicedEngine = new GameQueryEngine(12);
        using (var operation = slicedEngine.BeginPublicQuery(JObject.Parse("{'from':'thing_defs','args':{'exact_names':['steel']},'select':['def_name','label']}"), delayedRoot, Session, () => true, () => Session.GameTick))
        {
            int frames = 0;
            while (!operation.Advance()) frames++;
            var completed = operation.Outcome!;
            Require(frames > 0 && completed.Status == "ok" && (int?)completed.Data?["results"]?[0]?["matched_count"] == 1 && (string?)completed.Data?["results"]?[0]?["rows"]?[0]?["def_name"] == "steel", "Completed native root acquisition poisoned the first enumeration slice: " + completed.Status + " " + completed.Data);
        }
        using (var operation = slicedEngine.BeginPublicQuery(JObject.Parse("{from:'pawns',args:{map_id:'current'},select:['id']}"), new Data { CurrentMapDelayMilliseconds = 40 }, Session, () => true, () => Session.GameTick))
        {
            while (!operation.Advance()) { }
            Require(operation.Outcome!.Status == "ok" && (int?)operation.Outcome.Data?["results"]?[0]?["matched_count"] == 2, "Completed current-map getter poisoned later map validation/enumeration: " + operation.Outcome.Data);
        }
        var pawnProgress = engine.Execute("query", JObject.Parse("{from:'pawns',select:['id']}"), new Data(), Session);
        Require(pawnProgress.Status == "ok" && (int?)pawnProgress.Data?["results"]?[0]?["matched_count"] == 2 && ((JArray)pawnProgress.Data!["results"]![0]!["rows"]!).Count == 2, "Pawn scan scheduling entities leaked into evidence/counts");
        Console.WriteLine("Selection tests passed: shared native booleans, numeric/text/null/collection/ancestry conditions, unknown logic, new schema fields, paging and pre-read rejection.");
    }

    private sealed class Data : IGameData, IThingData, IQueryData
    {
        public int Reads; public string? FailField; public int RootDelayMilliseconds; public int CurrentMapDelayMilliseconds; public int NativeRootReads, MapReads, GameReads; public bool FailRootDeadline; public int DefinitionFieldReads; public int IdentityDelayMilliseconds;
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
        public GameNode DefinitionNode(string definition) => definitions.Single(node => node.Key == definition);
        public void CaptureDefinitionNames()
        {
            for (int index = 0; index < definitions.Length; index++) {
                var node = definitions[index]; var fields = (Dictionary<string, object?>)node.Handle!;
                definitions[index] = new GameNode(node.Kind, node.Key, node.Owner, node.Handle, new Dictionary<string, object?> { ["defName"] = fields["defName"], ["label"] = fields["label"] });
            }
        }
        public void SetLabel(string definition, string label) => ((Dictionary<string, object?>)definitions.Single(node => node.Key == definition).Handle!)["label"] = label;
        public IEnumerable<GameNode> QueryThings(IReadOnlyDictionary<string, object?> args, ReadContext context, Func<GameNode, bool>? definitionFilter = null, IReadOnlyCollection<string>? exactIds = null) => Array.Empty<GameNode>();
        public IEnumerable<GameEntity> QueryPawns(IReadOnlyDictionary<string, object?> args, ReadContext context)
        {
            yield return new GameEntity("", null, ReadProgress.Instance);
            yield return new GameEntity("pawn1", null, new object());
            yield return new GameEntity("", null, ReadProgress.Instance);
            yield return new GameEntity("pawn2", null, new object());
        }
        public IEnumerable<GameEntity> Maps() { MapReads++; return new[] { new GameEntity("map", null, new object()) }; }
        public IEnumerable<GameEntity> Colonists() => Array.Empty<GameEntity>();
        public object? GameField(string field) { Reads++; GameReads++; if (field == "currentMapId") { if (CurrentMapDelayMilliseconds > 0) System.Threading.Thread.Sleep(CurrentMapDelayMilliseconds); return "map"; } return false; }
        public object? MapField(GameEntity map, string field) => null;
        public object? ColonistField(GameEntity pawn, string field) => null;
        public object? NodeField(GameNode node, string field, IReadOnlyDictionary<string, object?> arguments) { Reads++; if (node.Kind == "ThingDef") DefinitionFieldReads++; if (node.Kind == "ThingDef" && field is "defName" or "label" && IdentityDelayMilliseconds > 0) System.Threading.Thread.Sleep(IdentityDelayMilliseconds); if (field == FailField) throw ReadContext.Error("field_unavailable", "Injected missing field"); return ((Dictionary<string, object?>)node.Handle!)[field]; }
        public object? NativeField(GameNode node, string field, IReadOnlyDictionary<string, object?> args, ReadContext context) => NodeField(node, field, args);
        public object? NativeRoot(string root, IReadOnlyDictionary<string, object?> args, ReadContext context) {
            // Emulate an indivisible native getter: deadline checked before entry,
            // but the completed initialization can exceed one read slice.
            if (RootDelayMilliseconds > 0) { context.Check(); System.Threading.Thread.Sleep(RootDelayMilliseconds); }
            Reads++; NativeRootReads++;
            if (FailRootDeadline) throw ReadContext.Error("deadline_exceeded", "Injected native root deadline.");
            if (root == "thing_def") return definitions.First(n => n.Key == (string)args["defName"]!);
            var selected = args.TryGetValue("exactNames", out var exact) && exact != null
                ? DefinitionCandidates.ExactSteps(definitions, ((System.Collections.IEnumerable)exact).Cast<string>(), n => n.Key, n => (string?)((Dictionary<string, object?>)n.Handle!)["label"], context.Check) : definitions.Cast<object>();
            return selected;
        }
    }
}
