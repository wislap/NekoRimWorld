using System;
using System.Collections.Generic;
using System.Linq;
using GraphQL;
using GraphQL.Types;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal interface IThingData
{
    object? NativeRoot(string root, IReadOnlyDictionary<string, object?> arguments, ReadContext context);
    object? NativeField(GameNode node, string field, IReadOnlyDictionary<string, object?> arguments, ReadContext context);
}

/// <summary>Explicit native member projections. Only references and collection windows are transport wrappers.</summary>
internal sealed class ThingSchema
{
    public ObjectGraphType Thing { get; } = Type("Thing", "Verse.Thing instance: may be an item, building, plant, Pawn or other native object, not necessarily an inventory item. id identifies the instance; relations describe containment, not ownership or usability.");
    public ObjectGraphType Def { get; } = Type("ThingDef", "Verse.ThingDef definition, not an instance. Native category, content category tree and query groups are distinct.");
    public ObjectGraphType Equipment { get; } = Type("Pawn_EquipmentTracker", "Verse.Pawn_EquipmentTracker; equipped things, not inventory or hauling.");
    public ObjectGraphType Apparel { get; } = Type("Pawn_ApparelTracker", "RimWorld.Pawn_ApparelTracker; worn apparel.");
    public ObjectGraphType Inventory { get; } = Type("Pawn_InventoryTracker", "Verse.Pawn_InventoryTracker; carried inventory, not the active hauling container.");
    public ObjectGraphType Carry { get; } = Type("Pawn_CarryTracker", "Verse.Pawn_CarryTracker; active carried thing and its container.");
    public ObjectGraphType Map { get; } = Type("NativeMap", "Native map reference with explicitly selected storage and resource-counter members.");
    public ObjectGraphType Faction { get; } = Type("Faction", "Native RimWorld.Faction: the faction this Thing or Pawn belongs to. Faction membership is a native fact, not an inference from names, kinds or containers. A null faction means this instance has no faction (for example a wild animal or a loose item), not that the fact is unknown.");
    public List<ObjectGraphType> ComponentTypes { get; } = new();

