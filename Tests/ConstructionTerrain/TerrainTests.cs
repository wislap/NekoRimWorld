using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Neko.RimWorld;
using Verse;
using RimWorld;

internal static class TerrainTests
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    internal static void Main()
    {
        var bridge = new TerrainDef { defName = "Bridge", isFoundation = true };
        var floor = new TerrainDef { defName = "WoodFloor" };
        var temporary = new TerrainDef { defName = "TempFloor", temporary = true };
        var map = new Map(); var cell = new IntVec3(3, 0, 4);
        map.terrainGrid.Foundation = bridge; map.terrainGrid.Top = floor; map.terrainGrid.Temporary = temporary;
        Check((bool)ConstructionTerrainState.Observe(map, cell, bridge)["matches"]!, "foundation remains observed underneath another floor and temporary layer");
        Check((bool)ConstructionTerrainState.Observe(map, cell, floor)["matches"]!, "permanent floor uses top layer underneath temporary terrain");
        Check((bool)ConstructionTerrainState.Observe(map, cell, temporary)["matches"]!, "temporary floor uses temporary layer");
        var layers = ConstructionTerrainState.Layers(map, cell);
        Check((string?)layers["foundation"] == "Bridge" && (string?)layers["top"] == "WoodFloor" && (string?)layers["temporary"] == "TempFloor", "all terrain layers retain their own identities");
        map.terrainGrid.Foundation = null; map.terrainGrid.Top = bridge;
        Check(!(bool)ConstructionTerrainState.Observe(map, cell, bridge)["matches"]!, "visible matching name cannot substitute for missing native foundation");
        Check(ConstructionTerrainState.Layers(map, cell)["foundation"]!.Type == JTokenType.Null, "missing native terrain layer is JSON null");
        map.terrainGrid.Foundation = bridge; map.terrainGrid.Top = floor;
        var frame = new Frame { Position = cell, def = new ThingDef { entityDefToBuild = bridge } };
        ConstructionCompletionScope.Completing = frame; ConstructionCompletionScope.CompletingMap = map;
        Current.Game = new Game(); var registry = Current.Game.Component.ConstructionIntents;
        var bridgeIntent = registry.Register(map, Element(bridge.defName, "bridge", "floor"), frame);
        var unproven = registry.Register(map, Element(bridge.defName, "another", "floor"), new Frame { Position = cell, def = frame.def });
        var temporaryFrame = new Frame { Position = cell, def = new ThingDef { entityDefToBuild = temporary } };
        var temporaryIntent = registry.Register(map, Element(temporary.defName, "temporary", "floor"), temporaryFrame);
        var floorFrame = new Frame { Position = cell, def = new ThingDef { entityDefToBuild = floor } };
        var floorIntent = registry.Register(map, Element(floor.defName, "floor", "floor"), floorFrame);
        ConstructionTerrainCompletion.Record(map.terrainGrid, cell, bridge, "foundation");
        Check(bridgeIntent.Stage == "floor_complete" && bridgeIntent.PreviousObjectId == frame.GetUniqueLoadID(), "exact native completion scope transfers frame ownership");
        Check(unproven.Target != null, "an independently registered same-cell frame is never used as the predecessor");
        ConstructionCompletionScope.Completing = temporaryFrame;
        void Pending(string message) => Check(ReferenceEquals(temporaryIntent.Target, temporaryFrame) && !temporaryIntent.TransitionKnown, message);
        ConstructionTerrainCompletion.Record(new TerrainGrid { Temporary = temporary }, cell, temporary, "temporary");
        Pending("another terrain grid cannot claim current completion");
        ConstructionTerrainCompletion.Record(map.terrainGrid, new IntVec3(4, 0, 4), temporary, "temporary");
        Pending("another cell cannot claim current completion");
        ConstructionTerrainCompletion.Record(map.terrainGrid, cell, temporary, "top");
        Pending("wrong setter layer cannot claim current completion");
        var otherTemporary = new TerrainDef { defName = temporary.defName, temporary = true };
        ConstructionTerrainCompletion.Record(map.terrainGrid, cell, otherTemporary, "temporary");
        Pending("definition-name equality cannot replace exact native definition identity");
        map.terrainGrid.Temporary = null; ConstructionTerrainCompletion.Record(map.terrainGrid, cell, temporary, "temporary");
        Pending("setter must actually leave the intended definition in the intended native layer");
        map.terrainGrid.Temporary = temporary; ConstructionCompletionScope.Completing = null; ConstructionTerrainCompletion.Record(map.terrainGrid, cell, temporary, "temporary");
        Pending("terrain writes outside Frame.CompleteConstruction do not invent ownership");
        ConstructionCompletionScope.Completing = temporaryFrame; map.Disposed = true; ConstructionTerrainCompletion.Record(map.terrainGrid, cell, temporary, "temporary");
        Pending("disposed map cannot claim current completion");
        map.Disposed = false;
        ConstructionTerrainCompletion.Record(map.terrainGrid, cell, temporary, "temporary");
        Check(temporaryIntent.Stage == "floor_complete", "temporary native completion is tracked");
        ConstructionCompletionScope.Completing = floorFrame; ConstructionTerrainCompletion.Record(map.terrainGrid, cell, floor, "top");
        Check(floorIntent.Stage == "floor_complete", "ordinary native completion remains tracked");
        DoorLineage(registry, map, cell);
        FailedConstruction(registry, map, cell);
        Console.WriteLine("Construction terrain and native door lineage checks passed: " + checks);
    }

    private static void FailedConstruction(ConstructionIntents registry, Map map, IntVec3 cell)
    {
        var wood = new ThingDef { defName = "WoodLog" };
        var wall = new ThingDef { defName = "FailWall", thingClass = typeof(Building) };
        var blueprintDef = new ThingDef { entityDefToBuild = wall, thingClass = typeof(Blueprint_Build) };
        wall.blueprintDef = blueprintDef;
        var frame = new Frame { def = new ThingDef { entityDefToBuild = wall }, Stuff = wood, Map = map, Position = cell, Destroyed = true };
        var intent = registry.Register(map, Element(wall.defName, "failed"), frame);
        ConstructionCompletionScope.Completing = frame; ConstructionCompletionScope.CompletingMap = map;
        var otherMaterial = new Blueprint_Build { def = blueprintDef, Material = null, Map = map, Position = cell };
        ConstructionBuildingTransition.Postfix(otherMaterial, otherMaterial);
        Check(ReferenceEquals(intent.Target, frame), "a replacement blueprint with another material cannot claim a failed frame");
        var plain = new Blueprint { def = blueprintDef, Material = wood, Map = map, Position = cell };
        ConstructionBuildingTransition.Postfix(plain, plain);
        Check(ReferenceEquals(intent.Target, frame), "a non-build blueprint cannot claim a failed frame");
        var replacement = new Blueprint_Build { def = blueprintDef, Material = wood, Map = map, Position = cell };
        ConstructionBuildingTransition.Postfix(replacement, replacement);
        Check(ReferenceEquals(intent.Target, replacement) && intent.Stage == "blueprint" && intent.PreviousObjectId == frame.GetUniqueLoadID() && intent.Lineage!.Count == 2, "a failed frame hands its element to the replacement blueprint");
        Check((string?)intent.Evidence()["stage"] == "blueprint", "the element is reported as a live blueprint, not destroyed");
        ConstructionCompletionScope.Completing = null; ConstructionCompletionScope.CompletingMap = null;
    }

    private static JObject Element(string definition, string id, string kind = "building") => new() {
        ["plan_id"] = "house", ["element_id"] = id, ["kind"] = kind, ["definition"] = definition,
        ["rotation"] = 0, ["x"] = 3, ["z"] = 4
    };

    private static void DoorLineage(ConstructionIntents registry, Map map, IntVec3 cell)
    {
        var wood = new ThingDef { defName = "WoodLog" };
        var door = new ThingDef { defName = "Door", thingClass = typeof(Building_Door) };
        var blueprintDef = new ThingDef { entityDefToBuild = door, thingClass = typeof(Blueprint_Door) };
        door.blueprintDef = blueprintDef;
        DefDatabase<ThingDef>.ByName.Add(door.defName, door);
        var blueprint = new Blueprint_Door { def = blueprintDef, Material = wood, Rotation = 1, Map = map, Position = cell };
        var intent = registry.Register(map, Element(door.defName, "door"), blueprint);
        var evidence = intent.Evidence();
        Check((string?)evidence["rotation_policy"] == "native_door" && (int)evidence["element"]!["rotation"]! == 0 && (int)evidence["target"]!["rotation"]! == 1,
            "door evidence preserves both declared and actual rotation");
        Check((string?)evidence["cancel_guard"]!["rotation_policy"] == "native_door", "the door policy is part of the original guard");
        var unrelated = new Blueprint_Door { def = blueprintDef, Material = wood, Rotation = 1, Map = map, Position = cell };
        var frame = new Frame { def = new ThingDef { entityDefToBuild = door }, Stuff = wood, Rotation = 0, Map = map, Position = cell };
        ConstructionFrameTransition.Postfix(unrelated, true, frame, map);
        Check(ReferenceEquals(intent.Target, blueprint), "an unrelated same-cell blueprint cannot claim the lineage");
        ConstructionFrameTransition.Postfix(blueprint, true, frame, new Map());
        Check(ReferenceEquals(intent.Target, blueprint), "a wrong-map frame cannot claim the lineage");
        frame.Stuff = null;
        ConstructionFrameTransition.Postfix(blueprint, true, frame, map);
        Check(ReferenceEquals(intent.Target, blueprint), "a wrong-material frame cannot claim the lineage");
        frame.Stuff = wood; frame.def.Size = (2, 1);
        ConstructionFrameTransition.Postfix(blueprint, true, frame, map);
        Check(ReferenceEquals(intent.Target, blueprint), "an altered footprint cannot use the door rotation exception");
        frame.def.Size = (1, 1);
        ConstructionFrameTransition.Postfix(blueprint, true, frame, map);
        Check(ReferenceEquals(intent.Target, frame) && intent.PreviousObjectId == blueprint.GetUniqueLoadID(), "native door frame retains exact blueprint identity despite rotation change");

        var built = new Building_Door { def = door, Stuff = wood, Rotation = 1, Map = map, Position = cell };
        frame.Destroyed = true;
        ConstructionCompletionScope.Completing = frame; ConstructionCompletionScope.CompletingMap = map;
        ConstructionBuildingTransition.Postfix(new Building_Door(), built);
        Check(ReferenceEquals(intent.Target, frame), "a different spawn argument cannot claim the completion result");
        built.Position = new IntVec3(4, 0, 4);
        ConstructionBuildingTransition.Postfix(built, built);
        Check(ReferenceEquals(intent.Target, frame), "a different position cannot claim door completion");
        built.Position = cell;
        ConstructionBuildingTransition.Postfix(built, built);
        Check(ReferenceEquals(intent.Target, built) && intent.PreviousObjectId == frame.GetUniqueLoadID() && intent.Lineage!.Count == 3, "native door completion retains exact frame and all three IDs");
        built.Rotation = 2;
        evidence = intent.Evidence();
        Check((string?)evidence["rotation_policy"] == "native_door" && (int)evidence["target"]!["rotation"]! == 2 && (int)evidence["element"]!["rotation"]! == 0,
            "subsequent native draw rotation changes only the actual evidence");

        var wall = new ThingDef { defName = "Wall", thingClass = typeof(Building) };
        var wallBlueprint = new Blueprint { def = new ThingDef { entityDefToBuild = wall }, Material = wood, Rotation = 1, Map = map, Position = cell };
        var wallIntent = registry.Register(map, Element(wall.defName, "wall"), wallBlueprint);
        var wallFrame = new Frame { def = new ThingDef { entityDefToBuild = wall }, Stuff = wood, Rotation = 0, Map = map, Position = cell };
        ConstructionFrameTransition.Postfix(wallBlueprint, true, wallFrame, map);
        Check(ReferenceEquals(wallIntent.Target, wallBlueprint) && wallIntent.Evidence()["rotation_policy"] == null, "ordinary constructions keep exact rotations and do not emit the door marker");
        wallFrame.Rotation = 1;
        ConstructionFrameTransition.Postfix(wallBlueprint, true, wallFrame, map);
        Check(ReferenceEquals(wallIntent.Target, wallFrame), "ordinary equal-rotation transition still works");
        door.Size = (2, 1);
        Check(!ConstructionRotation.NativeDoor(door), "multicell doors do not gain the single-cell policy");
    }
}

