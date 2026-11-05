using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal sealed class ConstructionIntent : IExposable
{
    internal string ElementId = "", PlanId = "", ElementJson = "", Stage = "blueprint", SourceId = "", PreviousObjectId = "";
    internal List<string>? Lineage = new();
    internal Map? Map; internal Thing? Target; internal bool TransitionKnown;
    public void ExposeData() {
        if (Scribe.mode == LoadSaveMode.Saving && Target?.Destroyed == true && Stage != "user_cancelled" && Stage != "model_cancelled" && Stage != "floor_complete") Stage = "destroyed";
        Scribe_Values.Look(ref ElementId, "elementId", ""); Scribe_Values.Look(ref PlanId, "planId", "");
        Scribe_Values.Look(ref ElementJson, "element", ""); Scribe_Values.Look(ref SourceId, "sourceId", "");
        Scribe_Values.Look(ref PreviousObjectId, "previousObjectId", ""); Scribe_Values.Look(ref Stage, "stage", "blueprint");
        Scribe_Values.Look(ref TransitionKnown, "transitionKnown"); Scribe_Collections.Look(ref Lineage, "lineage", LookMode.Value);
        Scribe_References.Look(ref Map, "map");
        // Unspawned originals remain in RAM for epoch-local undo but have no
        // save cross-reference. Their known cancellation/destruction persists.
        Thing? persistedTarget = Target;
        if (Scribe.mode == LoadSaveMode.Saving && (persistedTarget?.Destroyed == true || persistedTarget?.Spawned == false)) persistedTarget = null;
        Scribe_References.Look(ref persistedTarget, "target");
        if (Scribe.mode != LoadSaveMode.Saving) Target = persistedTarget;
    }
    internal JObject Evidence() {
        var e = JObject.Parse(ElementJson); var actual = Target;
        var floorDefinition = Stage == "floor_complete" ? DefDatabase<TerrainDef>.GetNamedSilentFail((string)e["definition"]!) : null;
        var floor = floorDefinition != null && Map != null && !Map.Disposed && ConstructionReadings.Position(e).InBounds(Map)
            ? ConstructionTerrainState.Observe(Map, ConstructionReadings.Position(e), floorDefinition) : null;
        var stage = Stage == "floor_complete" || Stage == "user_cancelled" || Stage == "model_cancelled" || Stage == "destroyed" ? Stage : actual == null ? "unknown" : actual.Destroyed ? "destroyed" : actual is Frame ? "frame" : actual is Blueprint ? "blueprint" : "built";
        if (actual?.Spawned == true && actual.Map != Map) stage = "unknown";
        var evidence = new JObject { ["status"] = "observed", ["plan_id"] = PlanId, ["element_id"] = ElementId, ["map_id"] = JsonValues.Text(Map?.GetUniqueLoadID()), ["element"] = e, ["source_id"] = SourceId, ["stage"] = stage,
            ["field_identity"] = new JObject { ["object"] = JsonValues.Text(Map?.GetUniqueLoadID()), ["field"] = "construction:" + ConstructionIntents.Key(e) },
            ["target"] = actual == null || actual.Destroyed || !actual.Spawned ? null : ConstructionReadings.Object(actual), ["previous_object_id"] = JsonValues.Text(string.IsNullOrEmpty(PreviousObjectId) ? null : PreviousObjectId), ["lineage"] = new JArray(Lineage ?? new List<string>()), ["provenance"] = TransitionKnown ? "native_transition" : "native_creation",
            ["floor_observed"] = floor?["matches"]?.DeepClone() ?? JValue.CreateNull(), ["floor_layer"] = floor?["layer"]?.DeepClone() ?? JValue.CreateNull(),
            ["floor_state"] = floor == null ? JValue.CreateNull() : floor };
        if ((string?)e["kind"] == "building" && ConstructionRotation.NativeDoor(DefDatabase<ThingDef>.GetNamedSilentFail((string)e["definition"]!)))
            evidence["rotation_policy"] = "native_door";
        evidence["cancel_guard"] = evidence.DeepClone();
        if (Map != null && (stage == "destroyed" || stage == "user_cancelled" || stage == "model_cancelled")) { try { evidence["restart_guard"] = new JObject { ["intent"] = evidence["cancel_guard"]!.DeepClone(), ["site"] = ConstructionReadings.SiteState(Map, e) }; } catch (Exception ex) when (ReadContext.Recoverable(ex)) { evidence["restart_guard"] = JValue.CreateNull(); } }
        return evidence;
    }
}
internal sealed class ConstructionIntents
{
    private List<ConstructionIntent>? entries = new();
    internal void ExposeData() { Scribe_Collections.Look(ref entries, "nekoConstructionIntents", LookMode.Deep); if (Scribe.mode == LoadSaveMode.PostLoadInit) entries ??= new(); }
    internal static string Key(JObject element) => GameQueryEngine.Hash((string)element["plan_id"]! + "\0" + (string)element["element_id"]!);
    internal ConstructionIntent? Find(Map map, string key) => entries!.SingleOrDefault(e => e.Map == map && Key(JObject.Parse(e.ElementJson)) == key);
    internal ConstructionIntent Register(Map map, JObject element, Thing target) { if (Find(map, Key(element)) != null) throw new InvalidOperationException("Construction element already exists."); var entry = new ConstructionIntent { Map = map, PlanId = (string)element["plan_id"]!, ElementId = (string)element["element_id"]!, ElementJson = element.ToString(Newtonsoft.Json.Formatting.None), SourceId = target.GetUniqueLoadID(), Target = target, Lineage = new List<string> { target.GetUniqueLoadID() } }; entries!.Add(entry); return entry; }
    internal void Remove(ConstructionIntent entry) => entries!.Remove(entry);
    internal void CancelledByDesignator(Thing original) { if (!original.Destroyed) return; foreach (var e in entries!.Where(e => ReferenceEquals(e.Target, original))) e.Stage = "user_cancelled"; }
    internal void Restore(ConstructionIntent entry) { if (!entries!.Contains(entry)) entries.Add(entry); }
    internal void Transition(Thing from, Thing to) { foreach (var e in entries!.Where(e => ReferenceEquals(e.Target, from))) { e.PreviousObjectId = from.GetUniqueLoadID(); e.Lineage ??= new(); e.Lineage.Add(to.GetUniqueLoadID()); e.Target = to; e.Stage = to is Frame ? "frame" : to is Blueprint ? "blueprint" : "built"; e.TransitionKnown = true; } }
    internal void TerrainCompleted(Frame from, Map map, IntVec3 cell, TerrainDef terrain) { foreach (var e in entries!.Where(e => ReferenceEquals(e.Target, from) && e.Map == map)) { var p = JObject.Parse(e.ElementJson); if (ConstructionReadings.Position(p) == cell && (string?)p["definition"] == terrain.defName) { e.PreviousObjectId = from.GetUniqueLoadID(); e.Target = null; e.Stage = "floor_complete"; e.TransitionKnown = true; } } }
    internal JObject Read(JObject args) { var map = GameWorldReferences.ResolveMap((string?)args["map"]); var ids = ColonyReadContract.Texts(args, "ids"); var plan = (string?)args["plan_id"]; var selected = entries!.Where(e => e.Map == map && (plan == null || e.PlanId == plan) && (ids.Length == 0 || ids.Contains(e.ElementId, StringComparer.Ordinal))).OrderBy(e => e.PlanId, StringComparer.Ordinal).ThenBy(e => e.ElementId, StringComparer.Ordinal).ToArray(); if (plan == null && ids.Length > 0 && selected.GroupBy(e => e.ElementId).Any(g => g.Count() > 1)) throw new ArgumentException("Element selector is ambiguous; provide plan_id."); int offset = (int?)args["offset"] ?? 0, limit = (int?)args["limit"] ?? 20; var rows = selected.Skip(offset).Take(limit).Select(e => e.Evidence()).ToArray(); return new JObject { ["format"] = "construction_intents.v1", ["map_id"] = map.GetUniqueLoadID(), ["objects"] = new JArray(rows), ["total"] = selected.Length, ["complete"] = offset == 0 && rows.Length == selected.Length, ["missing_ids"] = new JArray(ids.Where(id => !selected.Any(e => e.ElementId == id))) }; }
}

