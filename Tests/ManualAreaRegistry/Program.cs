using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Neko.RimWorld;
using Verse;

internal static class Program
{
    private static int checks;
    private const string Command = "RimWorld.Designator_AreaAllowedExpand.DesignateSingleCell";
    private static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    private static Area NewArea(Map map,string id="Area_1"){var a=new Area_Allowed(id,map);map.areaManager.AllAreas.Add(a);return a;}
    private static void Save(NativeAreaManualRegistry registry){Scribe.mode=LoadSaveMode.Saving;Current.Game.Component.NativeIdentities.ExposeData();registry.ExposeData();Scribe.mode=LoadSaveMode.Inactive;}
    private static NativeAreaManualRegistry Load(){var result=new NativeAreaManualRegistry();foreach(var mode in new[]{LoadSaveMode.LoadingVars,LoadSaveMode.ResolvingCrossRefs,LoadSaveMode.PostLoadInit}){Scribe.mode=mode;Current.Game.Component.NativeIdentities.ExposeData();result.ExposeData();}Scribe.mode=LoadSaveMode.Inactive;return result;}
    private static List<Dictionary<string,object?>> Records()=>Scribe.Saved["nekoAreaManualSources"];
    private static void Main()
    {
        var map=new Map("Map_1");Find.Maps.Add(map);var area=NewArea(map);var c=new IntVec3(4,0,5);var other=new IntVec3(6,0,7);var registry=new NativeAreaManualRegistry();
        Check(registry.Valid&&!registry.HasArea(area),"fresh registry has no implicit area population");
        Check(registry.ManualId(area,c)==null&&registry.HasArea(area),"first selector establishes only its cell fence");
        Check(registry.Watched(area).Length==1&&area.ActiveCellEnumerations==0,"watched selector does not enumerate Area.ActiveCells");
        area[c]=true;var first=registry.Change(area,c,false,true,Command,null,10,"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")!;
        Check((string?)first["source"]=="user_designator"&&first["ownership"]!.Type==JTokenType.Null,"unowned observed UI edit has source provenance");
        string firstId=(string)first["manual_id"]!;
        area[c]=false;var aba=registry.Change(area,c,true,false,Command,null,11,"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")!;
        Check((string?)aba["old_value"]?["manual_id"]==firstId&&registry.ManualId(area,c)!=(string?)first["manual_id"]&&area[c]==false,"UI ABA retains new opaque cell generation despite equal original boolean");
        Check(registry.Snapshot().Count==1&&registry.Ids(area).Count==1,"latest same-cell source replaces previous source sparsely");
        var sameTick=new List<JObject>();for(int i=0;i<3;i++){bool old=area[c];area[c]=!old;sameTick.Add(registry.Change(area,c,old,!old,Command,null,11,"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")!);}
        Check(sameTick.Select(s=>(long)s["manual_order"]!).SequenceEqual(new long[]{3,4,5})&&sameTick.Select(s=>(long)s["tick"]!).Distinct().Single()==11,"same-tick manual generations have monotonic order independent of GUID or game clock");
        var revision=registry.Revision(area);area[other]=true;
        Check(registry.Change(area,other,false,true,Command,null,12,"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")==null&&registry.Ids(area).Count==1&&registry.Watched(area).Length==1,"unselected cell does not become a source or watched cell");
        Check(registry.Revision(area)!=revision,"existing whole-area revision observes changes outside selected cells");
        var stable=registry.Revision(area);Check(registry.Change(area,c,false,false,Command,null,13,"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")==null&&registry.Revision(area)==stable,"no change emits no source and does not bump revision");
        var unseen=NewArea(map,"Area_2");Check(registry.Change(unseen,c,false,true,Command,null,14,"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")==null&&!registry.HasArea(unseen),"unobserved user edits do not establish hidden fences");
        var aiValue=JObject.Parse("{'native_instance':'original-ai','enabled':false,'manual_id':null}");var ownership=new JObject{["changeset_id"]="ai-change",["value"]=aiValue.DeepClone()};
        var owned=registry.Change(unseen,c,false,true,Command,ownership,15,"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")!;ownership["value"]!["enabled"]=true;
        var exact=new JArray(new JObject{["object"]=area.GetUniqueLoadID(),["field"]=AreaCellFlags.FieldName(c)});
        Check(registry.Snapshot(exact).Count==1&&(string?)registry.Snapshot(exact)[0]!["object"]==area.GetUniqueLoadID(),"scoped manual snapshot excludes other areas before projection");
        Check((bool?)owned["ownership"]?["value"]?["enabled"]==false&&registry.ManualId(unseen,c)==(string?)owned["manual_id"],"committed AI cell establishes fence and preserves original AI value by copy");
        var sourceId=registry.ManualId(area,c);var areaToken=NativeObjectIdentities.Token(area);var ownedToken=NativeObjectIdentities.Token(unseen);
        var fenceOnly=NewArea(map,"Area_3");registry.ManualId(fenceOnly,c);var oldOnlyRevision=registry.Revision(fenceOnly);
        Save(registry);Check(Records().Count==2,"only live areas with actual source are persisted; source-free fences remain epoch-local");
        var savedRaw=Records().SelectMany(r=>(List<string>)r["sources"]!).ToArray();
        // Native identities restore first, before manual source/instance checks.
        Current.Game=new Game();Find.Maps.Clear();var loadedMap=new Map("Map_1");Find.Maps.Add(loadedMap);var loadedArea=NewArea(loadedMap);var loadedOwned=NewArea(loadedMap,"Area_2");var loadedFence=NewArea(loadedMap,"Area_3");
        Scribe.Bindings=new(){{"Area_1",loadedArea},{"Area_2",loadedOwned},{"Area_3",loadedFence}};
        var loaded=Load();
        Check(loaded.Valid&&NativeObjectIdentities.Token(loadedArea)==areaToken&&NativeObjectIdentities.Token(loadedOwned)==ownedToken,"manual sources rebind only after original native instances restore");
        Check(loaded.ManualId(loadedArea,c)==sourceId&&loaded.Ids(loadedArea).Count==1,"same manual ID survives save/reload of native area objects");
        Check((long)loaded.Snapshot().Single(s=>(string?)s["object"]==loadedArea.GetUniqueLoadID())["manual_order"]! == 5,"same-cell monotonic manual order survives save/reload");
        Check((string?)loaded.Snapshot().Single(s=>(string?)s["object"]==loadedArea.GetUniqueLoadID())["origin_load_epoch"]=="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","source origin epoch remains the original UI action epoch after reload");
        Check(!loaded.HasArea(loadedFence)&&loaded.Revision(loadedFence)!=oldOnlyRevision,"source-free fences are not revived across load epoch");
        Check(loaded.Snapshot().Any(s=>s["ownership"]?.Type==JTokenType.Null),"snapshot includes unowned user edits");
        Save(loaded);Check(savedRaw.SequenceEqual(Records().SelectMany(r=>(List<string>)r["sources"]!)),"valid source JSON is retained byte-for-byte on save roundtrip");
        loadedMap.areaManager.AllAreas.Remove(loadedArea);var reused=NewArea(loadedMap);Check(loaded.ManualId(reused,c)==null&&NativeObjectIdentities.Token(reused)!=areaToken,"same Area ID reused in RAM cannot inherit source or native identity");
        Save(loaded);Check(Records().All(r=>(string?)r["id"]!="Area_1"),"save omits removed originals even when a same-ID replacement exists");
        loadedMap.areaManager.AllAreas.Remove(reused);loadedMap.areaManager.AllAreas.Add(loadedArea);Check(loaded.ManualId(loadedArea,c)==sourceId,"same-epoch restored original retains its manual source after autosave");loadedMap.areaManager.AllAreas.Remove(loadedArea);loadedMap.areaManager.AllAreas.Add(reused);
        // Corrupt a live source: retain exact raw bytes and fail closed.
        var raw="{ this is invalid original source ";((List<string>)Records()[0]["sources"]!).Add(raw);
        var invalid=Load();Check(!invalid.Valid&&invalid.Snapshot().Any(s=>(string?)s["raw_source"]==raw),"bad source bytes are preserved and registry fails closed");
        Save(invalid);Check(((List<string>)Records()[0]["sources"]!).Contains(raw),"saving an invalid registry does not overwrite its original source bytes");
        bool rejected=false;try{invalid.ManualId(loadedOwned,c);}catch(InvalidOperationException){rejected=true;}Check(rejected,"invalid provenance cannot authorize a fresh cell observation");
        ((List<string>)Records()[0]["sources"]!).Remove(raw);
        var validText=((List<string>)Records()[0]["sources"]!)[0];var repeated=JObject.Parse(validText);repeated["old_value"]!["manual_id"]=repeated["manual_id"]!.DeepClone();((List<string>)Records()[0]["sources"]!)[0]=repeated.ToString();Check(!Load().Valid,"source change cannot reuse its previous manual generation");((List<string>)Records()[0]["sources"]!)[0]=validText;
        var source=JObject.Parse(((List<string>)Records()[0]["sources"]!)[0]);source["future_field"]=true;var future=source.ToString();((List<string>)Records()[0]["sources"]!)[0]=future;
        Check(!Load().Valid,"unknown future source shape remains preserved instead of silently downgraded");
        ((List<string>)Records()[0]["sources"]!)[0]=savedRaw.Last();
        // Force cross-reference resolution to a same-ID new native instance.
        var replacement=new Area_Allowed("Area_2",loadedMap);loadedMap.areaManager.AllAreas.Remove(loadedOwned);loadedMap.areaManager.AllAreas.Add(replacement);Scribe.Bindings["Area_2"]=replacement;
        Current.Game.Component.NativeIdentities=new NativeObjectIdentityRegistry();NativeObjectIdentities.Token(replacement);
        // Existing native-identity token cannot be replaced by manual-record IDs.
        foreach(var mode in new[]{LoadSaveMode.LoadingVars,LoadSaveMode.ResolvingCrossRefs}){Scribe.mode=mode;invalid.ExposeData();}Scribe.mode=LoadSaveMode.PostLoadInit;invalid.ExposeData();Scribe.mode=LoadSaveMode.Inactive;
        Check(!invalid.Valid,"Area ID cross-ref reuse with a different native instance fails closed");
        Check(area.ActiveCellEnumerations==0&&loadedArea.ActiveCellEnumerations==0,"registry never expands observed scope into full active-cell enumeration");
        Console.WriteLine($"ManualAreaRegistry: {checks} assertions passed.");
    }
}

