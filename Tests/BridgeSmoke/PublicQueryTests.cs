using System;
using System.Collections.Generic;
using System.Linq;
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
        // v1.6.1: equivalent forms exercise the public compiler, not a second parser.
        var canonical = Query("{from:'things',where:{'definition.category':{eq:'Item'}},select:['id'],aggregate:{n:{op:'count'}}}");
        var nestedInput = JObject.Parse("{from:'things',where:{definition:{category:{eq:'Item'}}},select:['id'],aggregate:{n:{op:'count'}}}");
        var originalNested = nestedInput.DeepClone();
        var nestedResult = engine.Execute("query",nestedInput,data,session);
        Check(nestedResult.Status=="ok" && JToken.DeepEquals(nestedInput,originalNested) && JToken.DeepEquals(canonical.Data?["results"]?[0]?["rows"],nestedResult.Data?["results"]?[0]?["rows"]),"Bare nested condition changed selected objects or input",nestedResult);
        foreach(var body in new[]{"['quality']","{quality:true}","{select:['quality']}"}) {
            result=Query("{from:'things',select:{all_comps:{on:{CompQuality:"+body+"}}}}");
            Check(result.Status=="ok" && (string?)result.Data?["results"]?[0]?["rows"]?[0]?["all_comps"]?["rows"]?[0]?["quality"]=="Good","Concrete selection forms differ",result);
        }
        data.Reads=0;
        result=Query("{from:'things',select:{all_comps:{on:'CompQuality',select:['quality']}}}");
        Check(result.Status=="failed" && data.Reads==0 && result.Message!.Contains("on must be an object") && !result.Message.Contains("Unknown field"),"Dependent field error hid invalid on structure",result);
        data.Reads=0;
        result=Query("{requests:[{from:'things',select:['no_such_field']},{from:'maps',select:['id']},{from:'things',select:{all_comps:{on:{NoSuchComponent:['quality']}}}}]}");
        Check(result.Status=="failed" && data.Reads==0 && result.Data==null && result.Issues.Length==2 && (string?)result.Issues[0].Path[1]=="0" && (string?)result.Issues[1].Path[1]=="2" && (int?)result.Execution?["validation"]?["valid_request_indices"]?[0]==1,"Batch lost independent errors or performed reads",result);
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
        result = Query("{'from':'things','select':{'definition':{'def_name':true,'label':true}}}");
        Check(result.Status == "ok" && JToken.DeepEquals(expectedSelection,result.Data?["results"]?[0]?["rows"]),"Bare fields changed the projection",result);
        var mixed = JObject.Parse("{'from':'things','select':['id',{'definition':['def_name','label']}]}");
        var unchanged = mixed.DeepClone();
        result = engine.Execute("query",mixed,data,session);
        var explicitRead = Query("{'from':'things','select':{'id':true,'definition':{'select':['def_name','label']}}}");
        Check(result.Status == "ok" && JToken.DeepEquals(result.Data,explicitRead.Data) && JToken.DeepEquals(mixed,unchanged),"Mixed selection changed facts or mutated caller input",result);
        foreach (var item in new[] {
            ("{'from':'things','select':['id',{'id':true}]}", "id"),
            ("{'from':'things','select':['definition.label',{'definition':['def_name']}]}", "definition"),
            ("{'from':'things','select':[{'definition':['def_name']},'definition.label']}", "1"),
            ("{'from':'spawned_free_colonists','select':{'health':{'summary':true,'args':{}}}}", "summary"),
            ("{'from':'spawned_free_colonists','select':{'health':{'args':{'visible_only':true},'select':['summary']}}}", "visible_only"),
            ("{'from':'things','select':{'bad/name':true}}", "bad/name"),
            ("{'from':'things','select':{'definition':{'select_definition':['def_name']}}}", "select_definition")
        }) {
            data.Reads=0; result=Query(item.Item1);
            Check(result.Status=="failed" && data.Reads==0 && result.Issues.Length==1 && (string?)result.Issues[0].Path.Last==item.Item2,"Ambiguous input was accepted or lost its original error position",result);
        }
        // Twelve related-object edges are legal; a thirteenth is rejected before reads.
        foreach (var edges in new[] { 12, 13 }) {
            JToken selection=new JObject { ["stack_count"]=true };
            for (var edge=edges-1;edge>=0;edge--) selection=new JObject { [edge%2==0 ? "holding_owner" : "items"]=new JObject { ["select"]=selection } };
            data.Reads=0; result=engine.Execute("query",new JObject { ["from"]="things",["select"]=selection },data,session);
            Check(edges==12 ? result.Status=="ok" : result.Status=="failed" && data.Reads==0,"Selection depth boundary changed",result);
        }
        var directory = engine.Execute("query_catalog",new JObject(),data,session);
        foreach (var typeNote in ((JObject)directory.Data!["public_query"]!["field_notes"]!).Properties()) {
            var described=engine.Execute("describe_query",new JObject { ["types"]=new JArray(typeNote.Name) },data,session);
            foreach (var note in ((JObject)typeNote.Value).Properties()) {
                var field=((JArray)described.Data!["types"]![0]!["fields"]!).OfType<JObject>().Single(f=>(string?)f["name"]==note.Name);
                if (!JToken.DeepEquals(note.Value,field["description"])) throw new InvalidOperationException("Injected field meaning differs from discovery metadata");
            }
        }
        foreach (var example in (JArray)directory.Data!["public_query"]!["examples"]!) {
            result = engine.Execute("query",(JObject)example,data,session);
            Check(result.Status == "ok","Advertised query example cannot execute",result);
        }
        var index=(JObject)directory.Data!["public_query"]!["field_index"]!;
        (JObject Fields,int Cost) Broad(string type,int depth) {
            var fields=new JObject(); var cost=1;
            foreach (var p in ((JObject)index[type]!).Properties()) {
                var descriptor=(string)p.Value!;
                if (descriptor.Contains("!")) continue;
                var child=descriptor.Split(' ')[0].Trim('[',']');
                if (index[child]!=null) {
                    if (depth==0) continue;
                    var nested=Broad(child,depth-1);fields[p.Name]=new JObject { ["select"]=nested.Fields };cost+=1+nested.Cost;
                } else { fields[p.Name]=true;cost++; }
            }
            return (fields,cost);
        }
        var broad=Broad("Thing",1);var fitting=512/broad.Cost;
        if (fitting<1 || fitting>=8) throw new InvalidOperationException("Invalid field-boundary fixture size");
        foreach (var count in new[] { fitting,fitting+1 }) {
            var requests=new JArray(Enumerable.Range(0,count).Select(_=>new JObject { ["from"]="things",["select"]=broad.Fields.DeepClone() }));
            data.Reads=0;result=engine.Execute("query",new JObject { ["requests"]=requests },data,session);
            Check(count==fitting ? result.Status=="ok" : result.Status=="failed" && data.Reads==0,"Whole-batch 512-field budget changed",result);
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
