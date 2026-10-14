using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class ThingQueryTests
{
    private static readonly SessionDescriptor Session = new(new string('2', 32), new string('3', 32), 100, 7);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static QueryOutcome Query(GameQueryEngine engine,Data data,string query) => engine.Execute("query",JObject.Parse(query),data,Session);
    public static void Run()
    {
        var engine=new GameQueryEngine(1000);var source=new Data();
        var described=engine.Execute("describe_query",JObject.Parse("{types:['Thing','ThingComp','CompRottable']}"),source,Session);
        Require(described.Status=="ok" && source.Reads.Count==0,"Description read game state");
        var result=Query(engine,source,"{from:'thing',args:{id:'thing1'},select:{id:true,all_comps:{select:['ref','runtime_type','supported','supported_type'],on:{CompRottable:['stage']}}}}");
        Require(result.Status=="ok","Native component projection failed: "+result.Message);
        var comps=result.Data!["results"]![0]!["rows"]![0]!["all_comps"]!["rows"]!;
        Require((string?)comps[0]?["runtime_type"]=="CustomMod.Rot" && (string?)comps[0]?["stage"]=="Fresh","Base projection erased derived runtime class");
        Require((bool?)comps[1]?["supported"]==false && comps[1]?["supported_type"]?.Type==JTokenType.Null && comps[1]?["stage"]==null,"Unknown component lost unsupported metadata");
        Require((string?)comps[0]?["ref"]!=(string?)comps[2]?["ref"],"Same-class components collapsed");
        source=new Data();
        result=Query(engine,source,"{requests:[{from:'thing',args:{id:'thing1'},select:['id','stack_count']},{from:'pawn',args:{id:'pawn1'},select:['inventory.id','inventory.stack_count']}]}");
        Require(result.Status=="ok" && (string?)result.Data?["results"]?[0]?["rows"]?[0]?["id"]==(string?)result.Data?["results"]?[1]?["rows"]?[0]?["inventory"]?["rows"]?[0]?["id"],"Identity changed across holder routes");
        Require(source.Reads.Count(x=>x=="thing1:stackCount")==1,"Same native member read twice through holder routes");
        Require(!source.Reads.Any(x=>x.EndsWith(":AllComps") || x.EndsWith(":def")),"Unselected native fields read");
        source=new Data {FailRot=true};
        result=Query(engine,source,"{from:'thing',args:{id:'thing1'},select:{all_comps:{on:{CompRottable:['stage']},limit:1}}}");
        Require(result.Status=="partial" && (bool?)result.Data?["results"]?[0]?["rows"]?[0]?["all_comps"]?["fields_complete"]==false,"Failed component falsely complete");
        foreach(var bad in new[]{"{from:'thing',args:{id:'thing1',after:'broken'},select:['id']}","{from:'thing',args:{id:'thing1'},limit:101,select:['id']}","{from:'thing',args:{id:'thing1'},select:{all_comps:{on:{Invented:['stage']}}}}"}) {
            source=new Data();result=Query(engine,source,bad);
            Require(result.Status=="failed" && source.Reads.Count==0,"Invalid native selection read state");
        }
        source=new Data {MixedComponents=true};
        result=Query(engine,source,"{from:'thing',args:{id:'thing1'},select:{all_comps:{on:{CompQuality:['quality'],CompRottable:['stage']}}}}");
        Require(result.Status=="ok" && (string?)result.Data?["results"]?[0]?["rows"]?[0]?["all_comps"]?["rows"]?[0]?["quality"]=="Good" && (string?)result.Data?["results"]?[0]?["rows"]?[0]?["all_comps"]?["rows"]?[1]?["stage"]=="Fresh","Polymorphic fields changed");
        var ctx=new ReadContext(new Data(),Session,1000);var holder=new object();
        Require(ctx.NativeReference(holder)==ctx.NativeReference(holder),"Same observation holder changed identity");
        var later=new ReadContext(new Data(),new SessionDescriptor(Session.ColonyId,Session.LoadEpoch,101,8),1000);
        Require(ctx.NativeReference(holder)!=later.NativeReference(holder),"Holder identity escaped observation scope");
        var identityQuery=JObject.Parse("{from:'thing',args:{id:'missing'},select:['id']}");
        var missing=new Data {LookupFailure="object_not_found"};
        var absent=engine.Execute("query",identityQuery,missing,Session);
        Require(absent.Status=="ok" && (int?)absent.Data?["results"]?[0]?["matched_count"]==0 && (bool?)absent.Data?["results"]?[0]?["scan_complete"]==true,"Missing identity must be complete empty");
        missing.LookupFailure="deadline_exceeded";
        var unknown=engine.Execute("query",identityQuery,missing,Session);
        Require(unknown.Status=="partial" && unknown.Data?["results"]?[0]?["matched_count"]?.Type==JTokenType.Null,"Interrupted identity became empty");
        Console.WriteLine("Thing query tests passed: public native/component getters, unsupported metadata, shared holder identities, scope/cache, unknown lookup and pre-read rejection.");
    }

    private sealed class Data : IGameData, IThingData
    {
        public readonly List<string> Reads = new();
        public bool FailRot, MixedComponents;
        public string? LookupFailure;
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
            if (LookupFailure != null) throw ReadContext.Error(LookupFailure,"lookup failure");
            if(root=="thing")return Thing((string)args["id"]!);
            throw ReadContext.Error("unknown_target","Unsupported test root");
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
