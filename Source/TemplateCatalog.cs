using System;
using System.Collections.Generic;
using System.Linq;
using GraphQL;
using GraphQL.Types;
using GraphQLParser;
using GraphQLParser.AST;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal sealed class ExpandedQuery
{
    public string Document { get; set; } = "";
    public JObject Variables { get; set; } = new();
    public JArray Bindings { get; set; } = new();
}

/// <summary>Object selectors plus sections. All selected sections merge into one canonical object tree.</summary>
internal sealed class TemplateCatalog
{
    private sealed class Section
    {
        public string Name = "";
        public string Description = "";
        public string Selection = "";
    }
    private sealed class Target
    {
        public string Name = "";
        public string Root = "";
        public string Identity = "";
        public Dictionary<string, string> Parameters = new();
        public Section[] Sections = Array.Empty<Section>();
    }
    private readonly ISchema schema;
    private const string SliceInfo = "total offset returned hasMore nextOffset complete";
    private static readonly string StatusSelection = "status { downed drafted dead inMentalState mentalState position { x y z mapId } currentJob { id defName report targets(first:3) { items { slot kind thingId mapId x y z } " + SliceInfo + " } } needs(first:20) { items { defName label level maxLevel percentage } " + SliceInfo + " } } mood { level } health { summary { healthPercent pain } }";
    private static readonly string MoodSelection = "mood { level instant breakThresholds { minor major extreme } thoughtGroups(first:20) { items { defName label stageIndex moodOffset memberCount } " + SliceInfo + " } }";
    private static readonly string HealthSelection = "health { summary { healthPercent pain bleedRate downed inPainShock } capacities(first:20) { items { defName label level } " + SliceInfo + " } conditions(first:20) { items { id defName label severity severityLabel visible part { key defName label index parentKey } tending { isTended quality ticksLeft } immunity { level fullyImmune } } " + SliceInfo + " } }";
    private const string ThingBasic = "id runtimeType def { defName label category thingClass } stackCount Stuff { defName } HitPoints MaxHitPoints";
    private const string Placement = "Spawned Map { id } MapHeld { id } Position { x y z } PositionHeld { x y z } holdingOwner { Owner { ref runtimeType thingId } } ParentHolder { ref runtimeType thingId }";
    private const string Components = "AllComps(first:10) { items { ref runtimeType supported supportedType ... on CompQuality { Quality } ... on CompRottable { RotProgress Stage TicksUntilRotAtCurrentTemp } ... on CompForbiddable { Forbidden } ... on CompIngredients { ingredients(first:10) { items { defName label } " + SliceInfo + " } } } " + SliceInfo + " }";
    private const string EquipmentSelection = "equipment { Primary { id } AllEquipmentListForReading(first:5) { items { " + ThingBasic + " } " + SliceInfo + " } } apparel { WornApparel(first:10) { items { " + ThingBasic + " } " + SliceInfo + " } } inventory { innerContainer { items(first:10) { items { " + ThingBasic + " } " + SliceInfo + " } } } carryTracker { CarriedThing { " + ThingBasic + " } }";
    private const string PageInfo = "pageInfo { hasNextPage endCursor }";
    private static readonly string ProfileSelection = "profile { gender raceDefName kindDefName age { biologicalYears chronologicalYears lifeStage } backstories(first:2) { items { slot defName title description } " + SliceInfo + " } traits(first:20) { items { defName label description degree suppressed sourceGeneDefName suppressedByGeneDefName } " + SliceInfo + " } skills(first:32) { items { defName label level passion aptitude totallyDisabled permanentlyDisabled } " + SliceInfo + " } }";
    private readonly Target[] targets = {
        new() { Name = "pawn", Root = "pawn", Identity = "id name mapId", Parameters = new() { ["id"] = "id" }, Sections = new[] {
            new Section { Name = "status", Description = "Current detailed flags, location, job targets and needs with mood/health overview. Need levels are not stock quantities.", Selection = StatusSelection },
            new Section { Name = "mood", Description = "Current/instant mood, game break thresholds and effective thought groups. Raw memories/group members are available through exact queries. Groups use the game's contribution, not a sum of memories.", Selection = MoodSelection },
            new Section { Name = "health", Description = "Health summary, body capacities and UI-visible conditions with body-part references and optional tending/immunity. Conditions include implants, not only wounds. Full body tree via exact query.", Selection = HealthSelection },
            new Section { Name = "equipment", Description = "Equipped weapons, worn apparel and carried inventory, with item identity and basic item properties. Quality null means not applicable.", Selection = EquipmentSelection },
            new Section { Name = "profile", Description = "Age, background, traits including suppression, and learned skills. Calculated stats are separate: discover statDefinitions then request stats(defNames).", Selection = ProfileSelection },
        } },
        new() { Name = "colony", Root = "colony", Sections = new[] {
            new Section { Name = "overview", Description = "Current game and loaded-map overview, not a complete colony safety assessment.", Selection = "game { paused currentMapId } maps { id name isCurrent isPlayerHome colonistCount hasVisibleThreat }" },
        } },
        new() { Name = "colonists", Root = "colonists", Parameters = new() { ["ids"] = "ids", ["map_id"] = "mapId", ["name"] = "name", ["first"] = "first", ["after"] = "after" }, Sections = new[] {
            new Section { Name = "summary", Description = "Find spawned free colonists on loaded maps and inspect a compact summary before selecting IDs for details. Name substring may match multiple people.", Selection = "nodes { id name mapId status { downed drafted inMentalState mentalState } mood { level } health { summary { healthPercent } } } pageInfo { hasNextPage endCursor }" },
        } },
        new() { Name = "thing", Root = "thing", Identity = "id runtimeType", Parameters = new() { ["id"] = "id", ["map_id"] = "mapId" }, Sections = new[] {
            new Section { Name = "basic", Description = "Native Thing identity, def reference, stackCount, Stuff and HP; not a flattened Item DTO.", Selection = ThingBasic },
            new Section { Name = "placement", Description = "Native Spawned/Map/MapHeld/Position/PositionHeld and holder references. Storage position is not container ownership.", Selection = Placement },
            new Section { Name = "components", Description = "Actual component instances, runtime type and supported native base members. Unknown components remain visible as unsupported metadata.", Selection = Components },
            new Section { Name = "contents", Description = "Direct IThingHolder contents and special MinifiedThing/Corpse references; no recursive full expansion.", Selection = "InnerThing { id runtimeType def { defName } } InnerPawn { id runtimeType } GetDirectlyHeldThings { Owner { ref runtimeType } items(first:10) { items { " + ThingBasic + " } " + SliceInfo + " } }" },
        } },
        new() { Name = "things", Root = "things", Parameters = new() { ["map_id"] = "mapId", ["request_group"] = "requestGroup", ["def_names"] = "defNames", ["name"] = "name", ["visible_only"] = "visibleOnly", ["first"] = "first", ["after"] = "after" }, Sections = new[] {
            new Section { Name = "basic", Description = "Spawned map things from a native query group, optional definitions/name and cursor. Excludes held contents. No promise of current pawn-specific hauling eligibility.", Selection = "nodes { " + ThingBasic + " } " + PageInfo },
            new Section { Name = "placement", Description = "Map thing placement and holder references, with per-page continuation.", Selection = "nodes { id " + Placement + " } " + PageInfo },
            new Section { Name = "components", Description = "Bounded component data for the selected page; narrow first for expensive combinations.", Selection = "nodes { id " + Components + " } " + PageInfo },
        } },
        new() { Name = "thing_def", Root = "thing_def", Identity = "defName", Parameters = new() { ["def_name"] = "defName" }, Sections = DefinitionSections(false) },
        new() { Name = "thing_defs", Root = "thing_defs", Parameters = new() { ["name"] = "name", ["category_def_name"] = "categoryDefName", ["first"] = "first", ["after"] = "after" }, Sections = DefinitionSections(true) },
        new() { Name = "map", Root = "map", Identity = "id", Parameters = new() { ["id"] = "id" }, Sections = new[] {
            new Section { Name = "storage_groups", Description = "Native SlotGroup references, settings priority and currently stored things. HeldThings overlap map things, do not double-count. Settings are not actual contents.", Selection = "haulDestinationManager { AllGroupsListForReading(first:5) { items { ref GroupingLabel parent { ref runtimeType thingId } Settings { Priority } HeldThings(first:10) { items { " + ThingBasic + " } " + SliceInfo + " } } " + SliceInfo + " } }" },
            new Section { Name = "resource_counter", Description = "Game cached ResourceCounter entries. Stored-resource rules apply; not all-map totals, not held inventory. Does not force a game counter update.", Selection = "resourceCounter { AllCountedAmounts(first:30) { items { key { defName label } value } " + SliceInfo + " } }" },
        } },
    };
    private static Section[] DefinitionSections(bool page) => new[] {
        new Section { Name="basic", Description="Native ThingDef identity, runtime class, category and stack limit.", Selection="defName label thingClass category stackLimit" },
        new Section { Name="hauling", Description="Definition-level native hauling properties, not current pawn eligibility.", Selection="defName alwaysHaulable designateHaulable EverHaulable CountAsResource" },
        new Section { Name="ingestible", Description="Native ingestible definition data; null if absent, not a pawn-specific food permission.", Selection="defName ingestible { foodType drugCategory HumanEdible IsMeal CachedNutrition maxNumToIngestAtOnce }" },
        new Section { Name="component_definitions", Description="Declared CompProperties and content-category references; no reflection of custom members.", Selection="defName comps(first:20) { items { runtimeType compClass } " + SliceInfo + " } thingCategories(first:20) { items { defName label parent { defName } } " + SliceInfo + " }" },
    }.Select(section => { if (page) section.Selection="nodes { " + section.Selection + " } " + PageInfo; return section; }).ToArray();
    public TemplateCatalog(ISchema schema) {
        this.schema = schema;
        foreach (var target in targets)
            if (schema.Query!.GetField(target.Root)!.Arguments?.Any(a => a.Name == "where") == true) target.Parameters["where"] = "where";
    }
    public JArray Describe() => new(targets.Select(t => new JObject {
        ["target"] = t.Name, ["parameters"] = ParameterSchema(t),
        ["sections"] = new JArray(t.Sections.Select(s => new JObject { ["name"] = s.Name, ["description"] = s.Description, ["returns"] = s.Selection })),
    }));
    public JObject InputSchema() => new() {
        ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("requests"),
        ["properties"] = new JObject { ["requests"] = new JObject {
            ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 4,
            ["items"] = new JObject { ["anyOf"] = new JArray(targets.Select(t => new JObject {
                ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("target", "sections"),
                ["properties"] = new JObject {
                    ["target"] = ParameterSchema(t),
                    ["sections"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = t.Sections.Length, ["uniqueItems"] = true,
                        ["items"] = new JObject { ["type"] = "string", ["enum"] = new JArray(t.Sections.Select(s => s.Name)) },
                        ["description"] = string.Join("\n", t.Sections.Select(s => s.Name + ": " + s.Description + " Returns " + s.Selection)),
                    },
                },
            })) },
        } },
    };
    private JObject ParameterSchema(Target target)
    {
        var props = new JObject { ["type"] = new JObject { ["type"] = "string", ["enum"] = new JArray(target.Name) } };
        var required = new JArray("type");
        foreach (var binding in target.Parameters)
        {
            var argument = Argument(target.Root, binding.Value);
            var value = JsonType(argument.ResolvedType!);
            value["description"] = argument.Description;
            if (argument.DefaultValue != null) value["default"] = JToken.FromObject(argument.DefaultValue);
            if (argument.Metadata.TryGetValue("json_limits", out var raw) && raw is JObject limits)
                foreach (var property in limits.Properties()) value[property.Name] = property.Value.DeepClone();
            props[binding.Key] = value;
            if (argument.ResolvedType is NonNullGraphType) required.Add(binding.Key);
        }
        return new JObject { ["type"] = "object", ["properties"] = props, ["required"] = required, ["additionalProperties"] = false };
    }
    private QueryArgument Argument(string root, string name) => schema.Query!.GetField(root)!.Arguments!.First(x => x.Name == name);
    private static JObject JsonType(IGraphType type) => type switch {
        NonNullGraphType n => JsonType(n.ResolvedType!),
        ListGraphType l => new JObject { ["type"] = "array", ["items"] = JsonType(l.ResolvedType!) },
        ConditionGraphType => new JObject { ["type"] = "object" },
        FloatGraphType => new JObject { ["type"] = "number" },
        IntGraphType => new JObject { ["type"] = "integer" },
        BooleanGraphType => new JObject { ["type"] = "boolean" },
        _ => new JObject { ["type"] = "string" },
    };
    private static string TypeName(IGraphType type) => type switch {
        NonNullGraphType n => TypeName(n.ResolvedType!) + "!", ListGraphType l => "[" + TypeName(l.ResolvedType!) + "]", _ => type.Name,
    };
    private sealed class ObjectPlan
    {
        public Target Target = null!;
        public JObject Selector = null!;
        public string Root = "";
        public List<SelectField> Fields = new();
    }
    public ExpandedQuery Expand(JToken? requests)
    {
        if (requests is not JArray rows || rows.Count < 1 || rows.Count > 4)
            throw ReadContext.Error("invalid_arguments", "requests must contain 1..4 object requests.");
        var output = new ExpandedQuery();
        var plans = new Dictionary<string, ObjectPlan>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            try
            {
                if (rows[i] is not JObject row) throw ReadContext.Error("invalid_arguments", "request must be an object.");
                // The directory already owns each target's selectors. Use it
                // to locate misplaced keys, without moving or interpreting values.
                var hintedTarget = targets.FirstOrDefault(t => row["target"] is JObject hint && hint["type"]?.Type == JTokenType.String && (string?)hint["type"] == t.Name);
                var path = "requests[" + i + "]";
                CheckKeys(row, new[] { "target", "sections" }, path,
                    key => hintedTarget?.Parameters.ContainsKey(key) == true ? path + ".target." + key : null);
                if (row["target"] is not JObject selector || selector["type"]?.Type != JTokenType.String)
                    throw ReadContext.Error("invalid_arguments", "Each request requires a target object and sections array.");
                var target = targets.FirstOrDefault(t => t.Name == (string?)selector["type"])
                    ?? throw ReadContext.Error("unknown_target", "Unknown target; consult the supplied template directory.");
                CheckKeys(selector, target.Parameters.Keys.Concat(new[] { "type" }), path + ".target",
                    key => key == "sections" ? path + ".sections" : null);
                if (row["sections"] is not JArray sections)
                    throw ReadContext.Error("invalid_arguments", path + ".sections must be an array of section names.");
                var spec = ParameterSchema(target);
                foreach (var required in ((JArray)spec["required"]!).Values<string>())
                    if (!selector.ContainsKey(required!)) throw ReadContext.Error("invalid_arguments", "Target requires " + required + ".");
                foreach (var property in selector.Properties()) ValidateValue(property.Value, (JObject)spec["properties"]![property.Name]!, property.Name);
                if (sections.Count == 0 || sections.Count > target.Sections.Length || sections.Any(x => x.Type != JTokenType.String) || sections.Select(x => x.ToString()).Distinct().Count() != sections.Count)
                    throw ReadContext.Error("invalid_sections", "sections must be nonempty unique names applicable to this target.");
                var selected = sections.Select(x => target.Sections.FirstOrDefault(s => s.Name == x.ToString())
                    ?? throw ReadContext.Error("unknown_section", "Target " + target.Name + " supports: " + string.Join(", ", target.Sections.Select(s => s.Name)))).ToArray();
                var key = new JObject(selector.Properties().OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => new JProperty(x.Name, x.Value.DeepClone()))).ToString(Formatting.None);
                if (!plans.TryGetValue(key, out var plan))
                {
                    plan = new ObjectPlan { Target = target, Selector = selector, Root = "o" + plans.Count };
                    Merge(plan.Fields, Parse(target.Identity)); plans.Add(key, plan);
                }
                foreach (var section in selected) Merge(plan.Fields, Parse(section.Selection));
                output.Bindings.Add(new JObject { ["index"] = i, ["root"] = plan.Root, ["target"] = selector.DeepClone(), ["sections"] = sections.DeepClone() });
            }
            catch (ExecutionError error) {
                var prefix = "requests[" + i + "]";
                throw ReadContext.Error(error.Code ?? "invalid_arguments", error.Message.StartsWith(prefix, StringComparison.Ordinal) ? error.Message : prefix + ": " + error.Message);
            }
        }
        var declarations = new List<string>(); var roots = new List<string>();
        foreach (var plan in plans.Values)
        {
            var actualArgs = new List<string>();
            foreach (var binding in plan.Target.Parameters)
            {
                if (!plan.Selector.ContainsKey(binding.Key)) continue;
                var variable = plan.Root + "_" + binding.Key;
                declarations.Add("$" + variable + ":" + TypeName(Argument(plan.Target.Root, binding.Value).ResolvedType!));
                output.Variables[variable] = plan.Selector[binding.Key]!.DeepClone();
                actualArgs.Add(binding.Value + ":$" + variable);
            }
            roots.Add(plan.Root + ":" + plan.Target.Root + (actualArgs.Count == 0 ? "" : "(" + string.Join(",", actualArgs) + ")") + " { " + Render(plan.Fields) + " }");
        }
        output.Document = "query" + (declarations.Count == 0 ? "" : "(" + string.Join(",", declarations) + ")") + " { " + string.Join(" ", roots) + " }";
        return output;
    }
    private static void CheckKeys(JObject value, IEnumerable<string> allowed, string path, Func<string, string?> location)
    {
        var names = allowed.ToArray();
        var errors = value.Properties().Where(p => !names.Contains(p.Name)).Select(p => {
            var expected = location(p.Name);
            return expected == null
                ? path + "." + p.Name + " is not supported here; allowed: " + string.Join(", ", names) + "."
                : path + "." + p.Name + " is misplaced; supply it only at " + expected + ". If both locations are present, choose the intended value there; no value was moved or used.";
        }).ToArray();
        if (errors.Length > 0) throw ReadContext.Error("invalid_arguments", string.Join(" ", errors));
    }
    private static void ValidateValue(JToken value, JObject schema, string name)
    {
        var type = (string)schema["type"]!;
        if (type == "object") {
            if (value is not JObject) throw ReadContext.Error("invalid_arguments", name + " must be an object.");
        } else if (type == "array") {
            if (value is not JArray array || schema["maxItems"] != null && array.Count > (int)schema["maxItems"]!)
                throw ReadContext.Error("invalid_arguments", name + " must be a bounded array.");
            foreach (var item in (JArray)value) ValidateValue(item, (JObject)schema["items"]!, name);
        } else if (type == "integer") {
            if (value.Type != JTokenType.Integer || schema["minimum"] != null && (long)value < (long)schema["minimum"]! || schema["maximum"] != null && (long)value > (long)schema["maximum"]!)
                throw ReadContext.Error("invalid_arguments", name + " has an invalid integer value.");
        } else if (type == "boolean") {
            if (value.Type != JTokenType.Boolean) throw ReadContext.Error("invalid_arguments", name + " must be boolean.");
        } else if (value.Type != JTokenType.String || ((string)value)!.Length > ((int?)schema["maxLength"] ?? 256) || ((string)value)!.Length < ((int?)schema["minLength"] ?? 0))
            throw ReadContext.Error("invalid_arguments", name + " must be bounded text.");
    }
    private sealed class SelectField
    {
        public string Name = "";
        public string Arguments = "";
        public bool Fragment;
        public List<SelectField> Children = new();
    }
    private static List<SelectField> Parse(string selection)
    {
        if (string.IsNullOrWhiteSpace(selection)) return new();
        var doc = Parser.Parse("{ " + selection + " }");
        List<SelectField> Fields(GraphQLSelectionSet set) => set.Selections.Select(selection => {
            if (selection is GraphQLInlineFragment fragment) return new SelectField { Name = fragment.TypeCondition!.Type.Name.StringValue, Fragment = true, Children = Fields(fragment.SelectionSet) };
            var field = (GraphQLField)selection;
            return new SelectField {
                Name = field.Name.StringValue,
                Arguments = field.Arguments == null ? "" : string.Join(",", field.Arguments.Select(a => a.Name.StringValue + ":" +
                    (a.Value is GraphQLIntValue n ? n.Value.ToString() : a.Value is GraphQLBooleanValue b ? b.BoolValue ? "true" : "false" : throw new InvalidOperationException("Template arguments must be fixed numeric/boolean values.")))),
                Children = field.SelectionSet == null ? new() : Fields(field.SelectionSet),
            };
        }).ToList();
        return Fields(((GraphQLOperationDefinition)doc.Definitions[0]).SelectionSet);
    }
    private static void Merge(List<SelectField> target, List<SelectField> incoming)
    {
        foreach (var field in incoming)
        {
            var existing = target.FirstOrDefault(x => x.Name == field.Name && x.Fragment == field.Fragment);
            if (existing == null) { target.Add(field); continue; }
            if (existing.Arguments != field.Arguments) throw new InvalidOperationException("Conflicting arguments in section templates for " + field.Name);
            Merge(existing.Children, field.Children);
        }
    }
    private static string Render(List<SelectField> fields) => string.Join(" ", fields.Select(f => (f.Fragment ? "... on " : "") + f.Name + (f.Arguments.Length == 0 ? "" : "(" + f.Arguments + ")") + (f.Children.Count == 0 ? "" : " { " + Render(f.Children) + " }")));
    public IEnumerable<string> ValidationQueries() => targets.Select(t => {
        var selector = new JObject { ["type"] = t.Name };
        foreach (var argument in t.Parameters) if (Argument(t.Root, argument.Value).ResolvedType is NonNullGraphType) selector[argument.Key] = "validation-id";
        // Include variables inline for standalone validator execution at schema startup.
        var expanded = Expand(new JArray(new JObject { ["target"] = selector, ["sections"] = new JArray(t.Sections.Select(s => s.Name)) }));
        var body = expanded.Document.Substring(expanded.Document.IndexOf('{'));
        foreach (var variable in expanded.Variables.Properties()) body = body.Replace("$" + variable.Name, variable.Value.ToString(Formatting.None));
        return "query " + body;
    });
}
