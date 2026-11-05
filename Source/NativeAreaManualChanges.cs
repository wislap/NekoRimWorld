using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

// Provenance comes from audited native UI commands, not a changed mask.
// Automatic Home/roof setters and bridge transactions have no manual scope.
internal static class NativeAreaManualChanges
{
    [ThreadStatic] private static string? source;
    [ThreadStatic] private static int modelWrites;
    internal static bool Complete => NekoRimWorldMod.ManualAreaSourcesReady && Current.Game?.GetComponent<NekoGameComponent>()?.ManualAreas.DataValid == true;
    internal static string? Source => modelWrites == 0 && Complete ? source : null;
    internal static string? Enter(string command) { var before = source; if (modelWrites == 0) source = command; return before; }
    internal static void Exit(string? before) => source = before;
    internal static T WithinModelWrite<T>(Func<T> write) { modelWrites++; try { return write(); } finally { modelWrites--; } }
    internal static string? ManualId(Area area, IntVec3 cell) => Current.Game?.GetComponent<NekoGameComponent>()?.ManualAreas.Registry.ManualId(area, cell);
    internal static JArray Ids(Area area) => Current.Game?.GetComponent<NekoGameComponent>()?.ManualAreas.Registry.Ids(area) ?? new JArray();
    internal static string Revision(Area area) => Current.Game?.GetComponent<NekoGameComponent>()?.ManualAreas.Registry.Revision(area) ?? "unavailable";
    internal static void RequireAvailable() { if (!Complete) throw new InvalidOperationException("Native manual area provenance is unavailable; guarded area writing is disabled."); }
    internal static bool Interested(Area area, IntVec3 cell) => Current.Game?.GetComponent<NekoGameComponent>()?.ManualAreas.EnsureInterest(area, cell) == true;
    internal static void Observe(Area area, IntVec3 cell, bool before, string? command) {
        if (command == null || Source == null || !cell.InBounds(area.Map) || !Interested(area, cell)) return;
        bool after = area[cell]; if (before == after) return;
        Current.Game?.GetComponent<NekoGameComponent>()?.ManualAreas.ObserveChange(area, cell, before, after, command);
    }
    internal static (IntVec3 Cell, bool Value)[] CaptureTracked(Area area) {
        var component = Current.Game?.GetComponent<NekoGameComponent>(); if (Source == null || component == null) return Array.Empty<(IntVec3, bool)>();
        foreach (var row in component.ManualAreas.TrackedFields(area.GetUniqueLoadID())) {
            var parts = ((string)row["field"]!).Split(':');
            if (parts.Length != 3 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int z)) continue;
            var cell = new IntVec3(x, 0, z); if (cell.InBounds(area.Map)) component.ManualAreas.Registry.EnsureTracked(area, cell);
        }
        return component.ManualAreas.Registry.Watched(area);
    }
}

