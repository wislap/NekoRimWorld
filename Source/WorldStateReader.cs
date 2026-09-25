using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GraphQL;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
using Verse.AI;
namespace Neko.RimWorld;

// Explicit native facets. Unknown components are discovered, never reflected.
internal sealed class WorldStateReader
{
    private readonly Map map;
    private List<ReservationManager.Reservation> reservations = null!;
    private readonly ReadContext? context;
    private Dictionary<Thing, ReservationManager.Reservation[]> reservationIndex = null!;
    private PhysicalInteractionReservationManager.PhysicalInteractionReservation[] physicalRows = null!;
    private PawnDestinationReservationManager.PawnDestinationReservation[] destinationRows = null!;
    private int reservationTick;
    private bool reservationsRead;
    internal WorldStateReader(Map map, ReadContext? context = null) { this.map = map; this.context = context; }
    private void ReadReservations()
    {
        if (reservationsRead) return;
        reservations = map.reservationManager.ReservationsReadOnly.ToList();
        reservationIndex = reservations.Where(r => r.Target.HasThing).GroupBy(r => r.Target.Thing).ToDictionary(g => g.Key, g => g.ToArray());
        physicalRows = WorldNativeAccess.PhysicalRows(map);
        destinationRows = WorldNativeAccess.DestinationRows(map);
        reservationTick = Find.TickManager.TicksGame;
        reservationsRead = true;
    }
    internal static JObject Cell(Map map, IntVec3 c) => SpatialQuery.DescribeCell(map, c);
    internal static JObject Identity(Thing t) => new()
    {
        ["id"] = t.GetUniqueLoadID(),
        ["occupied_rect"] = t.Spawned ? new JObject { ["min_x"] = t.OccupiedRect().minX, ["max_x"] = t.OccupiedRect().maxX, ["min_z"] = t.OccupiedRect().minZ, ["max_z"] = t.OccupiedRect().maxZ } : null,
        ["kind"] = t is Pawn ? "pawn" : "thing",
        ["name"] = WorldNativeAccess.NativeMethod(t, "get_Label") ? t.LabelShortCap.ToString() : t.def.LabelCap.ToString(),
        ["definition"] = t.def.defName,
        ["runtime_type"] = t.GetType().FullName,
        ["map"] = t.MapHeld?.GetUniqueLoadID(),
        ["spawned"] = t.Spawned,
        ["destroyed"] = t.Destroyed,
        ["position"] = t.Spawned && t.Map != null ? Cell(t.Map, t.Position) : null,
        ["held_position"] = t.SpawnedOrAnyParentSpawned && t.MapHeld != null ? Cell(t.MapHeld, t.PositionHeld) : null,
        ["position_kind"] = t.Spawned ? "own" : "holder",
        ["holder_type"] = t.ParentHolder?.GetType().FullName, ["holder_chain"] = HolderChain(t),
        ["pawn_membership"] = t is Pawn member ? new JObject{["is_colonist"]=member.IsColonist,["is_free_colonist"]=member.IsFreeColonist,["is_prisoner"]=member.IsPrisonerOfColony,["is_slave"]=member.IsSlaveOfColony}:null,
        ["faction"] = t.Faction == null ? null : new JObject { ["name"] = t.Faction.Name, ["definition"] = t.Faction.def.defName, ["is_player"] = t.Faction == Faction.OfPlayer },
        ["capabilities"] = new JArray(ThingCapabilities.For(t)),
        ["capability_meanings"] = t is IBillGiver ? WorldObservationContract.CapabilityMeanings() : null
    };
    private static JObject ObjectIdentity(Thing thing)
    {
        var value = Identity(thing);
        value["name"] = thing is Pawn ? thing.LabelShort : thing.Label;
        value["definition_label"] = thing.def.LabelCap.ToString();
        value["guest_status"] = (thing as Pawn)?.GuestStatus?.ToString();
        var holder = ThingData.HeldByPawn(thing);
        value["held_by"] = holder == null ? null : new JObject { ["id"] = holder.GetUniqueLoadID(), ["runtime_type"] = holder.GetType().FullName };
        JObject? FactionInfo(Faction? faction) => faction == null ? null : new JObject {
            ["name"] = faction.Name, ["def_name"] = faction.def.defName,
            ["is_player"] = faction == Faction.OfPlayer, ["player_relation"] = faction.PlayerRelationKind.ToString()
        };
        value["faction"] = FactionInfo(thing.Faction);
        value["held_by_faction"] = FactionInfo(holder?.Faction);
        return value;
    }

