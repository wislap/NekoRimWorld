using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using RimWorld;
using Verse;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>Explicit native API reads, no reflection serialization or cross-query live handles.</summary>
internal static class ThingData
{
    public static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "Thing", "ThingReference", "ThingDef", "NativeMap", "IntVec3", "ThingHolder", "ThingOwner", "Pawn_EquipmentTracker", "Pawn_ApparelTracker", "Pawn_InventoryTracker", "Pawn_CarryTracker", "ThingCategoryDef", "IngestibleProperties", "CompProperties", "CompQuality", "CompRottable", "CompForbiddable", "CompIngredients", "UnsupportedThingComp", "NativeStat", "StorageSettings", "StorageParent", "ThingFilter", "SlotGroup", "HaulDestinationManager", "ResourceCounter", "ResourceCountEntry" };
    public static GameNode Node(Thing t, string kind = "Thing") => new(kind, t.GetUniqueLoadID(), new GameEntity(t.GetUniqueLoadID(), null, t), t);
    public static GameNode Definition(ThingDef def) => new("ThingDef", def.defName, new GameEntity("def:" + def.defName, null, def), def);
    public static GameNode MapNode(Map map) => new("NativeMap", map.GetUniqueLoadID(), new GameEntity(map.GetUniqueLoadID(), map.GetUniqueLoadID(), map), map);
    public static GameNode? Wrap(GameNode parent, string kind, string key, object? value) => value == null ? null : new GameNode(kind, parent.Key + "/" + key, parent.Owner, value);
    private static string TypeOf(object value) => value.GetType().FullName ?? value.GetType().Name;
    private static string Ref(GameNode node, ReadContext context) => context.NativeReference(node.Handle!);

    public static object? Root(string root, IReadOnlyDictionary<string, object?> args, ReadContext context)
    {
        string? Text(string key) => args.TryGetValue(key, out var value) ? value as string : null;
        var mapId = Text(root == "map" ? "id" : "mapId");
        var maps = Find.Maps.Where(x => x != null && !x.Disposed && (mapId == null || x.GetUniqueLoadID() == mapId)).ToArray();
        if (mapId != null && maps.Length == 0) throw ReadContext.Error("object_unavailable", "The selected map is not loaded.");
        if (root == "map") return MapNode(maps[0]);
        if (root == "thing_def")
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(Text("defName"));
            if (def == null) throw ReadContext.Error("unknown_definition", "ThingDef was not found.");
            return Definition(def);
        }
        if (root == "thing")
        {
            var id = Text("id"); var seen = new HashSet<Thing>(new IdentityComparer<Thing>()); var holders = new HashSet<IThingHolder>(new IdentityComparer<IThingHolder>());
            foreach (var map in maps)
            {
                foreach (var thing in map.listerThings.AllThings)
                {
                    context.Check();
                    if (!thing.Destroyed && thing.GetUniqueLoadID() == id) return Node(thing);
                }
                foreach (var thing in Descendants(map, context, holders, seen, 0))
                    if (thing.GetUniqueLoadID() == id) return Node(thing);
            }
            throw ReadContext.Error("object_unavailable", "Thing ID was not found in loaded maps and their holder trees. IDs can disappear after stack changes.");
        }
        if (root == "thing_defs")
        {
            var categoryName = Text("categoryDefName");
            var category = categoryName == null ? null : DefDatabase<ThingCategoryDef>.GetNamedSilentFail(categoryName);
            if (categoryName != null && category == null) throw ReadContext.Error("unknown_definition", "ThingCategoryDef was not found.");
            IEnumerable<GameNode> Definitions()
            {
                foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    context.Check();
                    if (category != null && !def.IsWithinCategory(category)) continue;
                    if (!Match(Text("name"), def.defName, def.label)) continue;
                    if (args.TryGetValue("isMedicine", out var medicine) && medicine is bool requiredMedicine && def.IsMedicine != requiredMedicine) continue;
                    yield return Definition(def);
                }
            }
            return ThingPaging.Page(Definitions(), args, context);
        }
        if (root != "things") throw ReadContext.Error("unknown_target", "Unsupported native target.");
        var groupName = Text("requestGroup") ?? "HaulableEver";
        if (groupName is not ("HaulableEver" or "HaulableAlways" or "HaulableEverOrMinifiable" or "Everything")) throw ReadContext.Error("invalid_arguments", "Unsupported ThingRequestGroup.");
        var names = Strings(args, "defNames");
        var defs = names?.Select(name => DefDatabase<ThingDef>.GetNamedSilentFail(name) ?? throw ReadContext.Error("unknown_definition", "Unknown ThingDef: " + name)).ToArray();
        var group = (ThingRequestGroup)Enum.Parse(typeof(ThingRequestGroup), groupName);
        var visible = !args.TryGetValue("visibleOnly", out var rawVisible) || rawVisible is not false;
        IEnumerable<GameNode> Candidates()
        {
            foreach (var map in maps)
            {
                var candidates = defs == null ? map.listerThings.ThingsInGroup(group).AsEnumerable() : defs.SelectMany(def => map.listerThings.ThingsOfDef(def));
                foreach (var thing in candidates)
                {
                    context.Check();
                    if (thing.Destroyed || !thing.Spawned || !group.Includes(thing.def)) continue;
                    if (visible && thing.Position.Fogged(map)) continue;
                    var search = Text("name");
                    if (search != null && !Match(search, thing.def.defName, thing.Label)) continue;
                    yield return Node(thing);
                }
            }
        }
        return ThingPaging.Page(Candidates(), args, context);
    }

    private static IEnumerable<Thing> Descendants(IThingHolder holder, ReadContext context, HashSet<IThingHolder> visited, HashSet<Thing> seen, int depth)
    {
        context.Check();
        if (!visited.Add(holder)) yield break;
        if (depth > 24 || visited.Count > 16384) throw ReadContext.Error("holder_scan_limit", "Holder lookup exceeded its scan limit; narrow the map.");
        var own = holder.GetDirectlyHeldThings();
        if (own != null) foreach (var thing in own)
        {
            context.Check();
            if (!thing.Destroyed && seen.Add(thing)) yield return thing;
        }
        var children = new List<IThingHolder>(); holder.GetChildHolders(children);
        foreach (var child in children)
            if (child != null) foreach (var thing in Descendants(child, context, visited, seen, depth + 1)) yield return thing;
    }
    private static bool Match(string? search, params string?[] values) => search == null || values.Any(value => value?.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
    internal static string[]? Strings(IReadOnlyDictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value == null) return null;
        if (value is not System.Collections.IEnumerable sequence || value is string) throw ReadContext.Error("invalid_arguments", name + " must be a list.");
        return sequence.Cast<object>().Select(x => x as string ?? throw ReadContext.Error("invalid_arguments", "Expected definition names.")).Distinct().ToArray();
    }

    public static object? Read(GameNode node, string field, IReadOnlyDictionary<string, object?> args, ReadContext context)
    {
        var h = node.Handle!;
        if (h is Thing destroyed && destroyed.Destroyed) throw ReadContext.Error("object_unavailable", "Thing no longer exists.");
        if (field == "runtimeType") return TypeOf(h);
        if (field == "ref") return Ref(node, context);
        if (node.Kind is "Thing" or "ThingReference")
        {
            var t = (Thing)h;
            return field switch
            {
                "id" => t.GetUniqueLoadID(), "def" => Definition(t.def), "Label" => t.Label, "stackCount" => t.stackCount,
                "Stuff" => t.Stuff == null ? null : Definition(t.Stuff), "HitPoints" => t.def.useHitPoints ? (int?)t.HitPoints : null,
                "MaxHitPoints" => t.def.useHitPoints ? (int?)t.MaxHitPoints : null, "Spawned" => t.Spawned, "Destroyed" => t.Destroyed,
                "Map" => t.Map == null ? null : MapNode(t.Map), "MapHeld" => t.MapHeld == null ? null : MapNode(t.MapHeld),
                "Position" => t.Spawned ? Wrap(node, "IntVec3", field, t.Position) : null,
                "PositionHeld" => t.SpawnedOrAnyParentSpawned ? Wrap(node, "IntVec3", field, t.PositionHeld) : null,
                "holdingOwner" => Wrap(node, "ThingOwner", field, t.holdingOwner), "ParentHolder" => Wrap(node, "ThingHolder", field, t.ParentHolder),
                "IngestibleNow" => t.IngestibleNow,
                "InnerThing" => t is MinifiedThing min && min.InnerThing != null ? Node(min.InnerThing, "ThingReference") : null,
                "InnerPawn" => t is Corpse corpse && corpse.InnerPawn != null ? Node(corpse.InnerPawn, "ThingReference") : null,
                "GetDirectlyHeldThings" => t is IThingHolder holder ? Wrap(node, "ThingOwner", field, holder.GetDirectlyHeldThings()) : null,
                "AllComps" => t is ThingWithComps wc ? Components(node, wc) : null,
                "stats" => Stats(node, t, args), _ => Unknown(node, field),
            };
        }
        if (node.Kind == "ThingDef")
        {
            var d = (ThingDef)h;
            return field switch {
                "defName" => d.defName, "label" => d.label, "thingClass" => d.thingClass?.FullName, "category" => d.category.ToString(),
                "stackLimit" => d.stackLimit, "alwaysHaulable" => d.alwaysHaulable, "designateHaulable" => d.designateHaulable,
                "EverHaulable" => d.EverHaulable, "IsMedicine" => d.IsMedicine, "IsWeapon" => d.IsWeapon, "IsIngestible" => d.IsIngestible, "CountAsResource" => d.CountAsResource,
                "thingCategories" => d.thingCategories?.Select((x,i) => Wrap(node, "ThingCategoryDef", "category" + i, x)!),
                "comps" => d.comps?.Select((x,i) => Wrap(node, "CompProperties", "compDef" + i, x)!),
                "ingestible" => Wrap(node, "IngestibleProperties", field, d.ingestible), _ => Unknown(node, field),
            };
        }
        if (node.Kind == "NativeMap")
        {
            var map = (Map)h; if (map.Disposed) throw ReadContext.Error("object_unavailable", "Map was unloaded.");
            return field switch { "id" => map.GetUniqueLoadID(), "haulDestinationManager" => Wrap(node, "HaulDestinationManager", field, map.haulDestinationManager), "resourceCounter" => Wrap(node, "ResourceCounter", field, map.resourceCounter), _ => Unknown(node, field) };
        }
        if (node.Kind == "IntVec3") { var pos = (IntVec3)h; return field switch { "x" => pos.x, "y" => pos.y, "z" => pos.z, _ => Unknown(node, field) }; }
        if (node.Kind is "ThingHolder" or "StorageParent") return field == "thingId" ? (h as Thing)?.GetUniqueLoadID() : Unknown(node, field);
        if (node.Kind == "ThingOwner") { var owner = (ThingOwner)h; return field switch { "Owner" => Wrap(node, "ThingHolder", field, owner.Owner), "Count" => owner.Count, "TotalStackCount" => owner.TotalStackCount, "items" => owner.Select(t => Node(t)), _ => Unknown(node, field) }; }
        if (node.Kind == "Pawn_EquipmentTracker") { var t = (Pawn_EquipmentTracker)h; return field switch { "Primary" => t.Primary == null ? null : Node(t.Primary, "ThingReference"), "AllEquipmentListForReading" => t.AllEquipmentListForReading.Select(x => Node(x)), _ => Unknown(node, field) }; }
        if (node.Kind == "Pawn_ApparelTracker") return field == "WornApparel" ? ((Pawn_ApparelTracker)h).WornApparel.Select(x => Node(x)) : Unknown(node, field);
        if (node.Kind == "Pawn_InventoryTracker") return field == "innerContainer" ? Wrap(node, "ThingOwner", field, ((Pawn_InventoryTracker)h).innerContainer) : Unknown(node, field);
        if (node.Kind == "Pawn_CarryTracker") { var c = (Pawn_CarryTracker)h; return field switch { "innerContainer" => Wrap(node, "ThingOwner", field, c.innerContainer), "CarriedThing" => c.CarriedThing == null ? null : Node(c.CarriedThing), _ => Unknown(node, field) }; }
        if (node.Kind == "ThingCategoryDef") { var c = (ThingCategoryDef)h; return field switch { "defName" => c.defName, "label" => c.label, "parent" => Wrap(node, "ThingCategoryDef", "parent", c.parent), _ => Unknown(node, field) }; }
        if (node.Kind == "CompProperties") return field == "compClass" ? ((CompProperties)h).compClass?.FullName : Unknown(node, field);
        if (node.Kind == "IngestibleProperties") { var i = (IngestibleProperties)h; return field switch { "foodType" => i.foodType.ToString(), "drugCategory" => i.drugCategory.ToString(), "HumanEdible" => i.HumanEdible, "IsMeal" => i.IsMeal, "CachedNutrition" => i.CachedNutrition, "maxNumToIngestAtOnce" => i.maxNumToIngestAtOnce, _ => Unknown(node, field) }; }
        if (h is ThingComp comp)
        {
            if (comp.parent.Destroyed) throw ReadContext.Error("object_unavailable", "Component's Thing no longer exists.");
            if (field == "supported") return node.Kind != "UnsupportedThingComp";
            if (field == "supportedType") return node.Kind == "UnsupportedThingComp" ? null : "RimWorld." + node.Kind;
            return (comp, field) switch {
                (CompQuality q, "Quality") => q.Quality.ToString(), (CompRottable r, "RotProgress") => r.RotProgress,
                (CompRottable r, "RotProgressPct") => r.RotProgressPct, (CompRottable r, "Stage") => r.Stage.ToString(),
                (CompRottable r, "TicksUntilRotAtCurrentTemp") => r.TicksUntilRotAtCurrentTemp,
                (CompForbiddable f, "Forbidden") => f.Forbidden,
                (CompIngredients i, "ingredients") => i.ingredients.Select(Definition), _ => Unknown(node, field),
            };
        }
        if (node.Kind == "NativeStat") { var d = (StatDef)h; var t = (Thing)node.Owner.Handle; return field switch { "defName" => d.defName, "label" => d.label, "value" => t.GetStatValue(d), "valueText" => d.ValueToString(Convert.ToSingle(context.ReadNode(node, "value", args))), _ => Unknown(node, field) }; }
        if (node.Kind == "HaulDestinationManager") return field == "AllGroupsListForReading" ? ((HaulDestinationManager)h).AllGroupsListForReading.Select((g,i) => Wrap(node, "SlotGroup", "slot" + i, g)!) : Unknown(node, field);
        if (node.Kind == "SlotGroup") { var g = (SlotGroup)h; return field switch { "GroupingLabel" => g.GroupingLabel, "HeldThingsCount" => g.HeldThingsCount, "parent" => Wrap(node, "StorageParent", field, g.parent), "HeldThings" => g.HeldThings.Select(t => Node(t)), "CellsList" => g.CellsList.Select((p,i) => Wrap(node, "IntVec3", "cell" + i, p)!), "Settings" => Wrap(node, "StorageSettings", field, g.Settings), _ => Unknown(node, field) }; }
        if (node.Kind == "StorageSettings") { var s = (StorageSettings)h; return field switch { "Priority" => s.Priority.ToString(), "filter" => Wrap(node, "ThingFilter", field, s.filter), _ => Unknown(node, field) }; }
        if (node.Kind == "ThingFilter") return field == "AllowedThingDefs" ? ((ThingFilter)h).AllowedThingDefs.Select(Definition) : Unknown(node, field);
        if (node.Kind == "ResourceCounter") { var r = (ResourceCounter)h; return field switch { "TotalHumanEdibleNutrition" => r.TotalHumanEdibleNutrition, "AllCountedAmounts" => r.AllCountedAmounts.OrderBy(x => x.Key.defName, StringComparer.Ordinal).Select((p,i) => Wrap(node, "ResourceCountEntry", "count" + i, p)!), _ => Unknown(node, field) }; }
        if (node.Kind == "ResourceCountEntry") { var pair = (KeyValuePair<ThingDef,int>)h; return field switch { "key" => Definition(pair.Key), "value" => pair.Value, _ => Unknown(node, field) }; }
        return Unknown(node, field);
    }
    private static IEnumerable<GameNode> Components(GameNode parent, ThingWithComps t)
    {
        for (var i = 0; i < t.AllComps.Count; i++)
        {
            var comp = t.AllComps[i];
            var kind = comp switch { CompQuality => "CompQuality", CompRottable => "CompRottable", CompForbiddable => "CompForbiddable", CompIngredients => "CompIngredients", _ => "UnsupportedThingComp" };
            yield return Wrap(parent, kind, "comps/" + i, comp)!;
        }
    }
    private static IEnumerable<GameNode> Stats(GameNode parent, Thing t, IReadOnlyDictionary<string, object?> args)
    {
        var names = Strings(args, "defNames");
        if (names == null || names.Length < 1 || names.Length > 16) throw ReadContext.Error("invalid_arguments", "stats requires 1..16 explicit definition names.");
        foreach (var name in names)
        {
            var stat = DefDatabase<StatDef>.GetNamedSilentFail(name) ?? throw ReadContext.Error("unknown_stat", "Unknown StatDef: " + name);
            yield return Wrap(parent, "NativeStat", "stat/" + name, stat)!;
        }
    }
    private sealed class IdentityComparer<T> : IEqualityComparer<T> where T : class
    {
        public bool Equals(T? a, T? b) => ReferenceEquals(a, b);
        public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
    }
    private static object Unknown(GameNode node, string field) => throw ReadContext.Error("unsupported_member", "No supported reader for " + node.Kind + "." + field + ".");
}
