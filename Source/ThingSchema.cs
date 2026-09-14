using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GraphQL;
using GraphQL.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal interface IThingData
{
    object? NativeRoot(string root, IReadOnlyDictionary<string, object?> arguments, ReadContext context);
    object? NativeField(GameNode node, string field, IReadOnlyDictionary<string, object?> arguments, ReadContext context);
}

internal sealed class ThingPage
{
    public List<GameNode> Nodes = new();
    public bool HasNextPage;
    public string? EndCursor;
}

/// <summary>Explicit native member projections. Only references and collection windows are transport wrappers.</summary>
internal sealed class ThingSchema
{
    public ObjectGraphType Thing { get; } = Type("Thing", "Verse.Thing native members. id/runtimeType are transport metadata; subtype members are nullable when inapplicable. References preserve identity; not a claim of ownership or usability.");
    public ObjectGraphType Def { get; } = Type("ThingDef", "Verse.ThingDef definition, not an instance. Native category, content category tree and query groups are distinct.");
    public ObjectGraphType Equipment { get; } = Type("Pawn_EquipmentTracker", "Verse.Pawn_EquipmentTracker; equipped things, not inventory or hauling.");
    public ObjectGraphType Apparel { get; } = Type("Pawn_ApparelTracker", "RimWorld.Pawn_ApparelTracker; worn apparel.");
    public ObjectGraphType Inventory { get; } = Type("Pawn_InventoryTracker", "Verse.Pawn_InventoryTracker; carried inventory, not the active hauling container.");
    public ObjectGraphType Carry { get; } = Type("Pawn_CarryTracker", "Verse.Pawn_CarryTracker; active carried thing and its container.");
    public ObjectGraphType Map { get; } = Type("NativeMap", "Native map reference with explicitly selected storage and resource-counter members.");
    public List<ObjectGraphType> ComponentTypes { get; } = new();
    private readonly ObjectGraphType defPage;
    private readonly ObjectGraphType thingPage;