    private static JObject HolderChain(Thing t)
    {
        var rows = new JArray(); var seen = new HashSet<IThingHolder>(); var h = t.ParentHolder;
        while (h != null && rows.Count < 24 && seen.Add(h)) {
            rows.Add(new JObject { ["runtime_type"] = h.GetType().FullName, ["thing_id"] = (h as Thing)?.GetUniqueLoadID() }); h = h.ParentHolder;
        }
        return new JObject { ["rows"] = rows, ["complete"] = h == null, ["meaning"] = "Current containment chain, not ownership or walking access." };
    }
    internal JObject CellReservations(IntVec3 cell)
    {
        ReadReservations();
        var value = new JObject {
            ["normal"] = new JArray(reservations.Where(r => !r.Target.HasThing && r.Target.Cell == cell).Select(r => new JObject { ["claimant"] = r.Claimant?.GetUniqueLoadID(), ["job"] = JobState(r.Job), ["layer"] = r.Layer?.defName, ["max_pawns"] = r.MaxPawns, ["stack_count"] = r.StackCount })),
            ["physical_interaction"] = new JArray(physicalRows.Where(r => !r.target.HasThing && r.target.Cell == cell).Select(r => new JObject { ["claimant"] = r.claimant?.GetUniqueLoadID(), ["job"] = JobState(r.job) })),
            ["destinations"] = new JArray(destinationRows.Where(r => r.target == cell).Select(r => new JObject { ["claimant"] = r.claimant?.GetUniqueLoadID(), ["job"] = JobState(r.job), ["obsolete"] = r.obsolete })),
            ["sample_tick"] = reservationTick, ["meaning"] = "Cell-specific records; reservations of objects occupying the cell remain in object state." };
        WorldPresentation.Reservations(value, Cell(map, cell), true);
        return value;
    }
    internal JObject ReadSpatial(Thing t, Pawn? actor, Pawn? user, bool details)
        => Read(t, actor, user, Array.Empty<string>(), details, new[] { "spatial" });

