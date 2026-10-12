using System;
using System.Collections.Generic;
using GraphQL;
using GraphQL.Types;

namespace Neko.RimWorld;

/// <summary>Selected business facts over native objects. Creating a relation never expands its columns.</summary>
internal sealed class ObjectFactsSchema
{
    private readonly ObjectGraphType identity = Type("ObjectIdentity", "Native identity and physical scope; containment does not imply property ownership.");
    private readonly ObjectGraphType gear = Type("ObjectGear", "Four distinct Pawn holding relations, not a combined inventory.");
    private readonly ObjectGraphType work = Type("ObjectWork", "Assignments and current activity are different observations. Medical care belongs to the recipient.");
    private readonly ObjectGraphType state = Type("ObjectState", "Independent nullable native facets. Only selected relations and selected columns are read.");
    internal ObjectGraphType CellType { get; }

    internal ObjectFactsSchema(ThingSchema native)
    {
        Scalars(identity, "s:id s:name e:gender:Verse.Gender s:runtimeType b:spawned b:destroyed b:isFreeColonist b:isColonist b:isPrisonerOfColony b:isSlaveOfColony s:guestStatus");
        Link(identity, "faction", native.Faction); Link(identity, "definition", native.Def);
        Link(identity, "map", native.Map); Link(identity, "mapHeld", native.Map);
        var cell = Type("ObjectCell", "Coordinates belong to the reported map, x/z are the map plane.");
        CellType = cell;
        Scalars(cell, "s:mapId i:x i:y i:z b:fogged");
        Link(identity, "position", cell); Link(identity, "heldPosition", cell);
        foreach (var relation in new[] { "equipment", "apparel", "inventory", "carried" }) Collection(gear, relation, native.Thing);

        var job = Type("ObjectJob", "Native current/queued Job. A queued Job is not currently executing.");
        Scalars(job, "s:id s:defName s:report b:isCurrent b:playerForced");
        var target = Type("ObjectJobTarget", "A Job target is a Thing or a cell. Not necessarily a reserved destination.");
        Scalars(target, "e:slot:Verse.AI.TargetIndex s:kind s:thingId s:mapId i:x i:y i:z");
        Collection(job, "targets", target);
        Link(work, "currentJob", job); Collection(work, "queuedJobs", job);
        Scalars(work, "b:drafted b:inMentalState s:mentalState s:medicalCare s:medicalCareOwnerId s:medicalCareRole s:allowedAreaId");
        var assignment = Type("ObjectWorkAssignment", "Priority 0 is unassigned; 1 precedes 4. Equal priorities use natural priority. Not complete manual Job eligibility.");
        Scalars(assignment, "s:defName s:label i:priority b:disabled i:naturalPriority");
        Collection(work, "assignments", assignment);
        work.GetField("medicalCare")!.Description = "Pawn.playerSettings.medCare for THIS Pawn when receiving treatment, not how this Pawn treats others. Null means no player settings.";
        work.GetField("medicalCare")!.Metadata["object_semantic_role"] = "care_received";

        var condition = Facet("condition", "ObjectCondition", "Physical durability, native flags and actor-specific forbidden state are independent.");
        Scalars(condition, "i:stackCount i:hitPoints i:maxHitPoints b:burning s:quality b:forbiddableComponentPresent b:forbidden b:forbiddenToActor");
        Roles(condition, "forbiddenToActor", "actor");
        var power = Facet("power", "ObjectPower"); Scalars(power, "b:on f:outputWatts b:connected");
        var fuel = Facet("fuel", "ObjectFuel"); Scalars(fuel, "f:amount f:capacity f:target b:hasFuel b:autoRefuel");
        var battery = Facet("battery", "ObjectBattery"); Scalars(battery, "f:storedWattDays f:capacityWattDays");
        var temperature = Facet("temperatureControl", "ObjectTemperatureControl"); Scalars(temperature, "f:targetC b:operatingHighPower");
        var ammunition = Facet("ammunition", "ObjectAmmunition"); Scalars(ammunition, "s:loadedDefinition i:loadedCount");
        var breakdown = Facet("breakdown", "ObjectBreakdown"); Scalars(breakdown, "b:broken");
        var flick = Facet("switch", "ObjectSwitch"); Scalars(flick, "b:on");
        var rot = Facet("rot", "ObjectRot"); Scalars(rot, "f:progress e:stage:RimWorld.RotStage i:ticksUntilRotAtCurrentTemperature");
        var manning = Facet("manning", "ObjectManning"); Scalars(manning, "s:pawnId b:manned");
        var explosive = Facet("explosive", "ObjectExplosive", "Observed fuse state, not an explosion prediction."); Scalars(explosive, "b:wickStarted");
        var door = Facet("door", "ObjectDoor", "Current native passage predicates, no opening or movement issued.");
        Scalars(door, "b:open b:holdOpen b:actorCanOpen b:actorCanPhysicallyPass");
        Roles(door, "actorCanOpen", "actor"); Roles(door, "actorCanPhysicallyPass", "actor");
        var bed = Facet("bed", "ObjectBed", "Bed definition and medical designation differ. Predicates use actor CURRENT position, never simulated carry position.");
        Scalars(bed, "b:medicalDesignation s:ownerType i:sleepingSlots b:canUseBedNow b:isValidBedFor");
        Roles(bed, "canUseBedNow", "recipient"); Roles(bed, "isValidBedFor", "recipient", "actor");
        Collection(bed, "assigned", native.Thing);
        var occupant = Type("ObjectBedOccupant", "One native sleeping slot; empty occupancy is not a reservation.");
        Scalars(occupant, "i:slot s:pawnId"); Collection(bed, "occupants", occupant);
        var production = Facet("production", "ObjectProduction", "IBillGiver includes Pawn surgery orders. Ingredient filters are allowed definitions, not required quantities or stock.");
        Scalars(production, "s:billOwnerId s:billOwnerRole s:nativeInterface");
        var bill = Type("ObjectBill", "Native bill policy, not proof of an executable Job. Only the active repeat mode's quantity applies.");
        Scalars(bill, "s:id s:billKind s:runtimeType s:label s:recipe b:suspended s:repeatMode i:repeatCount i:targetCount");
        Collection(bill, "ingredientDefinitions", native.Def);
        Collection(production, "bills", bill);
        var storage = Facet("storage", "ObjectStorage", "Native storage acceptance and contents, not a hauling or acceptance guarantee for a specific instance.");
        Scalars(storage, "i:storedRecords e:priority:RimWorld.StoragePriority"); Collection(storage, "cells", cell); Collection(storage, "allowedDefinitions", native.Def);
        var plant = Facet("plant", "ObjectPlant"); Scalars(plant, "f:growth s:lifeStage b:blighted b:harvestableNow s:yieldNowStatus s:yieldNowReason");
        var turret = Facet("turret", "ObjectTurret", "Existing attack tracker only; no lazy attack selection or firing prediction.");
        Scalars(turret, "b:active s:verb f:range"); Link(turret, "target", target);
        var trap = Facet("trap", "ObjectTrap", "Native trigger chance for supplied actor, not a simulated trigger.");
        Scalars(trap, "f:springChanceToActor"); Roles(trap, "springChanceToActor", "actor");
        var construction = Facet("construction", "ObjectConstruction"); Scalars(construction, "s:targetDefinition s:stage s:workProgress f:workRemaining f:workTotal f:fractionComplete");
        var containment = Facet("containment", "ObjectContainment", "Immediate holder and contents are containment, not ownership, walking access or hauling state.");
        Scalars(containment, "s:holderType b:realContents"); Link(containment, "holder", native.Thing); Collection(containment, "contents", native.Thing);
        var holder = Type("ObjectHolder", "Actual native holder chain; references without Thing IDs are snapshot local.");
        Scalars(holder, "s:ref s:runtimeType s:thingId"); Collection(containment, "holderChain", holder, "values");
        var reservations = Facet("reservations", "ObjectReservations", "Records ON this target, not reservations claimed BY it; occupancy is separate.");
        var reservation = Type("ObjectReservation", "A native reservation record in this snapshot. Job targets may differ from the reserved cell.");
        Scalars(reservation, "s:ref s:claimantId s:layer i:maxPawns i:stackCount b:wholeStack b:obsolete");
        Link(reservation, "job", job); Link(reservation, "cell", cell);
        foreach (var relation in new[] { "normal", "physicalInteraction", "destinations" }) Collection(reservations, relation, reservation, "values");
        Scalars(reservations, "i:sampleTick");
        var cellState = Type("ObjectCellState", "Selected cell observations; no region/path simulation.");
        var environment = Type("ObjectCellEnvironment", "Cell environment; native danger is temperature/vacuum classification, not overall safety.");
        Scalars(environment, "s:terrain b:walkable s:roof b:roofWithinSupportRange f:temperatureC f:glowFraction f:snowDepth b:polluted s:nativeDangerForActor");
        Roles(environment, "nativeDangerForActor", "actor");
        var gas = Type("ObjectCellGas", "Native GasType density 0..255 at the selected cell.");
        Scalars(gas, "s:defName i:density"); Collection(environment, "gases", gas, "immutable");
        var room = Type("ObjectRoom", "Current room topology and native room readings. ID may change after topology changes.");
        Scalars(room, "i:id s:role i:cellCount b:outdoors f:temperatureC f:vacuum f:cleanliness");
        Link(environment, "room", room); environment.GetField("room")!.Metadata["object_capture"] = "values";
        Link(cellState, "environment", environment); Link(cellState, "reservations", reservations); Link(cell, "state", cellState);
        var component = Type("ObjectComponent", "Unsupported component discovery only; no arbitrary subtype getters are invoked.");
        Scalars(component, "s:ref s:runtimeType"); Collection(state, "unsupportedComponents", component, "values");
        Attach(native.Thing);
    }

