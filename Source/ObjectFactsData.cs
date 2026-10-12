using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;

namespace Neko.RimWorld;

/// <summary>Lazy columns and unpaged native relation sources for object reading.</summary>
internal static class ObjectFactsData
{
    internal static readonly (Type Type, string Method)[] ReadDependencies = {
        (typeof(RestUtility), nameof(RestUtility.CanUseBedNow)),
        (typeof(RestUtility), nameof(RestUtility.IsValidBedFor)),
        (typeof(ForbidUtility), nameof(ForbidUtility.IsForbidden)),
        (typeof(Building_Door), nameof(Building_Door.PawnCanOpen)),
        (typeof(Building_Door), nameof(Building_Door.CanPhysicallyPass)),
        (typeof(Building_Trap), "SpringChance")
    };

    internal static bool Supports(GameNode node, string field) => node.Kind.StartsWith("Object", StringComparison.Ordinal)
        || node.Kind is "Pawn" or "Thing" && field is "identity" or "gear" or "work" or "state";

    internal static object? Read(GameNode node, string field, IReadOnlyDictionary<string, object?> args, ReadContext context)
    {
        GameImplementation.RequireKnownBinary();
        var thing = node.Owner.Handle as Thing;
        if (thing?.Destroyed == true) throw ReadContext.Error("object_unavailable", "The object no longer exists.");
        if (node.Kind is "Pawn" or "Thing") return field switch {
            "identity" => Child(node, "ObjectIdentity", field, node.Handle),
            "state" => Child(node, "ObjectState", field, node.Handle),
            "gear" => thing is Pawn pawn ? Child(node, "ObjectGear", field, pawn) : null,
            "work" => thing is Pawn worker ? Child(node, "ObjectWork", field, worker) : null,
            _ => Unknown(node, field)
        };
        switch (node.Kind) {
            case "ObjectIdentity":
                var identity = (Thing)node.Handle!; var member = identity as Pawn;
                return field switch {
                    "id" => identity.GetUniqueLoadID(), "name" => member?.LabelShort ?? identity.Label,
                    "gender" => member?.gender.ToString(), "runtimeType" => identity.GetType().FullName,
                    "spawned" => identity.Spawned, "destroyed" => identity.Destroyed,
                    "isFreeColonist" => member == null ? null : member.IsFreeColonist,
                    "isColonist" => member == null ? null : member.IsColonist,
                    "isPrisonerOfColony" => member == null ? null : member.IsPrisonerOfColony,
                    "isSlaveOfColony" => member == null ? null : member.IsSlaveOfColony,
                    "guestStatus" => member?.GuestStatus?.ToString(),
                    "faction" => ThingData.Wrap(node, "Faction", field, identity.Faction),
                    "definition" => ThingData.Definition(identity.def),
                    "map" => identity.Map == null ? null : ThingData.MapNode(identity.Map),
                    "mapHeld" => identity.MapHeld == null ? null : ThingData.MapNode(identity.MapHeld),
                    "position" => identity.Spawned ? Cell(node, field, identity.Map, identity.Position) : null,
                    "heldPosition" => identity.SpawnedOrAnyParentSpawned ? Cell(node, field, identity.MapHeld, identity.PositionHeld) : null,
                    _ => Unknown(node, field)
                };
            case "ObjectCell":
                var cell = (CellSource)node.Handle!;
                return field switch { "mapId" => cell.MapId, "x" => cell.Value.x, "y" => cell.Value.y, "z" => cell.Value.z, "fogged" => cell.Value.Fogged(CellMap(cell)), "state" => Child(node, "ObjectCellState", "state", cell), _ => Unknown(node, field) };
            case "ObjectCellState":
                return field switch { "environment" => Child(node, "ObjectCellEnvironment", "environment", node.Handle), "reservations" => Child(node, "ObjectReservations", "reservations", node.Handle), _ => Unknown(node, field) };
            case "ObjectCellEnvironment":
                var environmentCell = (CellSource)node.Handle!; var environmentMap = CellMap(environmentCell); var position = environmentCell.Value;
                return field switch {
                    "terrain" => position.GetTerrain(environmentMap).defName, "walkable" => position.Walkable(environmentMap), "roof" => position.GetRoof(environmentMap)?.defName,
                    "roofWithinSupportRange" => position.Roofed(environmentMap) ? RoofCollapseUtility.WithinRangeOfRoofHolder(position, environmentMap) : null,
                    "temperatureC" => GenTemperature.GetTemperatureForCell(position, environmentMap), "glowFraction" => environmentMap.glowGrid.GroundGlowAt(position),
                    "snowDepth" => environmentMap.snowGrid.GetDepth(position), "polluted" => position.IsPolluted(environmentMap),
                    "nativeDangerForActor" => position.GetDangerFor(PawnContext(context, node, false), environmentMap).ToString(),
                    "gases" => Enum.GetValues(typeof(GasType)).Cast<GasType>().Select(g => Child(node, "ObjectCellGas", g.ToString(), new GasSource(environmentCell, g))),
                    "room" => position.GetRoom(environmentMap) is Room room ? Child(node, "ObjectRoom", "room:" + room.ID, room) : null,
                    _ => Unknown(node, field)
                };
            case "ObjectCellGas":
                var gas = (GasSource)node.Handle!;
                return field switch { "defName" => gas.Type.ToString(), "density" => (int)CellMap(gas.Cell).gasGrid.DensityAt(gas.Cell.Value, gas.Type), _ => Unknown(node, field) };
            case "ObjectRoom":
                var observedRoom = (Room)node.Handle!;
                if (observedRoom.Dereferenced) throw ReadContext.Error("object_unavailable", "Room topology changed.");
                return field switch {
                    "id" => observedRoom.ID, "role" => observedRoom.Role?.defName, "cellCount" => observedRoom.CellCount,
                    "outdoors" => observedRoom.PsychologicallyOutdoors, "temperatureC" => observedRoom.Temperature,
                    "vacuum" => observedRoom.Vacuum, "cleanliness" => observedRoom.GetStat(RoomStatDefOf.Cleanliness), _ => Unknown(node, field)
                };
            case "ObjectGear":
                var gear = (Pawn)node.Handle!;
                return field switch {
                    "equipment" => gear.equipment == null ? null : gear.equipment.AllEquipmentListForReading.Select(t => Item(node, t)),
                    "apparel" => gear.apparel == null ? null : gear.apparel.WornApparel.Select(t => Item(node, t)),
                    "inventory" => gear.inventory == null ? null : gear.inventory.innerContainer.Select(t => Item(node, t)),
                    "carried" => gear.carryTracker == null ? null : gear.carryTracker.CarriedThing is Thing carried ? new[] { Item(node, carried) } : Array.Empty<GameNode>(),
                    _ => Unknown(node, field)
                };
            case "ObjectWork":
                var work = (Pawn)node.Handle!;
                return field switch {
                    "assignments" => DefDatabase<WorkTypeDef>.AllDefsListForReading.Select(d => Child(node, "ObjectWorkAssignment", d.defName, d)),
                    "currentJob" => work.CurJob == null ? null : Job(node, work.CurJob),
                    "queuedJobs" => work.jobs == null ? null : work.jobs.jobQueue.Select(q => Job(node, q.job)),
                    "drafted" => work.Drafted, "inMentalState" => work.InMentalState, "mentalState" => work.MentalStateDef?.defName,
                    "medicalCare" => work.playerSettings?.medCare.ToString(),
                    "medicalCareOwnerId" => work.GetUniqueLoadID(), "medicalCareRole" => "care_received",
                    "allowedAreaId" => work.playerSettings?.EffectiveAreaRestrictionInPawnCurrentMap?.GetUniqueLoadID(),
                    _ => Unknown(node, field)
                };
            case "ObjectWorkAssignment":
                var type = (WorkTypeDef)node.Handle!; var actor = (Pawn)node.Owner.Handle;
                return field switch {
                    "defName" => type.defName, "label" => type.LabelCap.ToString(), "naturalPriority" => type.naturalPriority,
                    "disabled" => actor.WorkTypeIsDisabled(type), "priority" => actor.workSettings == null ? null : actor.workSettings.GetPriority(type),
                    _ => Unknown(node, field)
                };
            case "ObjectJob":
                var job = (Job)node.Handle!;
                return field switch {
                    "id" => job.GetUniqueLoadID(), "defName" => job.def?.defName,
                    "isCurrent" => node.Owner.Handle is Pawn jobOwner ? jobOwner.CurJob == job : null,
                    "playerForced" => job.playerForced,
                    "report" => node.Owner.Handle is Pawn p && p.CurJob == job ? p.GetJobReport() : null,
                    "targets" => JobTargets(node, job), _ => Unknown(node, field)
                };
            case "ObjectJobTarget":
                var jobTarget = (TargetSource)node.Handle!; var nativeTarget = jobTarget.Value;
                return field switch {
                    "slot" => jobTarget.Slot, "kind" => nativeTarget.HasThing ? "thing" : "cell",
                    "thingId" => nativeTarget.HasThing ? nativeTarget.Thing?.GetUniqueLoadID() : null,
                    "mapId" => nativeTarget.HasThing ? nativeTarget.Thing?.MapHeld?.GetUniqueLoadID() : node.Owner.MapId,
                    "x" => nativeTarget.Cell.x, "y" => nativeTarget.Cell.y, "z" => nativeTarget.Cell.z,
                    _ => Unknown(node, field)
                };
            case "ObjectState": return field == "unsupportedComponents" ? UnsupportedComponents(node, (Thing)node.Handle!) : Facet(node, field, (Thing)node.Handle!);
            case "ObjectCondition":
                var condition = (Thing)node.Handle!;
                return field switch {
                    "stackCount" => condition.stackCount,
                    "hitPoints" => condition.def.useHitPoints ? condition.HitPoints : null,
                    "maxHitPoints" => condition.def.useHitPoints ? condition.MaxHitPoints : null,
                    "burning" => condition.IsBurning(), "quality" => condition.TryGetQuality(out var quality) ? quality.ToString() : null,
                    "forbiddableComponentPresent" => condition.TryGetComp<CompForbiddable>() != null,
                    "forbidden" => condition.TryGetComp<CompForbiddable>()?.Forbidden,
                    "forbiddenToActor" => Forbidden(condition, PawnContext(context, node, false)), _ => Unknown(node, field)
                };
            case "ObjectPower":
                var power = (CompPowerTrader)node.Handle!;
                return field switch { "on" => power.PowerOn, "outputWatts" => power.PowerOutput, "connected" => power.PowerNet != null, _ => Unknown(node, field) };
            case "ObjectFuel":
                var fuel = (CompRefuelable)node.Handle!;
                return field switch { "amount" => fuel.Fuel, "capacity" => fuel.Props.fuelCapacity, "target" => fuel.TargetFuelLevel, "hasFuel" => fuel.HasFuel, "autoRefuel" => fuel.allowAutoRefuel, _ => Unknown(node, field) };
            case "ObjectBattery":
                var battery = (CompPowerBattery)node.Handle!;
                return field switch { "storedWattDays" => battery.StoredEnergy, "capacityWattDays" => battery.Props.storedEnergyMax, _ => Unknown(node, field) };
            case "ObjectTemperatureControl":
                var temperature = (CompTempControl)node.Handle!;
                return field switch { "targetC" => temperature.targetTemperature, "operatingHighPower" => temperature.operatingAtHighPower, _ => Unknown(node, field) };
            case "ObjectAmmunition":
                var ammunition = (CompChangeableProjectile)node.Handle!;
                return field switch { "loadedDefinition" => ammunition.LoadedShell?.defName, "loadedCount" => ammunition.loadedCount, _ => Unknown(node, field) };
            case "ObjectBreakdown": return field == "broken" ? ((CompBreakdownable)node.Handle!).BrokenDown : Unknown(node, field);
            case "ObjectSwitch": return field == "on" ? ((CompFlickable)node.Handle!).SwitchIsOn : Unknown(node, field);
            case "ObjectRot":
                var rot = (CompRottable)node.Handle!;
                return field switch { "progress" => rot.RotProgress, "stage" => rot.Stage.ToString(), "ticksUntilRotAtCurrentTemperature" => rot.TicksUntilRotAtCurrentTemp, _ => Unknown(node, field) };
            case "ObjectManning":
                var manning = (CompMannable)node.Handle!;
                return field switch { "pawnId" => manning.ManningPawn?.GetUniqueLoadID(), "manned" => manning.MannedNow, _ => Unknown(node, field) };
            case "ObjectExplosive": return field == "wickStarted" ? ((CompExplosive)node.Handle!).wickStarted : Unknown(node, field);
            case "ObjectDoor":
                var door = (Building_Door)node.Handle!;
                return field switch {
                    "open" => door.Open, "holdOpen" => door.HoldOpen,
                    "actorCanOpen" => DoorCheck(door, PawnContext(context, node, false), false),
                    "actorCanPhysicallyPass" => DoorCheck(door, PawnContext(context, node, false), true), _ => Unknown(node, field)
                };
            case "ObjectBed":
                var bed = (Building_Bed)node.Handle!;
                return field switch {
                    "medicalDesignation" => bed.Medical, "ownerType" => bed.ForOwnerType.ToString(), "sleepingSlots" => bed.SleepingSlotsCount,
                    "assigned" => bed.OwnersForReading.Select(p => Item(node, p)),
                    "occupants" => Enumerable.Range(0, bed.SleepingSlotsCount).Select(i => Child(node, "ObjectBedOccupant", "slot:" + i, new OccupantSource(bed, i))),
                    "canUseBedNow" => CanUseBed(bed, PawnContext(context, node, true)),
                    "isValidBedFor" => ValidBed(bed, PawnContext(context, node, true), PawnContext(context, node, false)), _ => Unknown(node, field)
                };
            case "ObjectBedOccupant":
                var occupant = (OccupantSource)node.Handle!;
                return field switch { "slot" => occupant.Slot, "pawnId" => BedOccupant(occupant)?.GetUniqueLoadID(), _ => Unknown(node, field) };
            case "ObjectProduction":
                var giver = (IBillGiver)node.Handle!;
                return field switch {
                    "billOwnerId" => node.Owner.Id, "billOwnerRole" => node.Owner.Handle is Pawn ? "surgery_recipient" : node.Owner.Handle is Building_WorkTable ? "worktable" : "bill_giver",
                    "nativeInterface" => "IBillGiver", "bills" => Bills(node, giver), _ => Unknown(node, field)
                };
            case "ObjectBill":
                var bill = (Bill)node.Handle!; var productionBill = bill as Bill_Production;
                return field switch {
                    "id" => bill.GetUniqueLoadID(), "billKind" => bill is Bill_Production ? "production" : bill is Bill_Medical ? "medical" : "other",
                    "runtimeType" => bill.GetType().FullName, "label" => bill.LabelCap.ToString(), "recipe" => bill.recipe?.defName,
                    "suspended" => bill.suspended, "repeatMode" => productionBill?.repeatMode?.defName,
                    "repeatCount" => productionBill?.repeatCount, "targetCount" => productionBill?.targetCount,
                    "ingredientDefinitions" => bill.ingredientFilter.AllowedThingDefs.Select(ThingData.Definition), _ => Unknown(node, field)
                };
            case "ObjectStorage":
                var storage = (ISlotGroupParent)node.Handle!;
                return field switch {
                    "storedRecords" => StorageGroup(storage).HeldThingsCount,
                    "priority" => StorageSettings(storage).Priority.ToString(),
                    "cells" => StorageGroup(storage).CellsList.Select(c => Cell(node, c.x + ":" + c.z, thing!.MapHeld, c)),
                    "allowedDefinitions" => StorageSettings(storage).filter.AllowedThingDefs.Select(ThingData.Definition), _ => Unknown(node, field)
                };
            case "ObjectPlant":
                var plant = (Plant)node.Handle!;
                return field switch {
                    "growth" => Native(plant, "get_Growth", () => plant.Growth), "lifeStage" => Native(plant, "get_LifeStage", () => plant.LifeStage.ToString()),
                    "blighted" => plant.Blighted, "harvestableNow" => Native(plant, "get_HarvestableNow", () => plant.HarvestableNow),
                    "yieldNowStatus" => "not_computed", "yieldNowReason" => "native_yield_consumes_rng", _ => Unknown(node, field)
                };
            case "ObjectTurret":
                var turret = (Building_Turret)node.Handle!;
                return field switch {
                    "active" => turret is Building_TurretGun gun ? gun.Active : null,
                    "target" => TurretTarget(node, turret), "verb" => WorldNativeAccess.ExistingAttacks(turret).FirstOrDefault()?.GetType().FullName,
                    "range" => WorldNativeAccess.ExistingAttacks(turret).FirstOrDefault()?.verbProps.range, _ => Unknown(node, field)
                };
            case "ObjectTrap": return field == "springChanceToActor" ? TrapChance((Building_Trap)node.Handle!, PawnContext(context, node, false)) : Unknown(node, field);
            case "ObjectConstruction":
                var construct = (Thing)node.Handle!;
                return field switch {
                    "targetDefinition" => construct is Frame frame ? frame.BuildDef?.defName : (construct as Blueprint)?.def.entityDefToBuild?.defName,
                    "stage" => construct is Blueprint ? "blueprint" : "frame",
                    "workProgress" => construct is Blueprint ? "not_started" : null,
                    "workRemaining" => construct is Frame f1 ? f1.WorkLeft : null,
                    "workTotal" => construct is Frame f2 ? f2.WorkToBuild : null,
                    "fractionComplete" => construct is Frame f3 ? f3.PercentComplete : null, _ => Unknown(node, field)
                };
            case "ObjectContainment":
                var container = (Thing)node.Handle!;
                return field switch {
                    "holderType" => container.ParentHolder?.GetType().FullName,
                    "holder" => container.ParentHolder is Thing parent ? Item(node, parent) : null,
                    "realContents" => container.ParentHolder == null || ThingOwnerUtility.AreImmediateContentsReal(container.ParentHolder),
                    "holderChain" => HolderChain(node, container), "contents" => Contents(node, container), _ => Unknown(node, field)
                };
            case "ObjectHolder":
                var holder = (IThingHolder)node.Handle!;
                return field switch { "ref" => context.NativeReference(holder), "runtimeType" => holder.GetType().FullName, "thingId" => (holder as Thing)?.GetUniqueLoadID(), _ => Unknown(node, field) };
            case "ObjectComponent": return field switch { "ref" => context.NativeReference(node.Handle!), "runtimeType" => node.Handle!.GetType().FullName, _ => Unknown(node, field) };
            case "ObjectReservations":
                if (node.Handle is CellSource reservedCell) return field switch {
                    "normal" => CellNormalReservations(node, reservedCell), "physicalInteraction" => CellPhysicalReservations(node, reservedCell), "destinations" => CellDestinationReservations(node, reservedCell), "sampleTick" => Find.TickManager.TicksGame, _ => Unknown(node, field)
                };
                return field switch { "normal" => NormalReservations(node, thing!), "physicalInteraction" => PhysicalReservations(node, thing!), "destinations" => DestinationReservations(node, thing!), "sampleTick" => Find.TickManager.TicksGame, _ => Unknown(node, field) };
            case "ObjectReservation": return Reservation(node, field, context);
            default: return Unknown(node, field);
        }
    }