    public ThingSchema()
    {
        var reference = Type("ThingReference", "Detached Thing identity. Resolve id through thing; no recursive object expansion.");
        Scalars(reference, "s:id s:runtimeType"); Link(reference, "def", Def);
        var holder = Type("ThingHolder", "IThingHolder reference. ref is observation-scoped; runtimeType does not imply Pawn. Thing id is present only for Thing holders.");
        Scalars(holder, "s:ref s:runtimeType s:thingId");
        Link(holder,"parent",holder,"Native IThingHolder.ParentHolder. Follow this relation to identify containing pawns or root holders; refs remain observation-local.",referenceOnly:true);
        var owner = Type("ThingOwner", "Verse.ThingOwner native holder and contents. items is a bounded enumeration wrapper.");
        Link(owner, "Owner", holder, referenceOnly: true); Scalars(owner, "i:Count i:TotalStackCount"); Slice(owner, "items", Thing);
        var position = Type("IntVec3", "Verse.IntVec3 integer coordinates; map identity is read separately.");
        Scalars(position, "i:x i:y i:z");
        Scalars(Thing, "s:id s:runtimeType s:Label i:stackCount i:HitPoints i:MaxHitPoints b:Spawned b:Destroyed b:IngestibleNow");
        Link(Thing, "def", Def); Link(Thing, "Stuff", Def);
        Link(Thing, "Map", Map, referenceOnly: true); Link(Thing, "MapHeld", Map, referenceOnly: true);
        Link(Thing, "Position", position); Link(Thing, "PositionHeld", position);
        Link(Thing, "holdingOwner", owner, referenceOnly: true); Link(Thing, "ParentHolder", holder, referenceOnly: true);
        Link(Thing, "InnerThing", reference, "RimWorld.MinifiedThing.InnerThing; null for other runtime types. Outer and inner identity are not interchangeable.");
        Link(Thing, "InnerPawn", reference, "Verse.Corpse.InnerPawn; reference only, null for other runtime types.");
        Link(Thing, "GetDirectlyHeldThings", owner, "IThingHolder.GetDirectlyHeldThings for supported holders. Unsupported holders produce an issue, not an empty container.");
        Thing.GetField("GetDirectlyHeldThings")!.Metadata["public_hidden"] = true;
        PawnSchema.Contents(Thing,"contents","contents",Thing,new[] { "GetDirectlyHeldThings","items" });
        Thing.GetField("contents")!.Metadata["explicit_selection"] = true;
        Link(Equipment, "Primary", reference); Slice(Equipment, "AllEquipmentListForReading", Thing);
        Slice(Apparel, "WornApparel", Thing); Link(Inventory, "innerContainer", owner);
        Link(Carry, "innerContainer", owner); Link(Carry, "CarriedThing", Thing);

        var category = Type("ThingCategoryDef", "Verse.ThingCategoryDef reference and parent; not the ThingCategory enum.");
        Scalars(category, "s:defName s:label"); Link(category, "parent", category, referenceOnly: true);
        var ingestible = Type("IngestibleProperties", "RimWorld.IngestibleProperties definition data; HumanEdible is not a pawn-specific permission.");
        Scalars(ingestible, "e:foodType:RimWorld.FoodTypeFlags e:drugCategory:RimWorld.DrugCategory b:HumanEdible b:IsMeal f:CachedNutrition i:maxNumToIngestAtOnce");
        var compDef = Type("CompProperties", "Verse.CompProperties metadata. compClass names the component class; runtimeType names its properties class. Unknown fields are not reflected.");
        Scalars(compDef, "s:runtimeType s:compClass");
        Scalars(Def, "s:defName s:label s:thingClass e:category:Verse.ThingCategory i:stackLimit b:alwaysHaulable b:designateHaulable b:EverHaulable b:IsMedicine b:IsWeapon b:IsIngestible b:CountAsResource");
        Def.GetField("category")!.Description = "Native Verse.ThingCategory enum rendered as a string: the engine's broad thing kind, not a ThingCategoryDef content category. Do not invent enum values from user-facing category labels; inspect definition values and thing_categories for their respective meanings.";
        Def.GetField("IsWeapon")!.Description = "Native attack-capable Item with verbs/tools, excluding apparel. Includes improvised weapons; not an exclusive content category.";
        Def.GetField("CountAsResource")!.Description = "Native resourceReadoutPriority != Uncounted. Readout classification, not all possessions or all useful items.";
        Def.GetField("IsIngestible")!.Description = "Native ingestible properties exist; includes plants and non-human food. Not a human-food inventory category. Combine with native Item category when counting food items.";
        Def.GetField("IsMedicine")!.Description = "Native definition contains a MedicalPotency base stat; not current treatment eligibility.";
        Def.GetField("EverHaulable")!.Description = "Native alwaysHaulable || designateHaulable; not current pawn-specific reachability, reservation or permission.";
        Slice(Def, "thingCategories", category); Slice(Def, "comps", compDef); Link(Def, "ingestible", ingestible);
        Def.GetField("thingCategories")!.Description = "Native ThingDef.thingCategories content-category memberships. Each ThingCategoryDef has its own defName, label and parent ancestry; these names are not values of ThingDef.category or capability flags.";
        var stat = Type("NativeStat", "Explicit game-computed StatDef value for a Thing; valueText is game formatted. This is not a raw instance field.");
        Scalars(stat, "s:defName s:label f:value s:valueText");
        stat.GetField("value")!.Description = "Derived read via Thing.GetStatValue(selected StatDef); not a native stored scalar.";
        stat.GetField("valueText")!.Description = "Derived display via StatDef.ValueToString for the same requested stat value.";
        Slice(Thing, "stats", stat, new QueryArguments(GameArguments.Create("defNames", new NonNullGraphType(new ListGraphType(new NonNullGraphType(new StringGraphType()))), "Explicit StatDef names; no automatic relevance selection.", new JObject { ["minItems"] = 1, ["maxItems"] = 16 })), 16);

        var comp = new InterfaceGraphType { Name = "ThingComp", Description = "One actual component instance. runtimeType preserves Mod subtype, supportedType names the supported base projection. ref is scoped to this observation. UnsupportedThingComp reports discovery only." };
        foreach (var name in new[] { "ref", "runtimeType", "supportedType" })
            comp.AddField(new FieldType { Name = name, ResolvedType = new StringGraphType() });
        comp.AddField(new FieldType { Name = "supported", ResolvedType = new BooleanGraphType() });
        var compTypes = new Dictionary<string, ObjectGraphType>();
        foreach (var name in new[] { "CompQuality", "CompRottable", "CompForbiddable", "CompIngredients", "UnsupportedThingComp" })
        {
            var type = Type(name, name == "UnsupportedThingComp" ? "Unknown component; no custom getters are invoked." : "Supported native component members. Derived runtime classes preserve runtimeType and use this base projection.");
            Scalars(type, "s:ref s:runtimeType s:supportedType b:supported");
            type.AddResolvedInterface(comp);
            compTypes[name] = type;
            ComponentTypes.Add(type);
            comp.AddPossibleType(type);
        }
        comp.ResolveType = value => compTypes[((GameNode)value).Kind];
        Scalars(compTypes["CompQuality"], "e:Quality:RimWorld.QualityCategory");
        Scalars(compTypes["CompRottable"], "f:RotProgress f:RotProgressPct e:Stage:RimWorld.RotStage i:TicksUntilRotAtCurrentTemp");
        Scalars(compTypes["CompForbiddable"], "b:Forbidden");
        Slice(compTypes["CompIngredients"], "ingredients", Def);
        Slice(Thing, "AllComps", comp);

        var settings = Type("StorageSettings", "Native settings: AllowedToAccept and filter are acceptance policy, not actual contents.");
        var filter = Type("ThingFilter", "Native ThingFilter; AllowedThingDefs enumerates definition acceptance only, not instance quality/HP acceptance.");
        Slice(filter, "AllowedThingDefs", Def); Scalars(settings, "e:Priority:RimWorld.StoragePriority"); Link(settings, "filter", filter);
        var storageParent = Type("StorageParent", "Native ISlotGroupParent reference (zone or storage building); not necessarily an IThingHolder.");
        Scalars(storageParent, "s:ref s:runtimeType s:thingId");
        var slot = Type("SlotGroup", "Native SlotGroup: HeldThings reads storage cells; these things remain map things. ref is observation-scoped.");
        Link(Thing,"StorageGroup",slot,"Membership in a storage cell for an EverStorable spawned Thing. Overlaps map membership; not acceptance by current storage settings. Null for held/non-storable objects.",referenceOnly:true);
        Scalars(slot, "s:ref s:GroupingLabel i:HeldThingsCount"); Link(slot, "parent", storageParent, referenceOnly: true);
        Slice(slot, "CellsList", position); Slice(slot, "HeldThings", Thing); Link(slot, "Settings", settings);
        var destinations = Type("HaulDestinationManager", "Native map storage groups, not arbitrary ThingOwner containers.");
        Slice(destinations, "AllGroupsListForReading", slot);
        var count = Type("ResourceCountEntry", "Dictionary entry wrapper from ResourceCounter.AllCountedAmounts; key and value retain native meaning.");
        Link(count, "key", Def); Scalars(count, "i:value");
        var counter = Type("ResourceCounter", "Game's cached stored-resource counts; not all map things or held inventory. Game filters and update cadence apply. Read never forces an update.");
        Slice(counter, "AllCountedAmounts", count); Scalars(counter, "f:TotalHumanEdibleNutrition");
        Scalars(Map, "s:id s:name b:isCurrent b:isPlayerHome i:colonistCount b:hasVisibleThreat"); Link(Map, "haulDestinationManager", destinations); Link(Map, "resourceCounter", counter);
        Map.GetField("id")!.ResolvedType = new NonNullGraphType(new IdGraphType());
        foreach (var name in new[] { "name","isCurrent","isPlayerHome","colonistCount","hasVisibleThreat" })
            Map.GetField(name)!.Description = "Game-derived map summary, using the same reader and meaning as Map." + name + ".";
        thingPage = Page("ThingPage", Thing); defPage = Page("ThingDefPage", Def);
    }

