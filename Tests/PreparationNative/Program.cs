using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
namespace Neko.RimWorld;
internal static class Program {
    private static int assertions;
    private static void Check(bool yes,string why){assertions++;if(!yes)throw new Exception(why);}
    private static void Reject(Action action,string why){assertions++;try{action();}catch(ArgumentException){return;}throw new Exception("Expected rejection: "+why);}
    private static void Apply(IPreparedWriteField field,bool expected,bool desired){field.Validate(new JValue(desired));if(!JToken.DeepEquals(field.Read(),new JValue(expected)))throw new Exception("CAS");field.Write(new JValue(desired));Check(JToken.DeepEquals(field.Read(),new JValue(desired)),"Native designation readback");}
    private static void Main(){
        var map=new Map();Find.Maps.Add(map);var resolver=new VerseWriteFieldResolver();var building=new Building("Building_1",map,10);var rock=new Mineable("Rock_1",map,20);var plant=new Plant("Plant_1",map,30);
        Check(ConstructionPreparation.Read(building).Properties().All(p=>p.Value.Type==JTokenType.Boolean&&!(bool)p.Value),"Read exposes exact false states");
        var decon=resolver.ResolveTest("deconstruct",building);Apply(decon,false,true);Check(!building.Destroyed,"Designation does not complete deconstruction");var original=map.designationManager.DesignationOn(building,DesignationDefOf.Deconstruct);Apply(decon,true,false);Check(map.designationManager.All.Count==0,"Cancel removes requested kind");decon.Restore(new JValue(true));Check(ReferenceEquals(map.designationManager.DesignationOn(building,DesignationDefOf.Deconstruct),original),"Undo cancellation restores original native designation instance");Apply(decon,true,false);
        building.WorkToBuild=0;Reject(()=>decon.Validate(new JValue(true)),"Zero-work native deconstruction would instantly destroy");Check(!building.Destroyed,"Unsafe native path was never invoked");building.WorkToBuild=10;DebugSettings.godMode=true;Reject(()=>decon.Validate(new JValue(true)),"God-mode deconstruction rejected");DebugSettings.godMode=false;
        var frame=new Frame("Frame_1",map,11);Reject(()=>resolver.ResolveTest("deconstruct",frame).Validate(new JValue(true)),"Frames use instant native destroy path");var blueprint=new Blueprint("Blueprint_1",map,12);Reject(()=>resolver.ResolveTest("deconstruct",blueprint).Validate(new JValue(true)),"Blueprints are outside preparation path");
        building.ForbiddenComp.Forbidden=true;Reject(()=>decon.Validate(new JValue(true)),"Native AddDesignation must not silently clear a player's forbidden flag");Check(building.ForbiddenComp.Forbidden,"Forbidden original preserved");building.ForbiddenComp.Forbidden=false;
        building.NativeRejected=true;Reject(()=>decon.Validate(new JValue(true)),"Native CanDesignateThing must accept");building.NativeRejected=false;
        var mine=resolver.ResolveTest("mine",rock);Apply(mine,false,true);Check(map.designationManager.DesignationAt(rock.Position,DesignationDefOf.Mine)!=null,"Mining is native cell-based");Check((bool)ConstructionPreparation.Read(rock)["mine"]!,"Read associates cell designation with original mineable");var loose=new Thing("Item",map,20);Check(!(bool)ConstructionPreparation.Read(loose)["mine"]!,"Another thing on the same cell does not own mining designation");
        var other=new Designation(rock.Position,DesignationDefOf.SmoothWall);map.designationManager.AddDesignation(other);Apply(mine,true,false);Check(map.designationManager.All.Contains(other),"Cancelling mine preserves smooth-wall original");Reject(()=>mine.Validate(new JValue(true)),"Mining may not implicitly cancel smooth wall");map.designationManager.RemoveDesignation(other);
        DebugSettings.godMode=true;Reject(()=>mine.Validate(new JValue(true)),"God-mode mining instant destroy path rejected");DebugSettings.godMode=false;
        var newRock=new Mineable("Rock_2",map,20);rock.Spawned=false;Reject(()=>mine.Validate(new JValue(true)),"Replaced ore cannot inherit old preparation operation");Check(!(bool)ConstructionPreparation.Read(newRock)["mine"]!,"Replacement did not receive old designation");rock.Spawned=true;newRock.Spawned=false;
        var cut=resolver.ResolveTest("cut_plant",plant);var harvest=new Designation(plant,DesignationDefOf.HarvestPlant);map.designationManager.AddDesignation(harvest);Reject(()=>cut.Validate(new JValue(true)),"Cut must not swallow native harvest designation");Check(map.designationManager.All.Contains(harvest),"Harvest designation original preserved");map.designationManager.RemoveDesignation(harvest);Apply(cut,false,true);map.designationManager.AddDesignation(harvest);Apply(cut,true,false);Check(map.designationManager.All.Contains(harvest),"Cancelling cut removes only CutPlant");
        map.Fog.Add(plant.Position);Reject(()=>cut.Validate(new JValue(true)),"Fogged target refused");map.Fog.Clear();plant.def.Official=false;Reject(()=>cut.Validate(new JValue(true)),"Unsupported custom definition refused");plant.def.Official=true;
        Reject(()=>resolver.ResolveTest("mine",rock,"current"),"Canonical map ID required");var second=new Map();Find.Maps.Add(second);var foreign=new Mineable("ForeignRock",second,40);Reject(()=>resolver.ResolveTest("mine",foreign).Validate(new JValue(true)),"Foreign map cannot borrow current-map native qualifications");
        Check(resolver.ResolvePreparationLedger(rock.Id,"designation:mine",rock)!.NativeTarget==rock,"Ledger recovers original native target");Check(resolver.ResolvePreparationLedger(rock.Id,"designation:unsupported",rock)==null,"Unsupported ledger fields refused");Check(VerseWriteFieldResolver.Specs().Length==3,"Three preparation operations registered");
        Console.WriteLine($"PreparationNative: {assertions} assertions passed.");
    }
}
