using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class PublicQueryTests
{
    public static void Run()
    {
        var engine = new GameQueryEngine(1000);
        var data = new Data();
        var session = new SessionDescriptor(new string('2',32), new string('3',32),100,7);
        QueryOutcome Query(string json) => engine.Execute("query",JObject.Parse(json),data,session);
        var result = Query("{'requests':[{'from':'maps','select':['id','name','is_player_home'],'where':{'is_player_home':{'eq':true}}}]}");
        if (result.Status != "ok" || (string?)result.Data?["results"]?[0]?["rows"]?[0]?["id"] != "map1" ||
            (int?)result.Data?["results"]?[0]?["matched_count"] != 1)
            throw new InvalidOperationException("Unified field selection/filter failed: " + result.Message + result.Data);
        result = Query("{'requests':[{'from':'things','where':{'definition.def_name':{'eq':'Steel'}},'select':['id','definition.def_name','stack_count'],'limit':1,'aggregate':{'units':{'op':'sum','field':'stack_count'},'records':{'op':'count'}}}]}");
        Check(result.Status == "ok" && (int?)result.Data?["results"]?[0]?["aggregates"]?["units"]?["value"] == 200 &&
            (int?)result.Data?["results"]?[0]?["aggregates"]?["records"]?["value"] == 2 &&
            (bool?)result.Data?["results"]?[0]?["page"]?["has_more"] == true,"Deduplication/full-selection aggregates were affected by output limit",result);
        result = Query("{'requests':[{'from':'things','group_by':['definition.def_name'],'aggregate':{'units':{'op':'sum','field':'stack_count'}}}]}");
        Check((int?)result.Data?["results"]?[0]?["rows"]?[0]?["aggregates"]?["units"]?["value"] == 200,"Grouping did not use the same selection",result);
        foreach (var json in new[] {
            "{'requests':[{'from':'maps','select':['id']},{'from':'things','select':['StackCount']}]}",
            "{'requests':[{'from':'things','aggregate':{'hp':{'op':'sum','field':'hit_points'}}}]}",
            "{'requests':[{'from':'things','where':{'definition.is_weapon':{'eq':'yes'}}}]}",
            "{'requests':[{'from':'things','order_by':[{'field':'definition'}]}]}",
        }) {
            data.Reads = 0; result = Query(json); Check(result.Status == "failed" && data.Reads == 0,"Invalid batch performed reads",result);
        }
        data.Fail = true;
        result = Query("{'requests':[{'from':'things','where':{'definition.is_weapon':{'eq':true}},'aggregate':{'records':{'op':'count'}}}]}");
        Check(result.Status == "partial" && result.Data?["results"]?[0]?["aggregates"]?["records"]?["value"]?.Type == JTokenType.Null &&
            (bool?)result.Data?["results"]?[0]?["scan_complete"] == false,"Unknown membership became exact zero",result);
        data.Fail = false;
        data.FailQuantity = true;
        result = Query("{'requests':[{'from':'things','select':['id'],'aggregate':{'units':{'op':'sum','field':'stack_count'}}}]}");
        Check(result.Status == "partial" && (bool?)result.Data?["results"]?[0]?["scan_complete"] == true &&
            (bool?)result.Data?["results"]?[0]?["fields_complete"] == false &&
            result.Data?["results"]?[0]?["aggregates"]?["units"]?["value"]?.Type == JTokenType.Null,"Unreadable aggregate falsely complete",result);
        data.FailQuantity = false;
        result = Query("{'requests':[{'from':'things','where':{'all_comps':{'some':{'on':{'CompQuality':{'quality':{'eq':'Good'}}}}}},'select':{'id':true,'all_comps':{'select':['supported'],'on':{'CompQuality':{'select':['quality']}}}}}]}");
        Check(result.Status == "ok" && (int?)result.Data?["results"]?[0]?["matched_count"] == 2 &&
            (string?)result.Data?["results"]?[0]?["rows"]?[0]?["all_comps"]?["rows"]?[0]?["quality"] == "Good","Polymorphic conditions/projection failed",result);
        result = Query("{'requests':[{'from':'things','group_by':['id'],'aggregate':{'units':{'op':'sum','field':'stack_count'}},'order_by':[{'field':'units','direction':'desc'}],'limit':1}]}");
        Check(result.Status == "ok" && (string?)result.Data?["results"]?[0]?["rows"]?[0]?["key"]?["id"] == "b","Grouped aggregate ordering ignored",result);
        foreach (var json in new[] {
            "{'requests':[{'from':'things','select':{'definition':{'where':{'is_weapon':{'eq':true}}}}}]}",
            "{'requests':[{'from':'things','select':{'all_comps':{'on':{'CompQuality':{'where':{'quality':{'eq':'Good'}}}}}}}]}",
            "{'requests':[{'from':'things','where':{'definition':{'where':{'is_weapon':{'eq':true}},'fields':{'IsWeapon':{'eq':false}}}}}]}",
            "{'requests':[{'from':'maps','group_by':['id','id'],'aggregate':{'rows':{'op':'count'}}}]}"
        }) { data.Reads = 0; result = Query(json); Check(result.Status == "failed" && data.Reads == 0,"Inapplicable nested options silently ignored",result); }
        data.Fail = true;
        result = Query("{'requests':[{'from':'things','where':{'definition.is_weapon':{'eq':true},'spawned':{'eq':true}},'aggregate':{'rows':{'op':'count'}}}]}");
        Check(result.Status == "ok" && (int?)result.Data?["results"]?[0]?["matched_count"] == 0,"Definition pushdown changed unknown AND false",result);
        data.Fail = false;
        result = Query("{'requests':[{'from':'things','args':{'map_id':'current'},'select':['id']}]}" );
        Check(result.Status == "ok" && data.LastMap == "map1" && (string?)result.Data?["results"]?[0]?["scope"]?["args"]?["map_id"] == "map1","Current map selector failed to bind effective scope",result);
        result = Query("{'requests':[{'from':'things','where':{'definition':{'where':{'def_name':{'eq':'Steel'}}}},'select':['id']}]}" );
        Check(result.Status == "ok" && (int?)result.Data?["results"]?[0]?["matched_count"] == 2,"Uniform where wrapper failed",result);
        result = Query("{'requests':[{'from':'things','select':{'definition':['def_name','label']}}]}" );
        Check(result.Status == "ok" && (string?)result.Data?["results"]?[0]?["rows"]?[0]?["definition"]?["def_name"] == "Steel","Nested path-array projection failed",result);
        var expectedSelection = result.Data!["results"]![0]!["rows"]!.DeepClone();
        foreach (var selection in new[] {
            "['definition.def_name','definition.label']",
            "{'definition':{'select':['def_name','label']}}"
        }) {
            result = Query("{'from':'things','select':" + selection + "}");
            Check(result.Status == "ok" && JToken.DeepEquals(expectedSelection,result.Data?["results"]?[0]?["rows"]),"Documented selection forms disagree",result);
        }
        foreach (var selection in new[] {
            "{'definition':{'def_name':true,'label':true}}",
            "['id',{'definition':['def_name']}]"
        }) {
            data.Reads = 0; result = Query("{'from':'things','select':" + selection + "}");
            Check(result.Status == "failed" && data.Reads == 0,"Input guidance silently widened the query grammar",result);
        }
        var directory = engine.Execute("query_catalog",new JObject(),data,session);
        foreach (var example in (JArray)directory.Data!["public_query"]!["examples"]!) {
            result = engine.Execute("query",(JObject)example,data,session);
            Check(result.Status == "ok","Advertised query example cannot execute",result);
        }
        foreach (var root in new[] { "things","pawns","spawned_free_colonists" }) {
            result = Query("{'requests':[{'from':'" + root + "','args':{'map_id':'missing'},'aggregate':{'rows':{'op':'count'}}}]}");
            Check(result.Status == "partial" && result.Data?["results"]?[0]?["aggregates"]?["rows"]?["value"]?.Type == JTokenType.Null,"Missing map reported as an empty population",result);
        }
        data.FailEnumeration = true;
        result = Query("{'requests':[{'from':'things','group_by':['definition.def_name'],'select':['stack_count'],'aggregate':{'rows':{'op':'count'}}}]}" );
        Check(result.Status == "partial" && result.Data?["results"]?[0]?["rows"]?[0]?["values"]?.Type == JTokenType.Null && (bool?)result.Data?["results"]?[0]?["fields_complete"] == false,"Incomplete group incorrectly claimed a shared value",result);
        Console.WriteLine("Public query tests passed: canonical selection, pre-read rejection, complete aggregates, deduplication, grouping and unknown membership.");
    }
    private static void Check(bool pass,string message,QueryOutcome result) { if (!pass) throw new InvalidOperationException(message + ": " + result.Message + result.Data); }
    private sealed class Data : IGameData, IQueryData
    {
        public int Reads;
        public bool Fail;
        public bool FailQuantity;
        public bool FailEnumeration;
        public string? LastMap;
        private static GameNode Thing(string id) => new("Thing",id,new GameEntity(id,"map1",id),id);
        public IEnumerable<GameNode> QueryThings(IReadOnlyDictionary<string,object?> args,ReadContext c,Func<GameNode,bool>? definitionFilter = null) {
            LastMap = args.TryGetValue("mapId",out var map) ? map as string : null;
            foreach (var n in new[] { Thing("a"),Thing("b"),Thing("a") })
                if (definitionFilter == null || definitionFilter((GameNode)c.ReadNode(n,"def")!)) { yield return n; if (FailEnumeration) throw ReadContext.Error("query_scan_limit","Incomplete fixture collection"); }
        }
        public IEnumerable<GameEntity> QueryPawns(IReadOnlyDictionary<string,object?> args,ReadContext c) => Colonists();
        public IEnumerable<GameEntity> Maps() => new[] { new GameEntity("map1",null,new object()), new GameEntity("map2",null,new object()) };
        public IEnumerable<GameEntity> Colonists() => Array.Empty<GameEntity>();
        public object? GameField(string field) => field == "currentMapId" ? "map1" : null;
        public object? MapField(GameEntity e,string field) { Reads++; return field == "isPlayerHome" ? e.Id == "map1" : "Map " + e.Id; }
        public object? ColonistField(GameEntity e,string field) => null;
        public object? NodeField(GameNode n,string field,IReadOnlyDictionary<string,object?> args)
        {
            Reads++;
            if (field == "stackCount" && FailQuantity) throw ReadContext.Error("field_unavailable","Unavailable quantity");
            if (n.Kind == "Thing") return field switch { "id" => n.Key,"Spawned" => false,"stackCount" => n.Key == "a" ? 75 : 125,"def" => new GameNode("ThingDef","Steel",new GameEntity("def:Steel",null,"Steel"),"Steel"),"AllComps" => new[] { new GameNode("CompQuality","quality",n.Owner,"quality") },_ => null };
            if (n.Kind == "CompQuality") return field == "Quality" ? "Good" : field == "supported" ? true : n.Key;
            if (field == "IsWeapon" && Fail) throw ReadContext.Error("field_unavailable","Unavailable definition member");
            return field switch { "defName" => "Steel","label" => "steel","IsWeapon" => false,_ => null };
        }
    }
}
