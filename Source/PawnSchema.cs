using System;
using System.Collections.Generic;
using System.Linq;
using GraphQL;
using GraphQL.Resolvers;
using GraphQL.Types;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>Nested observation schema. Section templates select these fields; no section DTO copies.</summary>
internal static class PawnSchema
{
    public const int DefaultFirst = 20;
    public const int MaxFirst = 100;
    public static ObjectGraphType Build(ThingSchema native)
    {
        var types = new Dictionary<string, ObjectGraphType>();
        ObjectGraphType Type(string name, string? description = null)
        {
            var type = new ObjectGraphType { Name = name, Description = description };
            types.Add(name, type); return type;
        }
        var status = Type("Status", "Current flags, job targets, position and needs. This is observation, not a risk score.");
        var position = Type("Position");
        var job = Type("Job"); var target = Type("JobTarget", "A target can be a Thing or a cell; fields not applicable to that kind are null.");
        var need = Type("Need", "Actual need record: level is raw, maxLevel is its maximum, percentage is 0..1. Food is not stock.");
        var mood = Type("Mood", "Mood tracker; null if this pawn does not have one.");
        var thresholds = Type("Thresholds", "Game-calculated mental break thresholds, not plugin risk classifications.");
        var group = Type("ThoughtGroup", "Current effective mood grouping, with the game-calculated group contribution; not the sum of every memory.");
        var thought = Type("Thought", "Thought instance. ref is valid within this observation only, not across queries or saves.");
        var health = Type("Health", "Hediffs include injuries, diseases, implants and other health conditions.");
        var summary = Type("HealthSummary"); var capacity = Type("Capacity"); var condition = Type("Condition");
        var part = Type("BodyPart", "Part key is scoped to the owner pawn and body definition/index; label/defName alone is not a unique part.");
        var tending = Type("Tending", "Only present for conditions with a tending component.");
        var immunity = Type("Immunity", "Only present for conditions with an immunity component.");
        var profile = Type("Profile"); var age = Type("Age"); var backstory = Type("Backstory");
        var trait = Type("Trait", "Traits preserve degree and suppression; suppressed traits are not silently treated as active.");
        var skill = Type("Skill", "Learned skill, passion and aptitude. Not a calculated StatDef value or a body capacity.");
        var statDefinition = Type("StatDefinition", "Available StatDef metadata; looking up a definition does not calculate any pawn stat.");
        var stat = Type("Stat", "Game-calculated StatDef value and formatted valueText. Requested explicitly by defNames, never all stats.");
        Scalars(status, "b:downed b:drafted b:dead b:inMentalState s:mentalState");
        Scalars(position, "i:x i:y i:z s:mapId"); Scalars(job, "s:id s:defName s:report");
        Scalars(target, "e:slot:Verse.AI.TargetIndex s:kind s:thingId s:mapId i:x i:y i:z");
        Scalars(need, "s:defName s:label f:level f:maxLevel f:percentage");
        Scalars(mood, "f:level f:instant"); Scalars(thresholds, "f:minor f:major f:extreme");
        Scalars(group, "s:defName s:label i:stageIndex f:moodOffset i:memberCount");
        Scalars(thought, "s:ref s:referenceScope s:defName s:label s:description i:stageIndex f:moodOffset s:kind s:otherPawnId i:ageTicks i:durationTicks");
        Scalars(summary, "f:healthPercent f:pain f:bleedRate b:downed b:inPainShock");
        Scalars(capacity, "s:defName s:label f:level");
        Scalars(condition, "s:id s:defName s:label s:description f:severity s:severityLabel b:visible");
        Scalars(part, "s:key s:defName s:label i:index s:parentKey f:health f:maxHealth");
        Scalars(tending, "b:isTended f:quality i:ticksLeft"); Scalars(immunity, "f:level b:fullyImmune");
        Scalars(profile, "e:gender:Verse.Gender s:raceDefName s:kindDefName");
        Scalars(statDefinition, "s:defName s:label s:description");
        Scalars(age, "f:biologicalYears f:chronologicalYears s:lifeStage");
        Scalars(backstory, "e:slot:RimWorld.BackstorySlot s:defName s:title s:description");
        Scalars(trait, "s:defName s:label s:description i:degree b:suppressed s:sourceGeneDefName s:suppressedByGeneDefName");
        Scalars(skill, "s:defName s:label i:level e:passion:RimWorld.Passion i:aptitude b:totallyDisabled b:permanentlyDisabled f:xpSinceLastLevel f:xpRequiredForLevelUp");
        Scalars(stat, "s:defName s:label f:value s:valueText");
        mood.GetField("level")!.Description = "Current mood need fraction, not the instantaneous target.";
        mood.GetField("instant")!.Description = "Game CurInstantLevelPercentage: current instantaneous/target mood from thoughts, not a historical observation.";
        group.GetField("moodOffset")!.Description = "Effective mood offset for this group as computed by the game. Do not sum raw memory offsets as a substitute.";
        thought.GetField("moodOffset")!.Description = "Individual thought offset; grouping/stacking can change its effective contribution.";
        thought.GetField("ageTicks")!.Description = "Memory age in game ticks; null for non-memory thoughts.";
        thought.GetField("durationTicks")!.Description = "Game memory duration in ticks; null for non-memory thoughts.";
        summary.GetField("bleedRate")!.Description = "Native BleedRateTotal: blood-loss fraction per game day (60000 ticks). Multiply by 100 for percentage points per game day; never per second. This rate alone is not a computed survival-time estimate.";
        trait.GetField("degree")!.Description = "Trait variant identifier, not a numeric stat bonus. The game-generated description reports this trait's effects; suppression is separate.";
        summary.GetField("healthPercent")!.Description = "Game summaryHealth.SummaryHealthPercent: an overall game-computed health indicator. Report it separately from conditions and body-part HP; this read is not a blood-volume measurement. A value of 1 does not establish a cause or a general rule about which conditions affect it.";
        summary.GetField("pain")!.Description = "Game PainTotal aggregate, distinct from injury severity and health percentage. This aggregate alone does not attribute pain to individual listed conditions.";
        condition.GetField("severity")!.Description = "Raw Hediff severity; meaning/range depends on its definition. Not a universal 0..1 health fraction.";
        skill.GetField("aptitude")!.Description = "Game skill Aptitude, distinct from learned level, XP and passion.";
        tending.GetField("ticksLeft")!.Description = "Remaining tending duration in game ticks; null when not tended.";
        stat.GetField("valueText")!.Description = "Game-formatted stat value including its display unit; use with raw value rather than guessing percent units.";
        Child(status, "position", position); Child(status, "currentJob", job); Collection(status, "needs", need);
        Collection(job, "targets", target);
        Child(mood, "breakThresholds", thresholds);
        Collection(mood, "thoughtGroups", group, VisibleArguments()); Collection(mood, "memories", thought, VisibleArguments());
        Collection(group, "members", thought);
        Child(health, "summary", summary); Collection(health, "capacities", capacity);
        Collection(health, "conditions", condition, VisibleArguments()); Collection(health, "bodyParts", part);
        Child(condition, "part", part); Child(condition, "tending", tending); Child(condition, "immunity", immunity);
        Child(profile, "age", age); Collection(profile, "backstories", backstory);
        Collection(profile, "traits", trait); Collection(profile, "skills", skill);
        var pawn = new ObjectGraphType { Name = "Pawn", Description = "Native Verse.Pawn instance, including humans, animals, mechanoids and other Pawn kinds; Pawn does not mean human or colonist. The selected root determines the population; id joins the same instance." };
        Scalars(pawn, "b:isFreeColonist b:isColonist b:isPrisonerOfColony b:isSlaveOfColony e:guestStatus:RimWorld.GuestStatus");
        pawn.GetField("isFreeColonist")!.Description = "Native Pawn.IsFreeColonist: this pawn is a free member of the player faction. This is the native membership test, not an inference from kind, name or carried items.";
        pawn.GetField("isColonist")!.Description = "Native Pawn.IsColonist: player-faction colonist. Distinct from IsFreeColonist (a colonist can be a slave or prisoner).";
        pawn.GetField("isPrisonerOfColony")!.Description = "Native Pawn.IsPrisonerOfColony.";
        pawn.GetField("isSlaveOfColony")!.Description = "Native Pawn.IsSlaveOfColony.";
        pawn.GetField("guestStatus")!.Description = "Native Pawn.GuestStatus (Guest/Prisoner/Slave); null when the pawn has no such status.";
        foreach (var field in new[] { "id", "name", "mapId" })
        {
            var name = field;
            Add(pawn, name, name == "id" ? new NonNullGraphType(new IdGraphType()) : new StringGraphType(),
                c => State(c).Read("Pawn", (GameEntity)c.Source!, name));
        }
        foreach (var pair in new[] { ("status", status), ("mood", mood), ("health", health), ("equipment", native.Equipment), ("apparel", native.Apparel), ("inventory", native.Inventory), ("carryTracker", native.Carry), ("profile", profile), ("faction", native.Faction) })
        {
            var name = pair.Item1;
            Add(pawn, name, pair.Item2, c => State(c).ReadNode(PawnNode((GameEntity)c.Source!), name)).Metadata["node_member"] = true;
        }
        pawn.GetField("mapId")!.Description = "Map ID attached to this Pawn by the selected root. Loaded-map pawn queries use the containing map (native MapHeld), including held Pawns; not evidence that the Pawn itself is spawned. status.position uses the Pawn's own spawned position.";
        status.GetField("position")!.Description = "This Pawn's own spawned map position; null when not spawned. A null position does not establish absence from a loaded map or caravan membership.";
        status.GetField("currentJob")!.Description = "Current native job; null when there is no current job. It does not establish health, faction or the reason for inactivity.";
        // Preserve native tracker fields for the legacy protocol, but expose their contents consistently to the Agent.
        foreach (var field in new[] { "inventory","equipment","apparel","carryTracker" }) pawn.GetField(field)!.Metadata["public_hidden"] = true;
        Contents(pawn,"inventoryItems","inventory",native.Thing,new[] { "inventory","innerContainer","items" });
        Contents(pawn,"equipmentItems","equipment",native.Thing,new[] { "equipment","AllEquipmentListForReading" });
        Contents(pawn,"apparelItems","apparel",native.Thing,new[] { "apparel","WornApparel" });
        Contents(pawn,"carriedItems","carried",native.Thing,new[] { "carryTracker","CarriedThing" },single:true);
        pawn.GetField("inventoryItems")!.Description = "Items in this Pawn's inventory, distinct from worn apparel, equipped items and the currently carried Thing. Inventory is a containment relation, not current hauling or faction ownership.";
        pawn.GetField("equipmentItems")!.Description = "Items equipped through this Pawn's equipment tracker, distinct from inventory and worn apparel. Read their definitions to identify weapons.";
        pawn.GetField("apparelItems")!.Description = "Apparel currently worn by this Pawn, distinct from equipment, inventory and the currently carried Thing.";
        pawn.GetField("carriedItems")!.Description = "The current Pawn_CarryTracker.CarriedThing as a zero/one collection. An empty complete result means no currently carried Thing, not an empty inventory or proof about past/future hauling.";
        Collection(pawn, "statDefinitions", statDefinition, new QueryArguments(GameArguments.Create("search", new StringGraphType(),
            "Case-insensitive substring of definition name or label. Does not compute stat values.", new JObject { ["maxLength"] = 128 })));
        Collection(pawn, "stats", stat, new QueryArguments(GameArguments.Create("defNames",
            new NonNullGraphType(new ListGraphType(new NonNullGraphType(new StringGraphType()))),
            "Explicit StatDef names, 1..16. No automatic relevance selection.", new JObject { ["minItems"] = 1, ["maxItems"] = 16 })), 16);
        return pawn;
    }
    private static GameNode PawnNode(GameEntity entity) => new("Pawn", "pawn", entity, entity.Handle);
    internal static void Contents(ObjectGraphType type,string name,string publicName,IGraphType element,string[] path,bool single = false)
    {
        Collection(type,name,element);
        var field = type.GetField(name)!;
        field.Resolver = new FuncFieldResolver<object?>(c => {
            var state = State(c); state.TraceField(c);
            var raw = state.ProjectContents(Node(c),path,single);
            if (raw == null) return null;
            var args = Arguments(c);
            var rows = state.Filter((System.Collections.IEnumerable)raw,element.Name,args.TryGetValue("where",out var where) ? where : null).Cast<GameNode>().ToList();
            var first = c.GetArgument<int?>("first") ?? DefaultFirst; var offset = c.GetArgument<int?>("offset") ?? 0;
            return new NodeSlice { Items = rows.Skip(offset).Take(first).ToList(), Total = rows.Count, Offset = offset };
        });
        field.Metadata["projection_path"] = path;
        field.Metadata["projection_single"] = single;
        field.Metadata["public_name"] = publicName;
        field.Metadata["collection_element"] = element;
        field.Metadata["list_default"] = DefaultFirst;
        field.Metadata["list_max"] = MaxFirst;
        field.Metadata["native_path"] = string.Join(".",path);
        field.Description = "Contents in the " + publicName + " relation. Read directly as a Thing collection with the same selection, conditions and aggregation as other collections. Preserves this relation's population; an absent container is not an empty collection.";
    }
    private static ReadContext State(IResolveFieldContext c) => (ReadContext)c.UserContext["read"]!;
    private static GameNode Node(IResolveFieldContext c) => c.Source is GameEntity pawn ? PawnNode(pawn) : (GameNode)c.Source!;
    private static Dictionary<string, object?> Arguments(IResolveFieldContext c) => c.Arguments == null ? new() :
        c.Arguments.Where(x => x.Key != "first" && x.Key != "offset").ToDictionary(x => x.Key, x => x.Value.Value);
    private static void Scalars(ObjectGraphType type, string declaration)
    {
        foreach (var word in declaration.Split(' '))
        {
            var parts = word.Split(':'); var name = parts[1];
            IGraphType graphType = parts[0] switch { "i" => new IntGraphType(), "f" => new FloatGraphType(), "b" => new BooleanGraphType(), _ => new StringGraphType() };
            var field = Add(type, name, graphType, c => State(c).ReadNode(Node(c), name, Arguments(c)));
            field.Metadata["node_member"] = true;
            if (parts[0] == "e") field.Metadata["native_enum"] = new NativeEnumScalar(parts[2]);
        }
    }
    private static void Child(ObjectGraphType type, string name, ObjectGraphType child) =>
        Add(type, name, child, c => State(c).ReadNode(Node(c), name, Arguments(c))).Metadata["node_member"] = true;
    private static QueryArguments VisibleArguments() => new(GameArguments.Create("visibleOnly", new BooleanGraphType(),
        "Default true: facts the game marks visible in this health/needs UI, not map fog or pathfinding. false includes hidden records in this relation.", defaultValue: true));
    internal static void Collection(ObjectGraphType type, string name, IGraphType item, QueryArguments? extra = null, int maximum = MaxFirst)
    {
        var slice = new ObjectGraphType { Name = type.Name + Upper(name) + "Slice", Description = "A bounded slice of this query's list. Offset continuation is a fresh observation, not a frozen snapshot. Null depends on the field and any issue at its path; a complete zero-match collection is observed empty under its own predicate, while an undisplayed page is not." };
        Add(slice, "items", new ListGraphType(item), c => ((NodeSlice)c.Source!).Items).Metadata["collection_view"] = true;
        Add(slice, "total", new IntGraphType(), c => ((NodeSlice)c.Source!).Total);
        Add(slice, "offset", new IntGraphType(), c => ((NodeSlice)c.Source!).Offset);
        Add(slice, "returned", new IntGraphType(), c => ((NodeSlice)c.Source!).Returned);
        Add(slice, "hasMore", new BooleanGraphType(), c => ((NodeSlice)c.Source!).HasMore);
        Add(slice, "nextOffset", new IntGraphType(), c => ((NodeSlice)c.Source!).NextOffset);
        Add(slice, "complete", new BooleanGraphType(), c => ((NodeSlice)c.Source!).Complete);
        var args = new QueryArguments(GameArguments.Create("first", new IntGraphType(), "Slice size 1.." + maximum + ".",
            new JObject { ["minimum"] = 1, ["maximum"] = maximum }, Math.Min(DefaultFirst, maximum)),
            GameArguments.Create("offset", new IntGraphType(), "Start index in this observation; 0..8192.",
                new JObject { ["minimum"] = 0, ["maximum"] = 8192 }, 0));
        if (extra != null) foreach (var argument in extra) args.Add(argument);
        var field = Add(type, name, slice, c => State(c).Slice(Node(c), name,
            c.GetArgument<int?>("first") ?? Math.Min(DefaultFirst, maximum), c.GetArgument<int?>("offset") ?? 0, Arguments(c), item.Name), args);
        field.Metadata["list_max"] = maximum;
        field.Metadata["list_default"] = Math.Min(DefaultFirst, maximum);
        field.Metadata["slice"] = true;
        field.Metadata["collection_element"] = item;
    }
    private static string Upper(string text) => char.ToUpperInvariant(text[0]) + text.Substring(1);
    internal static FieldType Add(ObjectGraphType type, string name, IGraphType graphType,
        Func<IResolveFieldContext, object?> resolver, QueryArguments? args = null)
    {
        var field = new FieldType { Name = name, ResolvedType = graphType, Arguments = args,
            Resolver = new FuncFieldResolver<object?>(c => { State(c).TraceField(c); return resolver(c); }) };
        type.AddField(field); return field;
    }
}