    public ThingSchema()
    {
        var reference = Type("ThingReference", "Detached Thing identity. Resolve id through thing; no recursive object expansion.");
        Scalars(reference, "s:id s:runtimeType"); Link(reference, "def", Def);
        var holder = Type("ThingHolder", "IThingHolder reference. ref is observation-scoped; runtimeType does not imply Pawn. Thing id is present only for Thing holders.");
        Scalars(holder, "s:ref s:runtimeType s:thingId");
        Link(holder,"parent",holder,"Native IThingHolder.ParentHolder. Follow this relation to identify containing pawns or root holders; refs remain observation-local.",referenceOnly:true);
        var owner = Type("ThingOwner", "Verse.ThingOwner native holder and contents. items is a bounded enumeration wrapper.");
        Link(owner, "Owner", holder, referenceOnly: true); Scalars(owner, "i:Count i:TotalStackCount"); Slice(owner, "items", Thing);
        var position = Type("IntVec3", "Verse.IntVec3 coordinates: x and z are the map plane; y is vertical. Read map identity separately.");
        Scalars(position, "i:x i:y i:z");
        var capabilities = PawnSchema.Add(Thing, "capabilities", new ListGraphType(new StringGraphType()), c => State(c).ReadNode((GameNode)c.Source!, "capabilities", Args(c)));
        capabilities.Metadata["node_member"] = true;
        capabilities.Metadata["explicit_selection"] = true;
        capabilities.Description = "Native capability membership shared with spatial surveys. Filter with some/in before aggregation and paging; overlapping capabilities do not imply usability.";
        Scalars(Thing, "s:id s:runtimeType s:Label i:stackCount i:HitPoints i:MaxHitPoints b:Spawned b:Destroyed b:IngestibleNow");
        Scalars(Faction, "s:defName s:name b:isPlayer e:playerRelation:RimWorld.FactionRelationKind");
        Link(Thing, "Faction", Faction, "Native Thing.Faction: the faction that owns this instance, null when it has none. This is the native membership fact. Container membership, position and definition do not establish faction.");
        Link(Thing, "HeldBy", reference, "Nearest containing Pawn along the real holder chain, null when this instance is not inside a Pawn. This is held-by containment, not ownership and not pathfinding access.");
        Link(Thing, "HeldByFaction", Faction, "Faction of the nearest containing Pawn (see HeldBy), null when no Pawn holds this instance or that Pawn has no faction. Use HeldByFaction.isPlayer instead of inferring ownership from containment.");
        Thing.GetField("HeldBy")!.Description = "Nearest containing Pawn along Thing.ParentHolder, so \"whose pocket is this in\" is a read fact rather than an inference. Not ownership; a loose ground item has no such holder.";
        Thing.GetField("HeldByFaction")!.Description = "Faction of the pawn returned by HeldBy. When both are null the instance is simply not carried, which is different from a faction that could not be read.";
        Faction.GetField("defName")!.Description = "Native Faction.def.defName, for example PlayerColony. A definition name identifies the faction type, not a specific faction instance.";
        Faction.GetField("name")!.Description = "Native Faction.Name: the faction's own display name.";
        Faction.GetField("isPlayer")!.Description = "Native Faction.IsPlayer: true only for the player's own faction. This is the native membership test for \"is this ours\"; it is not an ownership guess from inventory, containment or pawn kind.";
        Faction.GetField("playerRelation")!.Description = "Native Faction.PlayerRelationKind (Hostile/Neutral/Ally) toward the player faction. Only a relation, not an action or a threat assessment.";
        Link(Thing, "def", Def); Link(Thing, "Stuff", Def);
        Link(Thing, "Map", Map, referenceOnly: true); Link(Thing, "MapHeld", Map, referenceOnly: true);
        Link(Thing, "Position", position); Link(Thing, "PositionHeld", position);
        Link(Thing, "holdingOwner", owner, referenceOnly: true); Link(Thing, "ParentHolder", holder, referenceOnly: true);
        Thing.GetField("runtimeType")!.Description = "Actual CLR class name from GetType().FullName; an exact type, not its base-class hierarchy, faction or construction history.";
        reference.GetField("runtimeType")!.Description = Thing.GetField("runtimeType")!.Description;
        Thing.GetField("Spawned")!.Description = "Native Thing.Spawned: this instance itself is spawned on a map. false does not establish that it is outside loaded maps; a containing holder can be on a map.";
        Thing.GetField("Map")!.Description = "Native Thing.Map: map on which this instance itself is spawned; null when not spawned. For a held instance, map_held may still identify its containing map.";
        Thing.GetField("MapHeld")!.Description = "Native Thing.MapHeld: map of this instance or its containing holder chain. A non-null value does not imply this instance is spawned; null supplies no map reference, not a caravan identity.";
        Thing.GetField("Position")!.Description = "Native Thing.Position, exposed only when this instance is spawned; otherwise null. x/z are map coordinates, y is vertical.";
        Thing.GetField("PositionHeld")!.Description = "Native Thing.PositionHeld, exposed when this instance or an ancestor is spawned; otherwise null. Describes the instance/holder location, not walking access.";
        Thing.GetField("holdingOwner")!.Description = "Immediate ThingOwner containing this instance, if any. Container membership is not faction, property ownership or hauling status.";
        Thing.GetField("ParentHolder")!.Description = "Native Thing.ParentHolder containment link. Follow actual holder relations to identify a container; the reference alone does not classify a Pawn's faction or identity.";
        Thing.GetField("stackCount")!.Description = "Native Thing.stackCount: units in this instance. count measures records; sum(stack_count) measures units. Non-item Things can also have stack_count.";

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
        Def.GetField("category")!.Description = "Native Verse.ThingCategory enum: broad definition kind. Building includes natural objects; it is distinct from IsBuildingArtificial, faction ownership and construction history. Content-category memberships are in thing_categories.";
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
    }

    public void AddRoots(ObjectGraphType root)
    {
        Root(root, "thing", Thing, new QueryArguments(Text("id", true), Text("mapId")));
        // PublicQuery owns source enumeration. These source declarations have no
        // native-root resolver and do not retain the retired GraphQL execution path.
        root.AddField(PublicQuery.ThingsRoot(Thing));
        Root(root, "thing_def", Def, new QueryArguments(Text("defName", true)));
        root.AddField(new FieldType { Name = "thing_defs", ResolvedType = Def, Arguments = new QueryArguments(Text("name"), Text("categoryDefName"),
            GameArguments.Create("exactNames", new ListGraphType(new NonNullGraphType(new StringGraphType())),
                "Exact canonical defName OR current localized label, case-insensitive. Retains every matching definition (including ambiguities); duplicates do not duplicate rows. Does not assert any physical instance exists.",
                new JObject { ["minItems"] = 1, ["maxItems"] = DefinitionCandidates.MaxNames, ["items"] = new JObject { ["minLength"] = 1, ["maxLength"] = 128 } })) });
        Root(root, "map", Map, new QueryArguments(Text("id", true)));
    }
    private static QueryArgument Text(string name, bool required = false) => GameArguments.Create(name,
        required ? new NonNullGraphType(new StringGraphType()) : new StringGraphType(), "Exact identifier, or case-insensitive substring for name.", new JObject { ["minLength"] = 1, ["maxLength"] = 128 });
    private static void Root(ObjectGraphType root, string name, IGraphType type, QueryArguments args)
    {
        PawnSchema.Add(root, name, type, c => State(c).NativeRoot(name, Args(c)), args);
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
            field.Description = name is "id" or "ref" or "runtimeType" or "supportedType" or "supported" ? "Transport metadata; not a native gameplay member." : "Native " + type.Name + "." + name + "; selected getter only. Interpret null using this member's meaning; an issue at its path means failed/unavailable.";
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
