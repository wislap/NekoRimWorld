using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class PublicQueryTests
{
    public static void Run()
    {
        RunCooperative();
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
        result=Query("{from:'things',select:{all_comps:{on:{},select:['supported']}}}");
        Check(result.Status=="ok","Empty on compatibility regressed",result);
        // v1.6.1: equivalent forms exercise the public compiler, not a second parser.
        var canonical = Query("{from:'things',where:{'definition.def_name':{eq:'Steel'}},select:['id'],aggregate:{n:{op:'count'}}}");
        var nestedInput = JObject.Parse("{from:'things',where:{definition:{def_name:{eq:'Steel'}}},select:['id'],aggregate:{n:{op:'count'}}}");
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
        var eight=new JArray(Enumerable.Range(0,8).Select(i=>new JObject { ["from"]="things",["select"]=new JArray("bad_"+i) }));
        data.Reads=0;result=engine.Execute("query",new JObject { ["requests"]=eight },data,session);
        Check(result.Status=="failed" && result.Issues.Length==8 && data.Reads==0,"Batch diagnostics lost bounded independent roots",result);
        void CheckWire(QueryOutcome outcome) => ProtocolCodec.Serialize(ProtocolEnvelope.CreateQueryResult(
            new ProtocolEnvelope { RequestId=new string('4',32) },new string('1',32),1,session,
            outcome.Status,outcome.Data,outcome.Issues,outcome.ErrorCode,outcome.Message,outcome.Execution));
        CheckWire(result);
        foreach (var name in new[]{new string('x',300),""}) {
            data.Reads=0;
            result=engine.Execute("query",new JObject { ["from"]="things",["select"]=new JObject { [name]=true } },data,session);
            Check(result.Status=="failed" && data.Reads==0,"Bad path caused reads",result);
            CheckWire(result);
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
        var semantics=engine.Execute("describe_query",JObject.Parse("{types:['Thing','Pawn','Status','HealthSummary']}"),data,session);
        var semanticText=semantics.Data!.ToString();
        Check(semantics.Status=="ok" && semanticText.Contains("including humans, animals, mechanoids") && semanticText.Contains("map_held") && semanticText.Contains("not current hauling") && !semanticText.Contains("Null without issue means not applicable"),"Semantic descriptions missing or conflicting",semantics);
        var catalogText=directory.Data!.ToString();
        Check(catalogText.Contains("object enumeration, not pathfinding access") && catalogText.Contains("rows=[] alone") && !catalogText.Contains("All pawns reachable"),"Catalog retains ambiguous population/completeness claims",directory);
        AggregateReturnContract(engine,session);
        ScanDiagnostics(engine,session);
        Console.WriteLine("Public query tests passed: canonical selection, pre-read rejection, complete aggregates, deduplication, grouping and unknown membership.");
    }
    private static void AggregateReturnContract(GameQueryEngine engine,SessionDescriptor session)
    {
        var data = new Data();
        QueryOutcome Read(string json) => engine.Execute("query",JObject.Parse(json),data,session);
        void NoPage(QueryOutcome r,JToken? collection) => Check(collection is JObject obj && obj.Property("rows")==null && obj.Property("page")==null,"Unrequested aggregate detail advertised a page",r);
        foreach(var root in new[]{"things","maps","pawns"}) {
            var r=Read("{from:'"+root+"',limit:0,aggregate:{n:{op:'count'}}}");
            var c=r.Data?["results"]?[0];NoPage(r,c);
            Check(r.Status=="ok" && (bool?)c?["aggregates"]?["n"]?["complete"]==true && (int?)c?["aggregates"]?["n"]?["value"]==(root=="pawns"?0:2),"Aggregate-only count/scope changed",r);
        }
        foreach(var options in new[]{"","group_by:['definition.def_name'],","offset:1,order_by:[{field:'id'}],"}) {
            var r=Read("{from:'things',"+options+"limit:0,aggregate:{n:{op:'count'},units:{op:'sum',field:'stack_count'}}}");
            NoPage(r,r.Data?["results"]?[0]);
            Check(r.Status=="ok" && (int?)r.Data?["results"]?[0]?["aggregates"]?["units"]?["value"]==200,"Aggregate-only output changed measurement",r);
        }
        var nested=Read("{from:'things',limit:1,select:{all_comps:{limit:0,aggregate:{n:{op:'count'}}}}}");
        NoPage(nested,nested.Data?["results"]?[0]?["rows"]?[0]?["all_comps"]);
        Check((int?)nested.Data?["results"]?[0]?["rows"]?[0]?["all_comps"]?["aggregates"]?["n"]?["value"]==1,"Nested aggregate lost its measure",nested);
        var shared=Read("{from:'things',group_by:['definition.def_name'],aggregate:{n:{op:'count'}},select:{all_comps:{limit:0,aggregate:{n:{op:'count'}}}}}");
        Check(shared.Status=="ok" && (int?)shared.Data?["results"]?[0]?["rows"]?[0]?["values"]?["all_comps"]?["aggregates"]?["n"]?["value"]==1,"Complete nested aggregate mistaken for an undisplayed partial list",shared);
        data.VaryComps=true;
        var varies=Read("{from:'things',group_by:['definition.def_name'],aggregate:{n:{op:'count'}},select:{all_comps:{limit:0,aggregate:{n:{op:'count'}}}}}");
        Check(varies.Status=="partial" && varies.Data?["results"]?[0]?["rows"]?[0]?["values"]?["all_comps"]?.Type==JTokenType.Null,"Different nested aggregates falsely shared",varies);
        data.VaryComps=false;
        data.LargeContents=true;
        var unknown=Read("{from:'things',group_by:['definition.def_name'],aggregate:{n:{op:'count'}},select:{contents:{limit:0,select:[],aggregate:{n:{op:'count'}}}}}");
        Check(unknown.Status=="partial" && unknown.Data?["results"]?[0]?["rows"]?[0]?["values"]?["contents"]?.Type==JTokenType.Null,"Incomplete nested aggregate became a shared known value",unknown);
        data.LargeContents=false;
        foreach(var suffix in new[]{"",",group_by:['id']"}) {
            var first=Read("{from:'things',limit:1,aggregate:{n:{op:'count'}}"+suffix+"}");
            Check((bool?)first.Data?["results"]?[0]?["page"]?["has_more"]==true && (int?)first.Data?["results"]?[0]?["page"]?["next_offset"]==1,"Requested detail pagination lost continuation",first);
            var last=Read("{from:'things',limit:1,offset:1,aggregate:{n:{op:'count'}}"+suffix+"}");
            Check((bool?)last.Data?["results"]?[0]?["page"]?["has_more"]==false && (int?)last.Data?["results"]?[0]?["aggregates"]?["n"]?["value"]==2,"Page offset changed full aggregate",last);
        }
        var bare=Read("{from:'things',limit:0}");
        Check(bare.Data?["results"]?[0]?["rows"] is JArray && bare.Data?["results"]?[0]?["page"] is JObject,"Nonaggregate contract changed",bare);
        data.LargeThings=8193;
        var partial=Read("{from:'things',limit:0,aggregate:{n:{op:'count'}}}");NoPage(partial,partial.Data?["results"]?[0]);
        Check(partial.Status=="partial" && (bool?)partial.Data?["results"]?[0]?["scan_complete"]==false && partial.Data?["results"]?[0]?["aggregates"]?["n"]?["value"]?.Type==JTokenType.Null && (int?)partial.Data?["results"]?[0]?["aggregates"]?["n"]?["known_value"]==8192,"Partial scan became exact without page",partial);
        data.LargeThings=0;data.FailQuantity=true;
        var failed=Read("{from:'things',limit:0,aggregate:{units:{op:'sum',field:'stack_count'}}}");NoPage(failed,failed.Data?["results"]?[0]);
        Check(failed.Status=="partial" && (bool?)failed.Data?["results"]?[0]?["fields_complete"]==false && (bool?)failed.Data?["results"]?[0]?["aggregates"]?["units"]?["complete"]==false,"Unreadable aggregate became complete without page",failed);
    }
    private static void ScanDiagnostics(GameQueryEngine engine,SessionDescriptor session)
    {
        var data = new Data { LargeThings=8193 };
        QueryOutcome Read(string json) => engine.Execute("query",JObject.Parse(json),data,session);
        string Message(QueryOutcome r) {
            var message=r.Issues.Single(i=>i.Code=="query_scan_limit").Message!;
            Check(message.Length<=512,"Scan diagnostic exceeds wire limit",r);
            return message;
        }
        var singleton="{from:'things',args:{map_id:'current',def_names:['Steel']},select:[],limit:0,aggregate:{n:{op:'count'}}}";
        var result=Read(singleton);
        Check(Message(result).Contains("one resolved loaded map and one exact definition"),"Root singleton scan lacks contextual recovery diagnostic",result);
        Check((bool?)result.Data?["results"]?[0]?["scan_complete"]==false && result.Data?["results"]?[0]?["aggregates"]?["n"]?["value"]?.Type==JTokenType.Null && (int?)result.Data?["results"]?[0]?["known_matches"]==8192 && (int?)result.Data?["results"]?[0]?["aggregates"]?["n"]?["known_value"]==8192,"Scan diagnostic changed counts/completeness",result);
        ProtocolCodec.Serialize(ProtocolEnvelope.CreateQueryResult(new ProtocolEnvelope {RequestId=new string('4',32)},new string('1',32),1,session,result.Status,result.Data,result.Issues,result.ErrorCode,result.Message,result.Execution));
        result=Read(singleton.Replace("['Steel']","['Steel','Steel']"));
        Check(Message(result).Contains("one exact definition"),"Repeated names were mistaken for splittable definitions",result);
        foreach(var json in new[]{singleton.Replace("map_id:'current',",""),singleton.Replace("['Steel']","['Steel','WoodLog']"),singleton.Replace("args:{map_id:'current',def_names:['Steel']}","args:{map_id:'current'},where:{'definition.def_name':{eq:'Steel'}}")}) {
            result=Read(json);
            Check(!Message(result).Contains("already restricted") && Message(result).Contains("intended population"),"Broad/implicit source falsely diagnosed as an explicit singleton",result);
        }
        result=Read(singleton.Replace("limit:0","limit:1,offset:8192,order_by:[{field:'id',direction:'asc'}],group_by:['id'],where:{label:{eq:'absent'}}"));
        Check((int?)result.Data?["results"]?[0]?["known_matches"]==0 && (int?)result.Data?["results"]?[0]?["aggregates"]?["n"]?["known_value"]==0 && result.Data?["results"]?[0]?["aggregates"]?["n"]?["value"]?.Type==JTokenType.Null && Message(result).Contains("ordinary instance filters"),"Candidate cap became a matching-count lower bound or output options bypassed scan",result);
        foreach (var clause in new[] { "id:{eq:'large8192'}", "id:{in:['large8192','large8192','absent']}", "id:{eq:'large8192',in:['large8192','absent']}" }) {
            result=Read("{from:'things',where:{"+clause+"},select:['id'],aggregate:{n:{op:'count'}}}");
            Check(result.Status=="ok" && (int?)result.Data?["results"]?[0]?["aggregates"]?["n"]?["value"]==1 && (string?)result.Data?["results"]?[0]?["rows"]?[0]?["id"]=="large8192","Exact ID did not narrow source candidates before the scan cap",result);
        }
        foreach (var clause in new[] { "id:{eq:'absent'}", "id:{eq:'large8192',in:['absent']}", "id:{eq:'large8192',ne:'large8192'}" }) {
            result=Read("{from:'things',where:{"+clause+"},select:['id'],aggregate:{n:{op:'count'}}}");
            Check(result.Status=="ok" && (int?)result.Data?["results"]?[0]?["aggregates"]?["n"]?["value"]==0,"ID pushdown lost intersection or residual predicate semantics",result);
        }
        foreach (var clause in new[] { "id:{eq:'LARGE8192',case_sensitive:false}", "any:[{id:{eq:'large8192'}},{id:{eq:'large0'}}]", "not:{id:{eq:'large8192'}}" }) {
            result=Read("{from:'things',where:{"+clause+"},select:['id'],aggregate:{n:{op:'count'}}}");
            Check(result.Status=="partial" && result.Issues.Any(i=>i.Code=="query_scan_limit"),"Unsafe ID expression was narrowed",result);
        }
        data.LargeThings=8192;
        result=Read(singleton);
        Check(result.Status=="ok" && (int?)result.Data?["results"]?[0]?["aggregates"]?["n"]?["value"]==8192,"Exact boundary is not complete",result);
        data.LargeThings=8193;
        result=Read("{requests:["+singleton+",{from:'maps',select:[],limit:0,aggregate:{n:{op:'count'}}}]}");
        Check(result.Issues.Length==1 && result.Issues[0].Path.ToString(Newtonsoft.Json.Formatting.None)=="[\"results\",\"0\"]" && (int?)result.Data?["results"]?[1]?["aggregates"]?["n"]?["value"]==2,"Scan failure leaked into complete sibling",result);
        data.LargeThings=0;data.LargeContents=true;
        result=Read("{from:'things',args:{map_id:'current',def_names:['Steel']},limit:1,select:{contents:{select:[],limit:0,aggregate:{n:{op:'count'}}}}}");
        Check(result.Status=="partial" && !Message(result).Contains("one exact definition") && !Message(result).Contains("Map/definition") && result.Issues[0].Path.Count>2,"Nested Thing collection inherited root recovery assumptions",result);
        data.NativeContents=true;
        result=Read("{from:'things',args:{map_id:'current',def_names:['Steel']},limit:1,select:{contents:{select:[],limit:0}}}");
        Check(result.Issues.Any(i=>i.Code=="node_scan_limit" && i.Message=="Nested collection exceeds 8192 records."),"Native nested guard was rewritten",result);
        data.LargeContents=false;data.LargePawns=true;
        result=Read("{from:'pawns',args:{map_id:'current'},select:[],limit:0,aggregate:{n:{op:'count'}}}");
        Check(!Message(result).Contains("Map/definition") && !Message(result).Contains("one exact definition"),"Non-things source advertised unsupported map/definition splitting",result);
        data.LargePawns=false;data.FailEnumeration=true;
        foreach(var code in new[]{"holder_scan_limit","deadline_exceeded","query_scan_limit"}) {
            data.EnumerationError=code;result=Read(singleton);
            string expectedMessage = "Incomplete fixture collection" + (code == "deadline_exceeded" ? " Read stage: source_iteration." : "");
            Check(result.Issues.Any(i=>i.Code==code && i.Message==expectedMessage),"Backend-specific reason/message was lost or deadline stage is incorrect",result);
        }
    }
    private static void Check(bool pass,string message,QueryOutcome result) { if (!pass) throw new InvalidOperationException(message + ": " + result.Message + result.Data); }

    public static void RunMetadata()
    {
        var engine=new GameQueryEngine(1000);
        var data=new Data();
        var session=new SessionDescriptor(new string('2',32),new string('3',32),100,7);
        var input=JObject.Parse("{requests:[{from:'things',where:{'definition.def_name':{eq:'Steel'}},select:['id','definition.def_name','stack_count'],limit:1,aggregate:{units:{op:'sum',field:'stack_count'},records:{op:'count'}}}]}");
        var result=engine.Execute("query",input,data,session);
        Check(result.Status=="ok","Current metadata fixture did not execute",result);
        var execution=result.Execution!;
        if(execution.Property("evidence")!=null || execution.Property("requests")==null || execution.Property("schema_id")==null ||
            execution.Property("contract")==null || execution.Property("phases")==null)
            throw new Exception("Raw query metadata was removed or retired descriptors retained");
        var observed=result.Data!["results"]![0]!;
        if((int?)observed["aggregates"]?["units"]?["value"]!=200 || (string?)observed["aggregates"]?["units"]?["unit"]!="stack_units" ||
            (bool?)observed["aggregates"]?["units"]?["complete"]!=true || observed["scope"]?["where"]==null ||
            (bool?)observed["page"]?["has_more"]!=true || result.Data!["sample"]==null)
            throw new Exception("Measurements, units, completeness, scope/window or raw sample were lost");
        var catalog=engine.Execute("query_catalog",new JObject(),data,session);
        if(catalog.Data?["public_query"]?["references"]!=null)
            throw new Exception("Retired public reference contract remains advertised");
        foreach(var unsupported in new[]{"production","not_a_capability"}) {
            data.Reads=0;
            var retiredCapability=engine.Execute("query",new JObject { ["from"]="things",["args"]=new JObject { ["capabilities"]=new JArray(unsupported) },["select"]=new JArray("id") },data,session);
            if(retiredCapability.Status!="failed" || data.Reads!=0)
                throw new Exception("Unsupported capability was accepted or caused native reads: "+unsupported);
        }
        foreach(var supported in WorldObservationContract.Capabilities) {
            var canonicalCapability=engine.Execute("query",new JObject { ["from"]="things",["args"]=new JObject { ["capabilities"]=new JArray(supported) },["select"]=new JArray("id") },data,session);
            if(canonicalCapability.Status!="ok" || (string?)canonicalCapability.Data?["results"]?[0]?["scope"]?["args"]?["capabilities"]?[0]!=supported)
                throw new Exception("Declared capability was removed or silently remapped: "+supported);
        }
        Console.WriteLine("Public query metadata tests passed: raw request/schema/phases, units/completeness/scope/window/sample retained; no reference descriptors.");
    }
    private static void RunCooperative()
    {
        // Detached synthetic source: validate lifecycle/content independently of OS
        // scheduling jitter. Production reads still use their unchanged 12ms budget.
        var engine=new GameQueryEngine(1000);
        var input=JObject.Parse("{'from':'things','aggregate':{'units':{'op':'sum','field':'stack_count'}},'limit':0}");
        var source=new Data { LargeThings=600,DelayReadMilliseconds=1 };
        var session=new SessionDescriptor(new string('a',32),new string('b',32),10,1);
        using(var read=engine.BeginPublicQuery(input,source,session,()=>true,()=>20)) {
            int frames=0;
            while(!read.Advance()) { frames++;System.Threading.Thread.Yield(); }
            var outcome=read.Outcome!;
            if(outcome.Status!="ok" || (long?)outcome.Data?["results"]?[0]?["aggregates"]?["units"]?["value"]!=75000 || frames<2)
                throw new Exception("Cooperative aggregate lost exact units: "+outcome.Status+"/"+outcome.ErrorCode+"/"+outcome.Message+" "+outcome.Data);
            if ((bool?)outcome.Data?["sample"]?["atomic"] != false ||
                outcome.Execution?.Property("evidence") != null ||
                (long?)outcome.Data?["sample"]?["started_tick"] != 10 ||
                (long?)outcome.Data?["sample"]?["finished_tick"] != 20)
                throw new Exception("Cooperative observation interval/sample changed or descriptors retained");
        }
        bool current=true;
        using(var read=engine.BeginPublicQuery(input,new Data { LargeThings=600,DelayReadMilliseconds=1 },session,()=>current,()=>20)) {
            read.Advance();current=false;read.Advance();
            if(read.Outcome?.ErrorCode!="world_changed")throw new Exception("Cross-world continuation was accepted");
        }
        var bounded=new GameQueryEngine(0);
        var unknownSource=new Data { LargeThings=600 };
        using(var read=bounded.BeginPublicQuery(input,unknownSource,session,()=>true,()=>20)) {
            while(!read.Advance()) { }
            var outcome=read.Outcome!;
            if(outcome.Status!="partial" || !outcome.Issues.Any(i=>i.Code=="deadline_exceeded") ||
                outcome.Data?["results"]?[0]?["aggregates"]?["units"]?["value"]?.Type!=JTokenType.Null ||
                (bool?)outcome.Data?["results"]?[0]?["aggregates"]?["units"]?["complete"]!=false ||
                unknownSource.Reads!=0 || (long?)outcome.Data?["sample"]?["finished_tick"]!=20)
                throw new Exception("A deterministic exhausted read budget became success/zero, read facts or lost the sample");
        }
        Console.WriteLine("Cooperative query tests passed: wide-budget synthetic sum/multiple frames, deterministic exhausted-budget unknowns and world invalidation.");
    }

    private sealed class Data : IGameData, IQueryData
    {
        public int Reads;
        public int DelayReadMilliseconds;
        public int LargeThings;
        public bool LargeContents,LargePawns,NativeContents;
        public string EnumerationError="query_scan_limit";
        public bool Fail;
        public bool FailQuantity;
        public bool VaryComps;
        public bool FailEnumeration;
        public string? LastMap;
        private static GameNode Thing(string id) => new("Thing",id,new GameEntity(id,"map1",id),id);
        public IEnumerable<GameNode> QueryThings(IReadOnlyDictionary<string,object?> args,ReadContext c,Func<GameNode,bool>? definitionFilter = null, IReadOnlyCollection<string>? exactIds = null) {
            LastMap = args.TryGetValue("mapId",out var map) ? map as string : null;
            foreach (var n in LargeThings>0 ? Enumerable.Range(0,LargeThings).Select(i=>Thing("large"+i)) : new[] { Thing("a"),Thing("b"),Thing("a") })
                if ((exactIds == null || exactIds.Contains(n.Key)) && (definitionFilter == null || definitionFilter((GameNode)c.ReadNode(n,"def")!))) { yield return n; if (FailEnumeration) throw ReadContext.Error(EnumerationError,"Incomplete fixture collection"); }
        }
        public IEnumerable<GameEntity> QueryPawns(IReadOnlyDictionary<string,object?> args,ReadContext c) => LargePawns ? Enumerable.Range(0,8193).Select(i=>new GameEntity("pawn"+i,"map1",i)) : Colonists();
        public IEnumerable<GameEntity> Maps() => new[] { new GameEntity("map1",null,new object()), new GameEntity("map2",null,new object()) };
        public IEnumerable<GameEntity> Colonists() => Array.Empty<GameEntity>();
        public object? GameField(string field) => field == "currentMapId" ? "map1" : null;
        public object? MapField(GameEntity e,string field) { Reads++; return field == "isPlayerHome" ? e.Id == "map1" : "Map " + e.Id; }
        public object? ColonistField(GameEntity e,string field) => null;
        public object? NodeField(GameNode n,string field,IReadOnlyDictionary<string,object?> args)
        {
            if(DelayReadMilliseconds>0) {
                var delay=System.Diagnostics.Stopwatch.StartNew();
                while(delay.Elapsed.TotalMilliseconds<DelayReadMilliseconds)System.Threading.Thread.SpinWait(64);
            }
            Reads++;
            if (field == "stackCount" && FailQuantity) throw ReadContext.Error("field_unavailable","Unavailable quantity");
            if (n.Kind == "ThingOwner" && field=="items") { var rows=Enumerable.Range(0,8193).Select(i=>Thing("inside"+i)); return NativeContents ? (object)rows : rows.Select(n=>(object)n); }
            if (n.Kind == "Thing" && field=="GetDirectlyHeldThings" && LargeContents) return new GameNode("ThingOwner","contents",n.Owner,"contents");
            if (n.Kind == "Thing") return field switch { "id" => n.Key,"Spawned" => false,"stackCount" => n.Key == "a" ? 75 : 125,"def" => new GameNode("ThingDef","Steel",new GameEntity("def:Steel",null,"Steel"),"Steel"),"AllComps" => VaryComps && n.Key=="b" ? new[] { new GameNode("CompQuality","quality",n.Owner,"quality"),new GameNode("CompQuality","other",n.Owner,"other") } : new[] { new GameNode("CompQuality","quality",n.Owner,"quality") },_ => null };
            if (n.Kind == "CompQuality") return field == "Quality" ? "Good" : field == "supported" ? true : n.Key;
            if (field == "IsWeapon" && Fail) throw ReadContext.Error("field_unavailable","Unavailable definition member");
            return field switch { "defName" => "Steel","label" => "steel","IsWeapon" => false,_ => null };
        }
    }
}
