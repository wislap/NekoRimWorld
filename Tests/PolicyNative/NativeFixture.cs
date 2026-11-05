using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Verse {
    internal interface ILoadReferenceable { string GetUniqueLoadID(); }
    internal class Def { public string defName = ""; public bool Official = true; }
    internal class ThingDef : Def { public RimWorld.IngestibleProperties? ingestible;public float Nutrition=1; }
    internal class SpecialThingFilterDef : Def { }
    internal struct FloatRange { public float min,max; public FloatRange(float min,float max) { this.min=min;this.max=max; } }
    internal class ThingFilter {
        private readonly HashSet<ThingDef> allowed = new(); private readonly Dictionary<SpecialThingFilterDef,bool> special = new();
        public int MutationCount;public IEnumerable<ThingDef> AllowedThingDefs => allowed;
        public FloatRange AllowedHitPointsPercents = new(0,1), AllowedMentalBreakChance = new(0,1);
        public RimWorld.QualityRange AllowedQualityLevels = new(RimWorld.QualityCategory.Awful,RimWorld.QualityCategory.Legendary);
        public bool Allows(SpecialThingFilterDef def) => !special.TryGetValue(def,out var allow)||allow;
        public void SetAllow(ThingDef def,bool allow) { MutationCount++;if(allow)allowed.Add(def);else allowed.Remove(def); }
        public void SetAllow(SpecialThingFilterDef def,bool allow) {MutationCount++;special[def]=allow;}
        public void SetDisallowAll() { MutationCount++;allowed.Clear();special.Clear(); }
        public void CopyAllowancesFrom(ThingFilter other) { allowed.Clear();foreach(var d in other.allowed)allowed.Add(d);special.Clear();foreach(var p in other.special)special[p.Key]=p.Value; AllowedHitPointsPercents=other.AllowedHitPointsPercents;AllowedMentalBreakChance=other.AllowedMentalBreakChance;AllowedQualityLevels=other.AllowedQualityLevels; }
    }
    internal static class DefDatabase<T> where T:Def {
        public static List<T> AllDefsListForReading = new();
        public static T? GetNamedSilentFail(string? name) => AllDefsListForReading.SingleOrDefault(d=>d.defName==name);
        public static T GetNamed(string? name) => GetNamedSilentFail(name)??throw new ArgumentException("Definition missing");
    }
    internal class Pawn : ILoadReferenceable {
        public string Id=""; public bool Destroyed=false,Dead=false,IsMutant; public bool IsFreeColonist=true, Spawned=true;
        public RimWorld.Pawn_FoodRestrictionTracker foodRestriction; public RimWorld.Pawn_Ownership ownership;
        public RimWorld.Pawn_PlayerSettings playerSettings=new(); public RimWorld.MutantTracker mutant = new(); public Map Map = null!;
        public Pawn(string id) { Id=id;foodRestriction=new(this);ownership=new(this); }
        public string GetUniqueLoadID()=>Id;
    }
    internal class Map {private static int next;public string Id="Map_"+(++next);public string GetUniqueLoadID()=>Id;public bool Disposed=false;public HashSet<IntVec3> Fog=new(),Rejected=new(),ThickRoof=new();public ListerBuildings listerBuildings=new();public AreaManager areaManager;public ZoneManager zoneManager;public Map(){areaManager=new(this);zoneManager=new(this);} }
    internal class ListerBuildings { public List<object> allBuildingsColonist=new(); }
    internal static class Find { public static List<Map> Maps=new();public static Map CurrentMap=>Maps[0]; }
    internal class Game { public RimWorld.FoodRestrictionDatabase foodRestrictionDatabase=new(); }
    internal static class Current { public static Game Game=new(); }
}
namespace RimWorld {
    internal static class StatDefOf {public static readonly object Nutrition=new();}
    internal static class StatReading {public static float GetStatValueAbstract(this Verse.ThingDef def,object stat,Verse.ThingDef? stuff)=>def.Nutrition;}
    internal class IngestibleProperties { public bool HumanEdible=true; }
    internal enum QualityCategory { Awful,Poor,Normal,Good,Excellent,Masterwork,Legendary }
    internal struct QualityRange { public QualityCategory min,max; public QualityRange(QualityCategory min,QualityCategory max){this.min=min;this.max=max;} }
    internal class FoodPolicy : Verse.ILoadReferenceable { public int id;public string label="";public Verse.ThingFilter filter=new(); public string GetUniqueLoadID()=>"FoodPolicy_"+label+"_"+id;public void CopyFrom(FoodPolicy p){label=p.label;filter.CopyAllowancesFrom(p.filter);} }
    internal class FoodRestrictionDatabase {
        public List<FoodPolicy> AllFoodRestrictions=new();
        public FoodPolicy DefaultFoodRestriction() { if(AllFoodRestrictions.Count==0)MakeNewFoodRestriction();return AllFoodRestrictions[0]; }
        public FoodPolicy MakeNewFoodRestriction() { var p=new FoodPolicy {id=AllFoodRestrictions.Select(p=>p.id).DefaultIfEmpty(0).Max()+1,label="New"};AllFoodRestrictions.Add(p);return p; }
        public AcceptanceReport TryDelete(FoodPolicy p) { foreach(var pawn in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive)if(pawn.foodRestriction.CurrentFoodPolicy==p)return new(false,"In use"); foreach(var pawn in PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead)if(pawn.foodRestriction.CurrentFoodPolicy==p)pawn.foodRestriction.CurrentFoodPolicy=null!;AllFoodRestrictions.Remove(p);return new(true,""); }
    }
    internal class Pawn_FoodRestrictionTracker {
        private FoodPolicy? curPolicy; private readonly Verse.Pawn pawn;
        public Pawn_FoodRestrictionTracker(Verse.Pawn pawn){this.pawn=pawn;}
        public FoodPolicy CurrentFoodPolicy {get {if(pawn.IsMutant&&pawn.mutant.Def.disablePolicies)return null!;return curPolicy??=Verse.Current.Game.foodRestrictionDatabase.DefaultFoodRestriction();}set=>curPolicy=value;}
        public FoodPolicy? Raw=>curPolicy; public bool Configurable=true;
    }
    internal class MutantTracker { public MutantDef Def=new(); }
    internal class MutantDef { public bool disablePolicies; }
    internal static class PawnsFinder {
        public static List<Verse.Pawn> All_AliveOrDead=new(),AllMapsWorldAndTemporary_AliveOrDead=new(),AllMapsCaravansAndTravellingTransporters_Alive=new();
    }
    internal class Faction { public static readonly Faction OfPlayer=new(); }
    internal readonly struct AcceptanceReport { public bool Accepted{get;}public string Reason{get;} public AcceptanceReport(bool accepted,string reason){Accepted=accepted;Reason=reason;} }
    internal class Building_Bed : Verse.ILoadReferenceable {
        public string Id="";public bool Destroyed=false;public bool Spawned=true,ForColonists=true;public Faction Faction=Faction.OfPlayer;public Verse.ThingDef def=new();public Verse.Map Map=null!; public int SleepingSlotsCount=1;
        public List<Verse.Pawn> OwnersForReading=new();public CompAssignableToPawn_Bed CompAssignableToPawn;
        private bool medical; public bool Medical {get=>medical;set{medical=value;if(value)foreach(var p in OwnersForReading.ToArray())p.ownership.UnclaimBed();}}
        public Building_Bed(string id){Id=id;CompAssignableToPawn=new(this);}public string GetUniqueLoadID()=>Id;
    }
    internal class CompAssignableToPawn_Bed {
        private readonly Building_Bed bed;public List<Verse.Pawn> AssigningCandidates=new();public bool Reject;
        public CompAssignableToPawn_Bed(Building_Bed bed){this.bed=bed;}
        public AcceptanceReport CanAssignTo(Verse.Pawn pawn)=>new(!Reject,"Native rejection");
        public void TryAssignPawn(Verse.Pawn p){p.ownership.UnclaimBed();p.ownership.OwnedBed=bed;bed.OwnersForReading.Add(p);}
    }
    internal class Pawn_Ownership {private readonly Verse.Pawn pawn;public Building_Bed? OwnedBed; public Pawn_Ownership(Verse.Pawn pawn){this.pawn=pawn;}public bool UnclaimBed(){OwnedBed?.OwnersForReading.Remove(pawn);OwnedBed=null;return true;} }
    internal static class RestUtility { public static bool CanUseBedEver(Verse.Pawn pawn,Verse.ThingDef def)=>true; }
}
namespace Neko.RimWorld {
    internal class GameEntity {public string Id="";public object? Handle;}
    internal interface IGameData {IEnumerable<GameEntity> Colonists();}
    internal static class ColonyReadContract {internal static string[] Texts(JObject o,string key)=>o[key] is JArray a?a.Values<string>().Select(x=>x!).ToArray():Array.Empty<string>();}
    internal static class OfficialContent { internal static bool Contains(Verse.Def d)=>d.Official; }
    internal sealed class WriteFieldsHarness {
        private readonly WriteFieldDispatcher dispatcher; private readonly LedgerFieldRouter ledger;
        internal WriteFieldsHarness(IGameData data){var sets=new IWriteFieldSet[]{new PolicyWriteFields(),new BedWriteFields(data),new AreaCellFlagsWriteFields(),new AreaZoneWriteFields()};dispatcher=new(sets,Specs().Select(s=>(string)s["op"]!));ledger=new(sets);}
        internal IPreparedWriteField ResolveTest(JObject operation)=>dispatcher.Resolve(operation).Single().Field;
        internal IReadOnlyList<ResolvedWriteField> ResolveFieldsTest(JObject operation)=>dispatcher.Resolve(operation);
        internal IReadableWriteField? ResolveLedger(string id,string field,object? target,JToken value)=>ledger.Resolve(id,field,target,value);
        internal static JObject[] Specs()=>PolicyWriteCatalog.Operations.Concat(BedWriteCatalog.Operations).Concat(AreaZoneWriteCatalog.Operations).Concat(AreaCellFlagsWriteCatalog.Operations).ToArray();
    }
}

