using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class QueryEngineTests
{
    private static readonly SessionDescriptor Session = new(new string('2', 32), new string('3', 32), 100, 7);

    public static void Run()
    {
        var engine = new GameQueryEngine(1000);
        CatalogAndTemplates(engine);
        SelectionAndCache(engine);
        NestedCollectionsAndRelations(engine);
        ThoughtReferenceIdentity();
        ValidationBeforeReads(engine);
        Pagination(engine);
        PartialAndNullResults(engine);
        InlinePageCoverage(engine);
        var expired = Exact(new GameQueryEngine(0), new FakeData(), "{ game { paused } }");
        Require(expired.Status == "partial" && expired.Issues.Any(x => x.Code.Equals("deadline_exceeded", StringComparison.OrdinalIgnoreCase)), "read deadline not enforced");
        Console.WriteLine("Query tests passed: object sections, exact equivalence, nested selection, bounded collections, cache isolation, validation, partials and thread identity.");
    }

    private static void CatalogAndTemplates(GameQueryEngine engine)
    {
        var source = new FakeData();
        var catalog = engine.Execute("query_catalog", new JObject(), source, Session);
        Require(catalog.Status == "ok" && source.Reads.Count == 0, "catalog should not read game state");
        var fixture = Environment.GetEnvironmentVariable("NEKO_QUERY_CATALOG");
        if (!string.IsNullOrEmpty(fixture)) File.WriteAllText(fixture, catalog.Data!.ToString());
        var batch = Templates(engine, source, @"{'requests':[
            {'target':{'type':'colony'},'sections':['overview']},
            {'target':{'type':'pawn','id':'pawn1'},'sections':['status','mood','health','equipment','profile']}
        ]}");
        Ok(batch, "five-section object query");
        var exact = engine.Execute("graphql", new JObject {
            ["document"] = batch.Execution!["document"], ["variables"] = batch.Execution["variables"]
        }, new FakeData(), Session);
        Ok(exact, "expanded exact query");
        Require(JToken.DeepEquals(batch.Data, exact.Data), "template and exact results differ");
        var pawn = (JObject)batch.Data!["o1"]!;
        foreach (var section in new[] { "status", "mood", "health", "equipment", "profile" })
            Require(pawn[section] is JObject, "requested section missing: " + section);
        Require(((JArray)batch.Execution["bindings"]!).Count == 2, "missing object bindings");
        var wireFixture = Environment.GetEnvironmentVariable("NEKO_TEMPLATE_RESULT");
        if (!string.IsNullOrEmpty(wireFixture))
        {
            var request = ProtocolEnvelope.Create(ProtocolMessageTypes.QueryRequest, new string('1', 32), 2,
                new ProtocolPayload { Operation = "template_query", Arguments = new JObject() }, Session, new string('4', 32));
            var envelope = ProtocolEnvelope.CreateQueryResult(request, request.ConnectionEpoch, 3, Session,
                batch.Status, batch.Data, batch.Issues, execution: batch.Execution);
            File.WriteAllBytes(wireFixture, ProtocolCodec.Serialize(envelope));
        }

        source = new FakeData();
        var merged = Templates(engine, source, @"{'requests':[
            {'target':{'type':'pawn','id':'pawn1'},'sections':['status','mood']},
            {'target':{'type':'pawn','id':'pawn1'},'sections':['health','mood']},
            {'target':{'type':'pawn','id':'pawn2'},'sections':['profile']}
        ]}");
        Ok(merged, "same-object merge");
        Require(merged.Data!.Properties().Count() == 2, "same pawn produced duplicate result roots");
        Require((string?)merged.Data["o0"]?["id"] == "pawn1" && (string?)merged.Data["o1"]?["id"] == "pawn2", "merged roots lost identity");
        Require(merged.Data["o0"]?["mood"] is JObject && merged.Data["o0"]?["health"] is JObject, "section union lost data");
        Require(source.Reads.Count(x => x == "pawn1:Mood.level") == 1, "overlapping sections reread shared mood field");
        Require(((JArray)merged.Execution!["bindings"]!).Count == 3, "merged requests lost bindings");
        Require(source.Reads.Count(x => x == "Colonists") == 1, "same execution repeated identity enumeration");
        AssertThread(source);
    }

    private static void SelectionAndCache(GameQueryEngine engine)
    {
        var source = new FakeData();
        Ok(Exact(engine, source, "{ game { paused } }"), "paused query");
        Require(source.Reads.SequenceEqual(new[] { "Game.paused" }), "paused queried unrelated data: " + string.Join(",", source.Reads));
        source = new FakeData();
        Ok(Exact(engine, source, "{ pawn(id: \"pawn1\") { id name mood { level } } }"), "selected nested field");
        Require(source.Reads.Contains("pawn1:Mood.level"), "selected mood field not read");
        Require(!source.Reads.Any(x => x.Contains(":Health") || x.Contains(":Equipment") || x.Contains(":Profile") || x.Contains(":Status")), "unrequested section read");
        Require(!source.Reads.Any(x => x.Contains("thoughtGroups") || x.Contains("memories")), "mood scalar read full mood details");
        source = new FakeData();
        var cached = Exact(engine, source, @"{
            a:pawn(id:""pawn1""){mood{level} health{conditions(first:1,offset:0){items{id} returned}}}
            b:pawn(id:""pawn1""){mood{level} health{conditions(first:1,offset:1){items{id} returned}}}
            c:pawn(id:""pawn2""){mood{level}}
        }");
        Ok(cached, "aliased reads with distinct arguments");
        Require(source.Reads.Count(x => x == "pawn1:Mood.level") == 1, "identical field aliases bypass cache");
        Require(source.Reads.Count(x => x == "pawn2:Mood.level") == 1, "different pawn alias shared cache");
        Require((string?)cached.Data!["a"]?["health"]?["conditions"]?["items"]?[0]?["id"] == "pawn1-condition0", "first slice mismatch");
        Require((string?)cached.Data["b"]?["health"]?["conditions"]?["items"]?[0]?["id"] == "pawn1-condition1", "different slice arguments shared cached page");
        source = new FakeData();
        var stats = Exact(engine, source, @"{pawn(id:""pawn1""){
            a:stats(defNames:[""MoveSpeed""]){items{defName value}}
            b:stats(defNames:[""WorkSpeedGlobal""]){items{defName value}}
        }}");
        Ok(stats, "selected derived stats");
        Require((string?)stats.Data!["pawn"]?["a"]?["items"]?[0]?["defName"] == "MoveSpeed" &&
            (string?)stats.Data["pawn"]?["b"]?["items"]?[0]?["defName"] == "WorkSpeedGlobal", "stat definition arguments shared cache");
        Require(!source.Reads.Any(x => x.Contains(":Profile") || x.Contains(":Equipment") || x.Contains(":Health")), "selected stats eagerly traversed unrelated object fields");
        var visible = Exact(engine, source, @"{pawn(id:""pawn1""){health{
            a:conditions(visibleOnly:true){total}
            b:conditions(visibleOnly:false){total}
        }}}");
        Ok(visible, "filtered nested collections");
        Require((int?)visible.Data!["pawn"]?["health"]?["a"]?["total"] == 3 &&
            (int?)visible.Data["pawn"]?["health"]?["b"]?["total"] == 4, "different visibility arguments shared a collection cache");
        Require(!source.Reads.Any(x => x.Contains(":Condition.")), "collection counts eagerly read per-condition details");
        source = new FakeData();
        var definitions = Exact(engine, source, "{pawn(id:\"pawn1\"){statDefinitions(search:\"Move\"){items{defName label description} total}}}");
        Ok(definitions, "stat definition discovery");
        Require((string?)definitions.Data!["pawn"]?["statDefinitions"]?["items"]?[0]?["defName"] == "MoveSpeed", "stat discovery lost definition identity");
        Require(!source.Reads.Any(x => x.Contains(":Stat.") || x.EndsWith(":Pawn.stats", StringComparison.Ordinal)), "discovering stat definitions calculated pawn values");
        AssertThread(source);
    }

    private static void NestedCollectionsAndRelations(GameQueryEngine engine)
    {
        var source = new FakeData();
        var first = Exact(engine, source, @"{pawn(id:""pawn1""){health{
            conditions(first:1,offset:0){items{id part{key label parentKey}} total offset returned hasMore nextOffset complete}
            bodyParts(first:3){items{key parentKey label} total returned}
        }}}");
        Ok(first, "bounded health objects");
        var page = first.Data!["pawn"]!["health"]!["conditions"]!;
        Require((int?)page["total"] == 3 && (int?)page["returned"] == 1 && (int?)page["offset"] == 0, "nested page counts incorrect");
        Require((bool?)page["hasMore"] == true && (int?)page["nextOffset"] == 1 && (bool?)page["complete"] == true, "normal page is not complete or cannot continue");
        var part = page["items"]![0]!["part"]!;
        Require((string?)part["key"] == "pawn1-part1" && (string?)part["parentKey"] == "pawn1-part0", "condition lost body-part relationship");
        Require(first.Data["pawn"]!["health"]!["bodyParts"]!["items"]!.Any(x => (string?)x["key"] == (string?)part["key"]), "condition points to missing body-part identity");
        var last = Exact(engine, source, @"{pawn(id:""pawn1""){health{conditions(first:2,offset:1){items{id} returned hasMore nextOffset complete}}}}");
        Ok(last, "last nested page");
        var lastPage = last.Data!["pawn"]!["health"]!["conditions"]!;
        Require((int?)lastPage["returned"] == 2 && (bool?)lastPage["hasMore"] == false && lastPage["nextOffset"]!.Type == JTokenType.Null, "last page advertises another page");
        var groups = Exact(engine, source, @"{pawn(id:""pawn1""){mood{thoughtGroups(first:1){items{defName moodOffset memberCount members(first:1){items{ref kind otherPawnId} total hasMore nextOffset}}}}}}");
        Ok(groups, "nested thought membership");
        var group = groups.Data!["pawn"]!["mood"]!["thoughtGroups"]!["items"]![0]!;
        Require((int?)group["memberCount"] == 2 && (int?)group["members"]?["total"] == 2, "thought group membership counts lost");
        Require((bool?)group["members"]?["hasMore"] == true, "nested list lacks independent continuation");
        var referenceQuery = new JObject { ["document"] = "{pawn(id:\"pawn1\"){mood{memories(first:1){items{ref referenceScope}}}}}" };
        var references = engine.Execute("graphql", referenceQuery, source, Session);
        var laterReferences = engine.Execute("graphql", referenceQuery, source,
            new SessionDescriptor(Session.ColonyId, Session.LoadEpoch, 101, 8));
        Ok(references, "observation-scoped thought refs"); Ok(laterReferences, "later thought refs");
        var currentRef = references.Data!["pawn"]!["mood"]!["memories"]!["items"]![0]!;
        var laterRef = laterReferences.Data!["pawn"]!["mood"]!["memories"]!["items"]![0]!;
        Require((string?)currentRef["referenceScope"] == "observation" && (string?)currentRef["ref"] != (string?)laterRef["ref"], "thought references pretended to persist across samples");
        AssertThread(source);
    }

    private static void ValidationBeforeReads(GameQueryEngine engine)
    {
        foreach (var invalid in new[] {
            "{ game { nonexistent } }", "mutation { game { paused } }", "{ __schema { queryType { name } } }",
            "{ colonists(first:51){nodes{id}} }", "{ game{paused} colonists(first:-1){nodes{id}} }",
            "{ game{paused} colonists(after:\"invalid\"){nodes{id}} }",
            "query { ...Fields } fragment Fields on Query { game { paused } }",
            "{game{paused} pawn(id:\"pawn1\"){health{conditions(first:101){items{id}}}}}",
            "{game{paused} pawn(id:\"pawn1\"){health{conditions(offset:-1){items{id}}}}}",
            "{game{paused} pawn(id:\"pawn1\"){health{conditions(offset:8193){items{id}}}}}",
            "{game{paused} pawn(id:\"pawn1\"){stats(defNames:[]){items{defName}}}}",
            "{pawn(id:\"pawn1\"){mood{thoughtGroups(first:100){items{members(first:100){items{defName label moodOffset}}}}}}}"
        })
        {
            var source = new FakeData();
            var bad = Exact(engine, source, invalid);
            Require(bad.Status == "failed" && source.Reads.Count == 0, "invalid/overbudget query performed reads: " + invalid + " status=" + bad.Status);
        }
        foreach (var invalid in new[] {
            @"{'requests':[{'target':{'type':'pawn','id':'pawn1'},'sections':['status']},{'target':{'type':'pawn','id':'pawn2'},'sections':['not_a_section']}]}",
            @"{'requests':[{'target':{'type':'colony'},'sections':['overview']},{'target':{'type':'pawn'},'sections':['health']}]}",
            @"{'requests':[{'target':{'type':'colony'},'sections':['health']}]}",
            @"{'requests':[{'target':{'type':'colonists','first':500},'sections':['summary']}]}",
            @"{'requests':[{'target':{'type':'unknown','id':'pawn1'},'sections':['status']}]}",
            @"{'requests':[{'target':{'type':'pawn','id':'pawn1'},'sections':[]}]}",
        })
        {
            var source = new FakeData();
            var bad = Templates(engine, source, invalid);
            Require(bad.Status == "failed" && source.Reads.Count == 0, "invalid template partially executed: " + invalid);
        }
    }

    private static void ThoughtReferenceIdentity()
    {
        var source = new FakeData();
        var pawn = new GameEntity("pawn1", "map1", new object());
        var sharedThought = new object();
        var memory = new GameNode("Thought", "mood/memories/0", pawn, sharedThought);
        var member = new GameNode("Thought", "mood/groups/1/members/2", pawn, sharedThought);
        var different = new GameNode("Thought", "mood/memories/0", pawn, new object());
        var context = new ReadContext(source, Session, 1000);
        var reference = (string?)context.ReadNode(memory, "ref");
        Require(!string.IsNullOrEmpty(reference) && reference == (string?)context.ReadNode(member, "ref"), "same Thought instance received different refs through memory and group paths");
        Require(reference != (string?)context.ReadNode(different, "ref"), "distinct Thought handles sharing a path received the same ref");
        Require(reference == (string?)context.ReadNode(memory, "ref"), "repeated read changed a Thought ref within the observation");
        var next = new ReadContext(source, new SessionDescriptor(Session.ColonyId, Session.LoadEpoch, 101, 8), 1000);
        Require(reference != (string?)next.ReadNode(memory, "ref"), "Thought ref survived a snapshot change");
        Require((string?)context.ReadNode(memory, "referenceScope") == "observation" && source.Reads.Count == 0, "detached reference metadata performed game reads");
    }

    private static void Pagination(GameQueryEngine engine)
    {
        var source = new FakeData();
        var first = Exact(engine, source, "{colonists(first:1){nodes{id} pageInfo{hasNextPage endCursor}}}");
        Ok(first, "first colonist page");
        var cursor = (string)first.Data!["colonists"]!["pageInfo"]!["endCursor"]!;
        var args = new JObject { ["document"] = "query($c:String){colonists(first:1,after:$c){nodes{id} pageInfo{hasNextPage endCursor}}}", ["variables"] = new JObject { ["c"] = cursor } };
        var second = engine.Execute("graphql", args, source, Session);
        Ok(second, "second colonist page");
        Require((string?)second.Data!["colonists"]?["nodes"]?[0]?["id"] == "pawn2", "paging duplicated or skipped an identity");
        var reloaded = engine.Execute("graphql", args, source, new SessionDescriptor(Session.ColonyId, new string('9', 32), 100, 0));
        Require(reloaded.ErrorCode == "stale_cursor", "cursor survived load change");
        var missing = Exact(engine, source, "{colonists(name:\"Missing\"){nodes{id}} pawn(id:\"missing\"){id}}");
        Ok(missing, "missing identity");
        Require(((JArray)missing.Data!["colonists"]!["nodes"]!).Count == 0 && missing.Data["pawn"]!.Type == JTokenType.Null, "not-found identity semantics incorrect");
        var filtered = Templates(engine, source, @"{'requests':[
            {'target':{'type':'colonists','ids':['pawn1'],'map_id':'map1','name':'wal','first':1},'sections':['summary']},
            {'target':{'type':'colonists','ids':['pawn2']},'sections':['summary']}
        ]}");
        Ok(filtered, "filtered colonist targets");
        Require((string?)filtered.Data!["o0"]?["nodes"]?[0]?["id"] == "pawn1" && (string?)filtered.Data["o1"]?["nodes"]?[0]?["id"] == "pawn2", "distinct filters collided");
    }

    private static void PartialAndNullResults(GameQueryEngine engine)
    {
        var source = new FakeData { FailMood = true };
        var partial = Exact(engine, source, "{pawn(id:\"pawn1\"){id mood{level} health{summary{healthPercent}} equipment{Primary{id}}}}");
        Require(partial.Status == "partial", "field failure lost partial semantics");
        Require((string?)partial.Data!["pawn"]?["id"] == "pawn1" && (double?)partial.Data["pawn"]?["health"]?["summary"]?["healthPercent"] == 0.75, "mood failure erased another section");
        Require(partial.Issues.Any(x => x.Path.ToString().Contains("level")), "field failure path missing");
        var aliased = Exact(engine, source, "{p:colonists(first:1){pi:pageInfo{cursor:endCursor} people:nodes{id m:mood{v:level}}}}");
        Require(aliased.Status == "partial" && aliased.Data!["p"]!["pi"]!["cursor"]!.Type == JTokenType.Null, "aliases bypassed partial-page cursor handling");
        var lastPage = Exact(engine, source, "{colonists(first:50){nodes{id mood{level}} pageInfo{hasNextPage endCursor}}}");
        Require(lastPage.Status == "partial" && (bool?)lastPage.Data!["colonists"]!["pageInfo"]!["hasNextPage"] == false &&
            lastPage.Data["colonists"]!["pageInfo"]!["endCursor"]!.Type == JTokenType.Null,
            "a failed detail field invented extra colonist identities on the final page");
        source = new FakeData { FailCondition = true };
        var failedPage = Exact(engine, source, "{pawn(id:\"pawn1\"){health{c:conditions(first:2){rows:items{id s:severity} more:hasMore next:nextOffset done:complete}}}}");
        Require(failedPage.Status == "partial", "failed nested field reported successful page");
        var page = failedPage.Data!["pawn"]!["health"]!["c"]!;
        Require((bool?)page["done"] == false && page["next"]!.Type == JTokenType.Null,
            "incomplete nested page advanced past failed fields: " + page + " issues=" + string.Join(";", failedPage.Issues.Select(x => x.Path.ToString())));
        source = new FakeData { MissingMood = true, EmptyConditions = true };
        var absent = Exact(engine, source, "{pawn(id:\"pawn1\"){mood{level} health{conditions{items{id} total returned complete}} equipment{Primary{id}}}}");
        Ok(absent, "not-applicable and empty collection");
        Require(absent.Data!["pawn"]!["mood"]!.Type == JTokenType.Null, "absent mood tracker should return null without issue");
        var conditions = absent.Data["pawn"]!["health"]!["conditions"]!;
        Require((int?)conditions["total"] == 0 && ((JArray)conditions["items"]!).Count == 0 && (bool?)conditions["complete"] == true, "empty list conflated with failed read");
        Require(absent.Data["pawn"]!["equipment"]!["Primary"]!.Type == JTokenType.Null, "absent primary weapon should be null");
        source = new FakeData { OversizedConditions = true };
        var tooMany = Exact(engine, source, "{pawn(id:\"pawn1\"){health{conditions(first:1){items{id} total}} mood{level}}}");
        Require(tooMany.Status == "partial" && tooMany.Issues.Any(x => x.Code.Equals("node_scan_limit", StringComparison.OrdinalIgnoreCase)), "oversized nested identity scan bypassed bounds");
        Require(tooMany.Data!["pawn"]!["health"]!["conditions"]!.Type == JTokenType.Null, "incomplete enumeration claimed an exact collection count");
        Require((double?)tooMany.Data["pawn"]?["mood"]?["level"] == 0.6, "collection scan failure erased unrelated section");
        AssertThread(source);
    }

    private static QueryOutcome Exact(GameQueryEngine engine, FakeData source, string document) => engine.Execute("graphql", new JObject { ["document"] = document }, source, Session);

    private static void InlinePageCoverage(GameQueryEngine engine)
    {
        const string query = "{p:colonists(first:1){people:nodes{id health{c:conditions(first:1){items{id severity} ... on HealthConditionsSlice {... {done:complete next:nextOffset}}}}} ... on ColonistPage {pi:pageInfo{... on PageInfo {cursor:endCursor more:hasNextPage}}}}}";
        var failed = Exact(engine, new FakeData { FailCondition = true }, query);
        Require(failed.Status == "partial", "inline failure did not remain partial");
        var slice = failed.Data!["p"]!["people"]![0]!["health"]!["c"]!;
        Require((bool?)slice["done"] == false && slice["next"]!.Type == JTokenType.Null,
            "inline metadata advanced past failed condition fields");
        Require(failed.Data["p"]!["pi"]!["cursor"]!.Type == JTokenType.Null && (bool?)failed.Data["p"]!["pi"]!["more"] == true,
            "inline root pageInfo allowed unsafe continuation");
        var success = Exact(engine, new FakeData(), query);
        Ok(success, "successful inline coverage");
        Require((bool?)success.Data!["p"]!["people"]![0]!["health"]!["c"]!["done"] == true &&
            (int?)success.Data["p"]!["people"]![0]!["health"]!["c"]!["next"] == 1 &&
            success.Data["p"]!["pi"]!["cursor"]!.Type == JTokenType.String, "inline success lost normal continuation");
    }
    private static QueryOutcome Templates(GameQueryEngine engine, FakeData source, string arguments) => engine.Execute("template_query", JObject.Parse(arguments), source, Session);
    private static void Ok(QueryOutcome result, string scenario) => Require(result.Status == "ok", scenario + ": " + result.Status + " " + result.Message + " " + string.Join(";", result.Issues.Select(x => x.Message)));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void AssertThread(FakeData source) => Require(source.Threads.All(x => x == Thread.CurrentThread.ManagedThreadId), "resolver left calling thread");

    private sealed class FakeData : IGameData
    {
        public readonly List<string> Reads = new();
        public readonly List<int> Threads = new();
        public bool FailMood, FailCondition, MissingMood, EmptyConditions, OversizedConditions;
        private void Mark(string key) { Reads.Add(key); Threads.Add(Thread.CurrentThread.ManagedThreadId); }
        public IEnumerable<GameEntity> Maps() { Mark("Maps"); yield return new GameEntity("map1", null, this); }
        public IEnumerable<GameEntity> Colonists() { Mark("Colonists"); for (var i = 1; i <= 3; i++) yield return new GameEntity("pawn" + i, "map1", this); }
        public object? GameField(string field) { Mark("Game." + field); return field == "paused" ? (object)false : "map1"; }
        public object? MapField(GameEntity map, string field) { Mark("Map." + field); return field switch { "name" => "Home", "colonistCount" => 3, _ => (object)false }; }
        public object? ColonistField(GameEntity pawn, string field)
        {
            Mark(pawn.Id + ":Pawn." + field);
            if (field != "name") throw new InvalidOperationException("Pawn root may only read name directly.");
            return pawn.Id == "pawn1" ? "Walter" : "Colonist " + pawn.Id;
        }
        public object? NodeField(GameNode node, string field, IReadOnlyDictionary<string, object?> arguments)
        {
            Mark(node.Owner.Id + ":" + node.Kind + "." + field);
            var index = node.Handle is int value ? value : 0;
            switch (node.Kind)
            {
                case "Pawn":
                    if (field == "mood" && MissingMood) return null;
                    if (field == "equipment") return Child(node, "Pawn_EquipmentTracker");
                    if (field == "apparel") return Child(node, "Pawn_ApparelTracker");
                    if (field == "inventory") return Child(node, "Pawn_InventoryTracker");
                    if (field == "carryTracker") return Child(node, "Pawn_CarryTracker");
                    if (field == "statDefinitions") return Many(node, "StatDefinition", 1);
                    if (field == "stats") return ((IEnumerable)arguments["defNames"]!).Cast<object>()
                        .Select(x => new GameNode("Stat", node.Key + "/Stat/" + x, node.Owner, x.ToString())).ToArray();
                    return Child(node, char.ToUpperInvariant(field[0]) + field.Substring(1));
                case "Status": return field switch { "currentJob" => Child(node, "Job"), "position" => Child(node, "Position"), "needs" => Many(node, "Need", 2), "mentalState" => null, _ => (object)false };
                case "Position": return field == "mapId" ? node.Owner.MapId : (object)5;
                case "Job": return field switch { "id" => "job1", "defName" => "Wait", "report" => "Standing", "targets" => Many(node, "JobTarget", 1), _ => null };
                case "JobTarget": return field switch { "slot" => "A", "kind" => "thing", "thingId" => "thing1", "mapId" => "map1", _ => (object)5 };
                case "Need": return field switch { "defName" => index == 0 ? "Food" : "Rest", "label" => index == 0 ? "Food" : "Rest", "maxLevel" => 1.0, _ => 0.6 };
                case "Mood":
                    if (field == "level" && FailMood) throw new InvalidOperationException("simulated mood unavailable");
                    return field switch { "level" => 0.6, "instant" => 0.65, "breakThresholds" => Child(node, "Thresholds"), "thoughtGroups" => Many(node, "ThoughtGroup", 2), "memories" => Many(node, "Thought", 3), _ => null };
                case "Thresholds": return field switch { "minor" => 0.35, "major" => 0.2, _ => 0.05 };
                case "ThoughtGroup": return field switch { "defName" => "Comfortable" + index, "label" => "Comfortable", "stageIndex" => index, "moodOffset" => 5.0, "memberCount" => 2, "members" => Many(node, "Thought", 2), _ => null };
                case "Thought": return field switch { "ref" => node.Key, "defName" => "AteFineMeal", "label" => "Ate fine meal", "description" => "A good meal.", "stageIndex" => 0, "moodOffset" => 5.0, "kind" => "memory", "otherPawnId" => "pawn2", "ageTicks" => 5, "durationTicks" => 60000, _ => null };
                case "Health": return field switch { "summary" => Child(node, "HealthSummary"), "capacities" => Many(node, "Capacity", 2), "conditions" => Many(node, "Condition", EmptyConditions ? 0 : OversizedConditions ? 8193 : arguments.TryGetValue("visibleOnly", out var visible) && visible is false ? 4 : 3), "bodyParts" => Many(node, "BodyPart", 3), _ => null };
                case "HealthSummary": return field switch { "healthPercent" => 0.75, "pain" => 0.1, "bleedRate" => 0.0, _ => (object)false };
                case "Capacity": return field switch { "defName" => index == 0 ? "Moving" : "Consciousness", "label" => index == 0 ? "Moving" : "Consciousness", _ => (object)0.8 };
                case "Condition":
                    if (field == "severity" && FailCondition) throw new InvalidOperationException("simulated condition unavailable");
                    return field switch {
                        "id" => node.Owner.Id + "-condition" + index, "defName" => "Bruise", "label" => "Bruise " + index,
                        "description" => "An injury.", "severity" => 0.1 + index, "severityLabel" => "10%", "visible" => true,
                        "part" => Child(node, "BodyPart", 1), "tending" => Child(node, "Tending"), "immunity" => null, _ => null
                    };
                case "BodyPart": return field switch { "key" => node.Owner.Id + "-part" + index, "defName" => index == 0 ? "Torso" : "Arm", "label" => index == 0 ? "Torso" : "Arm " + index, "index" => index, "parentKey" => index == 0 ? null : node.Owner.Id + "-part0", "health" => 20.0, "maxHealth" => 30.0, _ => null };
                case "Tending": return field switch { "isTended" => true, "quality" => 0.8, _ => 1000 };
                case "Immunity": return field == "level" ? (object)0.5 : false;
                case "Pawn_EquipmentTracker": return field == "Primary" ? null : Many(node, "Thing", 0);
                case "Pawn_ApparelTracker": return Many(node, "Thing", 2);
                case "Pawn_InventoryTracker": return Child(node, "ThingOwner");
                case "Pawn_CarryTracker": return null;
                case "ThingOwner": return field == "items" ? Many(node, "Thing", 1) : null;
                case "Thing": return field switch { "id" => node.Owner.Id + "-item" + index, "runtimeType" => "RimWorld.Apparel", "def" or "Stuff" => Child(node, "ThingDef"), "stackCount" => 1, "HitPoints" => 50, "MaxHitPoints" => 100, _ => null };
                case "ThingDef": return field switch { "defName" => "Shirt", "label" => "Cloth shirt", "category" => "Item", "thingClass" => "RimWorld.Apparel", _ => null };
                case "Profile": return field switch { "age" => Child(node, "Age"), "backstories" => Many(node, "Backstory", 2), "traits" => Many(node, "Trait", 2), "skills" => Many(node, "Skill", 3), _ => null };
                case "Age": return field switch { "biologicalYears" => 30.5, "chronologicalYears" => 130.5, _ => "Adult" };
                case "Backstory": return field switch { "slot" => index == 0 ? "childhood" : "adulthood", "defName" => "Farmer" + index, "title" => "Farmer", _ => "Grew food." };
                case "Trait": return field switch { "defName" => "Kind", "label" => "Kind", "degree" => 0, "suppressed" => false, _ => null };
                case "Skill": return field switch { "defName" => "Plants" + index, "label" => "Plants", "level" => 10, "passion" => "Major", "aptitude" => 1, "totallyDisabled" or "permanentlyDisabled" => false, "xpSinceLastLevel" => 25.0, "xpRequiredForLevelUp" => 1000.0, _ => null };
                case "Stat": return field switch { "defName" => node.Handle as string, "label" => "Selected stat", "value" => 4.0, _ => "4" };
                case "StatDefinition": return field switch { "defName" => "MoveSpeed", "label" => "Move speed", _ => "Movement cells per second." };
                default: throw new InvalidOperationException("Unexpected node kind " + node.Kind + "." + field);
            }
        }
        private static GameNode Child(GameNode parent, string kind, int index = 0) => new(kind, parent.Key + "/" + kind + "/" + index, parent.Owner, index);
        private static IEnumerable<GameNode> Many(GameNode parent, string kind, int count)
        { for (var i = 0; i < count; i++) yield return Child(parent, kind, i); }
    }
}
