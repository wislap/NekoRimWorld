using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>
/// Differential corpus: every delivery and continuation of a fixed request set, written as
/// normalized JSON lines so two engine builds can be compared byte for byte. Random ids are
/// renumbered by first appearance; frame-timing counters are masked because frame boundaries
/// depend on wall-clock slices in either build.
/// </summary>
internal static class ObjectReadDiff
{
    private static readonly string[] Selections={
        "[]",
        "[{field:'self',columns:['name']}]",
        "[{field:'health',children:[{field:'summary',columns:['pain']}]},{field:'profile',children:[{field:'skills',columns:['def_name','level']}]}]",
        "[{field:'health',children:[{field:'body_parts',columns:['index','health','health'],page:{size:20}}]}]",
        "[{field:'self',columns:['name']},{field:'health',children:[{field:'summary',columns:['pain']},{field:'body_parts',columns:['index','health'],page:{size:20}}]},{field:'profile',children:[{field:'skills',columns:['def_name','level']},{field:'traits',columns:['def_name','degree','suppressed'],page:{size:20}}]},{field:'stats',columns:['def_name','value'],page:{size:20}}]",
        "[{field:'health',children:[{field:'body_parts',columns:['index','health'],page:{size:3}}]},{field:'gear',children:[{field:'inventory',columns:['stack_count'],page:{size:7}}]}]",
        "[{field:'profile',children:[{field:'traits',columns:['def_name','degree'],page:{size:100}}]},{field:'stats',columns:['def_name','value'],page:{size:100}}]",
    };
    private static readonly (int Pawns,int Children)[] Sources={ (1,0),(3,1),(10,5),(25,12),(40,40),(137,137) };
    private static readonly int[] PageSizes={ 1,7,20,100 };
    private static readonly Regex Id=new("[0-9a-f]{32}",RegexOptions.Compiled);
    private static readonly string[] Masked={ "identity_candidates","selected_field_accesses","native_getter_reads" };

    internal static void Run(string path) {
        var session=new SessionDescriptor(new string('2',32),new string('3',32),1,1);
        using var writer=new StreamWriter(path) { NewLine="\n" };
        var cases=0;var outcomes=0;
        foreach(var (pawns,children) in Sources)foreach(var select in Selections)foreach(var size in PageSizes) {
            var engine=new GameQueryEngine(1000);var source=new ObjectReadTests.MutableData(pawns,children);
            var ids=new Dictionary<string,string>(StringComparer.Ordinal);
            var lease="diff";var input=JObject.Parse("{target:{kind:'colonists'},select:"+select+",lease_id:'"+lease+"',page:{size:"+size+"}}");
            writer.WriteLine("# pawns="+pawns+" children="+children+" size="+size+" select="+select);cases++;
            for(var request=0;request<600;request++) {
                var outcome=engine.Execute("read_objects",(JObject)input.DeepClone(),source,session);outcomes++;
                var record=new JObject { ["status"]=outcome.Status,["error"]=outcome.ErrorCode,["message"]=outcome.Message,["data"]=outcome.Data?.DeepClone(),
                    ["issues"]=outcome.Issues==null?null:JToken.FromObject(outcome.Issues) };
                foreach(var counter in record.Descendants().OfType<JProperty>().Where(p=>Masked.Contains(p.Name)).ToList())counter.Value="masked";
                writer.WriteLine(Id.Replace(record.ToString(Formatting.None),m=>ids.TryGetValue(m.Value,out var n)?n:ids[m.Value]="id"+ids.Count));
                var data=outcome.Data;if(outcome.ErrorCode!=null || data==null)break;
                var page=data["fragment"] is JObject fragment?fragment["page"]:data["page"];
                if((bool?)page?["has_more"]!=true || page?["cursor"]?.Type!=JTokenType.String)break;
                input=new JObject { ["lease_id"]=lease,["page"]=new JObject { ["cursor"]=page!["cursor"]!.DeepClone() } };
            }
        }
        Console.WriteLine("Object-read differential corpus: cases="+cases+" outcomes="+outcomes+" -> "+path);
    }
}