    public void AddRoots(ObjectGraphType root)
    {
        Root(root, "thing", Thing, new QueryArguments(Text("id", true), Text("mapId")));
        Root(root, "things", thingPage, new QueryArguments(Text("mapId", true),
            GameArguments.Create("requestGroup", new StringGraphType(), "Native query group: HaulableEver, HaulableAlways, HaulableEverOrMinifiable or Everything. This is not pawn-specific usability.", new JObject { ["enum"] = new JArray("HaulableEver", "HaulableAlways", "HaulableEverOrMinifiable", "Everything") }, "HaulableEver"),
            GameArguments.Ids("defNames"), Text("name"), GameArguments.Create("visibleOnly", new BooleanGraphType(), "Default true: exclude fogged map things.", defaultValue: true), First(), Text("after")));
        Root(root, "thing_def", Def, new QueryArguments(Text("defName", true)));
        Root(root, "thing_defs", defPage, new QueryArguments(Text("name"), Text("categoryDefName"),
            First(), Text("after")));
        Root(root, "map", Map, new QueryArguments(Text("id", true)));
    }
    private static QueryArgument Text(string name, bool required = false) => GameArguments.Create(name,
        required ? new NonNullGraphType(new StringGraphType()) : new StringGraphType(), "Exact identifier, or case-insensitive substring for name. after is a load/filter-bound cursor.", new JObject { ["minLength"] = 1, ["maxLength"] = name == "after" ? 1024 : 128 });
    private static QueryArgument First() => GameArguments.Create("first", new IntGraphType(), "Returned page size 1..50. Identity scanning is separately bounded by the game-thread deadline.", new JObject { ["minimum"] = 1, ["maximum"] = 50 }, 20);
    private static void Root(ObjectGraphType root, string name, ObjectGraphType type, QueryArguments args)
    {
        var field = PawnSchema.Add(root, name, type, c => State(c).NativeRoot(name, Args(c)), args);
        if (type.Name.EndsWith("Page", StringComparison.Ordinal)) { field.Metadata["cursor_page"] = true; field.Metadata["collection_element"] = ((ListGraphType)type.GetField("nodes")!.ResolvedType!).ResolvedType!; field.Metadata["list_default"] = 20; field.Metadata["list_max"] = 50; }
    }
    private static ObjectGraphType Page(string name, ObjectGraphType node)
    {
        var info = Type(name + "Info", "Load/filter-bound keyset cursor. Different pages are fresh observations, not one snapshot.");
        PawnSchema.Add(info, "hasNextPage", new BooleanGraphType(), c => ((ThingPage)c.Source!).HasNextPage);
        PawnSchema.Add(info, "endCursor", new StringGraphType(), c => ((ThingPage)c.Source!).EndCursor);
        var page = Type(name, "Bounded collection wrapper. No global total is implied; field errors disable continuation.");
        PawnSchema.Add(page, "nodes", new ListGraphType(node), c => ((ThingPage)c.Source!).Nodes).Metadata["collection_view"] = true;
        PawnSchema.Add(page, "pageInfo", info, c => c.Source);
        return page;
    }
    private static ObjectGraphType Type(string name, string description) => new() { Name = name, Description = description };
    private static ReadContext State(IResolveFieldContext c) => (ReadContext)c.UserContext["read"]!;
    private static Dictionary<string, object?> Args(IResolveFieldContext c) => c.Arguments == null ? new() : c.Arguments.ToDictionary(x => x.Key, x => x.Value.Value);
    private static void Scalars(ObjectGraphType type, string declaration)
    {
        foreach (var word in declaration.Split(' '))
        {
            var parts = word.Split(':'); var name = parts[1];
            IGraphType scalar = parts[0] switch { "i" => new IntGraphType(), "f" => new FloatGraphType(), "b" => new BooleanGraphType(), _ => new StringGraphType() };
            var field = PawnSchema.Add(type, name, scalar, c => State(c).ReadNode((GameNode)c.Source!, name, Args(c)));
            field.Metadata["node_member"] = true;
            if (parts[0] == "e") field.Metadata["native_enum"] = new NativeEnumScalar(parts[2]);
            field.Description = name is "id" or "ref" or "runtimeType" or "supportedType" or "supported" ? "Transport metadata; not a native gameplay member." : "Native " + type.Name + "." + name + "; selected getter only. Null without issue means not applicable; issue means failed/unavailable.";
        }
    }
    private static void Link(ObjectGraphType type, string name, ObjectGraphType child, string? description = null, bool referenceOnly = false)
    {
        var field = PawnSchema.Add(type, name, child, c => State(c).ReadNode((GameNode)c.Source!, name, Args(c)));
        field.Metadata["node_member"] = true;
        field.Description = description ?? "Native " + type.Name + "." + name + " reference; only selected child members are read.";
        if (referenceOnly) field.Metadata["default_reference"] = true;
    }
    private static void Slice(ObjectGraphType type, string name, IGraphType item, QueryArguments? extra = null, int maximum = 100) => PawnSchema.Collection(type, name, item, extra, maximum);
}