    internal void Attach(ObjectGraphType type)
    {
        Link(type, "identity", identity); Link(type, "gear", gear); Link(type, "work", work); Link(type, "state", state);
    }
    private ObjectGraphType Facet(string field, string name, string? description = null)
    {
        var type = Type(name, description); Link(state, field, type);
        state.GetField(field)!.Metadata["object_null_status"] = "not_applicable";
        return type;
    }
    private static ObjectGraphType Type(string name, string? description = null) => new() { Name = name, Description = description };
    private static ReadContext Context(IResolveFieldContext c) => (ReadContext)c.UserContext["read"]!;
    private static GameNode Node(IResolveFieldContext c) => c.Source is GameEntity entity ? new GameNode("Pawn", "pawn", entity, entity.Handle) : (GameNode)c.Source!;
    private static void Scalars(ObjectGraphType type, string declarations)
    {
        foreach (var declaration in declarations.Split(' ')) {
            var pieces = declaration.Split(':'); var name = pieces[1];
            IGraphType scalar = pieces[0] switch { "i" => new IntGraphType(), "f" => new FloatGraphType(), "b" => new BooleanGraphType(), _ => new StringGraphType() };
            var field = PawnSchema.Add(type, name, scalar, c => Context(c).ReadNode(Node(c), name));
            field.Metadata["node_member"] = true;
            if (pieces[0] == "e") field.Metadata["native_enum"] = new NativeEnumScalar(pieces[2]);
            field.Description = "Selected native " + type.Name + "." + name + "; null, not applicable and unavailable retain separate meanings.";
        }
    }
    private static void Link(ObjectGraphType type, string name, ObjectGraphType child)
    {
        var field = PawnSchema.Add(type, name, child, c => Context(c).ReadNode(Node(c), name));
        field.Metadata["node_member"] = true;
    }
    private static void Collection(ObjectGraphType type, string name, ObjectGraphType item, string capture = "identity")
    {
        PawnSchema.Collection(type, name, item);
        type.GetField(name)!.Metadata["object_capture"] = capture;
    }
    private static void Roles(ObjectGraphType type, string name, params string[] roles) => type.GetField(name)!.Metadata["object_roles"] = roles;
}
