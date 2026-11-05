using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
namespace Verse {
    internal readonly struct IntVec3 : IEquatable<IntVec3> { public readonly int x,y,z; public IntVec3(int x,int y,int z){this.x=x;this.y=y;this.z=z;} public bool Equals(IntVec3 c)=>x==c.x&&y==c.y&&z==c.z;public override bool Equals(object? o)=>o is IntVec3 c&&Equals(c);public override int GetHashCode()=>HashCode.Combine(x,y,z); }
    internal static class GridExtensions {public static bool InBounds(this IntVec3 c,Map map)=>c.x>=0&&c.z>=0&&c.x<100&&c.z<100;public static bool Fogged(this IntVec3 c,Map map)=>map.Fog.Contains(c);}
    internal readonly struct Color {public readonly float r,g,b,a;public Color(float r,float g,float b,float a){this.r=r;this.g=g;this.b=b;this.a=a;}}
    internal class Area : ILoadReferenceable {public Map Map;public string Id;public string Label="Area";public Color Color=new(1,1,1,1);private readonly HashSet<IntVec3> cells=new();public Area(Map map,string id){Map=map;Id=id;}public bool BlockFullEnumeration;public IEnumerable<IntVec3> ActiveCells=>BlockFullEnumeration?throw new InvalidOperationException("Full area enumeration was forbidden by the local-read fixture"):cells;public bool this[IntVec3 c]{get=>cells.Contains(c);set{if(value)cells.Add(c);else cells.Remove(c);}}public string GetUniqueLoadID()=>Id;public void Delete()=>Map.areaManager.AllAreas.Remove(this);}
    internal class AreaManager {public Map Map;public List<Area> AllAreas=new();public RimWorld.Area_Home Home;public RimWorld.Area_BuildRoof BuildRoof;public RimWorld.Area_NoRoof NoRoof;public AreaManager(Map map){Map=map;Home=new(map,"Home");BuildRoof=new(map,"BuildRoof");NoRoof=new(map,"NoRoof");AllAreas.AddRange(new Area[]{Home,BuildRoof,NoRoof});}public bool CanMakeNewAllowed()=>AllAreas.OfType<RimWorld.Area_Allowed>().Count()<10;public bool TryMakeNewAllowed(out RimWorld.Area_Allowed area){if(!CanMakeNewAllowed()){area=null!;return false;}area=new(Map,"Allowed_"+AllAreas.Count);AllAreas.Add(area);return true;}}
    internal class Zone : ILoadReferenceable {private static int next;public string Id="Zone_"+(++next);public ZoneManager Manager;public Map Map=>Manager.Map;public List<IntVec3> Cells=new();public bool Hidden=false;public string RenamableLabel="Zone";public Zone(ZoneManager manager){Manager=manager;}public string GetUniqueLoadID()=>Id;public bool ContainsCell(IntVec3 c)=>Cells.Contains(c);public void AddCell(IntVec3 c){Cells.Add(c);Map.areaManager.Home[c]=true;Map.areaManager.Home[new(c.x+1,0,c.z)]=true;}public void RemoveCell(IntVec3 c){Cells.Remove(c);if(Cells.Count==0)Manager.AllZones.Remove(this);}public void Delete(bool sound){Cells.Clear();Manager.AllZones.Remove(this);}}
    internal class ZoneManager {public readonly Map Map;public List<Zone> AllZones=new();public ZoneManager(Map map){Map=map;}public Zone? ZoneAt(IntVec3 c)=>AllZones.SingleOrDefault(z=>z.Cells.Contains(c));public void RegisterZone(Zone z)=>AllZones.Add(z);}
    internal abstract class Designator {public abstract RimWorld.AcceptanceReport CanDesignateCell(IntVec3 c);}
}
namespace RimWorld {
    internal class Pawn_PlayerSettings {private Dictionary<Map,Area> allowedAreas=new();public void Set(Map map,Area area)=>allowedAreas[map]=area;public void Clear()=>allowedAreas.Clear();}
    internal class Area_Allowed:Area{public Area_Allowed(Map m,string id):base(m,id){}public void SetLabel(string label)=>Label=label;}
    internal class Area_Home:Area{public Area_Home(Map m,string id):base(m,id){}}
    internal class Area_BuildRoof:Area{public Area_BuildRoof(Map m,string id):base(m,id){}}
    internal class Area_NoRoof:Area{public Area_NoRoof(Map m,string id):base(m,id){}}
    internal static class ThingDefOf {public static readonly ThingDef Plant_Potato=new(){defName="Plant_Potato"},Plant_Toxipotato=new(){defName="Plant_Toxipotato"};}
    internal static class PollutionUtility {public static bool SettableEntirelyPolluted(Zone_Growing growing)=>false;}
    internal enum StorageSettingsPreset {DefaultStockpile}
    internal enum StoragePriority {Normal,Important}
    internal class StorageSettings {public StoragePriority Priority;public ThingFilter filter=new();}
    internal class StorageGroup {public string GetUniqueLoadID()=>"Group_1";}
    internal class SlotGroup {public StorageGroup? StorageGroup;}
    internal class Zone_Stockpile:Zone {private readonly StorageSettings settings=new();private readonly SlotGroup group=new();public Zone_Stockpile(StorageSettingsPreset preset,ZoneManager manager):base(manager){}public StorageSettings GetStoreSettings()=>settings;public SlotGroup GetSlotGroup()=>group;}
    internal class Zone_Growing:Zone {public bool allowSow=true,allowCut=true;private ThingDef? plantDefToGrow;public ThingDef? RawPlant=>plantDefToGrow;public Zone_Growing(ZoneManager manager):base(manager){}public ThingDef GetPlantDefToGrow()=>plantDefToGrow??=ThingDefOf.Plant_Potato;}
    internal class Designator_ZoneAddStockpile_Resources:Designator {public override AcceptanceReport CanDesignateCell(IntVec3 c)=>new(c.InBounds(Find.CurrentMap)&&!c.Fogged(Find.CurrentMap)&&!Find.CurrentMap.Rejected.Contains(c),"Native zone rejection");}
    internal class Designator_ZoneAdd_Growing:Designator_ZoneAddStockpile_Resources{}
    internal class Designator_AreaBuildRoof:Designator {public override AcceptanceReport CanDesignateCell(IntVec3 c)=>new(c.InBounds(Find.CurrentMap)&&!c.Fogged(Find.CurrentMap)&&!Find.CurrentMap.areaManager.BuildRoof[c],"Native roof rejection");}
    internal class Designator_AreaNoRoof:Designator {public override AcceptanceReport CanDesignateCell(IntVec3 c)=>new(c.InBounds(Find.CurrentMap)&&!c.Fogged(Find.CurrentMap)&&!Find.CurrentMap.ThickRoof.Contains(c)&&!Find.CurrentMap.areaManager.NoRoof[c],"Native thick roof rejection");}
    internal class Bill_Production {private static int next;private readonly string id="Bill_"+(++next);public string GetUniqueLoadID()=>id;public SlotGroup? Store=null,Include=null;public SlotGroup? GetSlotGroup()=>Store;public SlotGroup? GetIncludeSlotGroup()=>Include;}
    internal static class BillUtility {public static List<Bill_Production> ExtraBills=new();public static IEnumerable<Bill_Production> GlobalBills()=>Find.Maps.SelectMany(m=>m.listerBuildings.allBuildingsColonist).OfType<Building_WorkTable>().SelectMany(t=>t.BillStack.Bills).Concat(ExtraBills);}
    internal class BillStack {public List<Bill_Production> Bills=new();}
    internal class Building_WorkTable {public BillStack BillStack=new();}
}
namespace Neko.RimWorld {internal static class GameWorldReferences {public static Map ResolveMap(string? id)=>Find.Maps.Single(m=>m.GetUniqueLoadID()==id||id=="current"&&m==Find.CurrentMap);}}
