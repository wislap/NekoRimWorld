using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace Neko.RimWorld;

// Branch order follows the audited Equip/Wear/Ingest providers in the matched
// game binary. Calls their native predicates, not GUI option constructors.
// No action callback, JobMaker, ordered job or SetForbidden call is executed.
internal static class NativeActionConditions
{
    private sealed class Reading
    {
        internal readonly Pawn Pawn; internal readonly Thing Target;
        internal readonly JObject Result;
        private readonly JArray checks=new();
        internal Reading(Pawn pawn,Thing target) { Pawn=pawn;Target=target;Result=new JObject { ["checks"]=checks,["mode"]="audited_native_provider_conditions",["execution_observed"]=false }; }
        internal bool Pass(string condition,bool value) { checks.Add(new JObject { ["condition"]=condition,["passed"]=value });return value; }
        internal JObject Stop(string reason,string? detail=null) { Result["status"]="blocked";Result["reason"]=reason;if(detail!=null)Result["native_reason"]=detail;return Result; }
        internal bool Reach(PathEndMode mode)=>Pass("Pawn.CanReach("+mode+",Danger.Deadly)",Pawn.CanReach(Target,mode,Danger.Deadly));
        internal JObject Available() { Result["status"]="available";Result["meaning"]="The inspected player-order eligibility branch passed now. Follow-up confirmation, reservation/job execution, future conditions, desirability and safety are not established.";return Result; }
    }
    internal static JObject Read(string action,Pawn pawn,Thing target)
    {
        var read=new Reading(pawn,target);
        return action switch { "equip"=>ReadEquip(read),"wear"=>ReadWear(read),"ingest"=>ReadIngest(read),_=>throw ReadContext.Error("invalid_arguments","Unknown action") };
    }
    private static JObject ReadEquip(Reading read)
    {
        var pawn=read.Pawn;var target=read.Target;
            if (!read.Pass("weapon violence capability", !target.def.IsWeapon || !pawn.WorkTagIsDisabled(WorkTags.Violent))) return read.Stop("incapable_of_violence");
            if (!read.Pass("ranged weapon shooting capability", !target.def.IsRangedWeapon || !pawn.WorkTagIsDisabled(WorkTags.Shooting))) return read.Stop("incapable_of_shooting");
            if (!read.Reach(PathEndMode.ClosestTouch)) return read.Stop("no_path");
            if (!read.Pass("Manipulation capacity", pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))) return read.Stop("incapable_of_manipulation");
            if (!read.Pass("not burning", !target.IsBurning())) return read.Stop("burning");
            if (!read.Pass("quest lodger equipment restriction", !pawn.IsQuestLodger() || EquipmentUtility.QuestLodgerCanEquip(target, pawn))) return read.Stop("quest_restriction");
            if (!read.Pass("EquipmentUtility.CanEquip(checkBonded:false)", EquipmentUtility.CanEquip(target, pawn, out var reason, checkBonded: false))) return read.Stop("cannot_equip", reason);
            if (EquipmentUtility.AlreadyBondedToWeapon(target, pawn)) return read.Stop("already_bonded_information_only", "The native option opens an information dialog instead of issuing an equip job.");

        return read.Available();
    }
    private static JObject ReadWear(Reading read)
    {
        var pawn=read.Pawn;var target=read.Target;
            var apparel = (Apparel)target;
            if (!read.Reach(PathEndMode.ClosestTouch)) return read.Stop("no_path");
            if (!read.Pass("not burning", !apparel.IsBurning())) return read.Stop("burning");
            if (!read.Pass("does not replace locked apparel", !pawn.apparel.WouldReplaceLockedApparel(apparel))) return read.Stop("would_replace_locked_apparel");
            if (!read.Pass("mutant apparel allowed", !pawn.IsMutant || !pawn.mutant.Def.disableApparel)) return read.Stop("mutant_disables_apparel");
            if (!read.Pass("ApparelUtility.HasPartsToWear", ApparelUtility.HasPartsToWear(pawn, apparel.def))) return read.Stop("missing_body_parts");
            if (!read.Pass("EquipmentUtility.CanEquip", EquipmentUtility.CanEquip(apparel, pawn, out var reason))) return read.Stop("cannot_equip", reason);

        return read.Available();
    }
    private static JObject ReadIngest(Reading read)
    {
        var pawn=read.Pawn;var target=read.Target;
            if (!read.Pass("Thing.IngestibleNow", target.IngestibleNow)) return read.Stop("not_ingestible_now");
            if (!read.Pass("RaceProps.CanEverEat", pawn.RaceProps.CanEverEat(target.def))) return read.Stop("race_cannot_eat");
            if (!read.Pass("food suitability", target.def.IsDrug || target.def.ingestible.nonDrugIngestibleWithoutFoodNeed || pawn.FoodIsSuitable(target.def))) return read.Stop("food_not_suitable");
            if (!read.Pass("drug suitability", !target.def.IsDrug || pawn.DrugIsSuitable(target.def))) return read.Stop("drug_not_suitable");
            if (ModsConfig.IdeologyActive && target.def.IsDrug) {
                bool Willing(HistoryEventDef definition) => new HistoryEvent(definition, pawn.Named(HistoryEventArgsNames.Doer)).DoerWillingToDo();
                if (!read.Pass("ingested drug ideology permission", Willing(HistoryEventDefOf.IngestedDrug) || PawnUtility.CanTakeDrugForDependency(pawn, target.def))) return read.Stop("ideoligion_forbids");
                if (target.def.IsNonMedicalDrug && !read.Pass("recreational drug ideology permission", Willing(HistoryEventDefOf.IngestedRecreationalDrug) || PawnUtility.CanTakeDrugForDependency(pawn, target.def))) return read.Stop("ideoligion_forbids");
                if (target.def.ingestible.drugCategory == DrugCategory.Hard && !read.Pass("hard drug ideology permission", Willing(HistoryEventDefOf.IngestedHardDrug))) return read.Stop("ideoligion_forbids");
            }
            if (!read.Pass("nonmedical drug trait permission", !target.def.IsNonMedicalDrug || pawn.CanTakeDrug(target.def))) return read.Stop("drug_trait_restriction");
            if (!read.Pass("FoodUtility.InappropriateForTitle", !FoodUtility.InappropriateForTitle(target.def, pawn, allowIfStarving: true))) return read.Stop("food_below_title_requirement");
            if (!read.Reach(PathEndMode.OnCell)) return read.Stop("no_path");
            var count = FoodUtility.GetMaxAmountToPickup(target, pawn, FoodUtility.WillIngestStackCountOf(pawn, target.def, FoodUtility.NutritionForEater(pawn, target)));
            read.Result["native_max_pickup_count"] = count;
            if (!read.Pass("GetMaxAmountToPickup != 0", count != 0)) return read.Stop("no_pickup_quantity");
        return read.Available();
    }
}
