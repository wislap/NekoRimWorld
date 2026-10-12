using System;
using GraphQL;
using GraphQL.Types;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>Selectable situation relations. Unselected columns never refresh UI caches.</summary>
internal static class SituationFactsSchema
{
    internal static ObjectGraphType Create()
    {
        var board = Type("ObjectSituation", "Selected situation sources; alerts and Archive are global, conditions/groups bind the selected map.");
        var clock = Type("ObjectSituationClock");
        Columns(clock, "i:ticksGame b:paused i:year s:quadrum i:dayOfQuadrum i:hour s:season s:dateText");
        var condition = Type("ObjectSituationCondition");
        Columns(condition, "s:id s:defName s:label s:scope i:sinceTick b:permanent i:ticksLeft");
        var quest = Type("ObjectSituationQuest", "Direct visible Quest link; native outcome states remain distinct.");
        Columns(quest, "s:id s:state");
        var subject = Type("ObjectSituationSubject", "Humanlike Pawn IDs; other Things grouped by definition, not inferred ownership.");
        Columns(subject, "s:id s:kind s:defName i:count s:mapId i:x i:z s:label");
        var group = Type("ObjectSituationThreatGroup", "Visible members of hostile Lords, not every hostile unit or a safety assessment.");
        Columns(group, "s:id s:factionId s:factionName s:factionDef s:lordJob s:toil");
        Collection(group, "members", subject, "values");
        var entry = Type("ObjectSituationEvent", "Archived letters and threat messages; recent window plus directly linked pending/ongoing tasks.");
        Columns(entry, "s:id s:kind s:defName s:label i:tick b:pinned");
        Link(entry, "quest", quest); Collection(entry, "subjects", subject, "values");
        var alert = Type("ObjectSituationAlert", "Active game UI cached fields. No stable native ID or true start time; no tooltip/report refresh.");
        Columns(alert, "s:type s:label s:priority b:active");
        Link(alert, "quest", quest);
        Link(board, "clock", clock);
        Collection(board, "conditions", condition, "identity");
        Collection(board, "threatGroups", group, "identity");
        Collection(board, "events", entry, "identity");
        Collection(board, "alerts", alert, "values");
        var comparisonType=Type("ObjectSituationComparison","Selected complete saved-source comparison, derived by the plugin without further game reads.");
        Columns(comparisonType,"b:available s:reason s:meaning s:baselineObservationId i:baselineTick");
        foreach(var name in new[]{"comparedSections","excludedSections","newItemIds","newSourceIds"}) {
            var value=PawnSchema.Add(comparisonType,name,new ListGraphType(new StringGraphType()),c=>Context(c).ReadNode(Node(c),name));
            value.Metadata["node_member"]=true;
        }
        var comparison = PawnSchema.Add(board, "comparison", comparisonType,
            c => Context(c).ReadNode(Node(c), "comparison"));
        comparison.Metadata["node_member"] = true;
        comparison.Metadata["object_derived"] = "saved_situation_comparison";
        comparison.Description = "Comparison of selected complete saved sources, derived by the consultation owner; never an extra game read.";
        return board;
    }
    private static ObjectGraphType Type(string name, string? description = null) => new() { Name = name, Description = description };
    private static ReadContext Context(IResolveFieldContext context) => (ReadContext)context.UserContext["read"]!;
    private static GameNode Node(IResolveFieldContext context) => (GameNode)context.Source!;
    private static void Columns(ObjectGraphType type, string declarations)
    {
        foreach (var declaration in declarations.Split(' '))
        {
            var pair = declaration.Split(':');
            IGraphType scalar = pair[0] switch { "i" => new IntGraphType(), "b" => new BooleanGraphType(), _ => new StringGraphType() };
            var field = PawnSchema.Add(type, pair[1], scalar, c => Context(c).ReadNode(Node(c), pair[1]));
            field.Metadata["node_member"] = true;
        }
    }
    private static void Link(ObjectGraphType type, string name, ObjectGraphType child)
    {
        var field = PawnSchema.Add(type, name, child, c => Context(c).ReadNode(Node(c), name));
        field.Metadata["node_member"] = true;
    }
    private static void Collection(ObjectGraphType type, string name, ObjectGraphType child, string capture)
    {
        PawnSchema.Collection(type, name, child);
        type.GetField(name)!.Metadata["object_capture"] = capture;
    }
}