internal static class ThingPaging
{
    internal static ThingPage Page(IEnumerable<GameNode> candidates, IReadOnlyDictionary<string, object?> args, ReadContext context)
    {
        var filter = Filter(JObject.FromObject(args));
        var after = args.TryGetValue("after", out var raw) ? raw as string : null;
        var last = Decode(after, context.Session.LoadEpoch, filter);
        var first = args.TryGetValue("first", out var size) && size != null ? Convert.ToInt32(size) : 20;
        if (first < 1 || first > 50) throw ReadContext.Error("invalid_arguments", "first must be 1..50.");
        // Bounded top-k identity selection: no whole-map DTO materialization or per-thing detail reads.
        var selected = new SortedDictionary<string, GameNode>(StringComparer.Ordinal); var scans = 0;
        var filters = new Dictionary<string, Func<object?, bool>>();
        foreach (var node in candidates)
        {
            context.Check();
            if (++scans > 100000) throw ReadContext.Error("entity_scan_limit", "Identity scan exceeded 100000 candidates; narrow definition or map.");
            if (last != null && StringComparer.Ordinal.Compare(node.Key, last) <= 0) continue;
            if (!filters.TryGetValue(node.Kind, out var matches)) filters[node.Kind] = matches = context.Matcher(node.Kind, args.TryGetValue("where", out var where) ? where : null);
            if (!matches(node)) continue;
            if (selected.Count > first && StringComparer.Ordinal.Compare(node.Key, selected.Last().Key) >= 0) continue;
            selected[node.Key] = node;
            if (selected.Count > first + 1) selected.Remove(selected.Last().Key);
        }
        var page = new ThingPage { Nodes = selected.Values.Take(first).ToList(), HasNextPage = selected.Count > first };
        if (page.Nodes.Count != 0) page.EndCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(context.Session.LoadEpoch + "\n" + filter + "\n" + page.Nodes.Last().Key));
        return page;
    }
    private static string Filter(JObject args) => GameQueryEngine.Hash(new JObject(args.Properties().Where(x => x.Name is not ("first" or "after") && x.Value.Type != JTokenType.Null).OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => new JProperty(x.Name, x.Value.DeepClone()))).ToString(Formatting.None));
    internal static void ValidateCursor(JObject args, string epoch) => Decode((string?)args["after"], epoch, Filter(args));
    private static string? Decode(string? cursor, string epoch, string filter)
    {
        if (cursor == null) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('\n');
            if (parts.Length != 3 || parts[2].Length == 0) throw new FormatException();
            if (parts[0] != epoch || parts[1] != filter) throw ReadContext.Error("stale_cursor", "Cursor belongs to another load or filter.");
            return parts[2];
        }
        catch (FormatException) { throw ReadContext.Error("invalid_cursor", "Invalid collection cursor."); }
    }

}