    internal JObject Read(Thing t, Pawn? actor, Pawn? user, IEnumerable<string> stats, bool details, string[]? sections = null, int? detailLimit = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew(); var facets = new JObject(); var unsupported = new JArray(); var result = new JObject { ["target"] = sections == null || sections.Contains("spatial") ? Identity(t) : ObjectIdentity(t), ["facets"] = facets, ["unsupported_components"] = unsupported, ["sample_tick"] = Find.TickManager.TicksGame };
        bool All = sections == null || sections.Contains("state");
        bool spatial = sections?.Contains("spatial") == true;
        bool Wants(string section) => All || sections!.Contains(section);
        bool Facet(string name) => spatial ? SpatialStatePolicy.Includes(name) : All || name == "stats" || name == "pawn" && (Wants("health") || Wants("skills") || Wants("gear")) || name == "work" && Wants("work");
        void Add(string name, Func<JObject> read) { if (!Facet(name)) return; try { context?.Check(); var value = read(); if (value["status"] == null) value["status"] = "observed"; facets[name] = value; } catch (Exception e) when (ReadContext.Recoverable(e)) { facets[name] = new JObject { ["status"] = (e as ExecutionError)?.Code == "unsupported_mod_override" ? "unsupported" : "unavailable", ["reason"] = (e as ExecutionError)?.Code ?? e.GetType().Name }; } }
        Add("condition", () => {
            var forbiddable = t.TryGetComp<CompForbiddable>();
            var value = new JObject { ["stack_count"] = t.stackCount, ["hit_points"] = t.def.useHitPoints ? (JToken)t.HitPoints : JValue.CreateNull(), ["max_hit_points"] = t.def.useHitPoints ? (JToken)t.MaxHitPoints : JValue.CreateNull(), ["burning"] = t.IsBurning(), ["quality"] = t.TryGetQuality(out var q) ? q.ToString() : null,
                ["forbiddable_component_present"] = forbiddable != null, ["forbidden"] = forbiddable?.Forbidden,
                ["forbidden_to_actor"] = actor == null ? null : (JToken)t.IsForbidden(actor),
                ["field_semantics"] = new JObject { ["forbidden"] = "CompForbiddable.Forbidden only. Absent component means this flag is not applicable; native faction-level forbidden defaults false. It does not settle actor area/job restrictions.", ["forbidden_to_actor"] = "Native IsForbidden(actor), including contextual restrictions; not universal job eligibility." } };
            WorldPresentation.NullField(value, "hit_points", "not_applicable", "definition_has_no_hit_points");
            WorldPresentation.NullField(value, "max_hit_points", "not_applicable", "definition_has_no_hit_points");
            WorldPresentation.NullField(value, "quality", "not_applicable", "native_quality_not_available");
            WorldPresentation.NullField(value, "forbidden", "not_applicable", "forbiddable_component_absent");
            WorldPresentation.NullField(value, "forbidden_to_actor", "not_computed", "actor_required");
            return value;
        });
        Add("reservations", () => ReservationState(t, actor));
        Add("containment", () => { if (t is IThingHolder hh) WorldNativeAccess.RequireNative(hh, "GetDirectlyHeldThings"); var owner = t is IThingHolder h ? h.GetDirectlyHeldThings() : null; var value = new JObject { ["holder"] = t.ParentHolder is Thing p ? Identity(p) : null, ["holder_type"] = t.ParentHolder?.GetType().FullName, ["holder_chain"] = HolderChain(t), ["real_contents"] = t.ParentHolder == null || ThingOwnerUtility.AreImmediateContentsReal(t.ParentHolder), ["contents"] = owner == null ? null : new JObject { ["count"] = owner.Count, ["rows"] = new JArray(owner.Cast<Thing>().Take(details ? 100 : 8).Select(Identity)), ["complete"] = owner.Count <= (details ? 100 : 8) } };
            WorldPresentation.NullField(value, "holder", t.ParentHolder == null ? "observed_none" : "not_applicable", t.ParentHolder == null ? "no_parent_holder" : "parent_holder_is_not_a_thing");
            WorldPresentation.NullField(value, "contents", "not_applicable", t is IThingHolder ? "no_direct_container" : "not_a_thing_holder"); return value; });
        if ((All || spatial) && t is ThingWithComps twc)
        {
            foreach (var comp in twc.AllComps.ToArray())
            {
                if (comp is CompPowerTrader power) Add("power", () => new JObject { ["on"] = power.PowerOn, ["output_watts"] = power.PowerOutput, ["connected"] = power.PowerNet != null });
                else if (comp is CompRefuelable fuel) Add("fuel", () => new JObject { ["amount"] = fuel.Fuel, ["capacity"] = fuel.Props.fuelCapacity, ["target"] = fuel.TargetFuelLevel, ["has_fuel"] = fuel.HasFuel, ["auto_refuel"] = fuel.allowAutoRefuel });
                else if (comp is CompPowerBattery battery) Add("battery", () => new JObject { ["stored_watt_days"] = battery.StoredEnergy, ["capacity_watt_days"] = battery.Props.storedEnergyMax });
                else if (comp is CompTempControl temp) Add("temperature_control", () => new JObject { ["target_c"] = temp.targetTemperature, ["operating_high_power"] = temp.operatingAtHighPower });
                else if (comp is CompChangeableProjectile ammo) Add("ammunition", () => new JObject { ["loaded_definition"] = ammo.LoadedShell?.defName, ["loaded_count"] = ammo.loadedCount });
                else if (comp is CompBreakdownable broken) Add("breakdown", () => new JObject { ["broken"] = broken.BrokenDown });
                else if (comp is CompFlickable flick) Add("switch", () => new JObject { ["on"] = flick.SwitchIsOn });
                else if (comp is CompRottable rot) Add("rot", () => new JObject { ["progress"] = rot.RotProgress, ["stage"] = rot.Stage.ToString(), ["ticks_until_rot_at_current_temperature"] = rot.TicksUntilRotAtCurrentTemp });
                else if (comp is CompMannable man) Add("manning", () => new JObject { ["pawn"] = man.ManningPawn == null ? null : Identity(man.ManningPawn), ["manned"] = man.MannedNow });
                else if (comp is CompExplosive explosive) Add("explosive", () => new JObject { ["wick_started"] = explosive.wickStarted, ["meaning"] = "Observed fuse state; not a forecast of all explosion causes." });
                else if (comp is not (CompForbiddable or CompQuality or CompAssignableToPawn or CompIngredients or CompEquippable)) unsupported.Add(comp.GetType().FullName);
            }
        }
        if (t is Pawn pawn)
        {
            Add("pawn", () => PawnState(pawn, details, sections, detailLimit));
            Add("work", () => new JObject { ["current_job"] = CurrentJobState(pawn), ["queued_jobs"] = pawn.jobs == null ? null : new JArray(pawn.jobs.jobQueue.Take(32).Select(j => JobState(j.job))), ["queued_jobs_total"] = pawn.jobs?.jobQueue.Count, ["queued_jobs_complete"] = pawn.jobs == null || pawn.jobs.jobQueue.Count <= 32, ["drafted"] = pawn.Drafted, ["mental_state"] = pawn.MentalStateDef?.defName, ["work_types"] = new JArray(DefDatabase<WorkTypeDef>.AllDefsListForReading.Select(w => new JObject { ["definition"] = w.defName, ["disabled"] = pawn.WorkTypeIsDisabled(w), ["priority"] = pawn.workSettings == null ? null : (JToken)pawn.workSettings.GetPriority(w) })), ["medical_care"] = pawn.playerSettings?.medCare.ToString(), ["allowed_area"] = pawn.playerSettings?.EffectiveAreaRestrictionInPawnCurrentMap?.GetUniqueLoadID() });
        }
        if (t is Building_Bed bed) Add("bed", () => BedState(bed, user, actor));
        if (t is Building_Door door) Add("door", () => { WorldNativeAccess.RequireNative(door, "PawnCanOpen", "CanPhysicallyPass"); return new JObject { ["open"] = door.Open, ["hold_open"] = door.HoldOpen, ["context_checks"] = new JObject{["status"]=actor==null?"not_computed":"evaluated",["reason"]=actor==null?"actor_required":null,["actor_id"]=actor?.GetUniqueLoadID()}, ["actor_can_open"] = actor == null ? null : (JToken)door.PawnCanOpen(actor), ["actor_can_physically_pass"] = actor == null ? null : (JToken)door.CanPhysicallyPass(actor), ["checks_meaning"] = "Native door predicates at this observation; no move/open command issued." }; });
        if (t is IBillGiver giver) Add("production", () => { WorldNativeAccess.RequireNative(giver, "get_BillStack"); var bills = giver.BillStack.Bills; return new JObject {
            ["bill_owner"] = new JObject { ["id"] = t.GetUniqueLoadID(), ["role"] = t is Pawn ? "surgery_recipient" : t is Building_WorkTable ? "worktable" : "bill_giver", ["native_interface"] = "IBillGiver" },
            ["bills"] = new JArray(bills.Take(details ? 100 : 12).Select(b => new JObject { ["bill_kind"] = b is Bill_Production ? "production" : b is Bill_Medical ? "medical" : "other", ["runtime_type"] = b.GetType().FullName, ["label"] = b.LabelCap.ToString(), ["recipe"] = b.recipe.defName, ["suspended"] = b.suspended, ["repeat_mode"] = (b as Bill_Production)?.repeatMode?.defName, ["repeat_count"] = (b as Bill_Production)?.repeatCount, ["target_count"] = (b as Bill_Production)?.targetCount, ["ingredient_definitions"] = new JArray(b.ingredientFilter.AllowedThingDefs.Take(64).Select(d => d.defName)), ["ingredient_definitions_complete"] = b.ingredientFilter.AllowedThingDefs.Count() <= 64 })), ["complete"] = bills.Count <= (details ? 100 : 12), ["meaning"] = "IBillGiver bill policy (Pawn surgery orders or worktable bills). Compatibility facet name production does not imply manufacturing. Ingredient definitions are allowed filters, not required quantities or stock. Null repeat fields are not applicable to non-production bills. No proof of a runnable Job." }; });
        if (t is Plant plant) Add("plant", () => { WorldNativeAccess.RequireNative(plant, "get_Growth", "get_HarvestableNow", "get_LifeStage"); return new JObject { ["growth"] = plant.Growth, ["life_stage"] = plant.LifeStage.ToString(), ["blighted"] = plant.Blighted, ["harvestable_now"] = plant.HarvestableNow, ["yield_now"] = new JObject { ["status"] = "not_computed", ["reason"] = "Native YieldNow consumes game RNG; observation does not sample harvest output." } }; });
        if (t is Building_Turret turret) Add("turret", () => { WorldNativeAccess.RequireNative(turret, "get_CurrentTarget", "get_AttackVerb"); var verbs = WorldNativeAccess.ExistingAttacks(turret); return new JObject { ["active"] = turret is Building_TurretGun gun ? (JToken)gun.Active : JValue.CreateNull(), ["target"] = TargetState(turret.CurrentTarget), ["verb"] = verbs.FirstOrDefault()?.GetType().FullName, ["range"] = verbs.FirstOrDefault()?.verbProps.range, ["meaning"] = "Current native target and weapon; not a prediction of firing." }; });
        if (t is Building_Trap trap) Add("trap", () => { WorldNativeAccess.RequireNative(trap, "SpringChance"); return new JObject { ["context_checks"] = new JObject{["status"]=actor==null?"not_computed":"evaluated",["reason"]=actor==null?"actor_required":null,["meaning"]="Absent actor is not an unarmed or inactive trap."}, ["spring_chance_to_actor"] = actor == null ? null : JToken.FromObject(typeof(Building_Trap).GetMethod("SpringChance", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(trap, new object[] { actor })!), ["meaning"] = "Native chance for supplied actor, not a simulated trigger." }; });
        if (t is ISlotGroupParent storage) Add("storage", () => { WorldNativeAccess.RequireNative(storage, "GetSlotGroup", "GetStoreSettings"); var group = storage.GetSlotGroup(); var settings = storage.GetStoreSettings(); return new JObject { ["cells"] = new JArray(group.CellsList.Take(100).Select(c => Cell(map, c))), ["cells_complete"] = group.CellsList.Count <= 100, ["stored_records"] = group.HeldThingsCount, ["priority"] = settings.Priority.ToString(), ["allowed_definitions"] = new JArray(settings.filter.AllowedThingDefs.Take(100).Select(d => d.defName)), ["allowed_definitions_complete"] = settings.filter.AllowedThingDefs.Count() <= 100, ["meaning"] = "Acceptance policy, not a guarantee for a specific stack/quality or hauling Job." }; });
        if (t is Frame frame) Add("construction", () => new JObject { ["target_definition"] = frame.BuildDef?.defName, ["work_remaining"] = frame.WorkLeft, ["work_total"] = frame.WorkToBuild, ["fraction_complete"] = frame.PercentComplete });
        else if (t is Blueprint blueprint) Add("construction", () => new JObject { ["target_definition"] = blueprint.def.entityDefToBuild?.defName, ["stage"] = "blueprint", ["work_progress"] = "not_started" });
        var requested = stats.Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Length > 0) Add("stats", () => ReadStats(t, requested));
        if (facets["stats"] is JObject statsFacet && statsFacet["values"] is JArray) WorldPresentation.Stats(statsFacet);
        if (facets["work"] is JObject work && work["work_types"] is JArray) {
            WorldPresentation.Work(work);
            work["medical_care_context"] = new JObject { ["pawn_id"] = t.GetUniqueLoadID(), ["role"] = "care_received", ["native_source"] = "Pawn.playerSettings.medCare", ["meaning"] = "Medicine policy for this pawn when receiving treatment, not how this pawn treats others. Treatment medicine selection uses the recipient's setting." };
            WorldPresentation.NullField(work, "medical_care", "not_applicable", "player_settings_absent");
            WorldPresentation.NullField(work, "current_job", "observed_none", "no_current_job");
            WorldPresentation.NullField(work, "queued_jobs", "not_applicable", "job_tracker_absent");
            WorldPresentation.NullField(work, "allowed_area", "observed_none", "no_effective_area_restriction");
        }
        if (detailLimit.HasValue) {
            if (facets["pawn"] is JObject pawnFacts) WorldPresentation.LimitRows(pawnFacts, detailLimit.Value);
            if (facets["work"] is JObject workFacts) WorldPresentation.LimitRows(workFacts, detailLimit.Value);
        }
        if (!All && !spatial) result["unsupported_components"] = JValue.CreateNull();
        result["requested_sections"] = sections == null ? new JArray("state") : new JArray(sections);
        result["coverage"] = new JObject { ["registered_facets_read_successfully"] = !facets.Properties().Any(p => (string?)p.Value["status"] is "partial" or "unavailable" or "unsupported"), ["components_inspected"] = All || spatial, ["unmodeled_components"] = All || spatial ? (JToken)unsupported.Count : JValue.CreateNull(), ["meaning"] = spatial ? SpatialStatePolicy.Meaning : "Requested applicable registered facets above; omitted facets are not absent. Partial facets retain observed members. Unsupported components are not empty/false. Current state and named predicates, not universal action eligibility." };
        result["read_ms"] = watch.Elapsed.TotalMilliseconds; result["cooperative_budget_overrun"] = watch.Elapsed.TotalMilliseconds > 12;
        return result;
    }
    private JObject ReadStats(Thing thing, string[] requested)
    {
        var values = new JArray();
        foreach (var name in requested)
        {
            try {
                context?.Check();
                var def = DefDatabase<StatDef>.GetNamedSilentFail(name);
                if (def == null) { values.Add(UnknownStat(name)); continue; }
                if (def.Worker.GetType().Assembly != typeof(Pawn).Assembly || def.parts != null && def.parts.Any(part => part.GetType().Assembly != typeof(Pawn).Assembly)) {
                    values.Add(new JObject { ["definition"] = name, ["status"] = "unsupported_mod_stat_worker" }); continue;
                }
                float value = thing.GetStatValue(def);
                values.Add(new JObject { ["definition"] = name, ["value"] = value, ["display"] = def.ValueToString(value), ["status"] = "observed" });
            } catch (Exception e) when (ReadContext.Recoverable(e)) {
                values.Add(new JObject { ["definition"] = name, ["status"] = "unavailable", ["reason"] = (e as ExecutionError)?.Code ?? e.GetType().Name });
            }
        }
        return new JObject { ["values"] = values };
    }

