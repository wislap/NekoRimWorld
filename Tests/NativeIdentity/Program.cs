using System;
using System.Collections.Generic;
using System.Linq;
namespace Verse {
    internal interface ILoadReferenceable { string GetUniqueLoadID(); }
    internal interface IExposable { void ExposeData(); }
    internal enum LoadSaveMode {Inactive,Saving,LoadingVars,ResolvingCrossRefs,PostLoadInit}
    internal enum LookMode {Deep}
    internal static class Scribe {internal static LoadSaveMode mode;internal static Dictionary<string,string?>? Record;internal static Dictionary<string,List<Dictionary<string,string?>>> Saved=new();internal static Dictionary<string,ILoadReferenceable> Bindings=new();}
    internal static class Scribe_Values {internal static void Look(ref string value,string key,string defaultValue){if(Scribe.mode==LoadSaveMode.Saving)Scribe.Record![key]=value;else if(Scribe.mode==LoadSaveMode.LoadingVars)value=Scribe.Record!.TryGetValue(key,out var saved)?saved??defaultValue:defaultValue;} }
    internal static class Scribe_References {internal static void Look<T>(ref T? value,string key)where T:class,ILoadReferenceable{if(Scribe.mode==LoadSaveMode.Saving)Scribe.Record![key]=value?.GetUniqueLoadID();else if(Scribe.mode==LoadSaveMode.LoadingVars)value=null;else if(Scribe.mode==LoadSaveMode.ResolvingCrossRefs){var id=Scribe.Record![key];value=id!=null&&Scribe.Bindings.TryGetValue(id,out var resolved)?resolved as T:null;}}}
    internal static class Scribe_Collections {
        internal static void Look<T>(ref List<T>? list,string key,LookMode mode)where T:class,IExposable,new(){
            if(Scribe.mode==LoadSaveMode.Saving){var data=new List<Dictionary<string,string?>>();foreach(var item in list??new()){Scribe.Record=new();item.ExposeData();data.Add(Scribe.Record);}Scribe.Saved[key]=data;}
            else if(Scribe.mode==LoadSaveMode.LoadingVars){if(!Scribe.Saved.TryGetValue(key,out var data)){list=null;return;}list=new();foreach(var record in data){Scribe.Record=record;var item=new T();item.ExposeData();list.Add(item);}}
            else if(Scribe.mode==LoadSaveMode.ResolvingCrossRefs&&list!=null){var data=Scribe.Saved[key];for(var i=0;i<list.Count;i++){Scribe.Record=data[i];list[i].ExposeData();}}
            Scribe.Record=null;
        }
    }
    internal class Area:ILoadReferenceable {private readonly string id;internal Area(string id){this.id=id;}internal Map Map=>Find.Maps.First();public string GetUniqueLoadID()=>id;}
    internal class Zone:ILoadReferenceable {private readonly string id;internal Zone(string id){this.id=id;}internal ZoneManager? zoneManager=>Find.Maps.FirstOrDefault()?.zoneManager;public string GetUniqueLoadID()=>id;}
    internal class Thing:ILoadReferenceable {internal bool Destroyed=false;public string GetUniqueLoadID()=>"Thing_1";}
    internal class Map {internal bool Disposed=false;internal AreaManager areaManager=new();internal ZoneManager zoneManager=new();}
    internal class AreaManager {internal List<Area> AllAreas=new();}
    internal class ZoneManager {internal List<Zone> AllZones=new();}
    internal static class Find {internal static List<Map> Maps=new();}
    internal class Game {internal RimWorld.FoodRestrictionDatabase foodRestrictionDatabase=new();internal Neko.RimWorld.NekoGameComponent Component=new();internal T GetComponent<T>()where T:class=>Component as T??throw new ArgumentException();}
    internal static class Current {internal static Game Game=new();}
}
namespace RimWorld {
    internal class FoodPolicy:Verse.ILoadReferenceable {private readonly string id;internal string Label="";internal FoodPolicy(string id){this.id=id;}public string GetUniqueLoadID()=>Label.Length==0?id:id+"_"+Label;}
    internal class Bill {internal bool deleted=false;internal BillStack? billStack=null;}
    internal class BillStack {internal object? billGiver=null;}
    internal class FoodRestrictionDatabase {internal List<FoodPolicy> AllFoodRestrictions=new();}
}
namespace Neko.RimWorld {
    internal class NekoGameComponent {internal readonly NativeObjectIdentityRegistry NativeIdentities=new();}
    internal static class PolicyReadings {internal static string Id(global::RimWorld.FoodPolicy policy)=>"food_policy:"+NativeObjectIdentities.Token(policy);}
    internal sealed class WriteLedger {internal void RemoveConfirmedDestroyed(Func<string,bool> predicate) { }}
    internal static class Program {
        private static int assertions;
        private static void Check(bool yes,string reason){assertions++;if(!yes)throw new Exception(reason);}
        private static void Reject(Action action,string reason){assertions++;try{action();}catch(ArgumentException){return;}throw new Exception("Expected rejection: "+reason);}
        private static void Save(NativeObjectIdentityRegistry registry){Verse.Scribe.mode=Verse.LoadSaveMode.Saving;registry.ExposeData();Verse.Scribe.mode=Verse.LoadSaveMode.Inactive;}
        private static NativeObjectIdentityRegistry Load(){var registry=new NativeObjectIdentityRegistry();foreach(var mode in new[]{Verse.LoadSaveMode.LoadingVars,Verse.LoadSaveMode.ResolvingCrossRefs,Verse.LoadSaveMode.PostLoadInit}){Verse.Scribe.mode=mode;registry.ExposeData();}Verse.Scribe.mode=Verse.LoadSaveMode.Inactive;return registry;}
        private static List<Dictionary<string,string?>> Records()=>Verse.Scribe.Saved["nekoNativeObjectIdentities"];
        private static void Main(){
            var policy=new global::RimWorld.FoodPolicy("FoodPolicy_1");Verse.Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Add(policy);var map=new Verse.Map();Verse.Find.Maps.Add(map);var area=new Verse.Area("Area_1");map.areaManager.AllAreas.Add(area);var zone=new Verse.Zone("Zone_1");map.zoneManager.AllZones.Add(zone);
            var registry=Verse.Current.Game.Component.NativeIdentities;var policyToken=NativeObjectIdentities.Token(policy);var areaToken=registry.Token(area);var zoneToken=registry.Token(zone);Check(registry.Token(policy)==policyToken,"Same original object retains token");Check(new[]{policyToken,areaToken,zoneToken}.Distinct().Count()==3,"Different native objects have distinct tokens");
            Save(registry);Check(Records().Count==3,"All supported observed live objects saved");
            var loadedPolicy=new global::RimWorld.FoodPolicy("FoodPolicy_1");var loadedArea=new Verse.Area("Area_1");var loadedZone=new Verse.Zone("Zone_1");Verse.Current.Game=new();Verse.Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Add(loadedPolicy);Verse.Find.Maps.Clear();var loadedMap=new Verse.Map();Verse.Find.Maps.Add(loadedMap);loadedMap.areaManager.AllAreas.Add(loadedArea);loadedMap.zoneManager.AllZones.Add(loadedZone);Verse.Scribe.Bindings=new(){{"FoodPolicy_1",loadedPolicy},{"Area_1",loadedArea},{"Zone_1",loadedZone}};
            var loaded=Load();Check(loaded.Token(loadedPolicy)==policyToken,"Food-policy token survives native object reconstruction on save reload");Check(loaded.Token(loadedArea)==areaToken,"Area token survives save reload");Check(loaded.Token(loadedZone)==zoneToken,"Zone token survives save reload");
            Verse.Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Remove(loadedPolicy);var replacement=new global::RimWorld.FoodPolicy("FoodPolicy_1");Verse.Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Add(replacement);Check(loaded.Token(replacement)!=policyToken,"Same native load ID reused by a new object gets a different token");Save(loaded);Check(Records().Count(r=>r["id"]=="FoodPolicy_1")==1,"Deleted original is pruned before reference serialization");Check(Records().Single(r=>r["id"]=="FoodPolicy_1")["token"]==loaded.Token(replacement),"Saved reused ID is bound only to current real original");
            Check(loaded.Token(loadedPolicy)==policyToken,"Autosave preserves removed original's in-memory identity for same-epoch undo");
            Verse.Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Remove(replacement);Verse.Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Add(loadedPolicy);Check(loaded.Token(loadedPolicy)==policyToken,"Restoring retained original after autosave preserves its pre-delete CAS token");Save(loaded);Check(Records().Single(r=>r["id"]=="FoodPolicy_1")["token"]==policyToken,"Restored original becomes the only persisted original after undo");
            Verse.Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Remove(loadedPolicy);Verse.Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Add(replacement);Save(loaded);
            // An unseen replacement must never inherit a deleted object's token.
            loadedMap.areaManager.AllAreas.Remove(loadedArea);var unseenArea=new Verse.Area("Area_1");loadedMap.areaManager.AllAreas.Add(unseenArea);Save(loaded);Check(Records().All(r=>r["id"]!="Area_1"),"Stale area reference omitted even if its ID is reused before observation");
            var snapshot=Records().Select(r=>new Dictionary<string,string?>(r)).ToList();Verse.Scribe.Bindings["FoodPolicy_1"]=replacement;Verse.Scribe.Bindings["Area_1"]=unseenArea;var reloaded=Load();Check(reloaded.Token(unseenArea)!=areaToken,"Unobserved replacement cannot inherit pruned token on next reload");
            Verse.Scribe.Saved["nekoNativeObjectIdentities"]=snapshot;Verse.Scribe.Bindings.Remove("Zone_1");var unresolved=Load();Check(unresolved.Token(loadedZone)!=zoneToken,"Unresolved reference cannot recover an old token by ID fallback");
            Verse.Scribe.Saved["nekoNativeObjectIdentities"]=snapshot.Select(r=>new Dictionary<string,string?>(r)).ToList();Verse.Scribe.Bindings["Zone_1"]=loadedZone;Records().Single(r=>r["id"]=="Zone_1")["nativeType"]="Wrong.Native.Type";Check(Load().Token(loadedZone)!=zoneToken,"Native type mismatch invalidates saved binding");
            Verse.Scribe.Saved["nekoNativeObjectIdentities"]=snapshot.Select(r=>new Dictionary<string,string?>(r)).ToList();Records().Add(new(Records().Single(r=>r["id"]=="Zone_1")));Check(Load().Token(loadedZone)!=zoneToken,"Ambiguous duplicate native references cannot inherit old identity");
            loadedMap.Disposed=true;Save(loaded);Check(Records().All(r=>r["id"]!="Zone_1"&&r["id"]!="Area_1"),"Objects on removed maps do not produce saved references");Check(loaded.Token(loadedZone)==zoneToken,"Removed map originals retain epoch-local undo identities in memory");loadedMap.Disposed=false;
            Reject(()=>loaded.Token(new object()),"Unsupported objects");Reject(()=>loaded.Token(new Verse.Area("NeverRegistered")),"Unregistered new native objects");
            Verse.Scribe.Saved.Clear();Check(Load().Token(replacement)!=policyToken,"Old saves without registry establish a fresh identity baseline");
            RenameAndSavedWriteTarget();
            Console.WriteLine($"NativeIdentity: {assertions} assertions passed.");
        }
        private static void RenameAndSavedWriteTarget() {
            Verse.Scribe.Saved.Clear();Verse.Scribe.Bindings.Clear();Verse.Current.Game=new();
            var original=new global::RimWorld.FoodPolicy("FoodPolicy_Identity_77"){Label="Before"};Verse.Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Add(original);
            string stable=PolicyReadings.Id(original),oldNative=original.GetUniqueLoadID();var index=new WriteTargetIndex(_=>false);index.BindMany(new[]{new KeyValuePair<string,object?>(stable,original)});var refs=new WriteTargetReferences();
            original.Label="After";Check(original.GetUniqueLoadID()!=oldNative&&PolicyReadings.Id(original)==stable,"Renaming changes the real Scribe ID but not the saved-token business ID");
            Verse.Scribe.mode=Verse.LoadSaveMode.Saving;Verse.Current.Game.Component.NativeIdentities.ExposeData();refs.ExposeData(index);Verse.Scribe.mode=Verse.LoadSaveMode.Inactive;
            var saved=Verse.Scribe.Saved["nekoWriteTargets"].Single();Check(saved["id"]==stable&&saved["target"]==original.GetUniqueLoadID(),"Ledger target saves stable business key separately from current native label-bearing Scribe ref");
            var restored=new global::RimWorld.FoodPolicy("FoodPolicy_Identity_77"){Label="After"};Verse.Current.Game=new();Verse.Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Add(restored);Verse.Scribe.Bindings[restored.GetUniqueLoadID()]=restored;
            var restoredRefs=new WriteTargetReferences();var restoredIndex=new WriteTargetIndex(_=>false);
            foreach(var mode in new[]{Verse.LoadSaveMode.LoadingVars,Verse.LoadSaveMode.ResolvingCrossRefs,Verse.LoadSaveMode.PostLoadInit}){Verse.Scribe.mode=mode;Verse.Current.Game.Component.NativeIdentities.ExposeData();restoredRefs.ExposeData(restoredIndex);}Verse.Scribe.mode=Verse.LoadSaveMode.Inactive;
            Check(PolicyReadings.Id(restored)==stable&&ReferenceEquals(restoredIndex.Find(stable),restored),"Restored identity precedes business-key binding of the exact policy Scribe reference");
            Check(restoredIndex.Find(oldNative)==null&&restoredIndex.Find(restored.GetUniqueLoadID())==null,"Mutable native IDs do not become implicit API aliases");
            restored.Label="Again";Check(PolicyReadings.Id(restored)==stable&&ReferenceEquals(restoredIndex.Find(stable),restored),"Another native UI rename retains the restored ledger original");
        }
    }
}

