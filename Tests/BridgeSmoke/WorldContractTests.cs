using System;
using GraphQL;
using Newtonsoft.Json.Linq;
namespace Neko.RimWorld;
internal static class WorldContractTests
{
    internal static void Run()
    {
        void Good(string op,string json) => WorldObservationContract.Validate(op,JObject.Parse(json));
        void Bad(string op,string json) { try {Good(op,json);}catch(ExecutionError e)when(e.Code=="invalid_arguments"){return;}throw new Exception("Invalid world observation accepted: "+json); }
        Good("survey_space","{'region':{'near':{'id':'pawn'},'radius':0}}");
        Good("survey_space","{'region':{'rect':{'min_x':0,'min_z':0,'max_x':1,'max_z':2}}}");
        Good("inspect_state","{'targets':[{'id':'bed'},{'x':0,'z':0}],'user':{'name':'Patient'},'actor':{'name':'Helper'},'stats':['MoveSpeed']}");
        Good("inspect_state", "{'targets':[{'x':1,'z':1}],'sections':['identity'],'include_fogged':false,'detail_limit':1,'exact_lookup':true}");
        Bad("inspect_state", "{'targets':[{'id':'a'}],'sections':['rescue']}");
        Bad("inspect_state", "{'targets':[{'id':'a'}],'include_fogged':'false'}");
        Bad("inspect_state", "{'targets':[{'id':'a'}],'detail_limit':0}");
        Bad("inspect_state", "{'targets':[{'id':'a'}],'exact_lookup':1}");
        Good("inspect_route","{'actor':{'id':'pawn'},'to':{'id':'bed'},'start':{'x':0,'z':0}}");
        Bad("inspect_state", "{'targets':[{'id':'a'}],'sections':['spatial']}");
        foreach (var facet in new[] { "door", "trap", "explosive", "condition", "turret" })
            if (!SpatialStatePolicy.Includes(facet)) throw new Exception("Spatial hazard/passage facet omitted: " + facet);
        foreach (var facet in new[] { "pawn", "work", "reservations", "production", "storage", "containment" })
            if (SpatialStatePolicy.Includes(facet)) throw new Exception("Spatial reads must not acquire full object state: " + facet);
        foreach(var json in new[]{"{'region':{}}","{'region':{'radius':2}}","{'region':{'near':{'id':'x'},'room_of':{'id':'x'}}}","{'region':{'rect':{'min_x':2,'min_z':0,'max_x':1,'max_z':2}}}","{'region':{'map':'current'},'select':{'capabilities':['RescueBest']}}"})Bad("survey_space",json);
        foreach(var json in new[]{"{'targets':[]}","{'targets':[{'kind':'cell','id':'bed'}]}","{'targets':[{'kind':'pawn','x':1,'z':0}]}","{'targets':[{'x':2147483648,'z':0}]}","{'targets':[{'id':'a','name':'b'}]}","{'targets':[{'id':'a'}],'command':'reserve'}"})Bad("inspect_state",json);
        var first=JObject.Parse("{'region':{'map':'current','near':{'id':'x'}},'limit':20}");
        var reordered=JObject.Parse("{'cursor':'abc:2','region':{'near':{'id':'x'},'map':'current'},'limit':1}");
        if(WorldResultPaging.Signature(first)!=WorldResultPaging.Signature(reordered))throw new Exception("Equivalent continuation arguments differ");
        var rows=new System.Collections.Generic.List<JObject>();for(int i=0;i<5;i++)rows.Add(new JObject{["id"]=i,["text"]=new string('x',2000)});
        var result=new JObject{["status"]="partial",["objects_summary"]=new JObject{["records"]=5,["detail_retention_complete"]=true}};
        bool retained=false;WorldResultPaging.PutInitial(result,rows,5,"cursor",()=>{retained=true;return true;},5000);
        if(!retained)throw new Exception("Byte-reduced initial page did not retain continuation when row count <= limit");
        if((int)result["page"]!["returned"]! !=2||(string?)result["page"]!["next_cursor"]!="cursor:2"||(string?)result["status"]!="partial"||(int)result["objects_summary"]!["records"]! !=5)throw new Exception("Size-aware paging changed facts or lost continuation");
        WorldResultPaging.Put(result,rows,2,5,"cursor",5000);
        if((int)result["objects"]![0]!["id"]! !=2)throw new Exception("Page boundary duplicated/lost row");
        bool unnecessaryCache=false;WorldResultPaging.PutInitial(new JObject(),new System.Collections.Generic.List<JObject>{new JObject{["id"]=1}},5,"c",()=>{unnecessaryCache=true;return false;},5000);
        if(unnecessaryCache)throw new Exception("Complete single page unnecessarily retained or marked unavailable");
        Console.WriteLine("World observation contract tests passed: bounds, unified references, mutually exclusive footprints, no mutation arguments.");
    }
}
