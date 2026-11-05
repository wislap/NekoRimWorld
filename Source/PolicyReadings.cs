using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

// Business IDs use save-scoped native-object tokens: native load IDs contain
// the mutable policy label. User coverage includes maps, world pawns,
// temporary holders, caravans and transporters rather than only the current map.
internal static class PolicyReadings
{
    internal static string Instance(FoodPolicy policy) => NativeObjectIdentities.Token(policy);
    internal static string Id(FoodPolicy policy) => "food_policy:" + Instance(policy);
    internal static JObject Key(FoodPolicy policy) => new() { ["policy_id"] = Id(policy), ["native_instance"] = Instance(policy) };
    internal static JToken AssignedPolicyKey(Pawn pawn) => AssignedPolicy(pawn) is FoodPolicy policy ? Key(policy) : JValue.CreateNull();
    internal static FoodRestrictionDatabase Database => Current.Game.foodRestrictionDatabase;
    private static readonly FieldInfo CurrentPolicyField = typeof(Pawn_FoodRestrictionTracker).GetField("curPolicy", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(Pawn_FoodRestrictionTracker).FullName, "curPolicy");
    // The native getter lazily assigns a default. Read its backing reference and
    // apply the same default/mutant semantics without mutating the pawn.
    internal static FoodPolicy? RawPolicy(Pawn pawn) => pawn.foodRestriction == null ? null : CurrentPolicyField.GetValue(pawn.foodRestriction) as FoodPolicy;
    internal static FoodPolicy? AssignedPolicy(Pawn pawn)
    {
        if (pawn.foodRestriction == null || (pawn.IsMutant && pawn.mutant.Def.disablePolicies)) return null;
        return RawPolicy(pawn) ?? Database.AllFoodRestrictions.FirstOrDefault();
    }
    internal static Pawn[] Pawns() => PawnsFinder.All_AliveOrDead.Concat(PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead)
        .Concat(PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive)
        .Distinct().Where(p => p.foodRestriction != null).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
    internal static FoodPolicy Resolve(string? id) => Database.AllFoodRestrictions.SingleOrDefault(p => Id(p) == id)
        ?? throw new ArgumentException("Food policy not found.");
    internal static Pawn Pawn(string? id) => Pawns().SingleOrDefault(p => p.GetUniqueLoadID() == id)
        ?? throw new ArgumentException("Pawn with food settings not found.");
    internal static JArray Users(FoodPolicy policy) => new(Pawns().Where(p => ReferenceEquals(RawPolicy(p) ?? AssignedPolicy(p), policy)).Select(p => p.GetUniqueLoadID()));
    internal static JObject Filter(ThingFilter filter) => new()
    {
        ["allowed_defs"] = new JArray(filter.AllowedThingDefs.Select(d => d.defName).OrderBy(x => x, StringComparer.Ordinal)),
        ["special_filters"] = new JObject(DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.OrderBy(d => d.defName, StringComparer.Ordinal)
            .Select(d => new JProperty(d.defName, filter.Allows(d)))),
        ["hitpoints"] = new JArray(JsonValues.Number(filter.AllowedHitPointsPercents.min), JsonValues.Number(filter.AllowedHitPointsPercents.max)),
        ["quality"] = new JArray(filter.AllowedQualityLevels.min.ToString(), filter.AllowedQualityLevels.max.ToString()),
        ["mental_break_chance"] = new JArray(JsonValues.Number(filter.AllowedMentalBreakChance.min), JsonValues.Number(filter.AllowedMentalBreakChance.max))
    };
    internal static JObject Content(FoodPolicy policy) => new()
    {
        ["native_instance"] = Instance(policy),
        ["label"] = policy.label,
        ["filter"] = Filter(policy.filter),
        ["users"] = Users(policy),
        ["users_complete"] = true
    };
    internal static JObject Structure() => new() { ["policy_ids"] = new JArray(Database.AllFoodRestrictions.Select(Id)) };
    internal static JObject Read(JObject args)
    {
        var ids = ColonyReadContract.Texts(args, "ids");
        var policies = Database.AllFoodRestrictions.Where(p => ids.Length == 0 || ids.Contains(Id(p))).ToArray();
        if (policies.Length != ids.Distinct().Count() && ids.Length != 0) throw new ArgumentException("Food policy not found.");
        var objects = new JArray(policies.Select(p =>
        {
            var row = Content(p); row["write_content"] = Content(p);
            var copy = Structure(); copy["source_id"] = Id(p); copy["source"] = Content(p); row["copy_expect"] = copy;
            var deletion = Structure(); deletion["policy_id"] = Id(p); deletion["policy"] = Content(p); row["delete_expect"] = deletion; row["id"] = Id(p); row["policy_id"] = Id(p); row["native_load_id"] = p.GetUniqueLoadID();
            row["status"] = "observed"; row["is_default"] = ReferenceEquals(Database.AllFoodRestrictions.FirstOrDefault(), p); return row;
        }));
        return new JObject
        {
            ["status"] = "ok",
            ["complete"] = true,
            ["users_complete"] = true,
            ["user_coverage"] = "all native registered pawns including maps, world and temporary holders",
            ["structure"] = Structure(),
            ["objects"] = objects,
            ["assignments"] = new JArray(Pawns().Where(p => ids.Length == 0 || policies.Contains(RawPolicy(p) ?? AssignedPolicy(p))).Select(p => new JObject
            {
                ["pawn_id"] = p.GetUniqueLoadID(),
                ["policy_id"] = AssignedPolicy(p) is FoodPolicy assigned ? new JValue(Id(assigned)) : JValue.CreateNull(),
                ["write_key"] = AssignedPolicyKey(p),
                ["configurable"] = p.foodRestriction.Configurable
            }))
        };
    }
}


