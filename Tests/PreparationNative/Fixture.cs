using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
namespace Verse {
    internal readonly struct IntVec3:IEquatable<IntVec3> {public readonly int x,y,z;public IntVec3(int x,int y,int z){this.x=x;this.y=y;this.z=z;}public bool Equals(IntVec3 c)=>x==c.x&&z==c.z;public override bool Equals(object? o)=>o is IntVec3 c&&Equals(c);public override int GetHashCode()=>HashCode.Combine(x,z);public static bool operator ==(IntVec3 a,IntVec3 b)=>a.Equals(b);public static bool operator !=(IntVec3 a,IntVec3 b)=>!a.Equals(b);}
    internal class CompForbiddable {public bool Forbidden;}
    internal class ThingDef {public bool mineable,Official=true;}
    internal class Thing {public string Id;public bool Destroyed=false,Spawned=true,NativeRejected=false;public Map Map;public IntVec3 Position;public ThingDef def=new();public CompForbiddable ForbiddenComp=new();public T? TryGetComp<T>()where T:class=>ForbiddenComp as T;public float WorkToBuild=10;public Thing(string id,Map map,int x){Id=id;Map=map;Position=new(x,0,10);map.listerThings.AllThings.Add(this);}public string GetUniqueLoadID()=>Id;}
    internal class Building:Thing {public Building(string id,Map map,int x):base(id,map,x){}}
    internal class Blueprint:Building {public Blueprint(string id,Map map,int x):base(id,map,x){}}
    internal class Frame:Building {public Frame(string id,Map map,int x):base(id,map,x){}}
    internal class Plant:Thing {public Plant(string id,Map map,int x):base(id,map,x){}}
    internal class Mineable:Building {public Mineable(string id,Map map,int x):base(id,map,x){def.mineable=true;}}
    internal class Map {private static int next;public string Id="Map_"+(++next);public string GetUniqueLoadID()=>Id;public bool Disposed=false;public HashSet<IntVec3> Fog=new();public ListerThings listerThings=new();public DesignationManager designationManager=new();}
    internal class ListerThings {public List<Thing> AllThings=new();}
    internal static class Find {public static List<Map> Maps=new();public static Map CurrentMap=>Maps[0];}
    internal static class DebugSettings {public static bool godMode;}
    internal static class GridExtensions {public static bool InBounds(this IntVec3 c,Map map)=>c.x>=0&&c.x<100;public static bool Fogged(this IntVec3 c,Map map)=>map.Fog.Contains(c);public static Mineable? GetFirstMineable(this IntVec3 c,Map map)=>map.listerThings.AllThings.OfType<Mineable>().FirstOrDefault(t=>t.Position==c&&!t.Destroyed&&t.Spawned);}
    internal class DesignationDef {public readonly string Name;public DesignationDef(string name){Name=name;}}
    internal class Designation {public Thing? thing;public IntVec3 cell;public DesignationDef def;public Designation(Thing thing,DesignationDef def){this.thing=thing;this.def=def;cell=thing.Position;}public Designation(IntVec3 cell,DesignationDef def){this.cell=cell;this.def=def;}}
    internal class DesignationManager {public List<Designation> All=new();public Designation? DesignationOn(Thing t,DesignationDef def)=>All.SingleOrDefault(d=>ReferenceEquals(d.thing,t)&&d.def==def);public Designation? DesignationAt(IntVec3 c,DesignationDef def)=>All.SingleOrDefault(d=>d.thing==null&&d.cell==c&&d.def==def);public IEnumerable<Designation> AllDesignationsOn(Thing t)=>All.Where(d=>ReferenceEquals(d.thing,t));public void AddDesignation(Designation d){if(d.thing!=null)d.thing.ForbiddenComp.Forbidden=false;All.Add(d);}public void RemoveDesignation(Designation d)=>All.Remove(d);}
    internal abstract class Designator {public abstract RimWorld.AcceptanceReport CanDesignateThing(Thing t);public abstract void DesignateThing(Thing t);}
}
namespace RimWorld {
    internal readonly struct AcceptanceReport {public bool Accepted{get;}public string Reason=>"Native rejected";public AcceptanceReport(bool accepted){Accepted=accepted;}}
    internal static class DesignationDefOf {public static readonly Verse.DesignationDef Deconstruct=new("Deconstruct"),Mine=new("Mine"),CutPlant=new("CutPlant"),SmoothWall=new("SmoothWall"),HarvestPlant=new("HarvestPlant");}
    internal static class StatDefOf {public static readonly object WorkToBuild=new();}
    internal static class Stats {public static float GetStatValue(this Verse.Thing t,object def)=>t.WorkToBuild;}
    internal class Designator_Deconstruct:Verse.Designator {public override AcceptanceReport CanDesignateThing(Verse.Thing t)=>new(t is Verse.Building&&!t.NativeRejected);public override void DesignateThing(Verse.Thing t){if(Verse.DebugSettings.godMode||t is Verse.Frame||t.WorkToBuild==0){t.Destroyed=true;return;}t.Map.designationManager.AddDesignation(new(t,DesignationDefOf.Deconstruct));}}
    internal class Designator_Mine:Verse.Designator {public override AcceptanceReport CanDesignateThing(Verse.Thing t)=>new(t.def.mineable&&!t.NativeRejected);public override void DesignateThing(Verse.Thing t){var m=t.Map.designationManager;var smooth=m.DesignationAt(t.Position,DesignationDefOf.SmoothWall);if(smooth!=null)m.RemoveDesignation(smooth);if(Verse.DebugSettings.godMode){t.Destroyed=true;return;}m.AddDesignation(new(t.Position,DesignationDefOf.Mine));}}
    internal class Designator_PlantsCut:Verse.Designator {public override AcceptanceReport CanDesignateThing(Verse.Thing t)=>new(t is Verse.Plant&&!t.NativeRejected);public override void DesignateThing(Verse.Thing t){foreach(var d in t.Map.designationManager.AllDesignationsOn(t).ToArray())t.Map.designationManager.RemoveDesignation(d);t.Map.designationManager.AddDesignation(new(t,DesignationDefOf.CutPlant));}}
}
namespace Neko.RimWorld {
    internal interface IPreparedWriteField {string ObjectId{get;}string Field{get;}bool IsDestroyed{get;}bool MayChangeByGame{get;}object? NativeTarget{get;}JToken Read();void Validate(JToken value);void Write(JToken value);void Restore(JToken value);}
    internal class ResolvedWriteField {internal ResolvedWriteField(IPreparedWriteField f,JToken expected,JToken value){}}
    internal static class GameWorldReferences {public static Verse.Map ResolveMap(string? id)=>Verse.Find.Maps.Single(m=>id==m.Id||id=="current"&&m==Verse.Find.CurrentMap);}
    internal sealed partial class VerseWriteFieldResolver {
        private sealed class Registration {internal JObject Spec;internal Registration(JObject spec,Func<VerseWriteFieldResolver,JObject,IReadOnlyList<ResolvedWriteField>> resolve){Spec=spec;}}
        private static IReadOnlyList<ResolvedWriteField> One(IPreparedWriteField field,JObject operation)=>new[]{new ResolvedWriteField(field,operation["expect"]!,operation["value"]!)};
        private static bool Official(Verse.ThingDef def)=>def.Official;
        internal IPreparedWriteField ResolveTest(string kind,Verse.Thing thing,string? map=null)=>ResolvePreparation(new JObject{["op"]="set_"+kind,["thing"]=thing.Id,["map"]=map??thing.Map.Id},kind);
        internal static JObject[] Specs()=>PreparationCatalog.Select(r=>r.Spec).ToArray();
    }
}
