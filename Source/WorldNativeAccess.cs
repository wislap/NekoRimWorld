using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;
namespace Neko.RimWorld;

// Version-audited backing fields avoid mutating lazy getters and random attack selection.
internal static class WorldNativeAccess
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly FieldInfo? Verbs = typeof(VerbTracker).GetField("verbs", Fields);
    private static readonly FieldInfo? EquipmentTracker = typeof(CompEquippable).GetField("verbTracker", Fields);
    private static readonly FieldInfo? Destinations = typeof(PawnDestinationReservationManager).GetField("reservedDestinations", Fields);
    private static readonly FieldInfo? Physical = typeof(PhysicalInteractionReservationManager).GetField("reservations", Fields);
    private static readonly FieldInfo? HostileTargets = typeof(AttackTargetsCache).GetField("targetsHostileToFaction",Fields);
    internal static IAttackTarget[] HostileTargetSnapshot(Map map,Faction faction)
    {
        GameImplementation.RequireKnownBinary();
        if(HostileTargets?.GetValue(map.attackTargetsCache) is not Dictionary<Faction,HashSet<IAttackTarget>> values)
            throw ReadContext.Error("unsupported_native_layout","Hostile attack-target cache layout unavailable");
        return values.TryGetValue(faction,out var targets)?targets.ToArray():Array.Empty<IAttackTarget>();
    }
    private static readonly Dictionary<(Type, string), bool> nativeMethods = new();
    internal static bool NativeMethod(object target, string name)
    {
        var key = (target.GetType(), name); if (nativeMethods.TryGetValue(key, out var cached)) return cached;
        var methods = key.Item1.GetMethods(Fields).Where(m => m.Name == name || m.Name.EndsWith("." + name, StringComparison.Ordinal)).ToArray();
        return nativeMethods[key] = methods.Length > 0 && methods.All(m => m.DeclaringType?.Assembly == typeof(Pawn).Assembly);
    }
    internal static void RequireNative(object target, params string[] names) { if (names.Any(n => !NativeMethod(target, n))) throw ReadContext.Error("unsupported_mod_override", "Unreviewed virtual implementation; no method was invoked"); }
    internal static List<Verb> ExistingAttacks(Thing t) => ExistingAttacks(t, out _);
    internal static List<Verb> ExistingAttacks(Thing t, out bool complete)
    {
        bool allRead = true;
        var values = new List<Verb>();
        void Add(VerbTracker? tracker) { if (tracker != null && Verbs?.GetValue(tracker) is List<Verb> verbs) values.AddRange(verbs); else allRead = false; }
        if (t is Pawn p) { Add(p.verbTracker); if (p.equipment != null) foreach (var thing in p.equipment.AllEquipmentListForReading) { var comp = thing.TryGetComp<CompEquippable>(); if (comp != null) Add(EquipmentTracker?.GetValue(comp) as VerbTracker); } }
        else if (t is Building_Turret turret && NativeMethod(t, "get_AttackVerb"))
        {
            // Turret gun trackers have been initialized when their gun was spawned.
            if (turret is Building_TurretGun gun) { var comp = gun.GunCompEq; Add(EquipmentTracker?.GetValue(comp) as VerbTracker); }
        }
        complete = allRead; return values.Distinct().Where(v => v != null).ToList();
    }
    internal static PawnDestinationReservationManager.PawnDestinationReservation[] DestinationRows(Map map) =>
        Destinations?.GetValue(map.pawnDestinationReservationManager) is Dictionary<Faction, PawnDestinationReservationManager.PawnDestinationSet> dict ? dict.Values.SelectMany(v => v.list).ToArray() : throw ReadContext.Error("unsupported_native_layout", "Destination reservation layout unavailable");
    internal static PhysicalInteractionReservationManager.PhysicalInteractionReservation[] PhysicalRows(Map map) =>
        Physical?.GetValue(map.physicalInteractionReservationManager) is List<PhysicalInteractionReservationManager.PhysicalInteractionReservation> rows ? rows.ToArray() : throw ReadContext.Error("unsupported_native_layout", "Physical reservation layout unavailable");
}
