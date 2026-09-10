using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace Neko.RimWorld;

/// <summary>
/// Query-scoped sources for nested pawn facts. Returning a node does not read its child fields.
/// All handles remain inside the synchronous game-thread query and must never enter wire data.
/// </summary>
internal static class PawnDetailsData
{
    public static object? Read(GameNode node, string field, IReadOnlyDictionary<string, object?> args)
    {
        var pawn = (Pawn)node.Owner.Handle;
        if (pawn.Destroyed) throw ReadContext.Error("object_unavailable", "The pawn no longer exists.");
        switch (node.Kind)
        {
            case "Pawn":
                return field switch {
                    "status" => Child(node, "Status", "status", pawn),
                    "mood" => pawn.needs?.mood == null ? null : Child(node, "Mood", "mood", pawn.needs.mood),
                    "health" => pawn.health == null ? null : Child(node, "Health", "health", pawn.health),
                    "equipment" => ThingData.Wrap(node, "Pawn_EquipmentTracker", "equipment", pawn.equipment),
                    "apparel" => ThingData.Wrap(node, "Pawn_ApparelTracker", "apparel", pawn.apparel),
                    "inventory" => ThingData.Wrap(node, "Pawn_InventoryTracker", "inventory", pawn.inventory),
                    "carryTracker" => ThingData.Wrap(node, "Pawn_CarryTracker", "carryTracker", pawn.carryTracker),
                    "profile" => Child(node, "Profile", "profile", pawn),
                    "stats" => Stats(node, args),
                    "statDefinitions" => StatDefinitions(node, args),
                    _ => Unknown(node, field),
                };
            case "Status":
                return field switch {
                    "downed" => pawn.Downed,
                    "drafted" => pawn.Drafted,
                    "dead" => pawn.Dead,
                    "inMentalState" => pawn.InMentalState,
                    "mentalState" => pawn.MentalStateDef?.defName,
                    "currentJob" => pawn.CurJob == null ? null : Child(node, "Job", pawn.CurJob.GetUniqueLoadID(), pawn.CurJob),
                    "position" => !pawn.Spawned ? null : Child(node, "Position", "position", pawn),
                    "needs" => pawn.needs == null ? null : Needs(node, pawn),
                    _ => Unknown(node, field),
                };
            case "Position":
                return field switch {
                    "x" => pawn.Position.x, "y" => pawn.Position.y, "z" => pawn.Position.z,
                    "mapId" => pawn.Map?.GetUniqueLoadID(), _ => Unknown(node, field),
                };
            case "Job":
                var job = (Job)node.Handle!;
                return field switch {
                    "id" => job.GetUniqueLoadID(), "defName" => job.def?.defName,
                    "report" => pawn.GetJobReport(), "targets" => JobTargets(node, job),
                    _ => Unknown(node, field),
                };
            case "JobTarget":
                var target = (TargetSource)node.Handle!;
                return field switch {
                    "slot" => target.Slot,
                    "kind" => target.Target.HasThing ? "thing" : "cell",
                    "thingId" => target.Target.HasThing ? target.Target.Thing?.GetUniqueLoadID() : null,
                    "mapId" => target.Target.HasThing ? target.Target.Thing?.MapHeld?.GetUniqueLoadID() : node.Owner.MapId,
                    "x" => target.Target.Cell.x, "y" => target.Target.Cell.y, "z" => target.Target.Cell.z,
                    _ => Unknown(node, field),
                };
            case "Need":
                var need = (Need)node.Handle!;
                return field switch {
                    "defName" => need.def.defName, "label" => need.LabelCap.ToString(),
                    "level" => Number(need.CurLevel), "maxLevel" => Number(need.MaxLevel),
                    "percentage" => Number(need.CurLevelPercentage), _ => Unknown(node, field),
                };
            case "Mood":
                var mood = pawn.needs?.mood;
                if (mood == null) return null;
                return field switch {
                    "level" => Number(mood.CurLevelPercentage),
                    "instant" => Number(mood.CurInstantLevelPercentage),
                    "breakThresholds" => pawn.mindState?.mentalBreaker == null ? null : Child(node, "Thresholds", "thresholds", pawn.mindState.mentalBreaker),
                    "thoughtGroups" => ThoughtGroups(node, mood.thoughts, VisibleOnly(args)),
                    "memories" => Memories(node, mood.thoughts.memories, VisibleOnly(args)),
                    _ => Unknown(node, field),
                };
            case "Thresholds":
                var breaker = (MentalBreaker)node.Handle!;
                return field switch {
                    "minor" => Number(breaker.BreakThresholdMinor),
                    "major" => Number(breaker.BreakThresholdMajor),
                    "extreme" => Number(breaker.BreakThresholdExtreme), _ => Unknown(node, field),
                };
            case "ThoughtGroup":
                var group = (ThoughtGroupSource)node.Handle!;
                return field switch {
                    "defName" => group.Representative.def.defName,
                    "label" => Text(group.Representative.LabelCap),
                    "stageIndex" => group.Representative.CurStageIndex,
                    "moodOffset" => Number(group.Handler.MoodOffsetOfGroup(group.Representative)),
                    "memberCount" => group.Members.Count,
                    "members" => Thoughts(node, group.Members, false),
                    _ => Unknown(node, field),
                };
            case "Thought":
                var thought = (Thought)node.Handle!;
                var memory = thought as Thought_Memory;
                return field switch {
                    "ref" => node.Key, "defName" => thought.def.defName,
                    "label" => Text(thought.LabelCap), "description" => Text(thought.Description),
                    "stageIndex" => thought.CurStageIndex, "moodOffset" => Number(thought.MoodOffset()),
                    "kind" => memory != null ? "memory" : thought is Thought_Situational ? "situational" : "other",
                    "otherPawnId" => memory?.otherPawn?.GetUniqueLoadID(),
                    "ageTicks" => memory?.age, "durationTicks" => memory?.DurationTicks,
                    _ => Unknown(node, field),
                };
            case "Health":
                if (pawn.health == null) return null;
                return field switch {
                    "summary" => Child(node, "HealthSummary", "summary", pawn.health),
                    "capacities" => Capacities(node, pawn),
                    "conditions" => Conditions(node, pawn, VisibleOnly(args)),
                    "bodyParts" => BodyParts(node, pawn), _ => Unknown(node, field),
                };
            case "HealthSummary":
                return field switch {
                    "healthPercent" => Number(pawn.health?.summaryHealth?.SummaryHealthPercent),
                    "pain" => Number(pawn.health?.hediffSet?.PainTotal),
                    "bleedRate" => Number(pawn.health?.hediffSet?.BleedRateTotal),
                    "downed" => pawn.Downed, "inPainShock" => pawn.health?.InPainShock,
                    _ => Unknown(node, field),
                };
            case "Capacity":
                var capacity = (PawnCapacityDef)node.Handle!;
                return field switch {
                    "defName" => capacity.defName, "label" => capacity.LabelCap.ToString(),
                    "level" => Number(pawn.health?.capacities?.GetLevel(capacity)), _ => Unknown(node, field),
                };
            case "Condition":
                var condition = (Hediff)node.Handle!;
                return field switch {
                    "id" => condition.GetUniqueLoadID(), "defName" => condition.def.defName,
                    "label" => Text(condition.LabelCap), "description" => Text(condition.Description),
                    "severity" => Number(condition.Severity), "severityLabel" => Text(condition.SeverityLabel),
                    "visible" => condition.Visible,
                    "part" => condition.Part == null ? null : PartNode(node, condition.Part),
                    "tending" => Optional(node, "Tending", "tending", condition.TryGetComp<HediffComp_TendDuration>()),
                    "immunity" => Optional(node, "Immunity", "immunity", condition.TryGetComp<HediffComp_Immunizable>()),
                    _ => Unknown(node, field),
                };
            case "BodyPart":
                var part = (BodyPartRecord)node.Handle!;
                return field switch {
                    "key" => PartKey(node, part), "defName" => part.def.defName,
                    "label" => Text(part.LabelCap), "index" => part.Index,
                    "parentKey" => part.parent == null ? null : PartKey(node, part.parent),
                    "health" => Number(pawn.health?.hediffSet?.GetPartHealth(part)),
                    "maxHealth" => Number(part.def.GetMaxHealth(pawn)), _ => Unknown(node, field),
                };
            case "Tending":
                var tending = (HediffComp_TendDuration)node.Handle!;
                return field switch {
                    "isTended" => tending.IsTended,
                    "quality" => tending.IsTended ? Number(tending.tendQuality) : null,
                    "ticksLeft" => tending.IsTended ? (int?)tending.tendTicksLeft : null,
                    _ => Unknown(node, field),
                };
            case "Immunity":
                var immunity = (HediffComp_Immunizable)node.Handle!;
                return field switch {
                    "level" => Number(immunity.Immunity), "fullyImmune" => immunity.FullyImmune,
                    _ => Unknown(node, field),
                };
            case "Profile":
                return field switch {
                    "gender" => pawn.gender.ToString(),
                    "raceDefName" => pawn.def?.defName,
                    "kindDefName" => pawn.kindDef?.defName,
                    "age" => pawn.ageTracker == null ? null : Child(node, "Age", "age", pawn.ageTracker),
                    "backstories" => pawn.story == null ? null : Backstories(node, pawn),
                    "traits" => pawn.story?.traits == null ? null : Traits(node, pawn),
                    "skills" => pawn.skills == null ? null : Skills(node, pawn), _ => Unknown(node, field),
                };
            case "Age":
                var age = (Pawn_AgeTracker)node.Handle!;
                return field switch {
                    "biologicalYears" => Number(age.AgeBiologicalYearsFloat),
                    "chronologicalYears" => Number(age.AgeChronologicalYearsFloat),
                    "lifeStage" => age.CurLifeStage?.defName, _ => Unknown(node, field),
                };
            case "Backstory":
                var backstory = (BackstoryDef)node.Handle!;
                return field switch {
                    "slot" => backstory.slot.ToString(), "defName" => backstory.defName,
                    "title" => backstory.TitleCapFor(pawn.gender),
                    "description" => backstory.FullDescriptionFor(pawn).ToString(), _ => Unknown(node, field),
                };
            case "Trait":
                var trait = (Trait)node.Handle!;
                return field switch {
                    "defName" => trait.def.defName, "label" => Text(trait.LabelCap), "degree" => trait.Degree,
                    "description" => Text(trait.TipString(pawn)),
                    "suppressed" => trait.Suppressed, "sourceGeneDefName" => trait.sourceGene?.def?.defName,
                    "suppressedByGeneDefName" => trait.suppressedByGene?.def?.defName,
                    _ => Unknown(node, field),
                };
            case "Skill":
                var skill = (SkillRecord)node.Handle!;
                return field switch {
                    "defName" => skill.def.defName, "label" => skill.def.LabelCap.ToString(),
                    "level" => skill.Level, "passion" => skill.passion.ToString(), "aptitude" => skill.Aptitude,
                    "totallyDisabled" => skill.TotallyDisabled, "permanentlyDisabled" => skill.PermanentlyDisabled,
                    "xpSinceLastLevel" => Number(skill.xpSinceLastLevel),
                    "xpRequiredForLevelUp" => Number(skill.XpRequiredForLevelUp), _ => Unknown(node, field),
                };
            case "Stat":
                var stat = (StatSource)node.Handle!;
                return field switch {
                    "defName" => stat.Def.defName, "label" => Text(stat.Def.LabelForFullStatListCap),
                    "value" => Number(stat.Value),
                    "valueText" => stat.Def.ValueToString(stat.Value, stat.Def.toStringNumberSense),
                    _ => Unknown(node, field),
                };
            case "StatDefinition":
                var statDefinition = (StatDef)node.Handle!;
                return field switch {
                    "defName" => statDefinition.defName,
                    "label" => Text(statDefinition.LabelForFullStatListCap),
                    "description" => Text(statDefinition.description),
                    _ => Unknown(node, field),
                };
            default: return Unknown(node, field);
        }
    }

