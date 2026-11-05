using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

// Ordinary player bed ownership and medical designation.
internal sealed class BedWriteFields : IWriteFieldSet
{
    private readonly IGameData data;
    internal BedWriteFields(IGameData data) { this.data = data; }
    public IEnumerable<WriteOperationBinding> Operations => BedWriteCatalog.Operations.Select(spec => WriteOperationBinding.Single((string)spec["op"]!, ResolveBedOperation));
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
        if (bed.Destroyed || !bed.Spawned || !NativeFactions.IsPlayer(bed.Faction) || bed.GetType() != typeof(Building_Bed) || !OfficialContent.Contains(bed.def) || !bed.ForColonists || bed.CompAssignableToPawn == null) throw new ArgumentException("Only ordinary spawned official player colonist beds are supported.");
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
    public IReadableWriteField? ResolveLedger(string id, string field, object? target, JToken value)
    {
        if (field == "bed_assignment" && target is Pawn pawn && pawn.GetUniqueLoadID() == id && pawn.ownership != null) return new BedAssignment(pawn);
        if (field == "bed_medical" && target is Building_Bed bed && bed.GetUniqueLoadID() == id) return new BedMedical(bed);
        return null;
    }
}