namespace Verse {
    internal interface ILoadReferenceable{string GetUniqueLoadID();}internal interface IExposable{void ExposeData();}
    internal enum LoadSaveMode{Inactive,Saving,LoadingVars,ResolvingCrossRefs,PostLoadInit}internal enum LookMode{Deep,Value}
    internal static class Scribe{internal static LoadSaveMode mode;internal static Dictionary<string,object?>? Record;internal static Dictionary<string,List<Dictionary<string,object?>>> Saved=new();internal static Dictionary<string,ILoadReferenceable> Bindings=new();}
    internal static class Scribe_Values{internal static void Look(ref string value,string key,string defaultValue){if(Scribe.mode==LoadSaveMode.Saving)Scribe.Record![key]=value;else if(Scribe.mode==LoadSaveMode.LoadingVars)value=Scribe.Record!.TryGetValue(key,out var v)?v as string??defaultValue:defaultValue;}}
    internal static class Scribe_References{internal static void Look<T>(ref T? value,string key)where T:class,ILoadReferenceable{if(Scribe.mode==LoadSaveMode.Saving)Scribe.Record![key]=value?.GetUniqueLoadID();else if(Scribe.mode==LoadSaveMode.LoadingVars)value=null;else if(Scribe.mode==LoadSaveMode.ResolvingCrossRefs){var id=Scribe.Record![key] as string;value=id!=null&&Scribe.Bindings.TryGetValue(id,out var target)?target as T:null;}}}
    internal static class Scribe_Collections{
        internal static void Look<T>(ref List<T>? list,string key,LookMode mode){if(mode==LookMode.Value){if(Scribe.mode==LoadSaveMode.Saving)Scribe.Record![key]=list==null?null:new List<T>(list);else if(Scribe.mode==LoadSaveMode.LoadingVars)list=Scribe.Record!.TryGetValue(key,out var data)&&data is List<T> values?new List<T>(values):null;return;}
            var parent=Scribe.Record;if(Scribe.mode==LoadSaveMode.Saving){var data=new List<Dictionary<string,object?>>();foreach(var item in list??new()){Scribe.Record=new();((IExposable)item!).ExposeData();data.Add(Scribe.Record);}Scribe.Saved[key]=data;}
            else if(Scribe.mode==LoadSaveMode.LoadingVars){if(!Scribe.Saved.TryGetValue(key,out var data)){list=null;return;}list=new();foreach(var row in data){Scribe.Record=row;var item=Activator.CreateInstance<T>();((IExposable)item!).ExposeData();list.Add(item);}}
            else if(Scribe.mode==LoadSaveMode.ResolvingCrossRefs&&list!=null){var data=Scribe.Saved[key];for(var i=0;i<list.Count;i++){Scribe.Record=data[i];((IExposable)list[i]!).ExposeData();}}Scribe.Record=parent;}
    }
    internal struct IntVec3:IEquatable<IntVec3>{internal int x,z;internal IntVec3(int x,int y,int z){this.x=x;this.z=z;}internal bool InBounds(Map map)=>x>=0&&z>=0&&x<100&&z<100;public bool Equals(IntVec3 c)=>x==c.x&&z==c.z;public override bool Equals(object? obj)=>obj is IntVec3 c&&Equals(c);public override int GetHashCode()=>HashCode.Combine(x,z);}
    internal class Area:ILoadReferenceable{private readonly string id;internal Map Map;private readonly Dictionary<IntVec3,bool> cells=new();internal int ActiveCellEnumerations;internal Area(string id,Map map){this.id=id;Map=map;}public string GetUniqueLoadID()=>id;internal bool this[IntVec3 cell]{get=>cells.TryGetValue(cell,out var value)&&value;set=>cells[cell]=value;}internal IEnumerable<IntVec3> ActiveCells{get{ActiveCellEnumerations++;throw new Exception("must not enumerate");}}}
    internal sealed class Area_Allowed:Area{internal Area_Allowed(string id,Map map):base(id,map){}}
    internal class Zone:ILoadReferenceable{public string GetUniqueLoadID()=>"Zone_1";}
    internal class Map:ILoadReferenceable{private readonly string id;internal Map(string id){this.id=id;}internal bool Disposed=false;internal AreaManager areaManager=new();internal ZoneManager zoneManager=new();public string GetUniqueLoadID()=>id;}
    internal class AreaManager{internal List<Area> AllAreas=new();}internal class ZoneManager{internal List<Zone> AllZones=new();}
    internal static class Find{internal static List<Map> Maps=new();}internal class Game{internal RimWorld.FoodRestrictionDatabase foodRestrictionDatabase=new();internal NekoGameComponent Component=new();internal T GetComponent<T>()where T:class=>(Component as T)!;}internal static class Current{internal static Game Game=new();}
}
namespace RimWorld{internal class FoodPolicy:ILoadReferenceable{public string GetUniqueLoadID()=>"Policy_1";}internal class FoodRestrictionDatabase{internal List<FoodPolicy> AllFoodRestrictions=new();}}
namespace Neko.RimWorld{
    internal class NekoGameComponent{internal NativeObjectIdentityRegistry NativeIdentities=new();}
    internal static class AreaZoneReadings{internal static string Kind(Area area)=>area is Area_Allowed?"allowed":"unsupported";}
    internal static class AreaCellFlags{internal static string FieldName(IntVec3 cell)=>"area_flag:"+cell.x+":"+cell.z;}
    internal static class ReadContext{internal static bool Recoverable(Exception error)=>true;}
}