    private static GameNode Child(GameNode parent, string kind, string key, object handle) =>
        new(kind, parent.Key + "/" + key, parent.Owner, handle);
    private static GameNode? Optional(GameNode parent, string kind, string key, object? handle) =>
        handle == null ? null : Child(parent, kind, key, handle);
    private static object Unknown(GameNode node, string field) =>
        throw new ArgumentException("Unknown " + node.Kind + " field: " + field);
    private static string? Text(object? value) => value?.ToString();
    private static double? Number(float? value)
    {
        if (!value.HasValue) return null;
        if (float.IsNaN(value.Value) || float.IsInfinity(value.Value))
            throw ReadContext.Error("invalid_number", "The game returned a non-finite number.");
        return Math.Round((double)value.Value, 6);
    }
    private static bool VisibleOnly(IReadOnlyDictionary<string, object?> args) =>
        !args.TryGetValue("visibleOnly", out var value) || value == null || (bool)value;

    private static IEnumerable<GameNode> Needs(GameNode parent, Pawn pawn)
    {
        foreach (var need in pawn.needs.AllNeeds)
            if (need != null && need.ShowOnNeedList) yield return Child(parent, "Need", need.def.defName, need);
    }
    private static IEnumerable<GameNode> JobTargets(GameNode parent, Job job)
    {
        foreach (var slot in new[] { TargetIndex.A, TargetIndex.B, TargetIndex.C })
        {
            var target = job.GetTarget(slot);
            if (target.IsValid) yield return Child(parent, "JobTarget", slot.ToString(), new TargetSource(slot.ToString(), target));
        }
    }
    private static IEnumerable<GameNode> ThoughtGroups(GameNode parent, ThoughtHandler handler, bool visibleOnly)
    {
        var groups = new List<Thought>();
        handler.GetDistinctMoodThoughtGroups(groups);
        for (var i = 0; i < groups.Count; i++)
            if (!visibleOnly || groups[i].VisibleInNeedsTab)
                yield return Child(parent, "ThoughtGroup", "group:" + i, new ThoughtGroupSource(handler, groups[i]));
    }
    private static IEnumerable<GameNode> Memories(GameNode parent, MemoryThoughtHandler handler, bool visibleOnly)
    {
        for (var i = 0; i < handler.Memories.Count; i++)
        {
            var memory = handler.Memories[i];
            if (!visibleOnly || memory.VisibleInNeedsTab)
                yield return Child(parent, "Thought", "memory:" + i, memory);
        }
    }
    private static IEnumerable<GameNode> Thoughts(GameNode parent, IReadOnlyList<Thought> thoughts, bool visibleOnly)
    {
        for (var i = 0; i < thoughts.Count; i++)
            if (!visibleOnly || thoughts[i].VisibleInNeedsTab)
                yield return Child(parent, "Thought", "member:" + i, thoughts[i]);
    }
    private static IEnumerable<GameNode> Capacities(GameNode parent, Pawn pawn)
    {
        foreach (var capacity in DefDatabase<PawnCapacityDef>.AllDefsListForReading)
            if (capacity.CanShowOnPawn(pawn)) yield return Child(parent, "Capacity", capacity.defName, capacity);
    }
    private static IEnumerable<GameNode> Conditions(GameNode parent, Pawn pawn, bool visibleOnly)
    {
        foreach (var condition in pawn.health.hediffSet.hediffs)
            if (condition != null && (!visibleOnly || condition.Visible))
                yield return Child(parent, "Condition", condition.GetUniqueLoadID(), condition);
    }
    private static IEnumerable<GameNode> BodyParts(GameNode parent, Pawn pawn)
    {
        foreach (var part in pawn.RaceProps.body.AllParts) yield return PartNode(parent, part);
    }
    private static string PartKey(GameNode parent, BodyPartRecord part) =>
        parent.Owner.Id + "/body:" + part.body.defName + "/part:" + part.Index;
    private static GameNode PartNode(GameNode parent, BodyPartRecord part) =>
        new("BodyPart", PartKey(parent, part), parent.Owner, part);
    private static IEnumerable<GameNode> Backstories(GameNode parent, Pawn pawn)
    {
        if (pawn.story.Childhood != null) yield return Child(parent, "Backstory", "childhood", pawn.story.Childhood);
        if (pawn.story.Adulthood != null) yield return Child(parent, "Backstory", "adulthood", pawn.story.Adulthood);
    }
    private static IEnumerable<GameNode> Traits(GameNode parent, Pawn pawn)
    {
        foreach (var trait in pawn.story.traits.allTraits)
            yield return Child(parent, "Trait", trait.def.defName + ":" + trait.Degree, trait);
    }
    private static IEnumerable<GameNode> Skills(GameNode parent, Pawn pawn)
    {
        foreach (var skill in pawn.skills.skills) yield return Child(parent, "Skill", skill.def.defName, skill);
    }
    private static IEnumerable<GameNode> Stats(GameNode parent, IReadOnlyDictionary<string, object?> args)
    {
        if (!args.TryGetValue("defNames", out var supplied) || supplied is not IEnumerable<object> names)
            throw ReadContext.Error("invalid_arguments", "stats requires explicit defNames.");
        foreach (var name in names)
        {
            if (name is not string defName || string.IsNullOrWhiteSpace(defName))
                throw ReadContext.Error("invalid_arguments", "Stat definition names must be non-empty strings.");
            var def = DefDatabase<StatDef>.GetNamedSilentFail(defName);
            if (def == null) throw ReadContext.Error("unknown_stat", "Unknown stat definition: " + defName);
            yield return Child(parent, "Stat", defName, new StatSource((Pawn)parent.Owner.Handle, def));
        }
    }
    private static IEnumerable<GameNode> StatDefinitions(GameNode parent, IReadOnlyDictionary<string, object?> args)
    {
        var search = args.TryGetValue("search", out var supplied) ? supplied as string : null;
        foreach (var def in DefDatabase<StatDef>.AllDefsListForReading.OrderBy(x => x.defName, StringComparer.Ordinal))
        {
            if (!string.IsNullOrEmpty(search) &&
                def.defName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                (def.LabelForFullStatList?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) < 0)
                continue;
            yield return Child(parent, "StatDefinition", def.defName, def);
        }
    }

    private sealed class TargetSource
    {
        public TargetSource(string slot, LocalTargetInfo target) { Slot = slot; Target = target; }
        public string Slot { get; }
        public LocalTargetInfo Target { get; }
    }
    private sealed class ThoughtGroupSource
    {
        private List<Thought>? members;
        public ThoughtGroupSource(ThoughtHandler handler, Thought representative)
        { Handler = handler; Representative = representative; }
        public ThoughtHandler Handler { get; }
        public Thought Representative { get; }
        public List<Thought> Members
        {
            get
            {
                if (members != null) return members;
                var result = new List<Thought>();
                Handler.GetMoodThoughts(Representative, result);
                members = result;
                return members;
            }
        }
    }
    private sealed class StatSource
    {
        private readonly Pawn pawn;
        private float? value;
        public StatSource(Pawn pawn, StatDef def) { this.pawn = pawn; Def = def; }
        public StatDef Def { get; }
        public float Value
        {
            get
            {
                if (value.HasValue) return value.Value;
                var result = pawn.GetStatValue(Def);
                Number(result);
                value = result;
                return result;
            }
        }
    }
}
