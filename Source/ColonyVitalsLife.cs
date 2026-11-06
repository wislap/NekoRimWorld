using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

// Will anyone die? Native judgements reused as-is; no severity thresholds here.
internal static class ColonyVitalsLife
{
    internal static IEnumerable<int> Read(Pawn[] colonists, JObject domain)
    {
        var rows = new List<(int rank, float bleed, JObject row)>();
        int downed = 0, starving = 0, threatened = 0, needsTend = 0;
        float maxBleed = 0f;
        foreach (var pawn in colonists) {
            var threats = LifeThreatening(pawn).ToArray();
            bool isDowned = pawn.Downed, isStarving = pawn.needs?.food?.Starving == true, tend = pawn.health.HasHediffsNeedingTend();
            float bleed = pawn.health.hediffSet.BleedRateTotal;
            downed += isDowned ? 1 : 0; starving += isStarving ? 1 : 0; threatened += threats.Length > 0 ? 1 : 0; needsTend += tend ? 1 : 0;
            maxBleed = System.Math.Max(maxBleed, bleed);
            var why = new JArray();
            if (threats.Length > 0) why.Add("life_threatening");
            if (isDowned) why.Add(pawn.InBed() ? "downed_in_bed" : "downed_not_in_bed");
            if (bleed > 0f) why.Add("bleeding");
            if (isStarving) why.Add("starving");
            if (tend) why.Add("needs_tend");
            if (why.Count > 0) {
                var row = ColonyVitals.PawnRow(pawn);
                row["why"] = why; row["bleed_rate"] = bleed;
                row["hediffs"] = new JArray(pawn.health.hediffSet.hediffs.Where(h => h.Visible).OrderByDescending(h => h.Severity).Take(3)
                    .Select(h => new JObject { ["def"] = h.def.defName, ["label"] = h.LabelCap, ["severity"] = h.Severity,
                        ["life_threatening"] = threats.Contains(h), ["tendable_now"] = h.TendableNow() }));
                int rank = threats.Length > 0 ? 0 : isDowned && !pawn.InBed() ? 1 : bleed > 0f ? 2 : isDowned ? 3 : isStarving ? 4 : 5;
                rows.Add((rank, bleed, row));
            }
            yield return 0;
        }
        domain["numbers"] = new JObject { ["downed"] = downed, ["life_threatening"] = threatened, ["starving"] = starving,
            ["needs_tend"] = needsTend, ["max_bleed_rate"] = maxBleed };
        domain["method"] = new JObject {
            ["life_threatening"] = "Same per-hediff judgement as RimWorld.Alert_LifeThreateningHediff: IsCurrentlyLifeThreatening and not FullyImmune; Deathless carriers count only brain-destroying stages.",
            ["starving"] = "Native Need_Food.Starving (food level 0), the predicate of Alert_StarvationColonists.",
            ["needs_tend"] = "Native Pawn_HealthTracker.HasHediffsNeedingTend().",
            ["max_bleed_rate"] = "Largest native HediffSet.BleedRateTotal among colonists (native units).",
            ["worst"] = "life-threatening, downed outside a bed, bleeding (rate descending), downed in bed, starving, needs tend." };
        ColonyVitalsContract.SetWorst(domain, rows.OrderBy(r => r.rank).ThenByDescending(r => r.bleed).Select(r => r.row));
    }

    private static IEnumerable<Hediff> LifeThreatening(Pawn pawn)
    {
        bool deathless = ModsConfig.BiotechActive && pawn.genes != null && pawn.genes.HasActiveGene(GeneDefOf.Deathless);
        foreach (var hediff in pawn.health.hediffSet.hediffs) {
            if (!hediff.IsCurrentlyLifeThreatening || hediff.FullyImmune()) continue;
            if (deathless && hediff.CurStage?.mtbDeathDestroysBrain != true) continue;
            yield return hediff;
        }
    }
}
