// Standalone contract/transaction tests. Compile with the three production
// sources and AtomicWriteOperation.cs; these stubs model the audited 1.6 surface.
// They do not replace in-game qualification of native methods.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
using Neko.RimWorld;

internal static class ProductionTests
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static JObject Op(string name, Building_WorkTable table, JToken expect, JToken value, Bill? bill = null) {
        var op = new JObject { ["op"] = name, ["worktable"] = table.GetUniqueLoadID(), ["expect"] = expect, ["value"] = value, ["evidence"] = new JObject() };
        if (name != "add_production_bill") op["map"] = table.MapHeld!.GetUniqueLoadID();
        if (bill != null) op["bill"] = bill.GetUniqueLoadID(); return op;
    }
    private static JObject Run(JObject op, bool dry = false) {
        using var operation = new AtomicWriteOperation(ProductionDispatch.Create(), new JObject { ["load_epoch"] = "e", ["ops"] = new JArray(op) }, () => "e", dry);
        operation.Advance(); return operation.Outcome!.Data!;
    }
    internal static void Main()
    {
        var map = new Map(); Find.Maps.Add(map); var table = new Building_WorkTable { MapHeld = map }; map.listerBuildings.allBuildingsColonist.Add(table);
        Check(ProductionWriteCatalog.Operations.Where(s => (string?)s["op"] != "set_production_bill").All(s => (string?)s["expect_schema"]?["type"] == "object"), "structural guards use object expectation schemas independently of boolean intent");
        var steel = new ThingDef { defName = "Steel" }; var wood = new ThingDef { defName = "Wood" }; DefDatabase<ThingDef>.Values.AddRange(new[] { steel, wood });
        foreach (var name in new[] { "Forever", "RepeatCount", "TargetCount" }) DefDatabase<BillRepeatModeDef>.Values.Add(new BillRepeatModeDef { defName = name });
        foreach (var name in new[] { "DropOnFloor", "BestStockpile", "SpecificStockpile" }) DefDatabase<BillStoreModeDef>.Values.Add(new BillStoreModeDef { defName = name });
        DefDatabase<SpecialThingFilterDef>.Values.Add(new SpecialThingFilterDef { defName = "Fresh" });
        var recipe = new RecipeDef { defName = "Make", recipeUsers = new List<ThingDef> { table.def } }; recipe.ingredients.Add(new IngredientCount(10, steel, wood)); recipe.products.Add(new ThingDefCountClass { thingDef = steel, count = 1 }); DefDatabase<RecipeDef>.Values.Add(recipe);
        var create = Op("add_production_bill", table, new JObject { ["bill_ids"] = new JArray() }, new JObject { ["recipe"] = "Make", ["settings"] = new JObject { ["target_count"] = 8, ["pause_when_satisfied"] = true, ["ingredient_search_radius"] = 18 } });
        Check((string?)Run(create, true)["status"] == "validated" && Bill.Allocations == 0 && table.BillStack.Count == 0, "dry run must not allocate or add a bill");
        Check((string?)Run(create)["status"] == "applied", "create complete bill"); var first = (Bill_Production)table.BillStack.Bills.Single();
        Check(first.targetCount == 8 && first.pauseWhenSatisfied && first.ingredientSearchRadius == 18, "initial settings applied");
        recipe.unfinishedThingDef = steel;
        var secondCreate = Op("add_production_bill", table, new JObject { ["bill_ids"] = new JArray(first.GetUniqueLoadID()) }, new JObject { ["recipe"] = "Make" });
        Check((string?)Run(secondCreate)["status"] == "applied" && table.BillStack.Bills[1] is Bill_ProductionWithUft, "factory preserves standard unfinished subtype");
        var second = (Bill_ProductionWithUft)table.BillStack.Bills[1]; second.SetBoundUft(new UnfinishedThing { def = steel, Creator = new Pawn() });
        var beforeBoundReads = Bill_ProductionWithUft.BoundWorkerReads; var beforeFactoryReads = RecipeDef.CachedGetterReads;
        var snapshot = ProductionReadings.Read(table); Check(snapshot["bills"]![1]!["unfinished"] is JObject, "unfinished read includes identity");
        Check(Bill_ProductionWithUft.BoundWorkerReads == beforeBoundReads && RecipeDef.CachedGetterReads == beforeFactoryReads, "reads must not call mutating native getters");
        first.hpRange = new FloatRange(.35f, .85f);
        first.ingredientFilter.AllowedMentalBreakChance = new FloatRange(.15f, .75f);
        var beforeChange = ProductionReadings.Settings(first);
        var change = Op("set_production_bill", table, new JObject { ["hp_range"] = beforeChange["hp_range"]!.DeepClone() }, JObject.Parse("{'hp_range':{'min':0,'max':1}}"), first);
        change = JObject.Parse(change.ToString());
        Check((string?)Run(change)["status"] == "applied", "integer JSON ranges normalize to native floats");
        var wrongMap = (JObject)change.DeepClone(); wrongMap["map"] = "Map_other";
        Check((string?)Run(wrongMap)["status"] == "write_failed", "explicit worktable map identity must match");
        var missingMap = (JObject)change.DeepClone(); missingMap.Remove("map");
        Check((string?)Run(missingMap)["status"] == "write_failed", "production writes do not infer a missing map");
        var originalFilter = ProductionReadings.Filter(first.ingredientFilter);
        Check(JToken.DeepEquals(ProductionReadings.Settings(first), JObject.Parse(ProductionReadings.Settings(first).ToString())), "settings retain exact JSON null and numeric semantics across transport");
        var requestedFilter = (JObject)originalFilter.DeepClone(); requestedFilter["definitions"] = new JArray("Wood", "Steel");
        var filterChange = Op("set_production_bill", table, new JObject { ["ingredient_filter"] = originalFilter }, new JObject { ["ingredient_filter"] = requestedFilter }, first);
        Check((string?)Run(filterChange)["status"] == "applied", "full filter accepts unsorted definitions and has canonical readback");
        var revertFilter = Op("set_production_bill", table, new JObject { ["ingredient_filter"] = ProductionReadings.Filter(first.ingredientFilter) }, new JObject { ["ingredient_filter"] = originalFilter }, first);
        Check((string?)Run(revertFilter)["status"] == "applied" && JToken.DeepEquals(originalFilter, ProductionReadings.Filter(first.ingredientFilter)), "full filter restores original permissions");
        var oldOrder = new JObject { ["bill_ids"] = new JArray(first.GetUniqueLoadID(), second.GetUniqueLoadID()) };
        var newOrder = new JObject { ["bill_ids"] = new JArray(second.GetUniqueLoadID(), first.GetUniqueLoadID()) };
        Check((string?)Run(Op("reorder_production_bills", table, oldOrder, newOrder))["status"] == "applied", "reorder uses complete identity permutation");
        Check((string?)Run(Op("reorder_production_bills", table, oldOrder, newOrder))["status"] == "conflict", "stale queue CAS rejected");
        Check((string?)Run(Op("reorder_production_bills", table, newOrder, new JObject { ["bill_ids"] = new JArray(first.GetUniqueLoadID(), first.GetUniqueLoadID()) }))["status"] == "write_failed", "duplicate identities rejected");
        var boundRemoval = Op("remove_production_bill", table, ProductionWriteFields.RemovalGuard(table, second), new JValue(false), second);
        Check((string?)Run(boundRemoval)["status"] == "write_failed", "removing bound unfinished work rejected");
        var pawn = new Pawn(); pawn.jobs.curJob = new Job { bill = first }; map.mapPawns.AllPawns.Add(pawn);
        var removal = Op("remove_production_bill", table, ProductionWriteFields.RemovalGuard(table, first), new JValue(false), first);
        Check((string?)Run(removal)["status"] == "write_failed", "current Job protects bill deletion"); pawn.jobs.curJob = null;
        var prepared = (IPreparedStructuralWrite)ProductionDispatch.Create().Resolve(removal).Single().Field;
        var original = prepared.Read().DeepClone(); prepared.Validate(new JValue(false)); prepared.Write(new JValue(false)); var undo = prepared.CreateUndo();
        Check(JToken.DeepEquals(undo.Read(), prepared.UndoExpected()), "deletion undo guard reads live queue");
        undo.Validate(original); undo.Write(original); Check(JToken.DeepEquals(undo.Read(), original) && table.BillStack.Bills.Contains(first), "deletion undo restores the same native bill and order");
        first.ingredientSearchRadius = 30;
        Check(!JToken.DeepEquals(prepared.Read(), original), "bill mutation changes deletion guard");
        second.style = new Def { defName = "Style" }; second.globalStyle = true; second.graphicIndexOverride = 2;
        second.RenamableLabel = "Reserve coats";
        var copyOperation = Op("copy_production_bill", table, ProductionWriteFields.CopyGuard(table, second), new JValue(true), second);
        second.targetCount++;
        Check((string?)Run(copyOperation)["status"] == "conflict", "copy compares the source configuration as well as the queue"); second.targetCount--;
        var copyField = (IPreparedStructuralWrite)ProductionDispatch.Create().Resolve(copyOperation).Single().Field;
        var copyOriginal = copyField.Read().DeepClone(); copyField.Validate(new JValue(true)); copyField.Write(new JValue(true));
        var copied = (Bill_ProductionWithUft)table.BillStack.Bills.Last();
        Check(!ReferenceEquals(copied, second) && copied.BoundUft == null && JToken.DeepEquals(ProductionReadings.Settings(copied), ProductionReadings.Settings(second)), "copy preserves production policy and allocates independent identity without unfinished binding");
        Check(JToken.DeepEquals(ProductionReadings.Presentation(copied), ProductionReadings.Presentation(second)), "copy preserves native name and presentation settings");
        var copyUndo = copyField.CreateUndo(); Check(JToken.DeepEquals(copyUndo.Read(), copyField.UndoExpected()), "copy undo guard matches live source and new bill"); copyUndo.Validate(copyOriginal); copyUndo.Write(copyOriginal);
        Check(JToken.DeepEquals(copyUndo.Read(), copyOriginal) && second.BoundUft != null, "copy undo preserves source unfinished work");
        recipe.mechResurrection = true;
        var unsupported = Op("add_production_bill", table, new JObject { ["bill_ids"] = new JArray(table.BillStack.Bills.Select(b => b.GetUniqueLoadID())) }, new JObject { ["recipe"] = "Make" });
        var allocated = Bill.Allocations; Check((string?)Run(unsupported)["status"] == "write_failed" && allocated == Bill.Allocations, "special subtype rejected before allocation");
        Console.WriteLine("Production native contract checks passed: " + checks);
    }
}