internal static class ConstructionRotation
{
    // Blueprint_Door.DrawAt and Building_Door.DoorPreDraw recalculate the
    // rotation of a single-cell door from its surroundings on the audited game.
    internal static bool NativeDoor(BuildableDef? definition) => definition is ThingDef door
        && door.thingClass == typeof(Building_Door) && door.blueprintDef?.thingClass == typeof(Blueprint_Door)
        && !door.rotatable && door.Size.x == 1 && door.Size.z == 1;

    internal static bool Matches(Thing from, Thing to) => from.Rotation == to.Rotation
        || NativeDoor(from.def.entityDefToBuild)
        && (from.GetType() == typeof(Blueprint_Door) || from.GetType() == typeof(Frame))
        && (to.GetType() == typeof(Frame) || to.GetType() == typeof(Building_Door))
        && OccupiesAnchor(from) && OccupiesAnchor(to);

    private static bool OccupiesAnchor(Thing thing) {
        var bounds = thing.OccupiedRect();
        return bounds.minX == thing.Position.x && bounds.maxX == thing.Position.x
            && bounds.minZ == thing.Position.z && bounds.maxZ == thing.Position.z;
    }
}

// These hooks transfer identity from the exact objects passed through the native
// construction lifecycle. Position/definition matching never invents ownership.
[HarmonyPatch(typeof(Blueprint), "TryReplaceWithSolidThing")]
internal static class ConstructionFrameTransition
{
    private static bool Prepare() => NativeEventHookSupport.KnownBinary();
    private static void Prefix(Blueprint __instance, out Map? __state) => __state = __instance.Map;
    internal static void Postfix(Blueprint __instance, bool __result, Thing __1, Map? __state) {
        if (__result && __1 != null && __1.Spawned && __1.Map == __state && __1.Position == __instance.Position
            && (__1 is Frame && __1.def.entityDefToBuild == __instance.def.entityDefToBuild || __1 is Building && __1.def == __instance.def.entityDefToBuild)
            && __1.Stuff == __instance.EntityToBuildStuff() && ConstructionRotation.Matches(__instance, __1))
            Current.Game?.GetComponent<NekoGameComponent>()?.ConstructionIntents.Transition(__instance, __1);
    }
}
// A frame ends in one of two native ways, and each spawns its successor inside this scope:
// completion spawns the building, failure spawns a fresh blueprint.
[HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
[HarmonyPatch(typeof(Frame), nameof(Frame.FailConstruction))]
internal static class ConstructionCompletionScope
{
    [ThreadStatic] internal static Frame? Completing;
    [ThreadStatic] internal static Map? CompletingMap;
    private static bool Prepare() => NativeEventHookSupport.KnownBinary();
    private static void Prefix(Frame __instance, out Tuple<Frame?, Map?> __state) { __state = Tuple.Create(Completing, CompletingMap); Completing = __instance; CompletingMap = __instance.Map; }
    private static Exception? Finalizer(Exception? __exception, Tuple<Frame?, Map?> __state) { Completing = __state.Item1; CompletingMap = __state.Item2; return __exception; }
}
[HarmonyPatch(typeof(GenSpawn), nameof(GenSpawn.Spawn), new Type[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool) })]
internal static class ConstructionBuildingTransition
{
    private static bool Prepare() => NativeEventHookSupport.KnownBinary();
    internal static void Postfix(Thing __0, Thing __result) {
        var from = ConstructionCompletionScope.Completing;
        if (from?.Destroyed == true && __result != null && ReferenceEquals(__0, __result) && __result.Spawned
            && __result.Map == ConstructionCompletionScope.CompletingMap && __result.Position == from.Position
            && (__result is Building ? __result.def == from.def.entityDefToBuild && __result.Stuff == from.Stuff
                : __result is Blueprint_Build blueprint && blueprint.def == from.def.entityDefToBuild?.blueprintDef && blueprint.EntityToBuildStuff() == from.Stuff)
            && ConstructionRotation.Matches(from, __result))
            Current.Game?.GetComponent<NekoGameComponent>()?.ConstructionIntents.Transition(from, __result);
    }
}
[HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain), new Type[] { typeof(IntVec3), typeof(TerrainDef) })]
internal static class ConstructionFloorTransition
{
    private static bool Prepare() => NativeEventHookSupport.KnownBinary();
    private static void Postfix(TerrainGrid __instance, IntVec3 c, TerrainDef newTerr) => ConstructionTerrainCompletion.Record(__instance, c, newTerr, "top");
}
[HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetFoundation), new Type[] { typeof(IntVec3), typeof(TerrainDef) })]
internal static class ConstructionFoundationTransition
{
    private static bool Prepare() => NativeEventHookSupport.KnownBinary();
    private static void Postfix(TerrainGrid __instance, IntVec3 c, TerrainDef newTerr) => ConstructionTerrainCompletion.Record(__instance, c, newTerr, "foundation");
}
[HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTempTerrain), new Type[] { typeof(IntVec3), typeof(TerrainDef) })]
internal static class ConstructionTemporaryTerrainTransition
{
    private static bool Prepare() => NativeEventHookSupport.KnownBinary();
    private static void Postfix(TerrainGrid __instance, IntVec3 c, TerrainDef newTerr) => ConstructionTerrainCompletion.Record(__instance, c, newTerr, "temporary");
}

[HarmonyPatch(typeof(Designator_Cancel), nameof(Designator_Cancel.DesignateThing))]
internal static class ConstructionUserCancellation
{
    private static bool Prepare() => NativeEventHookSupport.KnownBinary();
    private static void Postfix(Thing t) { if (t is Blueprint || t is Frame) Current.Game?.GetComponent<NekoGameComponent>()?.ConstructionIntents.CancelledByDesignator(t); }
}