    private JObject UnknownStat(string name)
    {
        var row = new JObject { ["definition"] = name, ["status"] = "unknown_definition" };
        try {
            var discovery = DefinitionCandidates.Suggestions(DefDatabase<StatDef>.AllDefsListForReading.Select(d => (d.defName, (string?)d.label)), name, () => context?.Check());
            var capacity = DefDatabase<PawnCapacityDef>.AllDefsListForReading.FirstOrDefault(d => string.Equals(d.defName, name, StringComparison.OrdinalIgnoreCase) || string.Equals(d.label, name, StringComparison.OrdinalIgnoreCase));
            if (capacity != null) {
                discovery["matching_definition_kind"] = "PawnCapacityDef";
                discovery["matching_definition"] = capacity.defName;
                discovery["read_from"] = "health.capacities";
            }
            row["discovery"] = discovery;
        } catch (Exception e) when (ReadContext.Recoverable(e)) {
            row["discovery"] = new JObject { ["definition_kind"] = "StatDef", ["status"] = "unavailable", ["reason"] = (e as ExecutionError)?.Code ?? e.GetType().Name };
        }
        return row;
    }

    private JObject ReservationState(Thing t, Pawn? actor)
    {
        ReadReservations();
        var rows = reservationIndex.TryGetValue(t, out var reserved) ? reserved : Array.Empty<ReservationManager.Reservation>(); var physical = physicalRows.Where(r => r.target.HasThing && r.target.Thing == t).ToArray(); var destinations = destinationRows.Where(r => t.Spawned && t.OccupiedRect().Contains(r.target)).ToArray();
        var value = new JObject { ["normal"] = new JArray(rows.Select(r => new JObject { ["claimant"] = r.Claimant?.GetUniqueLoadID(), ["job"] = JobState(r.Job), ["layer"] = r.Layer?.defName, ["max_pawns"] = r.MaxPawns, ["stack_count"] = r.StackCount, ["whole_stack"] = r.StackCount == -1 })), ["physical_interaction"] = new JArray(physical.Select(r => new JObject { ["claimant"] = r.claimant.GetUniqueLoadID(), ["job"] = JobState(r.job) })), ["destinations"] = new JArray(destinations.Select(r => new JObject { ["cell"] = Cell(map, r.target), ["claimant"] = r.claimant.GetUniqueLoadID(), ["job"] = JobState(r.job), ["obsolete"] = r.obsolete })), ["normal_reservations_tick"] = reservationTick, ["other_reservations_tick"] = reservationTick, ["meaning"] = "Reservation records are separate from actual occupancy. Different jobs use different quantities/layers; no generic CanReserve default is substituted." };
        WorldPresentation.Reservations(value, new JObject { ["id"] = t.GetUniqueLoadID(), ["map"] = map.GetUniqueLoadID() });
        return value;
    }
    private static JObject BedState(Building_Bed bed, Pawn? user, Pawn? actor)
    {
        var o = new JObject { ["medical_designation"] = bed.Medical, ["owner_type"] = bed.ForOwnerType.ToString(), ["sleeping_slots"] = bed.SleepingSlotsCount, ["assigned"] = new JArray(bed.OwnersForReading.Select(p => p.GetUniqueLoadID())), ["occupants"] = new JArray(Enumerable.Range(0, bed.SleepingSlotsCount).Select(i => new JObject { ["slot"] = i, ["pawn"] = bed.GetCurOccupant(i)?.GetUniqueLoadID() })), ["type_meaning"] = "HospitalBed is a building definition; medical_designation is a separate setting, including ordinary beds." };
        if (user != null) { WorldNativeAccess.RequireNative(bed, "get_Medical", "get_SleepingSlotsCount", "GetCurOccupant"); o["can_use_bed_now"] = RestUtility.CanUseBedNow(bed, user, true); if (actor != null) o["is_valid_bed_for"] = RestUtility.IsValidBedFor(bed, user, actor, true); o["context"] = new JObject { ["user"] = user.GetUniqueLoadID(), ["actor"] = actor?.GetUniqueLoadID(), ["check_social_properness"] = true, ["ignore_other_reservations"] = false, ["meaning"] = "Native predicates, including their built-in scope; false does not identify a unique cause. IsValidBedFor checks from actor's CURRENT position, not a simulated carry leg. No bed is reserved." }; }
        else o["context_checks"] = new JObject { ["status"] = "not_computed", ["reason"] = "user_required" }; return o;
    }
    private static JObject PawnState(Pawn p, bool details, string[]? sections, int? detailLimit)
    {
        bool Wants(string section) => sections == null || sections.Contains("state") || sections.Contains(section);
        var result = new JObject();
        if (Wants("health"))
        {
        var hediffs = p.health.hediffSet.hediffs.Where(h => h.Visible).ToArray(); var parts = p.health.hediffSet.GetNotMissingParts().Where(b => p.health.hediffSet.GetPartHealth(b) < b.def.GetMaxHealth(p)).ToArray();
        var health = new JObject
        {
            ["dead"] = p.Dead,
            ["downed"] = p.Downed,
            ["health_summary_fraction"] = p.health.summaryHealth.SummaryHealthPercent,
            ["pain_fraction"] = p.health.hediffSet.PainTotal,
            ["in_pain_shock"] = p.health.InPainShock,
            ["bleed_fraction_per_game_day"] = p.health.hediffSet.BleedRateTotal,
            ["conditions"] = new JArray(hediffs.Select(h => new JObject { ["id"] = h.GetUniqueLoadID(), ["definition"] = h.def.defName, ["label"] = h.LabelCap, ["severity"] = h.Severity, ["severity_label"] = h.SeverityLabel, ["visible"] = h.Visible, ["part"] = h.Part?.Index, ["tended"] = h.IsTended() })),
            ["conditions_scope"] = "visible native hediffs; not all hidden conditions",
            ["body_parts"] = new JArray(p.RaceProps.body.AllParts.Take(detailLimit ?? (details ? 100 : 20)).Select(b => new JObject { ["key"] = p.GetUniqueLoadID() + "/body:" + b.body.defName + "/part:" + b.Index, ["index"] = b.Index, ["definition"] = b.def.defName, ["label"] = b.Label, ["health"] = p.health.hediffSet.GetPartHealth(b), ["max_health"] = b.def.GetMaxHealth(p) })),
            ["body_parts_total"] = p.RaceProps.body.AllParts.Count,
            ["body_parts_complete"] = p.RaceProps.body.AllParts.Count <= (detailLimit ?? (details ? 100 : 20)),
            ["missing_parts"] = new JArray(p.health.hediffSet.GetMissingPartsCommonAncestors().Select(h => new JObject { ["definition"] = h.Part?.def.defName, ["part_index"] = h.Part?.Index })),
            ["damaged_present_parts"] = new JArray(parts.Select(b => new JObject { ["index"] = b.Index, ["label"] = b.Label, ["health"] = p.health.hediffSet.GetPartHealth(b), ["max_health"] = b.def.GetMaxHealth(p) })),
            ["capacities"] = new JArray(DefDatabase<PawnCapacityDef>.AllDefsListForReading.Where(d => d.CanShowOnPawn(p)).Select(d => new JObject { ["definition"] = d.defName, ["label"] = d.LabelCap.ToString(), ["level"] = p.health.capacities.GetLevel(d) }))
        };
        foreach (var property in health.Properties()) result[property.Name] = property.Value.DeepClone();
        }
        if (Wants("skills")) { result["skills"] = p.skills == null ? null : new JArray(p.skills.skills.Select(s => new JObject { ["definition"] = s.def.defName, ["label"] = s.def.LabelCap.ToString(), ["level"] = s.Level, ["passion"] = s.passion.ToString(), ["aptitude"] = s.Aptitude, ["disabled"] = s.TotallyDisabled, ["permanently_disabled"] = s.PermanentlyDisabled })); WorldPresentation.NullField(result, "skills", "not_applicable", "skill_tracker_absent"); }
        if (sections == null || sections.Contains("state")) result["traits"] = p.story?.traits == null ? null : new JArray(p.story.traits.allTraits.Select(t => new JObject { ["definition"] = t.def.defName, ["variant_code"] = t.Degree, ["suppressed"] = t.Suppressed, ["description"] = t.TipString(p) }));
        if (Wants("gear"))
        {
            var gear = new JObject
            {
            ["carried"] = p.carryTracker?.CarriedThing == null ? null : GearIdentity(p.carryTracker.CarriedThing),
            ["equipment"] = p.equipment == null ? null : new JArray(p.equipment.AllEquipmentListForReading.Select(GearIdentity)),
            ["apparel"] = p.apparel == null ? null : new JArray(p.apparel.WornApparel.Select(GearIdentity)),
            ["inventory"] = p.inventory == null ? null : new JArray(p.inventory.innerContainer.Take(100).Select(GearIdentity)),
            ["inventory_total"] = p.inventory?.innerContainer.Count,
            ["inventory_complete"] = p.inventory == null || p.inventory.innerContainer.Count <= 100

            };
            WorldPresentation.NullField(gear, "carried", p.carryTracker == null ? "not_applicable" : "observed_none", p.carryTracker == null ? "carry_tracker_absent" : "nothing_carried");
            WorldPresentation.NullField(gear, "equipment", "not_applicable", "equipment_tracker_absent");
            WorldPresentation.NullField(gear, "apparel", "not_applicable", "apparel_tracker_absent");
            WorldPresentation.NullField(gear, "inventory", "not_applicable", "inventory_tracker_absent");
            foreach (var property in gear.Properties()) {
                if (property.Name == "field_states" && result[property.Name] is JObject prior) prior.Merge(property.Value);
                else result[property.Name] = property.Value.DeepClone();
            }
        }
        return result;
    }
    private static JObject GearIdentity(Thing thing)
    {
        var value = Identity(thing); value["stack_count"] = thing.stackCount; return value;
    }
    private static JObject? CurrentJobState(Pawn pawn)
    {
        var value = JobState(pawn.CurJob); if (value != null) value["report"] = pawn.GetJobReport(); return value;
    }
    internal static JObject? JobState(Job? job) => job == null ? null : new JObject { ["definition"] = job.def.defName, ["id"] = job.GetUniqueLoadID(), ["player_forced"] = job.playerForced, ["targets"] = new JArray(new JToken[] { (JToken?)TargetState(job.targetA) ?? JValue.CreateNull(), (JToken?)TargetState(job.targetB) ?? JValue.CreateNull(), (JToken?)TargetState(job.targetC) ?? JValue.CreateNull() }) };
    internal static JObject? TargetState(LocalTargetInfo target) => !target.IsValid ? null : target.HasThing ? Identity(target.Thing) : new JObject { ["x"] = target.Cell.x, ["z"] = target.Cell.z };
}
