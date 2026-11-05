using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal sealed partial class VerseWriteFieldResolver
{
    private static readonly Registration[] BedCatalog = {
        new Registration(JObject.Parse(@"{'op':'set_bed_assignment','schema':{'type':'object','additionalProperties':false,'properties':{'op':{'const':'set_bed_assignment'},'pawn':{'type':'string','minLength':1},'value':{'type':['string','null']}},'required':['op','pawn','value'],'description':'Assign a configurable living free colonist to a supported ordinary player bed with a free slot, or clear their bed assignment. Native assignment eligibility is checked; no other owner is displaced.'},'observation':{'read':{'tool':'look','arguments':{'object':{'kind':'pawn','id':{'arg':'pawn'}},'aspects':['work']},'native_operation':'inspect_state'},'extract':{'kind':'field','object_id':{'arg':'pawn'},'path':['facets','work','assigned_bed_id'],'allow_null':true,'objects_path':['objects'],'identity_path':['target','id'],'status_path':['facets','work','status'],'status_value':'observed'}}}"), (r,o) => One(r.ResolveBedOperation(o),o)),
        new Registration(JObject.Parse(@"{'op':'set_bed_medical','schema':{'type':'object','additionalProperties':false,'properties':{'op':{'const':'set_bed_medical'},'bed':{'type':'string','minLength':1},'value':{'type':'boolean'}},'required':['op','bed','value'],'description':'Set medical designation of a supported ordinary player colonist bed. Enabling requires an empty ownership list; clear assignments explicitly first.'},'observation':{'read':{'tool':'look','arguments':{'object':{'kind':'thing','id':{'arg':'bed'}},'aspects':['state'],'focus':{'state':['bed']}},'native_operation':'inspect_state'},'extract':{'kind':'field','object_id':{'arg':'bed'},'path':['facets','bed','medical_designation'],'allow_null':false,'objects_path':['objects'],'identity_path':['target','id'],'status_path':['facets','bed','status'],'status_value':'observed'}}}"), (r,o) => One(r.ResolveBedOperation(o),o))
    };
    private IPreparedWriteField ResolveBedOperation(JObject operation)
    {
        if (operation.Properties().Any(p => p.Name != "op" && p.Name != "pawn" && p.Name != "bed" && p.Name != "expect" && p.Name != "value" && p.Name != "evidence")) throw new ArgumentException("Invalid bed operation.");
        if ((string?)operation["op"] == "set_bed_assignment")
        {
            var pawn = data.Colonists().SingleOrDefault(p => p.Id == (string?)operation["pawn"])?.Handle as Pawn ?? throw new ArgumentException("Spawned free colonist not found.");
            return new BedAssignment(pawn);
        }
        return new BedMedical(ResolveOrdinaryBed((string?)operation["bed"]));
    }
    private static Building_Bed ResolveOrdinaryBed(string? id)
    {
        var bed = Find.Maps.SelectMany(m => m.listerBuildings.allBuildingsColonist).OfType<Building_Bed>().SingleOrDefault(b => b.GetUniqueLoadID() == id)
            ?? throw new ArgumentException("Player bed not found.");
        RequireOrdinaryBed(bed); return bed;
    }
    private static void RequireOrdinaryBed(Building_Bed bed)
    {
        if (bed.Destroyed || !bed.Spawned || bed.Faction != Faction.OfPlayer || bed.GetType() != typeof(Building_Bed) || !Official(bed.def) || !bed.ForColonists || bed.CompAssignableToPawn == null) throw new ArgumentException("Only ordinary spawned official player colonist beds are supported.");
    }
    private sealed class BedAssignment : IPreparedWriteField
    {
        private readonly Pawn pawn;
        internal BedAssignment(Pawn pawn) { this.pawn = pawn; }
        public string ObjectId => pawn.GetUniqueLoadID(); public string Field => "bed_assignment";
        public bool IsDestroyed => pawn.Destroyed; public bool MayChangeByGame => true; public object NativeTarget => pawn;
        public JToken Read() => pawn.ownership?.OwnedBed == null ? JValue.CreateNull() : new JValue(pawn.ownership.OwnedBed.GetUniqueLoadID());
        public void Validate(JToken value)
        {
            if (pawn.Destroyed || pawn.Dead || !pawn.Spawned || !pawn.IsFreeColonist || pawn.ownership == null) throw new ArgumentException("A living spawned free colonist is required for bed assignment.");
            // Existing unsupported beds are not silently unclaimed as a side effect.
            if (pawn.ownership.OwnedBed != null) RequireOrdinaryBed(pawn.ownership.OwnedBed);
            if (value.Type == JTokenType.Null) return;
            if (value.Type != JTokenType.String) throw new ArgumentException("Bed ID or null required.");
            var bed = ResolveOrdinaryBed((string?)value);
            if (bed.Map != pawn.Map || bed.Medical || !RestUtility.CanUseBedEver(pawn, bed.def) || !bed.CompAssignableToPawn.AssigningCandidates.Contains(pawn)) throw new ArgumentException("Pawn is not a native assignment candidate for this bed.");
            var eligibility = bed.CompAssignableToPawn.CanAssignTo(pawn);
            if (!eligibility.Accepted) throw new ArgumentException(eligibility.Reason);
            if (!bed.OwnersForReading.Contains(pawn) && bed.OwnersForReading.Count >= bed.SleepingSlotsCount) throw new InvalidOperationException("Bed has no free ownership slot; other owners will not be displaced.");
        }
        public void Write(JToken value)
        {
            Validate(value);
            if (value.Type == JTokenType.Null) pawn.ownership.UnclaimBed();
            else ResolveOrdinaryBed((string?)value).CompAssignableToPawn.TryAssignPawn(pawn);
        }
        public void Restore(JToken value) => Write(value);
    }
    private sealed class BedMedical : IPreparedWriteField
    {
        private readonly Building_Bed bed;
        internal BedMedical(Building_Bed bed) { this.bed = bed; }
        public string ObjectId => bed.GetUniqueLoadID(); public string Field => "bed_medical";
        public bool IsDestroyed => bed.Destroyed; public bool MayChangeByGame => true; public object NativeTarget => bed;
        public JToken Read() => new JValue(bed.Medical);
        public void Validate(JToken value)
        {
            RequireOrdinaryBed(bed);
            if (value.Type != JTokenType.Boolean) throw new ArgumentException("Medical designation must be boolean.");
            if ((bool)value && bed.OwnersForReading.Count != 0) throw new InvalidOperationException("Clear bed ownership before enabling medical designation.");
        }
        public void Write(JToken value) { Validate(value); bed.Medical = (bool)value; }
        public void Restore(JToken value) => Write(value);
    }
    internal IPreparedWriteField? ResolveBedLedger(string id, string field, object? target)
    {
        if (field == "bed_assignment" && target is Pawn pawn && pawn.GetUniqueLoadID() == id && pawn.ownership != null) return new BedAssignment(pawn);
        if (field == "bed_medical" && target is Building_Bed bed && bed.GetUniqueLoadID() == id) return new BedMedical(bed);
        return null;
    }
}
