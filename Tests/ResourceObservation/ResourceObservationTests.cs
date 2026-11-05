using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;
using Neko.RimWorld;
using Verse;

internal static class ResourceObservationTests
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static JObject Spec(string id="Steel") => JObject.Parse("{'kind':'resources','map_id':'Map_1','target_ids':['"+id+"']}");
    private static QueryOutcome Finish(ResourceObservation op) { int advances=0; while(!op.Advance()) { advances++; if(advances>1000)throw new Exception("did not finish"); } Check(advances>0,"actual iterator should resume after its delayed native step"); return op.Outcome!; }
    internal static void Main()
    {
        Current.Game = new Game(); Find.TickManager = new TickManager { TicksGame=10 }; var map = new Map(); Find.Maps.Add(map); GameWorldReferences.Map=map;
        var source = JObject.Parse("{'status':'ok','total':1,'page':{'has_more':false},'rows':[{'def_name':'Steel','label':'Steel','coverage':'complete_in_scope','observed_units':15,'sources':[{'source':'spawned_ground','units':10,'records':1},{'source':'trader_stock','units':5,'records':1,'holder':{'id':'trader'}}]}],'scope':{'map_id':'Map_1','population':'native_scope','ownership':'trader_stock_is_seller_goods','excluded':'off_map','deduplicated':true}}");
        ResourceReadings.Result=source; ReadContext.Restarts=0;
        using(var op=new ResourceObservation(Spec())) { var outcome=Finish(op); var data=outcome.Data!; var row=(JObject)data["objects"]![0]!;
            Check((bool)data["complete"]!&&(string?)row["id"]=="Steel"&&(string?)row["definition"]=="Steel","resource native row retains target identity and completion");
            Check((long)row["observed_units"]! == 15 && JToken.DeepEquals(row["sources"], source["rows"]![0]!["sources"]),"source quantities and seller partition are preserved without reclassification");
            Check(JToken.DeepEquals(row["scope"],source["scope"]),"native resource scope is retained beside the row");
            Check((string?)data["sample"]?["colony_id"]=="colony"&&(string?)data["sample"]?["load_epoch"]=="epoch"&&(long?)data["sample"]?["started_tick"]==10,"true session and tick sample metadata are retained");
            Check(ReadContext.Restarts>0&&ResourceReadings.ArgsValid,"resource adapter uses the real cooperative context and selected public reader arguments");
            row["observed_units"]=999; Check((long)source["rows"]![0]!["observed_units"]! == 15,"projected observer row does not mutate resource reader output");
        }
        ResourceReadings.Result=(JObject)source.DeepClone(); ResourceReadings.Result["status"]="partial";
        using(var op=new ResourceObservation(Spec())) {var data=Finish(op).Data!;Check(!(bool)data["complete"]!&&(string?)data["objects"]![0]!["status"]=="partial"&&(long?)data["objects"]![0]!["observed_units"]==15,"partial source retains its original facts and marks coverage incomplete");}
        ResourceReadings.Throw=true;
        using(var op=new ResourceObservation(Spec())) {var data=Finish(op).Data!;var row=data["objects"]![0]!;Check(!(bool)data["complete"]!&&(string?)row["status"]=="unknown"&&row["observed_units"]==null&&row["sources"]==null,"native failure yields unknown rather than invented zero or empty sources");} ResourceReadings.Throw=false;
        ResourceReadings.Result=(JObject)source.DeepClone();ResourceReadings.Result["rows"]![0]!["def_name"]="Wood";
        using(var op=new ResourceObservation(Spec())) {var data=Finish(op).Data!;Check(!(bool)data["complete"]!&&(string?)data["objects"]![0]!["status"]=="unknown","wrong native definition cannot become evidence for the selected target");}
        ResourceReadings.Result=source;
        using(var op=new ResourceObservation(Spec())) {op.Advance();Current.Game=new Game();op.Advance();Check((bool?)op.Outcome?.Data?["complete"]==false,"world replacement interrupts a retained resource traversal");}
        bool rejected=false;try{new ResourceObservation(JObject.Parse("{'kind':'resources','map_id':'Map_1','target_ids':['Steel','Wood']}"));}catch(GraphQL.ExecutionError){rejected=true;}Check(rejected,"resource observation only owns one exact target");
        Console.WriteLine("Resource observation checks passed: "+checks);
    }
}
namespace GraphQL { public class ExecutionError : Exception { public string? Code; public ExecutionError(string message):base(message){} } }
namespace Neko.RimWorld {
    internal sealed class SessionDescriptor { public string ColonyId="colony",LoadEpoch="epoch";public long SnapshotSequence=7; }
    internal sealed class QueryOutcome { internal string Status="ok";internal JObject? Data;internal static QueryOutcome Partial(JObject data,string code,string message)=>new(){Status="partial",Data=data}; }
    internal static class ProtocolStatuses { internal const string Ok="ok"; } internal static class ProtocolCodec { internal const int MaxFrameBytes=1024*1024; }
    internal sealed class VerseGameData { }
    internal sealed class ReadContext { internal static int Restarts;public ReadContext(VerseGameData data,SessionDescriptor session,double budget){} internal void RestartSlice()=>Restarts++; internal void Check(){} internal static bool Recoverable(Exception e)=>true;internal static GraphQL.ExecutionError Error(string code,string message)=>new(message){Code=code}; }
    internal sealed class NekoGameComponent { internal SessionDescriptor ObservationSession()=>new(); }
    internal static class GameImplementation { internal static void RequireKnownBinary(){} }
    internal static class GameWorldReferences { internal static Map Map=null!;internal static Map ResolveMap(string id)=>Map; }
    internal static class ResourceReadings {
        internal static JObject Result=null!;internal static bool Throw,ArgsValid;
        internal static IEnumerable<int> ReadSteps(JObject args,SessionDescriptor session,ReadContext context,Action<JObject> publish) {Thread.Sleep(3);context.Check();yield return 0;ArgsValid=(string?)args["map"]=="Map_1"&&(string?)args["items"]?[0]=="Steel"&&(int?)args["limit"]==1&&args["aspects"] is JArray aspects&&aspects.Count==2;if(Throw)throw ReadContext.Error("field_unavailable","native unknown");yield return 0;publish(Result);}
    }
}
namespace Verse {
    public class ThingDef { public string defName="Steel"; }
    public static class DefDatabase<T> where T:class,new() {public static T? GetNamedSilentFail(string name)=>name=="Steel"?new T():null;}
    public class Map {public bool Disposed;public string GetUniqueLoadID()=>"Map_1";}
    public class Game {internal NekoGameComponent Component=new();public T GetComponent<T>()where T:class=>(Component as T)!;}
    public static class Current {public static Game? Game;}
    public class TickManager {public int TicksGame;}
    public static class Find {public static TickManager? TickManager;public static List<Map> Maps=new();}
}
