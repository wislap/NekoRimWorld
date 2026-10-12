using GraphQL.Types;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>Selected native units and meanings, shared once per result without more game reads.</summary>
internal static class ObjectReadSemantics
{
    internal static JObject Describe(string owner, FieldType field)
    {
        var result = new JObject();
        var unit = (owner, field.Name) switch {
            ("HealthSummary", "bleedRate") => "blood_loss_fraction_per_game_day",
            ("HealthSummary", "healthPercent") => "overall_health_fraction",
            ("HealthSummary", "pain") => "pain_fraction",
            ("Need", "percentage") => "fraction_of_need_maximum",
            ("Need", "level") => "native_need_level",
            ("Need", "maxLevel") => "native_need_maximum",
            ("Capacity", "level") => "native_capacity_factor",
            ("Skill", "level") => "learned_skill_level",
            ("Trait", "degree") => "trait_variant",
            ("ObjectWorkAssignment", "priority") => "assigned_work_priority",
            ("ObjectWorkAssignment", "naturalPriority") => "native_work_type_precedence",
            ("ObjectBattery", "storedWattDays") => "watt_days",
            ("ObjectBattery", "capacityWattDays") => "watt_days",
            ("ObjectPower", "outputWatts") => "watts",
            ("ObjectTemperatureControl", "targetC") => "degrees_celsius",
            ("ObjectCellEnvironment", "temperatureC") => "degrees_celsius",
            ("ObjectRoom", "temperatureC") => "degrees_celsius",
            ("ObjectSituationClock", "ticksGame") => "game_ticks",
            ("ObjectSituationEvent", "tick") => "game_ticks",
            _ => null,
        };
        if (unit != null) result["unit"] = unit;
        var meaning = (owner, field.Name) switch {
            ("ObjectWorkAssignment", "priority") => "0 is unassigned for autonomous work; among assigned priorities 1 precedes 4. Assignment is not proof of an available Job or manual action eligibility.",
            ("ObjectWorkAssignment", "naturalPriority") => "Within equal assigned priority, higher native natural priority precedes lower; array order is not the work order.",
            ("ObjectWork", "medicalCare") => "This Pawn's policy when receiving treatment, not how this Pawn treats another patient.",
            ("ObjectBed", "medicalDesignation") => "A current designation, independent of Bed/HospitalBed definition.",
            ("ObjectBed", "canUseBedNow") => "Native recipient-specific current predicate, not complete rescue/Job eligibility or future success.",
            ("ObjectBed", "isValidBedFor") => "Native predicate for recipient and actor at actor's CURRENT real position; no hypothetical carrying or future state is simulated.",
            ("ObjectBill", "repeatCount") => "Meaningful for the native RepeatCount mode; not an extra batch multiplier in TargetCount or Forever mode.",
            ("ObjectBill", "targetCount") => "Native target inventory setting in TargetCount mode, not a required ingredient quantity.",
            ("ObjectCellEnvironment", "nativeDangerForActor") => "Native cell temperature/vacuum danger classification for the actor, not comprehensive safety or a future prediction.",
            ("ObjectRoom", "vacuum") => "Native room vacuum reading, distinct from the selected cell and not proof of safe travel.",
            ("Stat", "value") => "Game-calculated StatDef value; its unit belongs to that StatDef. Request value_text for the native formatted unit; do not infer percent or ETA.",
            _ => field.Description,
        };
        if (!string.IsNullOrWhiteSpace(meaning) && !meaning!.StartsWith("Selected native ")) result["meaning"] = meaning;
        if (field.Metadata.TryGetValue("object_roles", out var roles)) result["requires_roles"] = JToken.FromObject(roles);
        if (field.Metadata.TryGetValue("object_semantic_role", out var role)) result["role"] = JToken.FromObject(role);
        return result;
    }
}
