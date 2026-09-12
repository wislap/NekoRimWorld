using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal static class VerseResourceData
{
    public static bool DefinitionExists(string name, ReadContext context) { context.Check(); return DefDatabase<ThingDef>.GetNamedSilentFail(name) != null; }
    public static IEnumerable<ResourceItem> Items(string mapId, string scope, bool visibleOnly, string[]? definitions, ReadContext context)
    {
        var map = Find.Maps.FirstOrDefault(m => !m.Disposed && m.GetUniqueLoadID() == mapId) ?? throw ReadContext.Error("object_unavailable", "Map is no longer loaded");
        ResourceItem Record(ThingDef def, string? id, long count, Thing? instance = null) =>
            new() { Id = id, Definition = def.defName, Label = def.label ?? def.defName, Units = count,
                DefinitionNode = ThingData.Definition(def), InstanceNode = instance == null ? null : ThingData.Node(instance) };
        IEnumerable<ResourceItem> ConvertItems(IEnumerable<Thing> things, bool filterFog) {
            foreach (var thing in things) {
                context.Check();
                if (thing == null || thing.Destroyed || definitions != null && !definitions.Contains(thing.def.defName)) continue;
                if (filterFog && visibleOnly && thing.Position.Fogged(map)) continue;
                yield return Record(thing.def, thing.GetUniqueLoadID(), thing.stackCount, thing);
            }
        }
        if (scope == "ground") {
            var things = definitions == null ? map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).AsEnumerable() : definitions.SelectMany(n => map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed(n)));
            foreach (var row in ConvertItems(things.Where(t => t.Spawned && ThingRequestGroup.HaulableEver.Includes(t.def)), true)) yield return row;
        }
        else if (scope == "stored") {
            foreach (var group in map.haulDestinationManager.AllGroupsListForReading)
                foreach (var row in ConvertItems(group.HeldThings, false)) yield return row;
        }
        else if (scope == "cached") {
            foreach (var pair in map.resourceCounter.AllCountedAmounts) {
                context.Check();
                if (definitions == null || definitions.Contains(pair.Key.defName)) yield return Record(pair.Key, null, pair.Value);
            }
        }
        else foreach (var pawn in map.mapPawns.FreeColonistsSpawned) {
            context.Check();
            if (pawn == null || pawn.Destroyed) continue;
            IEnumerable<Thing> things;
            if (scope == "worn") things = pawn.apparel?.WornApparel?.Cast<Thing>() ?? throw ReadContext.Error("object_unavailable", "Apparel tracker unavailable");
            else if (scope == "equipment") things = pawn.equipment?.AllEquipmentListForReading?.Cast<Thing>() ?? throw ReadContext.Error("object_unavailable", "Equipment tracker unavailable");
            else if (scope == "carried") things = pawn.carryTracker == null ? throw ReadContext.Error("object_unavailable", "Carry tracker unavailable") : pawn.carryTracker.CarriedThing == null ? Array.Empty<Thing>() : new[] { pawn.carryTracker.CarriedThing };
            else things = pawn.inventory?.innerContainer?.Cast<Thing>() ?? throw ReadContext.Error("object_unavailable", "Inventory tracker unavailable");
            foreach (var row in ConvertItems(things, false)) yield return row;
        }
    }
}