internal static class NativeAreaManualHookSupport
{
    internal static readonly string[] Designators = { "RimWorld.Designator_AreaAllowedExpand", "RimWorld.Designator_AreaAllowedClear", "RimWorld.Designator_AreaBuildRoof", "RimWorld.Designator_AreaHome", "RimWorld.Designator_AreaIgnoreRoof", "RimWorld.Designator_AreaNoRoof" };
    internal static IEnumerable<MethodBase> Commands() {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var name in Designators) { var type = typeof(Area).Assembly.GetType(name); var method = type?.GetMethod("DesignateSingleCell", flags, null, new[] { typeof(IntVec3) }, null); if (method != null) yield return method; }
        var dialog = typeof(Area).Assembly.GetType("RimWorld.Dialog_ManageAreas"); var row = dialog?.GetMethod("DoAreaRow", flags); if (row != null) yield return row;
    }
    private static bool Owned(MethodBase? method, Type patch, bool finalizer) {
        if (method == null) return false; var info = Harmony.GetPatchInfo(method); if (info == null) return false;
        bool Member(Patch p) => p.owner == NekoRimWorldMod.PackageId && p.PatchMethod.DeclaringType == patch;
        return info.Prefixes.Any(Member) && (finalizer ? info.Finalizers.Any(Member) : info.Postfixes.Any(Member)) && info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).All(p => p.owner == NekoRimWorldMod.PackageId);
    }
    internal static bool Ready() => NativeEventHookSupport.KnownBinary() && Commands().Count() == Designators.Length + 1 && Commands().All(m => Owned(m, typeof(NativeAreaManualCommand), true))
        && Owned(AccessTools.DeclaredMethod(typeof(Area), "Set", new[] { typeof(IntVec3), typeof(bool) }), typeof(NativeAreaManualCell), false)
        && new[] { "Clear", "Invert" }.All(name => Owned(AccessTools.DeclaredMethod(typeof(Area), name), typeof(NativeAreaManualBulk), false));
    internal static JObject Describe() => new() { ["capability"] = "colony_write.manual_area.v1", ["available"] = NativeAreaManualChanges.Complete,
        ["tracking_scope"] = "explicitly_observed_or_committed_area_cells", ["source"] = "user_designator", ["commands"] = new JArray(Commands().Select(m => m.DeclaringType!.FullName + "." + m.Name)),
        ["bulk_commands"] = new JArray("Area.Invert and Area.Clear only inside the listed native UI scopes"), ["persistence"] = "save-scoped manual registry plus tracked ledger.manual_source", ["generation"] = "nullable manual_id per cell; opaque manual_revision for whole-mask CAS", ["ordering"] = "origin_load_epoch plus per-cell manual_order; global manual_id deduplication" };
}

[HarmonyPatch]
internal static class NativeAreaManualCommand
{
    private static bool Prepare() => NativeEventHookSupport.KnownBinary();
    private static IEnumerable<MethodBase> TargetMethods() => NativeAreaManualHookSupport.Commands();
    private static void Prefix(MethodBase __originalMethod, out string? __state) => __state = NativeAreaManualChanges.Enter(__originalMethod.DeclaringType!.FullName + "." + __originalMethod.Name);
    private static Exception? Finalizer(Exception? __exception, string? __state) { NativeAreaManualChanges.Exit(__state); return __exception; }
}

[HarmonyPatch(typeof(Area), "Set", new Type[] { typeof(IntVec3), typeof(bool) })]
internal static class NativeAreaManualCell
{
    private static bool Prepare() => NativeEventHookSupport.KnownBinary();
    private static void Prefix(Area __instance, IntVec3 __0, out Tuple<string?, bool> __state) { var command = NativeAreaManualChanges.Source; __state = Tuple.Create(command, command != null && __0.InBounds(__instance.Map) && NativeAreaManualChanges.Interested(__instance, __0) && __instance[__0]); }
    private static void Postfix(Area __instance, IntVec3 __0, Tuple<string?, bool> __state) => NativeAreaManualChanges.Observe(__instance, __0, __state.Item2, __state.Item1);
}

[HarmonyPatch]
internal static class NativeAreaManualBulk
{
    private static bool Prepare() => NativeEventHookSupport.KnownBinary();
    private static IEnumerable<MethodBase> TargetMethods() => new[] { AccessTools.DeclaredMethod(typeof(Area), "Clear"), AccessTools.DeclaredMethod(typeof(Area), "Invert") };
    private static void Prefix(Area __instance, MethodBase __originalMethod, out Tuple<string?, (IntVec3 Cell, bool Value)[], bool> __state) {
        var command = NativeAreaManualChanges.Source; var selected = NativeAreaManualChanges.CaptureTracked(__instance);
        bool changed = command != null && Current.Game?.GetComponent<NekoGameComponent>()?.ManualAreas.Registry.HasArea(__instance) == true && (__originalMethod.Name == "Invert" || __instance.ActiveCells.Any());
        __state = Tuple.Create(command, selected, changed);
    }
    private static void Postfix(Area __instance, Tuple<string?, (IntVec3 Cell, bool Value)[], bool> __state) { if (__state.Item3) Current.Game?.GetComponent<NekoGameComponent>()?.ManualAreas.Bump(__instance); foreach (var before in __state.Item2) NativeAreaManualChanges.Observe(__instance, before.Cell, before.Value, __state.Item1); }
}