namespace Neko.RimWorld {
    internal static class NativeObjectIdentities {
        private sealed class Identity { internal readonly string Token=Guid.NewGuid().ToString("N"); }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object,Identity> ids=new();
        internal static string Token(object native)=>ids.GetValue(native,_=>new Identity()).Token;
    }
}

namespace Neko.RimWorld {internal static class GameQueryEngine {internal static string Hash(string value)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();}}

namespace Neko.RimWorld {
    internal static class NativeAreaManualChanges {
        internal static bool Complete=true;internal static void RequireAvailable(){if(!Complete)throw new InvalidOperationException("Manual provenance unavailable");}
        private static readonly Dictionary<Verse.Area,Dictionary<Verse.IntVec3,string>> ids=new();
        internal static string? ManualId(Verse.Area area,Verse.IntVec3 cell)=>ids.TryGetValue(area,out var cells)&&cells.TryGetValue(cell,out var id)?id:null;
        internal static string Revision(Verse.Area area)=>GameQueryEngine.Hash(Ids(area).ToString(Newtonsoft.Json.Formatting.None));
        internal static JArray Ids(Verse.Area area)=>new(ids.TryGetValue(area,out var cells)?cells.OrderBy(p=>p.Key.z).ThenBy(p=>p.Key.x).Select(p=>new JObject{["x"]=p.Key.x,["z"]=p.Key.z,["manual_id"]=p.Value}):Enumerable.Empty<JObject>());
        internal static void Mark(Verse.Area area,Verse.IntVec3 cell){if(!ids.TryGetValue(area,out var cells))ids.Add(area,cells=new());cells[cell]=Guid.NewGuid().ToString("N");}
    }
}
