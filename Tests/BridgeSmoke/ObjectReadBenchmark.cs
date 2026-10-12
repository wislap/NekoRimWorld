using System;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>Offline scaling probe: frame-driven reads at N/2N/4N, CPU split from ObjectReadProfile.</summary>
internal static class ObjectReadBenchmark
{
    private static readonly (string Name,int Children,string Select)[] Workloads={
        ("wide",12,"[{field:'self',columns:['name']},{field:'health',children:[{field:'summary',columns:['pain']},{field:'body_parts',columns:['index','health'],page:{size:20}}]},{field:'profile',children:[{field:'skills',columns:['def_name','level']},{field:'traits',columns:['def_name','degree','suppressed'],page:{size:20}}]},{field:'stats',columns:['def_name','value'],page:{size:20}}]"),
        ("nested",12,"[{field:'health',children:[{field:'body_parts',columns:['index','health'],page:{size:20}}]},{field:'gear',children:[{field:'inventory',columns:['stack_count'],page:{size:20}}]}]"),
        ("roots",12,"[{field:'self',columns:['name']}]"),
    };
    // One look is one delivery: the plugin follows packet fragments only, selection cursors return to the model.
#if NETCOREAPP
    private static long Allocated()=>GC.GetAllocatedBytesForCurrentThread();
#else
    private static long Allocated()=>GC.GetTotalMemory(false);
#endif
    internal static void Run(int[] scales,int repeats) {
        var session=new SessionDescriptor(new string('2',32),new string('3',32),1,1);
        foreach(var workload in Workloads)foreach(var scale in scales) {
            var best=double.MaxValue;var profiledBest=double.MaxValue;string detail="",breakdown="";var plain=new System.Collections.Generic.List<double>();long allocated=0,collections=0;
            for(var attempt=0;attempt<repeats*2;attempt++) {
                var profiled=attempt%2==1;
                GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
                var engine=new GameQueryEngine(1000);var source=new ObjectReadTests.MutableData(scale,workload.Children);
                var input=JObject.Parse("{target:{kind:'colonists'},select:"+workload.Select+",lease_id:'bench',page:{size:"+Math.Min(scale,100)+"}}");
                ObjectReadProfile.Reset();ObjectReadProfile.Enabled=profiled;
                var allocatedBefore=Allocated();var collectionsBefore=GC.CollectionCount(0);
                long cpu=0;var chars=0;string status="";var fragments=0;
                while(true) {
                    using var operation=engine.BeginObjectRead((JObject)input.DeepClone(),source,session,()=>true,()=>session.GameTick);
                    var started=Stopwatch.GetTimestamp();while(!operation.Advance()) { }cpu+=Stopwatch.GetTimestamp()-started;
                    var outcome=operation.Outcome!;
                    if(outcome.ErrorCode!=null)throw new InvalidOperationException(workload.Name+" "+scale+" "+outcome.ErrorCode+" "+outcome.Message);
                    var data=outcome.Data!;fragments++;
                    if(data["fragment"] is JObject fragment) {
                        chars+=((string)fragment["text"]!).Length;
                        if((bool?)fragment["page"]?["has_more"]!=true)break;
                        input=new JObject { ["lease_id"]="bench",["page"]=new JObject { ["cursor"]=fragment["page"]!["cursor"]!.DeepClone() } };continue;
                    }
                    chars=data.ToString(Formatting.None).Length;
                    status=(bool?)data["delivery"]?["end_of_projection"]==true?"end":"more";break;
                }
                ObjectReadProfile.Enabled=false;
                var total=cpu*1000.0/Stopwatch.Frequency;
                if(!profiled) { plain.Add(total);allocated=Allocated()-allocatedBefore;collections=GC.CollectionCount(0)-collectionsBefore; }
                if(!profiled && total<best) { best=total;detail=$"{workload.Name,-7} N={scale,4} cpu_ms={total,7:0.0} fragments={fragments,2} kb={chars/1024,3} {status}"; }
                if(profiled && total<profiledBest) {
                    profiledBest=total;var parts=new[]{ObjectReadProfile.Part.Drain,ObjectReadProfile.Part.Branch,ObjectReadProfile.Part.Find,ObjectReadProfile.Part.Getter,ObjectReadProfile.Part.Explanations,ObjectReadProfile.Part.Metadata,ObjectReadProfile.Part.Finalize}.Sum(ObjectReadProfile.Milliseconds);
                    breakdown=$" other_ms={total-parts:0.0} "+ObjectReadProfile.Summary();
                }
            }
            plain.Sort();var median=plain.Count==0?0:plain[plain.Count/2];
            Console.WriteLine(detail+$" median_ms={median:0.0} alloc_mb={allocated/1048576.0:0.0} gen0={collections}"+breakdown);
        }
    }
}