namespace Neko.RimWorld {
    internal static class OfficialContent { internal static bool Contains(Def def) => true; }
    internal static class ProductionDispatch {
        internal static WriteFieldDispatcher Create() => new(new IWriteFieldSet[] { new ProductionWriteFields() }, ProductionWriteCatalog.Operations.Select(s => (string)s["op"]!).Append("add_production_bill"));
    }
    internal interface IFrameQueryOperation : IDisposable { bool Advance(); QueryOutcome? Outcome { get; } }
    internal sealed class QueryOutcome { internal JObject? Data; internal static QueryOutcome Failure(string code,string message,string? status=null) => new() { Data = new JObject { ["status"] = code, ["message"] = message } }; }
    internal static class ProtocolStatuses { internal const string StaleSession = "stale_session"; }
    internal static class ReadContext { internal static bool Recoverable(Exception e) => true; }
    internal static class GameQueryEngine { internal static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))); }
    internal static class WorldNativeAccess { internal static void RequireNative(object target, params string[] methods) { } }
    internal static class ResearchReadings { internal static bool UsesRecipe(ThingDef table, RecipeDef recipe) => recipe.recipeUsers?.Contains(table) == true; internal static bool? RecipeAvailableReadOnly(RecipeDef recipe) => true; }
}
namespace UnityEngine { public static class Mathf { public static int CeilToInt(float value) => (int)Math.Ceiling(value); } }
namespace Verse {
    public interface ILoadReferenceable { string GetUniqueLoadID(); }
    public class Def { public string defName = ""; }
    public enum ThingCategory { Item, Pawn, Building }
    public class ThingDef : Def { public ThingCategory category; public float VolumePerUnit = 1; }
    public class SpecialThingFilterDef : Def { }
    public static class DefDatabase<T> where T : Def { public static readonly List<T> Values = new(); public static List<T> AllDefsListForReading => Values; public static T? GetNamedSilentFail(string name) => Values.SingleOrDefault(v => v.defName == name); public static T GetNamed(string name) => GetNamedSilentFail(name) ?? throw new ArgumentException(name); }
    public class Thing : ILoadReferenceable { private static int next; private readonly int id = ++next; public ThingDef def = new(); public bool Destroyed; public bool Spawned = true; public Map? MapHeld; public int stackCount = 1; public virtual string GetUniqueLoadID() => "Thing_" + id; }
    public class Building : Thing { }
    public struct FloatRange { public float min,max; public FloatRange(float min,float max) { this.min=min;this.max=max; } }
    public struct IntRange { public int min,max; public IntRange(int min,int max) {this.min=min;this.max=max;} }
    public class ThingFilter { private readonly HashSet<ThingDef> allowed = new(); private readonly HashSet<SpecialThingFilterDef> disallowed = new(); public IEnumerable<ThingDef> AllowedThingDefs => allowed; public FloatRange AllowedHitPointsPercents = new(0,1),AllowedMentalBreakChance=new(0,1); public QualityRange AllowedQualityLevels = new(QualityCategory.Awful,QualityCategory.Legendary); public bool Allows(ThingDef def) => allowed.Contains(def); public bool Allows(SpecialThingFilterDef def) => !disallowed.Contains(def); public void SetAllow(ThingDef def,bool allow) { if(allow)allowed.Add(def);else allowed.Remove(def); } public void SetAllow(SpecialThingFilterDef def,bool allow) { if(allow)disallowed.Remove(def);else disallowed.Add(def); } }
    public class SkillRequirement { public SkillDef skill = new(); public int minLevel; }
    public class ThingDefCountClass { public ThingDef thingDef = new(); public int count; }
    public class RecipeDef : Def {
        public Type workerClass = typeof(RecipeWorker); public Type? workerCounterClass;
        private Type ingredientValueGetterClass = typeof(IngredientValueGetter_Volume);
        public static int CachedGetterReads; public object IngredientValueGetter { get { CachedGetterReads++; throw new Exception("mutating getter"); } }
        public bool mechResurrection; public int gestationCycles,formingTicks; public ThingDef? unfinishedThingDef; public bool UsesUnfinishedThing => unfinishedThingDef != null;
        public List<ThingDef>? recipeUsers; public List<IngredientCount> ingredients = new(); public List<ThingDefCountClass> products = new(); public ThingFilter fixedIngredientFilter = new();
        public bool allowMixingIngredients,ignoreIngredientCountTakeEntireStacks; public float workAmount; public SkillDef? workSkill; public List<SkillRequirement>? skillRequirements;
        public bool IsIngredient(ThingDef def) => ingredients.Any(i => i.filter.Allows(def));
        public Bill MakeNewBill() { var bill = UsesUnfinishedThing ? new Bill_ProductionWithUft() : new Bill_Production(); bill.recipe=this; return bill; }
    }
    public class IngredientCount { private float count; public ThingFilter filter = new(); public IngredientCount(float count,params ThingDef[] defs) {this.count=count;foreach(var d in defs)filter.SetAllow(d,true);} public float GetBaseCount()=>count; public bool IsFixedIngredient => filter.AllowedThingDefs.Count()==1; }
    public class RecipeWorker { public float GetIngredientCount(IngredientCount ingredient,Bill bill)=>ingredient.GetBaseCount(); }
    public class RecipeWorkerCounter { public RecipeDef recipe = null!; public bool CanCountProducts(Bill_Production bill) => recipe.products.Count > 0; public int CountProducts(Bill_Production bill)=>0; }
    public class UnfinishedThing : Thing { public Pawn? Creator; public Bill_ProductionWithUft? BoundBill; public float workLeft; public List<Thing> ingredients = new(); }
    public class Pawn : Thing { public bool Downed; public Faction Faction = Faction.OfPlayer; public Faction? HostFaction; public JobTracker jobs = new(); }
    public class JobTracker { public Job? curJob; public List<QueuedJob> jobQueue = new(); }
    public class Job { public Bill? bill; } public class QueuedJob { public Job? job; }
    public class Map : ILoadReferenceable { public ListerBuildings listerBuildings = new(); public MapPawns mapPawns = new(); public HaulDestinationManager haulDestinationManager = new(); public string GetUniqueLoadID()=>"Map_1"; }
    public class ListerBuildings { public List<Building> allBuildingsColonist = new(); } public class MapPawns { public List<Pawn> AllPawns = new(); }
    public class WorldPawns { public List<Pawn> AllPawnsAliveOrDead = new(); } public static class Find { public static List<Map> Maps = new(); public static WorldPawns WorldPawns = new(); }
}
namespace RimWorld {
    public class Faction : ILoadReferenceable { public static Faction OfPlayer = new(); public string GetUniqueLoadID()=>"Faction_1"; }
    public class SkillDef : Def { }
    public enum QualityCategory { Awful, Poor, Normal, Good, Excellent, Masterwork, Legendary }
    public struct QualityRange { public QualityCategory min,max; public QualityRange(QualityCategory min,QualityCategory max) {this.min=min;this.max=max;} }
    public class BillRepeatModeDef : Def { } public class BillStoreModeDef : Def { }
    public interface IBillGiver { }
    public class Building_WorkTable : Building, IBillGiver { public BillStack BillStack; public Building_WorkTable() { BillStack=new BillStack(this); } }
    public class Building_WorkTable_HeatPush : Building_WorkTable { }
    public class Bill : ILoadReferenceable {
        public const int MaxIngredientSearchRadius=999; public static int Allocations; private readonly int id=++Allocations;
        public BillStack billStack = null!; public RecipeDef recipe = null!; public bool suspended,deleted; public ThingFilter ingredientFilter=new(); public float ingredientSearchRadius=999; public IntRange allowedSkillRange=new(0,20);
        public Def? style; public Faction? precept; public bool globalStyle; public int? graphicIndexOverride;
        public Pawn? PawnRestriction; public bool SlavesOnly,MechsOnly,NonMechsOnly; public string Label => recipe.defName;
        public string GetUniqueLoadID()=>"Bill_"+id; public bool IsFixedOrAllowedIngredient(ThingDef def)=>recipe.ingredients.Any(i=>i.IsFixedIngredient&&i.filter.Allows(def)) || recipe.fixedIngredientFilter.Allows(def)&&ingredientFilter.Allows(def);
        public void SetPawnRestriction(Pawn pawn){SetAnyPawnRestriction();PawnRestriction=pawn;} public void SetAnyPawnRestriction(){PawnRestriction=null;SlavesOnly=MechsOnly=NonMechsOnly=false;} public void SetAnySlaveRestriction(){SetAnyPawnRestriction();SlavesOnly=true;} public void SetAnyMechRestriction(){SetAnyPawnRestriction();MechsOnly=true;} public void SetAnyNonMechRestriction(){SetAnyPawnRestriction();NonMechsOnly=true;}
    }
    public class Bill_Medical : Bill { }
    public class Bill_Production : Bill {
        private string? playerCustomName;
        public string? RenamableLabel { get => playerCustomName; set => playerCustomName=value; }
        public BillRepeatModeDef repeatMode=DefDatabase<BillRepeatModeDef>.GetNamed("RepeatCount"); public int repeatCount,targetCount,unpauseWhenYouHave; public bool pauseWhenSatisfied,includeEquipped,includeTainted,limitToAllowedStuff,paused;
        public FloatRange hpRange=new(0,1); public QualityRange qualityRange=new(QualityCategory.Awful,QualityCategory.Legendary); private BillStoreModeDef storeMode=DefDatabase<BillStoreModeDef>.GetNamed("BestStockpile"); private ISlotGroup? storeGroup,includeGroup;
        public BillStoreModeDef GetStoreMode()=>storeMode; public ISlotGroup? GetSlotGroup()=>storeGroup; public ISlotGroup? GetIncludeSlotGroup()=>includeGroup; public void SetIncludeGroup(ISlotGroup? group)=>includeGroup=group; public void SetStoreMode(BillStoreModeDef mode,ISlotGroup? group){storeMode=mode;storeGroup=group;}
    }
    public class Bill_ProductionWithUft : Bill_Production { private UnfinishedThing? boundUftInt; public static int BoundWorkerReads; public UnfinishedThing? BoundUft=>boundUftInt; public Pawn? BoundWorker{get {BoundWorkerReads++;boundUftInt=null;return null;}} public void SetBoundUft(UnfinishedThing uft){boundUftInt=uft;uft.BoundBill=this;} }
    public class BillStack { public const int MaxCount=15; public IBillGiver billGiver; public List<Bill> Bills=new(); public int Count=>Bills.Count; public BillStack(IBillGiver giver){billGiver=giver;} public void AddBill(Bill bill){Bills.Add(bill);bill.billStack=this;} public void Delete(Bill bill){bill.deleted=true;Bills.Remove(bill);} public void Reorder(Bill bill,int offset){var index=Bills.IndexOf(bill)+offset;Bills.Remove(bill);Bills.Insert(index,bill);} }
    public interface ISlotGroup { StorageGroup? StorageGroup {get;} }
    public interface ISlotGroupParent { }
    public class SlotGroup : ISlotGroup { public ISlotGroupParent parent=null!; public StorageGroup? StorageGroup {get;set;} }
    public class StorageGroup : ISlotGroup, ILoadReferenceable { StorageGroup? ISlotGroup.StorageGroup=>this; public string GetUniqueLoadID()=>"StorageGroup_1"; }
    public class HaulDestinationManager { public List<SlotGroup> AllGroupsListForReading=new(); }
    public abstract class IngredientValueGetter { public abstract float ValuePerUnitOf(ThingDef def); } public class IngredientValueGetter_Volume : IngredientValueGetter { public override float ValuePerUnitOf(ThingDef def)=>def.VolumePerUnit; } public class IngredientValueGetter_Nutrition : IngredientValueGetter { public override float ValuePerUnitOf(ThingDef def)=>1; }
}
