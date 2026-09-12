using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class ThingQueryTests
{
    private static readonly SessionDescriptor Session = new(new string('2', 32), new string('3', 32), 100, 7);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static QueryOutcome Exact(GameQueryEngine engine, Data data, string query) => engine.Execute("graphql", new JObject { ["document"] = query }, data, Session);
    public static void Run()
    {
        var engine = new GameQueryEngine(1000); var source = new Data();
        var described = engine.Execute("describe_schema", JObject.Parse("{'types':['Thing','ThingComp','CompRottable']}"), source, Session);
        Require(described.Status == "ok" && source.Reads.Count == 0, "schema metadata performed game reads");
        Require(described.Data!["types"]![1]!["possible_types"]!.Any(x => x.ToString() == "UnsupportedThingComp"), "unknown components missing from type directory");
        var input = JObject.Parse("{'requests':[{'target':{'type':'things','map_id':'map1','visible_only':false,'first':2},'sections':['basic','components']},{'target':{'type':'thing','id':'thing1'},'sections':['components']}]}");
        var templated = engine.Execute("template_query", input, source, Session);
        Require(templated.Status == "ok", "native template failed: " + string.Join(";", templated.Issues.Select(x => x.Message)));
        var exact = engine.Execute("graphql", new JObject { ["document"] = templated.Execution!["document"], ["variables"] = templated.Execution["variables"] }, new Data(), Session);
        Require(JToken.DeepEquals(templated.Data, exact.Data), "native template/exact values differ");
        var comps = templated.Data!["o1"]!["AllComps"]!["items"]!;
        Require((string?)comps[0]!["runtimeType"] == "CustomMod.Rot" && (string?)comps[0]!["Stage"] == "Fresh", "base projection erased derived runtime class");
        Require((bool?)comps[1]!["supported"] == false && comps[1]!["supportedType"]!.Type == JTokenType.Null, "unknown component lost its unsupported status");
        Require(comps[1]!["Stage"] == null, "inline fragment executed on unknown component");
        Require((string?)comps[0]!["ref"] != (string?)comps[2]!["ref"], "same-class components were collapsed");

        source = new Data();
        var joined = Exact(engine, source, "{ a:thing(id:\"thing1\"){id stackCount} pawn(id:\"pawn1\"){inventory{innerContainer{items(first:1){items{id stackCount}}}}} }");
        Require(joined.Status == "ok" && (string?)joined.Data!["a"]!["id"] == (string?)joined.Data["pawn"]!["inventory"]!["innerContainer"]!["items"]!["items"]![0]!["id"], "same thing lost identity across routes");
        Require(source.Reads.Count(x => x == "thing1:stackCount") == 1, "same thing member read twice across routes");
        Require(!source.Reads.Any(x => x.EndsWith(":AllComps") || x.EndsWith(":def")), "unselected fields were read");

        source = new Data();
        var firstQuery = Exact(engine, source, "{things(mapId:\"map1\",first:1){nodes{id} pageInfo{endCursor}}}");
        var cursor = (string)firstQuery.Data!["things"]!["pageInfo"]!["endCursor"]!;
        var nextQuery = engine.Execute("graphql", new JObject { ["document"] = "query($after:String){things(mapId:\"map1\",first:1,after:$after){nodes{id}}}", ["variables"] = new JObject { ["after"] = cursor } }, source, Session);
        Require(nextQuery.Status == "ok" && (string?)nextQuery.Data!["things"]!["nodes"]![0]!["id"] == "thing2", "defaulted arguments broke real query cursor roundtrip");
        source = new Data { FailRot = true };
        var failed = Exact(engine, source, "{things(mapId:\"map1\",first:2){nodes{AllComps(first:1){items{... on CompRottable{Stage}} nextOffset complete}} pageInfo{cursor:endCursor more:hasNextPage}}}");
        Require(failed.Status == "partial", "component read failure was not partial");
        Require(failed.Data!["things"]!["pageInfo"]!["cursor"]!.Type == JTokenType.Null, "failed component allowed advancing root cursor");
        Require((bool?)failed.Data["things"]!["nodes"]![0]!["AllComps"]!["complete"] == false, "failed component slice marked complete");
        foreach (var invalid in new[] {
            "{game{paused} things(mapId:\"map1\",after:\"broken\"){nodes{id}}}",
            "{game{paused} things(mapId:\"map1\",first:51){nodes{id}}}",
            "{game{paused} things(mapId:\"map1\",requestGroup:\"Invented\"){nodes{id}}}",
            "{game{paused} thing(id:\"thing1\"){AllComps{items{... on CompRottable @skip(if:true){Stage}}}}}",
            "{game{paused} things(mapId:\"map1\",first:50){nodes{AllComps(first:100){items{... on CompRottable{a:Stage b:Stage c:Stage d:Stage e:Stage}}}}}}",
        })
        {
            source = new Data(); var bad = Exact(engine, source, invalid);
            Require(bad.Status == "failed" && source.Reads.Count == 0, "invalid native query performed reads: " + invalid);
        }
        source = new Data { MixedComponents = true };
        var aliases = Exact(engine, source, "{thing(id:\"thing1\"){AllComps{items{... on CompQuality{x:Quality} ... on CompRottable{x:Stage}}}}}");
        Require(aliases.Status == "ok", "polymorphic alias query failed");
        var descriptions = (JObject)aliases.Execution!["evidence"]!["nodes"]!;
        Require((string?)descriptions["/thing/AllComps/items/0/x"]?["native"] == "CompQuality.Quality" &&
            (string?)descriptions["/thing/AllComps/items/1/x"]?["native"] == "CompRottable.Stage", "array descriptors relabeled polymorphic aliases: " + string.Join(";", descriptions.Properties().Where(p => p.Name.EndsWith("/x")).Select(p => p.ToString())));
        var emptyWindow = Exact(engine, source, "{thing(id:\"thing1\"){AllComps(offset:2){items{ref}}}}");
        var windowRef = (string)emptyWindow.Execution!["evidence"]!["nodes"]!["/thing/AllComps/items"]!["context_ref"]!;
        var window = emptyWindow.Execution!["evidence"]!["contexts"]![windowRef]!;
        Require((int?)window["window"]!["offset"] == 2 && (int?)window["window"]!["total"] == 2 &&
            (bool?)window["window"]!["selected_fields_complete"] == true, "empty citation lost window coverage");
        Require((int?)window["Thing.AllComps"]!["first"] == 20, "result descriptor dropped defaulted arguments");

        var ctx = new ReadContext(new Data(), Session, 1000);
        var sharedHolder = new object();
        Require(ctx.NativeReference(sharedHolder) == ctx.NativeReference(sharedHolder), "one holder acquired different refs");
        Require(ctx.NativeReference(sharedHolder) != ctx.NativeReference(new object()), "distinct holder instances collapsed");
        var laterContext = new ReadContext(new Data(), new SessionDescriptor(Session.ColonyId, Session.LoadEpoch, 101, 8), 1000);
        Require(ctx.NativeReference(sharedHolder) != laterContext.NativeReference(sharedHolder), "holder ref escaped observation scope");
        var args = new Dictionary<string, object?> { ["mapId"] = "map1", ["first"] = 2 };
        var nodes = new[] { Data.Thing("c"), Data.Thing("a"), Data.Thing("b") };
        var page = ThingPaging.Page(nodes, args, ctx);
        Require(string.Join(",", page.Nodes.Select(x => x.Key)) == "a,b" && page.HasNextPage, "keyset selection is not stable");
        args["after"] = page.EndCursor;
        var next = ThingPaging.Page(new[] { Data.Thing("d"), Data.Thing("b"), Data.Thing("c") }, args, ctx);
        Require(string.Join(",", next.Nodes.Select(x => x.Key)) == "c,d", "changed collection broke keyset continuation");
        args["mapId"] = "other";
        var rejected = false;
        try { ThingPaging.Page(nodes, args, ctx); } catch (GraphQL.ExecutionError error) { rejected = error.Code == "stale_cursor"; }
        Require(rejected, "cursor crossed map/filter boundary");
        Console.WriteLine("Thing query tests passed: native members, template parity, component polymorphism, unknown types, shared identities, selection budgets and changing collection pagination.");
    }

    private sealed class Data : IGameData, IThingData
    {
        public readonly List<string> Reads = new();
        public bool FailRot, MixedComponents;
        public static GameNode Thing(string id) => new("Thing", id, new GameEntity(id, "map1", id), id);
        private static GameNode Child(GameNode parent, string kind, string key) => new(kind, key, parent.Owner, key);
        public IEnumerable<GameEntity> Maps() { Reads.Add("Maps"); yield return new GameEntity("map1", null, "map1"); }
        public IEnumerable<GameEntity> Colonists() { Reads.Add("Colonists"); yield return new GameEntity("pawn1", "map1", "pawn1"); }
        public object? GameField(string field) { Reads.Add("game:" + field); return false; }
        public object? MapField(GameEntity map, string field) => null;
        public object? ColonistField(GameEntity pawn, string field) => "Pawn";
        public object? NodeField(GameNode node, string field, IReadOnlyDictionary<string, object?> args) => throw new InvalidOperationException();
        public object? NativeRoot(string root, IReadOnlyDictionary<string, object?> args, ReadContext context)
        {
            Reads.Add("root:" + root);
            return root == "thing" ? Thing((string)args["id"]!) : ThingPaging.Page(new[] { Thing("thing1"), Thing("thing2"), Thing("thing3") }, args, context);
        }
        public object? NativeField(GameNode n, string f, IReadOnlyDictionary<string, object?> args, ReadContext context)
        {
            Reads.Add(n.Key + ":" + f);
            if (n.Kind == "Pawn") return Child(n,"Pawn_InventoryTracker","inventory");
            if (n.Kind == "Pawn_InventoryTracker") return Child(n,"ThingOwner","owner");
            if (n.Kind == "ThingOwner") return new[] { Thing("thing1") };
            if (n.Kind == "Thing") return f switch {
                "id" => n.Key, "runtimeType" => "Verse.ThingWithComps", "stackCount" => 7,
                "def" => Child(n,"ThingDef","def"), "HitPoints" or "MaxHitPoints" => 50,
                "AllComps" => MixedComponents ? new[] { Child(n,"CompQuality",n.Key+"/q"), Child(n,"CompRottable",n.Key+"/r") } : new[] { Child(n,"CompRottable",n.Key+"/comp0"), Child(n,"UnsupportedThingComp",n.Key+"/comp1"), Child(n,"CompRottable",n.Key+"/comp2") }, _ => null };
            if (n.Kind == "ThingDef") return f switch { "defName" => "Steel", "category" => "Item", "thingClass" => "Verse.ThingWithComps", _ => "Steel" };
            if (n.Kind == "CompQuality") return f == "Quality" ? "Good" : n.Key;
            if (n.Kind is "CompRottable" or "UnsupportedThingComp")
            {
                if (f == "Stage" && FailRot) throw new InvalidOperationException("mod getter failure");
                return f switch { "ref" => n.Key, "runtimeType" => n.Kind == "CompRottable" ? "CustomMod.Rot" : "CustomMod.Unknown", "supported" => n.Kind == "CompRottable", "supportedType" => n.Kind == "CompRottable" ? "RimWorld.CompRottable" : null, "RotProgress" => 3.0, "TicksUntilRotAtCurrentTemp" => 1234, "Stage" => "Fresh", _ => throw new InvalidOperationException("unexpected component field") };
            }
            return null;
        }
    }
}