    private static object? Facet(GameNode node, string field, Thing t)
    {
        object? handle = field switch {
            "condition" or "containment" => t,
            "reservations" => t.MapHeld == null ? null : t,
            "bed" => t as Building_Bed, "door" => t as Building_Door, "production" => t as IBillGiver,
            "plant" => t as Plant, "turret" => t as Building_Turret, "trap" => t as Building_Trap, "storage" => t as ISlotGroupParent,
            "construction" => t is Frame or Blueprint ? t : null,
            "power" => t.TryGetComp<CompPowerTrader>(), "fuel" => t.TryGetComp<CompRefuelable>(), "battery" => t.TryGetComp<CompPowerBattery>(),
            "temperatureControl" => t.TryGetComp<CompTempControl>(), "ammunition" => t.TryGetComp<CompChangeableProjectile>(), "breakdown" => t.TryGetComp<CompBreakdownable>(),
            "switch" => t.TryGetComp<CompFlickable>(), "rot" => t.TryGetComp<CompRottable>(), "manning" => t.TryGetComp<CompMannable>(), "explosive" => t.TryGetComp<CompExplosive>(),
            _ => throw ReadContext.Error("unsupported_member", "Unknown physical facet: " + field)
        };
        return handle == null ? null : Child(node, "Object" + char.ToUpperInvariant(field[0]) + field.Substring(1), field, handle);
    }
    private static GameNode Child(GameNode parent, string kind, string key, object? handle) {
        var identity=kind is "ObjectHolder" or "ObjectComponent" or "ObjectRoom" || kind=="ObjectReservation" && handle!=null && !handle.GetType().IsValueType?handle:null;
        if(kind=="ObjectReservation" && handle!=null && handle.GetType().IsValueType)key=ReservationKey(handle,key.Split(':')[0]);
        return new(kind,parent.Key+"/"+key,parent.Owner,handle,identity);
    }
    private static string ReservationKey(object value,string kind) {
        string Target(LocalTargetInfo target)=>target.HasThing?target.Thing.GetUniqueLoadID():target.Cell.x+":"+target.Cell.y+":"+target.Cell.z;
        if(value is ReservationManager.Reservation normal)return kind+":"+normal.Claimant?.GetUniqueLoadID()+":"+normal.Job?.GetUniqueLoadID()+":"+normal.Layer?.defName+":"+Target(normal.Target);
        if(value is PhysicalInteractionReservationManager.PhysicalInteractionReservation physical)return kind+":"+physical.claimant?.GetUniqueLoadID()+":"+physical.job?.GetUniqueLoadID()+":"+Target(physical.target);
        if(value is PawnDestinationReservationManager.PawnDestinationReservation destination)return kind+":"+destination.claimant?.GetUniqueLoadID()+":"+destination.job?.GetUniqueLoadID()+":"+destination.target.x+":"+destination.target.y+":"+destination.target.z;
        throw ReadContext.Error("source_identity_unavailable","Reservation has no supported native identity.");
    }
    private static GameNode Item(GameNode parent, Thing thing) => new("Thing", thing.GetUniqueLoadID(), new GameEntity(thing.GetUniqueLoadID(), thing.MapHeld?.GetUniqueLoadID(), thing), thing);
    private static GameNode Job(GameNode parent, Job job) => Child(parent, "ObjectJob", job.GetUniqueLoadID(), job);
    private static GameNode Cell(GameNode parent, string key, Map? map, IntVec3 cell) => Child(parent, "ObjectCell", key, new CellSource(map, cell));
    private static object Unknown(GameNode node, string field) => throw ReadContext.Error("unsupported_member", "Unknown selected native field: " + node.Kind + "." + field);
    private static Pawn PawnContext(ReadContext context, GameNode node, bool recipient)
    {
        var entity = recipient ? context.ObjectRecipient : context.ObjectActor;
        if (entity?.Handle is not Pawn pawn || pawn.Destroyed) throw ReadContext.Error("context_unavailable", (recipient ? "recipient" : "actor") + " is required; no role is inferred.");
        var map = node.Owner.Handle is Map ownerMap ? ownerMap : (node.Owner.Handle as Thing)?.MapHeld;
        if (pawn.MapHeld != map) throw ReadContext.Error("context_unavailable", "Context and target are on different maps.");
        return pawn;
    }
    private static T Native<T>(object target, string method, Func<T> read) { WorldNativeAccess.RequireNative(target, method); return read(); }
    private static bool Forbidden(Thing target, Pawn actor) { GameImplementation.RequireInspected((typeof(ForbidUtility), nameof(ForbidUtility.IsForbidden))); return target.IsForbidden(actor); }
    private static bool CanUseBed(Building_Bed bed, Pawn user) { GameImplementation.RequireInspected((typeof(RestUtility), nameof(RestUtility.CanUseBedNow))); WorldNativeAccess.RequireNative(bed, "get_Medical", "get_SleepingSlotsCount", "GetCurOccupant"); return RestUtility.CanUseBedNow(bed, user, true); }
    private static bool ValidBed(Building_Bed bed, Pawn user, Pawn actor) { GameImplementation.RequireInspected((typeof(RestUtility), nameof(RestUtility.IsValidBedFor))); WorldNativeAccess.RequireNative(bed, "get_Medical", "get_SleepingSlotsCount", "GetCurOccupant"); return RestUtility.IsValidBedFor(bed, user, actor, true); }
    private static Pawn? BedOccupant(OccupantSource source) => Native(source.Bed, "GetCurOccupant", () => source.Bed.GetCurOccupant(source.Slot));
    private static bool DoorCheck(Building_Door door, Pawn actor, bool physical) { var method = physical ? nameof(Building_Door.CanPhysicallyPass) : nameof(Building_Door.PawnCanOpen); GameImplementation.RequireInspected((typeof(Building_Door), method)); WorldNativeAccess.RequireNative(door, method); return physical ? door.CanPhysicallyPass(actor) : door.PawnCanOpen(actor); }
    private static object? TrapChance(Building_Trap trap, Pawn actor) { GameImplementation.RequireInspected((typeof(Building_Trap), "SpringChance")); WorldNativeAccess.RequireNative(trap, "SpringChance"); return typeof(Building_Trap).GetMethod("SpringChance", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(trap, new object[] { actor }); }
    private static IEnumerable<GameNode> Bills(GameNode parent, IBillGiver giver) { WorldNativeAccess.RequireNative(giver, "get_BillStack"); foreach (var bill in giver.BillStack.Bills) yield return Child(parent, "ObjectBill", bill.GetUniqueLoadID(), bill); }
    private static SlotGroup StorageGroup(ISlotGroupParent parent) => Native(parent, "GetSlotGroup", parent.GetSlotGroup);
    private static StorageSettings StorageSettings(ISlotGroupParent parent) => Native(parent, "GetStoreSettings", parent.GetStoreSettings);
    private static GameNode? TurretTarget(GameNode parent, Building_Turret turret) { WorldNativeAccess.RequireNative(turret, "get_CurrentTarget"); var target = turret.CurrentTarget; return !target.IsValid ? null : Child(parent, "ObjectJobTarget", "target", new TargetSource(null, target)); }
    private static IEnumerable<GameNode> JobTargets(GameNode parent, Job job) { foreach (var slot in new[] { TargetIndex.A, TargetIndex.B, TargetIndex.C }) { var target = job.GetTarget(slot); if (target.IsValid) yield return Child(parent, "ObjectJobTarget", slot.ToString(), new TargetSource(slot.ToString(), target)); } }
    private static IEnumerable<GameNode>? Contents(GameNode parent, Thing thing) { if (thing is not IThingHolder holder) return null; WorldNativeAccess.RequireNative(holder, "GetDirectlyHeldThings"); var owner = holder.GetDirectlyHeldThings(); return owner?.Select(t => Item(parent, t)); }
    private static IEnumerable<GameNode> HolderChain(GameNode parent, Thing thing) { var seen = new HashSet<IThingHolder>(); var holder = thing.ParentHolder; while (holder != null) { if (!seen.Add(holder)) throw ReadContext.Error("containment_cycle", "Native holder chain contains a cycle."); yield return Child(parent, "ObjectHolder", "holder:" + seen.Count, holder); holder = holder.ParentHolder; } }
    private static IEnumerable<GameNode> NormalReservations(GameNode parent, Thing thing) { var index = 0; foreach (var row in thing.MapHeld.reservationManager.ReservationsReadOnly) if (row.Target.HasThing && row.Target.Thing == thing) yield return Child(parent, "ObjectReservation", "normal:" + index++, row); }
    private static IEnumerable<GameNode> PhysicalReservations(GameNode parent, Thing thing) { var index = 0; foreach (var row in WorldNativeAccess.PhysicalRows(thing.MapHeld)) if (row.target.HasThing && row.target.Thing == thing) yield return Child(parent, "ObjectReservation", "physical:" + index++, row); }
    private static IEnumerable<GameNode> DestinationReservations(GameNode parent, Thing thing) { var index = 0; if (!thing.Spawned) yield break; foreach (var row in WorldNativeAccess.DestinationRows(thing.Map)) if (thing.OccupiedRect().Contains(row.target)) yield return Child(parent, "ObjectReservation", "destination:" + index++, row); }
    private static IEnumerable<GameNode>? UnsupportedComponents(GameNode parent, Thing thing)
    {
        if (thing is not ThingWithComps composed) return null;
        return composed.AllComps.Where(c => c is not (CompPowerTrader or CompRefuelable or CompPowerBattery or CompTempControl or CompChangeableProjectile or CompBreakdownable or CompFlickable or CompRottable or CompMannable or CompExplosive or CompForbiddable or CompQuality or CompAssignableToPawn or CompIngredients or CompEquippable))
            .Select((c, index) => Child(parent, "ObjectComponent", "component:" + index, c));
    }
    private static object? Reservation(GameNode node, string field, ReadContext context)
    {
        var value = node.Handle!;
        if (field == "ref") return context.NativeReference(value);
        if (value is ReservationManager.Reservation normal) return field switch {
            "claimantId" => normal.Claimant?.GetUniqueLoadID(), "layer" => normal.Layer?.defName,
            "maxPawns" => normal.MaxPawns, "stackCount" => normal.StackCount, "wholeStack" => normal.StackCount == -1,
            "job" => normal.Job == null ? null : Job(node, normal.Job), "cell" => normal.Target.HasThing ? null : Cell(node, "cell", node.Owner.Handle as Map ?? (node.Owner.Handle as Thing)?.MapHeld, normal.Target.Cell), "obsolete" => null, _ => Unknown(node, field)
        };
        if (value is PhysicalInteractionReservationManager.PhysicalInteractionReservation physical) return field switch {
            "claimantId" => physical.claimant?.GetUniqueLoadID(), "job" => physical.job == null ? null : Job(node, physical.job),
            "cell" => physical.target.HasThing ? null : Cell(node, "cell", node.Owner.Handle as Map ?? (node.Owner.Handle as Thing)?.MapHeld, physical.target.Cell),
            "layer" or "maxPawns" or "stackCount" or "wholeStack" or "obsolete" => null, _ => Unknown(node, field)
        };
        if (value is PawnDestinationReservationManager.PawnDestinationReservation destination) return field switch {
            "claimantId" => destination.claimant?.GetUniqueLoadID(), "job" => destination.job == null ? null : Job(node, destination.job),
            "cell" => Cell(node, "cell", node.Owner.Handle as Map ?? (node.Owner.Handle as Thing)?.MapHeld, destination.target), "obsolete" => destination.obsolete,
            "layer" or "maxPawns" or "stackCount" or "wholeStack" => null, _ => Unknown(node, field)
        };
        return Unknown(node, field);
    }
    internal static IEnumerable<GameNode> CellsRoot(IReadOnlyDictionary<string, object?> args)
    {
        GameImplementation.RequireKnownBinary();
        var mapId = args.TryGetValue("mapId", out var rawMap) ? rawMap as string : null;
        var map = Find.Maps.SingleOrDefault(m => !m.Disposed && m.GetUniqueLoadID() == mapId) ?? throw ReadContext.Error("object_unavailable", "Selected cell map is not loaded.");
        if (!args.TryGetValue("xs", out var rawXs) || rawXs is not System.Collections.IEnumerable xs || !args.TryGetValue("zs", out var rawZs) || rawZs is not System.Collections.IEnumerable zs)
            throw ReadContext.Error("invalid_arguments", "Cell selection requires finite x and z coordinates.");
        var xValues = xs.Cast<object>().Select(Convert.ToInt32).Distinct().ToArray(); var zValues = zs.Cast<object>().Select(Convert.ToInt32).Distinct().ToArray();
        var visibleOnly = args.TryGetValue("visibleOnly", out var visible) && visible is true;
        foreach (var x in xValues) foreach (var z in zValues) {
            var value = new IntVec3(x, 0, z);
            if (!value.InBounds(map)) throw ReadContext.Error("object_unavailable", "Selected cell is outside the map.");
            if (visibleOnly && value.Fogged(map)) continue;
            yield return CellNode(map, x, z);
        }
    }
    internal static GameNode CellNode(Map map, int x, int z) => new("ObjectCell", map.GetUniqueLoadID() + "/cell:" + x + ":" + z, new GameEntity(map.GetUniqueLoadID(), map.GetUniqueLoadID(), map), new CellSource(map, new IntVec3(x, 0, z)));
    private static Map CellMap(CellSource source) => source.Map != null && !source.Map.Disposed ? source.Map : throw ReadContext.Error("object_unavailable", "Cell map is unavailable.");
    private static IEnumerable<GameNode> CellNormalReservations(GameNode parent, CellSource cell) { var index = 0; foreach (var row in CellMap(cell).reservationManager.ReservationsReadOnly) if (!row.Target.HasThing && row.Target.Cell == cell.Value) yield return Child(parent, "ObjectReservation", "normal:" + index++, row); }
    private static IEnumerable<GameNode> CellPhysicalReservations(GameNode parent, CellSource cell) { var index = 0; foreach (var row in WorldNativeAccess.PhysicalRows(CellMap(cell))) if (!row.target.HasThing && row.target.Cell == cell.Value) yield return Child(parent, "ObjectReservation", "physical:" + index++, row); }
    private static IEnumerable<GameNode> CellDestinationReservations(GameNode parent, CellSource cell) { var index = 0; foreach (var row in WorldNativeAccess.DestinationRows(CellMap(cell))) if (row.target == cell.Value) yield return Child(parent, "ObjectReservation", "destination:" + index++, row); }
    private sealed class CellSource { internal CellSource(Map? map, IntVec3 value) { Map = map; Value = value; } internal Map? Map { get; } internal string? MapId => Map?.GetUniqueLoadID(); internal IntVec3 Value { get; } }
    private sealed class GasSource { internal GasSource(CellSource cell, GasType type) { Cell = cell; Type = type; } internal CellSource Cell { get; } internal GasType Type { get; } }
    private sealed class TargetSource { internal TargetSource(string? slot, LocalTargetInfo value) { Slot = slot; Value = value; } internal string? Slot { get; } internal LocalTargetInfo Value { get; } }
    private sealed class OccupantSource { internal OccupantSource(Building_Bed bed, int slot) { Bed = bed; Slot = slot; } internal Building_Bed Bed { get; } internal int Slot { get; } }
}
