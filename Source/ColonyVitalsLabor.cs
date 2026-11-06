using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

// Is the essential work staffed?
internal static class ColonyVitalsLabor
{
    internal static IEnumerable<int> Read(Pawn[] colonists, JObject domain)
    {
        var rows = new List<JObject>();
        var uncovered = new JArray();
        var working = colonists.Where(p => !p.Downed && p.workSettings?.EverWork == true).ToArray();
        foreach (var name in ColonyVitalsContract.KeyWork) {
            var work = DefDatabase<WorkTypeDef>.GetNamedSilentFail(name);
            if (work == null) { ColonyVitalsContract.Partial(domain, "work_type_missing:" + name); continue; }
            var capable = working.Where(p => !p.WorkTypeIsDisabled(work)).ToArray();
            if (!capable.Any(p => p.workSettings.WorkIsActive(work))) {
                uncovered.Add(name);
                rows.Add(new JObject { ["work_type"] = name, ["why"] = new JArray("uncovered"),
                    ["capable_inactive"] = new JArray(capable.Select(p => ColonyVitals.PawnRow(p))) });
            }
            yield return 0;
        }
        var idle = colonists.Where(Idle).ToArray();
        foreach (var pawn in idle) { var row = ColonyVitals.PawnRow(pawn); row["why"] = new JArray("idle"); rows.Add(row); }
        domain["numbers"] = new JObject { ["uncovered_work"] = uncovered, ["idle"] = idle.Length, ["working_colonists"] = working.Length };
        domain["method"] = new JObject {
            ["uncovered_work"] = "Key work types (Cooking, Doctor, Growing, Construction, Hauling) with no non-downed colonist that can do it (not WorkTypeIsDisabled) and has it active (Pawn_WorkSettings.WorkIsActive).",
            ["idle"] = "Same per-pawn predicate as RimWorld.Alert_ColonistsIdle (mindState.IsIdle, not a quest lodger, royal title without suppressIdleAlert), without the alert's first-day gate. Momentary: changes with the current job.",
            ["worst"] = "Uncovered work types in key-work order, then idle colonists." };
        ColonyVitalsContract.SetWorst(domain, rows);
    }

    private static bool Idle(Pawn pawn)
    {
        if (!pawn.mindState.IsIdle || pawn.IsQuestLodger()) return false;
        return pawn.royalty?.MostSeniorTitle?.def.suppressIdleAlert != true;
    }
}