namespace Neko.RimWorld {
    internal sealed class NekoGameComponent { internal ConstructionIntents ConstructionIntents = new(); }
    internal static class NativeEventHookSupport { internal static bool KnownBinary() => true; }
    internal static class GameQueryEngine { internal static string Hash(string value) => value; }
    internal static class GameWorldReferences { internal static Map ResolveMap(string? id) => throw new NotSupportedException(); }
    internal static class ColonyReadContract { internal static string[] Texts(JObject args, string name) => Array.Empty<string>(); }
    internal static class ReadContext { internal static bool Recoverable(Exception ex) => true; }
    internal static class ConstructionReadings {
        internal static IntVec3 Position(JObject element) => new((int)element["x"]!, 0, (int)element["z"]!);
        internal static JObject Object(Thing thing) => new() { ["id"] = thing.GetUniqueLoadID(), ["rotation"] = thing.Rotation };
        internal static JObject SiteState(Map map, JObject element) => new();
    }
}
namespace Verse {
    public class BuildableDef { public string defName = ""; public ThingDef? blueprintDef; }
    public class TerrainDef : BuildableDef { public bool isFoundation,temporary; }
    public class TerrainGrid {
        public TerrainDef? Top,Foundation,Temporary;
        public TerrainDef? TopTerrainAt(IntVec3 c)=>Top; public TerrainDef? FoundationAt(IntVec3 c)=>Foundation; public TerrainDef? TempTerrainAt(IntVec3 c)=>Temporary;
        public void SetTerrain(IntVec3 c,TerrainDef d)=>Top=d; public void SetFoundation(IntVec3 c,TerrainDef d)=>Foundation=d; public void SetTempTerrain(IntVec3 c,TerrainDef d)=>Temporary=d;
    }
    public class Map { public bool Disposed; public TerrainGrid terrainGrid = new(); public string GetUniqueLoadID()=>"Map"; }
    public struct IntVec3 : IEquatable<IntVec3> { public int x,z; public IntVec3(int x,int y,int z) {this.x=x;this.z=z;} public bool InBounds(Map map)=>x>=0&&z>=0&&x<100&&z<100; public bool Equals(IntVec3 other)=>x==other.x&&z==other.z; public override bool Equals(object? other)=>other is IntVec3 cell&&Equals(cell); public override int GetHashCode()=>HashCode.Combine(x,z); public static bool operator ==(IntVec3 a,IntVec3 b)=>a.Equals(b); public static bool operator !=(IntVec3 a,IntVec3 b)=>!a.Equals(b); }
    public class ThingDef : BuildableDef {
        public BuildableDef? entityDefToBuild;
        public Type thingClass=typeof(Thing);
        public bool rotatable;
        public (int x,int z) Size=(1,1);
    }
    public class Thing {
        private static int next;
        private readonly int id=++next;
        public IntVec3 Position;
        public ThingDef def=new();
        public ThingDef? Stuff;
        public Map? Map;
        public bool Destroyed;
        public bool Spawned => Map != null && !Destroyed;
        public int Rotation;
        public string GetUniqueLoadID()=>"Thing_"+id;
        public (int minX,int maxX,int minZ,int maxZ) OccupiedRect() => (Position.x,Position.x+def.Size.x-1,Position.z,Position.z+def.Size.z-1);
    }
    public class Building : Thing { }
    public struct Rot4 { }
    public enum WipeMode { Vanish }
    public static class GenSpawn { public static Thing Spawn(Thing thing,IntVec3 cell,Map map,Rot4 rot,WipeMode wipe,bool respawn,bool forbid) => thing; }
    public interface IExposable { void ExposeData(); }
    public enum LoadSaveMode { Inactive,Saving,PostLoadInit }
    public enum LookMode { Value,Deep }
    public static class Scribe { public static LoadSaveMode mode; }
    public static class Scribe_Values { public static void Look<T>(ref T value,string key,T defaultValue=default!) { } }
    public static class Scribe_Collections { public static void Look<T>(ref List<T>? value,string key,LookMode mode) { } }
    public static class Scribe_References { public static void Look<T>(ref T? value,string key) where T : class { } }
    public static class DefDatabase<T> where T : BuildableDef {
        internal static readonly Dictionary<string,T> ByName=new();
        public static T? GetNamedSilentFail(string name)=>ByName.GetValueOrDefault(name);
    }
    public class Game { internal NekoGameComponent Component = new(); public T GetComponent<T>() where T : class => (Component as T)!; }
    public static class Current { public static Game? Game; }
}
namespace RimWorld {
    public class Blueprint : Thing { public ThingDef? Material; public ThingDef? EntityToBuildStuff() => Material; }
    public class Blueprint_Door : Blueprint { }
    public class Blueprint_Build : Blueprint { }
    public class Frame : Thing { public void CompleteConstruction() { } public void FailConstruction() { } }
    public class Building_Door : Building { }
    public class Designator_Cancel { public void DesignateThing(Thing t) { } }
}
namespace HarmonyLib {
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type type,string method,params Type[] arguments) { } }
}
