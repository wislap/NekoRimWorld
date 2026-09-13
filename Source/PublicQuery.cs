using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GraphQL;
using GraphQL.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal interface IQueryData
{
    IEnumerable<GameNode> QueryThings(IReadOnlyDictionary<string, object?> arguments, ReadContext context, Func<GameNode,bool>? definitionFilter = null);
    IEnumerable<GameEntity> QueryPawns(IReadOnlyDictionary<string, object?> arguments, ReadContext context);
}

/// <summary>Bounded relational reads over the existing executable field schema. No model-side joins or arithmetic.</summary>
internal sealed class PublicQuery
{
    internal const string Version = "public_query.v2";
    private readonly PublicFields registry;
    private readonly SelectionConditions conditions;
    private readonly Dictionary<string, FieldType> roots;
    private int compiledFields;
    private SelectionInput inputSyntax = new();
    private sealed class Projection
    {
        public JToken? Token;
        public string Path = "";
        public bool Generated;
        public IReadOnlyList<SelectionInput.Field>? Fields;
    }
    private readonly Dictionary<string,List<(string Type,FieldType Field,Dictionary<string,object?> Args)>> paths = new();
    private sealed class Selection
    {
        public string Type = "";
        public List<(string Name, FieldType Field, Dictionary<string, object?> Args, Selection? Child)> Fields = new();
        public Dictionary<string, Selection> On = new();
        public SelectionConditions.Predicate? Predicate;
        public JToken Where = new JObject();
        public int Limit = 20;
        public int Offset;
        public List<(string Name, string Op, string? Path)> Measures = new();
        public string[] GroupBy = Array.Empty<string>();
        public List<(string Path, bool Desc)> Order = new();
    }
    private sealed class Run
    {
        public readonly ReadContext Context;
        public readonly List<QueryIssue> Issues = new();
        public int Objects;
        public readonly JArray Phases = new();
        public readonly Dictionary<string,string> RelationSemantics = new();
        public readonly Dictionary<(object Value,string Type,string Path),JToken> PathValues = new();
        public Run(ReadContext context) { Context = context; }
        public void Check() { Context.Check(); if (++Objects > 20000) throw ReadContext.Error("query_limit","Query exceeds 20000 projected objects; narrow fields or rows."); }
        public void Fail(ExecutionError error,string path) => Issues.Add(new QueryIssue { Code = error.Code ?? "field_unavailable", Message = error.Message, Path = new JArray(path.Split('/').Where(s => s.Length > 0)) });
    }
    internal PublicQuery(Schema schema, SelectionConditions conditions)
    {
        registry = new PublicFields(schema,conditions); this.conditions = conditions;
        roots = schema.Query!.Fields.ToDictionary(f => f.Name == "colonists" ? "spawned_free_colonists" : PublicFields.Name(f.Name),f => f,StringComparer.Ordinal);
        roots["things"] = new FieldType { Name = "things", ResolvedType = registry.Type("Thing"), Description = "Things on the selected loaded maps, with holder contents only when include_held is true. Spawned storage-cell items remain map things when include_held is false. Includes buildings/pawns/items; no caravans or off-map world objects.", Arguments = new QueryArguments(
            GameArguments.Create("mapId",new StringGraphType(),"Loaded map ID; omitted means all loaded maps."),
            GameArguments.Create("includeHeld",new BooleanGraphType(),"Traverse nested holder contents in addition to spawned map Things. false retains spawned map Things, including storage-cell contents. This selects current containment, not player ownership or movement history.",defaultValue:true),
            GameArguments.Create("includeUnreal",new BooleanGraphType(),"Include native unreal contents (e.g. minified inner object/corpse pawn). Default false prevents counting those alongside their wrappers.",defaultValue:false),
            GameArguments.Create("visibleOnly",new BooleanGraphType(),"Exclude Things whose root held position is fogged.",defaultValue:true),
            GameArguments.Ids("defNames")) };
        roots["pawns"] = new FieldType { Name = "pawns", ResolvedType = registry.Type("Pawn"), Description = "All pawns reachable on selected loaded maps, including held pawns, prisoners and visitors. Does not include world caravans.", Arguments = new QueryArguments(GameArguments.Create("mapId",new StringGraphType(),"Loaded map ID; omitted means all loaded maps.")) };
    }
    internal JObject Catalog() {
        var commonTypes = new[] { "Thing", "ThingDef", "Pawn", "Map", "Game" };
        var fieldIndex = registry.CompactDirectory(roots.Keys.Select(RootType));
        var catalog = new JObject {
        ["version"] = Version,
        ["parameters"] = JObject.Parse(@"{'type':'object','properties':{'requests':{'type':'array','minItems':1,'maxItems':8,'items':{'type':'object','properties':{'from':{'type':'string'},'args':{'type':'object'},'where':{'type':'object'},'select':{'description':'Array of canonical field paths, or object mapping fields to true or nested selection options. Collections do not need items/nodes wrappers.','anyOf':[{'type':'array','items':{'type':'string'}},{'type':'object'}]},'aggregate':{'type':'object','description':'Named measures, e.g. {quantity:{op:sum,field:stack_count},records:{op:count}}. Operates on all matches before output limits; sum only for declared additive fields.'},'group_by':{'type':'array','items':{'type':'string'},'maxItems':12},'order_by':{'type':'array','items':{'type':'object','properties':{'field':{'type':'string'},'direction':{'type':'string','enum':['asc','desc']}},'required':['field'],'additionalProperties':false}},'limit':{'type':'integer','minimum':0,'maximum':100},'offset':{'type':'integer','minimum':0,'maximum':8192}},'required':['from'],'additionalProperties':false}}},'required':['requests'],'additionalProperties':false}"),
        ["roots"] = new JArray(roots.Select(p => new JObject { ["from"] = p.Key, ["type"] = RootType(p.Key), ["description"] = p.Value.Description ?? RootDescription(p.Key), ["args"] = new JArray((p.Value.Arguments?.ToArray() ?? Array.Empty<QueryArgument>()).Where(a => a.Name is not ("first" or "after" or "offset" or "where")).Select(a => new JObject { ["name"] = PublicFields.Name(a.Name), ["required"] = a.ResolvedType is NonNullGraphType, ["default"] = a.DefaultValue == null ? JValue.CreateNull() : JToken.FromObject(a.DefaultValue) })) })),
        ["rules"] = "Selection: use the canonical fields of the current type. A field on a related type is not a field on its parent. select arrays may combine string field paths and field-selection objects; duplicate or overlapping selections are rejected. String paths include, for example [\"definition.def_name\",\"stack_count\"]. An object maps each field to true, a child field array, or an options object. Equivalent child selections are {\"definition\":[\"def_name\",\"label\"]} and {\"definition\":{\"select\":[\"def_name\",\"label\"]}}. Use explicit select when configuring relation options. A nonempty object containing only known child field names, with no option keywords, is also a child selection. Do not mix child fields and relation options at the same level; unknown names are rejected rather than guessed. Single related objects accept select/args/on; collections also accept where/aggregate/group_by/order_by/limit/offset. Use on only for supported concrete component types. Collection selections do not need items/nodes transport wrappers.\nConditions: where is {field.path:{eq:value}}; clauses AND, with all/any/not for composition. Scalar ops: eq/ne/in/gt/gte/lt/lte/contains/starts_with/ends_with/is_null; case_sensitive:false for text. A condition path cannot cross a collection: use some/every/none with a nested where condition. Single-object conditions use where. reach:{field:parent,some:{...},include_self:true} traverses a declared same-type relation with cycle/depth limits. Do not infer condition paths from selection paths.\nDefaults: selected objects return readable scalars and parameter-free collections; objects without scalars expose related fields. Long descriptions, explicit contents and argument-dependent scans require selection. Collections default to limit20. Pawn inventory/equipment/apparel/carried are direct Thing collections; contents reads a Thing container. Their populations remain distinct, with the same collection operations; tracker traversal is internal. Map selectors accept map_id:current (map root id:current), resolved to the actual ID in scope without a preliminary map lookup. Native visible-record defaults remain in effect.\nMeasurements: group_by names scalar grouping fields. aggregate defines result aliases, not new source fields: quantity in {quantity:{op:sum,field:stack_count}} is not Thing.quantity. Count measures records; sum(stack_count) measures item units. Aggregates cover all matches before display limits. Grouped select returns only shared values in rows.values. A varying instance field is null with group_field_varies; selecting stack_count is not a substitute for summing it. Keep overlapping query totals separate; never add the ResourceCounter readout to physical counts.\nEvidence: assess each required fact using its effective scope and own completeness. Top-level partial does not invalidate a complete aggregate when only unrelated display fields failed. An incomplete aggregate is not an exact total. Complete aggregates do not prove every detail/group row was displayed: check that collection's page.has_more before claiming a complete list. scan_complete, fields_complete and page coverage answer different questions. Missing/failed is not zero. Empty results establish no matches for the actual predicate, not the absence of a broader user concept; verify uncertain predicate semantics, without routinely requerying every empty set. Continue only for an unmet requested fact, a relevant unknown or conflicting evidence; preserve known facts when a different field is unavailable. Every request/continuation is a fresh observation. Native capability flags, content categories and cached readout membership have different meanings; no task/category presets are applied.",
        ["common_fields"] = registry.Describe(commonTypes),
        ["field_index"] = fieldIndex,
        ["field_notes"] = registry.FieldNotes(fieldIndex.Properties().Select(p => p.Name).Except(commonTypes)),
        ["examples"] = JArray.Parse(@"[
            {'requests':[{'from':'things','args':{'map_id':'current','include_held':false},'where':{'definition.category':{'eq':'Item'}},'group_by':['definition.def_name'],'select':{'definition':['def_name','label','category','is_ingestible','is_weapon']},'aggregate':{'quantity':{'op':'sum','field':'stack_count'}}},{'from':'spawned_free_colonists','args':{'map_id':'current'},'select':['id','name','inventory','equipment','apparel','carried']}]}
        ]")
        };
        var properties = (JObject)catalog["parameters"]!["properties"]!["requests"]!["items"]!["properties"]!;
        properties["select"]!["description"] = "Select string field paths, true for defaults, or an object mapping fields to true, child arrays or options. Examples: {\"definition\":[\"def_name\",\"label\"]} or {\"definition\":{\"select\":[\"def_name\",\"label\"]}}. Child fields go inside select, not directly inside an options object. Selection arrays can combine string paths and field maps when their fields do not overlap. A bare child-field map is accepted only when all keys are known fields and none is a relation option. Collections do not need items/nodes wrappers.";
        properties["select"]!["anyOf"]![0]!["items"] = JObject.Parse("{'anyOf':[{'type':'string'},{'type':'object'}]}");
        ((JArray)properties["select"]!["anyOf"]!).Add(new JObject { ["const"] = true });
        properties["from"]!["enum"] = new JArray(roots.Keys);
        var arguments = new JObject();
        foreach (var root in roots.Values)
            foreach (var argument in root.Arguments?.ToArray() ?? Array.Empty<QueryArgument>())
                if (argument.Name is not ("where" or "first" or "after" or "offset")) arguments[PublicFields.Name(argument.Name)] = PublicFields.ArgumentSchema(argument);
        properties["args"] = new JObject { ["type"] = "object",["properties"] = arguments,["additionalProperties"] = false,["description"] = "Root arguments go ONLY here. Each root accepts the subset in its directory; map_id/current (map root id/current) avoids a preliminary map lookup." };
        var branches = new JArray(roots.Select(entry => {
            var fields = new JObject(); var required = new JArray();
            foreach (var argument in entry.Value.Arguments?.ToArray() ?? Array.Empty<QueryArgument>()) {
                if (argument.Name is "where" or "first" or "after" or "offset") continue;
                var key = PublicFields.Name(argument.Name); fields[key] = PublicFields.ArgumentSchema(argument);
                if (argument.ResolvedType is NonNullGraphType) required.Add(key);
            }
            var argsSchema = new JObject { ["type"] = "object",["properties"] = fields,["additionalProperties"] = false };
            if (required.Count > 0) argsSchema["required"] = required;
            var branch = new JObject { ["properties"] = new JObject { ["from"] = new JObject { ["const"] = entry.Key },["args"] = argsSchema },["required"] = new JArray("from") };
            if (required.Count > 0) ((JArray)branch["required"]!).Add("args");
            return branch;
        }));
        catalog["parameters"]!["properties"]!["requests"]!["items"]!["allOf"] = new JArray(new JObject { ["oneOf"] = branches.DeepClone() });
        var envelope = (JObject)catalog["parameters"]!;
        foreach (var property in properties.Properties()) envelope["properties"]![property.Name] = property.Value.DeepClone();
        envelope.Remove("required");
        envelope["oneOf"] = JArray.Parse("[{'required':['from'],'not':{'required':['requests']}},{'required':['requests'],'not':{'required':['from']}}]");
        envelope["allOf"] = new JArray(new JObject { ["anyOf"] = new JArray(
            new JObject { ["required"] = new JArray("requests") }, new JObject { ["oneOf"] = branches.DeepClone() }) });
        envelope["description"] = "Send one query directly with from/args/where/select, or send requests:[...] for a shared-sample batch. The query shape is identical; do not mix envelope and query fields.";
        return catalog;
    }
    private static string RootDescription(string name) => name switch {
        "spawned_free_colonists" => "Spawned free colonists on loaded maps only; not all pawns or containers.",
        "thing_defs" => "Loaded ThingDef definitions, not physical quantities. Filter on declared native properties, not label substring alone.",
        "map" => "Native loaded map: storage groups and resource counter. Requires id.",
        _ => "Existing public " + name + " root; exact identifiers use args, conditions use where."
    };
    internal JArray Describe(IEnumerable<string> types) => registry.Describe(types);
    private string RootType(string name) => name is "things" ? "Thing" : name is "pawns" or "spawned_free_colonists" ? "Pawn" : PublicFields.ValueType(roots[name]).Name;
    internal QueryOutcome Execute(JObject input, IGameData source, SessionDescriptor session, double budget, string schemaId)
    {
        var direct = input["from"] != null && input["requests"] == null;
        if (direct) input = new JObject { ["requests"] = new JArray(input.DeepClone()) };
        Keys(input,"requests");
        if (input["requests"] is not JArray requests || requests.Count is < 1 or > 8) throw PublicFields.Error("requests must contain 1..8 queries.");
        compiledFields = 0;
        inputSyntax = new SelectionInput();
        paths.Clear();
        var plans = new List<(string Root,Dictionary<string,object?> Args,Selection Select,SelectionConditions.Predicate? DefinitionFilter,bool FullyPushed)>();
        foreach (var token in requests)
        {
            if (token is not JObject obj || obj["from"]?.Type != JTokenType.String) throw PublicFields.Error("Each request needs from.");
            var name = (string)obj["from"]!;
            if (!roots.TryGetValue(name,out var root)) throw PublicFields.Error("Unknown from. Available: " + string.Join(", ",roots.Keys));
            var inputPath = direct ? "" : "/requests/" + requests.IndexOf(token);
            var arguments = ArgumentsAt(root, obj["args"], obj["args"] == null ? inputPath : SelectionInput.Pointer(inputPath, "args"), RootType(name));
            var selection = Compile(RootType(name),obj,0,true,inputPath);
            SelectionConditions.Predicate? definitionFilter = null;
            var fullyPushed = false;
            if (name == "things" && obj["where"] is JObject where)
            {
                var projected = DefinitionCondition(registry.Predicate("Thing",where),registry.Field("Thing","definition").Name);
                fullyPushed = projected.Exact;
                if (projected.Condition != null) definitionFilter = conditions.Compile("ThingDef",projected.Condition);
            }
            if (fullyPushed) selection.Predicate = null; // The data source applies the entire validated predicate once per definition.
            plans.Add((name,arguments,selection,definitionFilter,fullyPushed));
        }
        // Validate the whole batch before touching game state.
        var state = new ReadContext(source,session,budget) { Conditions = conditions }; var run = new Run(state); var results = new JArray();
        foreach (var plan in plans)
        {
            var path = "/results/" + results.Count;
            // Pushdown can discard only definitely false candidates. Unknown must reach the full AND predicate.
            Func<GameNode,bool>? filter = plan.DefinitionFilter == null ? null : n => {
                var match = plan.DefinitionFilter(n,state);
                if (match == null && plan.FullyPushed) throw ReadContext.Error("filter_unavailable","Definition membership is unknown.");
                return match != false;
            };
            var result = Collection(plan.Select, () => ReadRoot(plan.Root,plan.Args,source,state,filter), run,path);
            result["from"] = plan.Root;
            result["scope"] = new JObject { ["args"] = JObject.FromObject(plan.Args.ToDictionary(p => PublicFields.Name(p.Key),p => p.Value)), ["description"] = (roots[plan.Root].Description ?? RootDescription(plan.Root)) + " The resolved args and requested where below define this reading; completeness is per aggregate/collection." };
            result["scope"]!["argument_semantics"] = new JObject((roots[plan.Root].Arguments?.ToArray() ?? Array.Empty<QueryArgument>())
                .Where(a => plan.Args.ContainsKey(a.Name) && !string.IsNullOrEmpty(a.Description))
                .Select(a => new JProperty(PublicFields.Name(a.Name),a.Description)));
            result["scope"]!["where"] = requests[results.Count]!["where"]?.DeepClone() ?? new JObject();
            results.Add(result);
        }
        var data = new JObject { ["results"] = results, ["relation_semantics"] = JObject.FromObject(run.RelationSemantics), ["sample"] = new JObject { ["load_epoch"] = session.LoadEpoch,["game_tick"] = session.GameTick,["sequence"] = session.SnapshotSequence } };
        if (data.ToString(Formatting.None).Length > 80000) return QueryOutcome.Failure("query_result_too_large","Request fewer fields or displayed rows.");
        return new QueryOutcome { Status = run.Issues.Count == 0 ? "ok" : "partial", Data = data, Issues = run.Issues.Take(256).ToArray(), Execution = new JObject { ["schema_id"] = schemaId,["contract"] = Version,["requests"] = input["requests"]!.DeepClone(),["phases"] = run.Phases } };
    }
    private static (JObject? Condition,bool Exact) DefinitionCondition(JObject source,string definitionField)
    {
        // A necessary definition-only predicate. Non-definition clauses are TOP,
        // never false; NOT may move only when its whole child is exact.
        var terms = new List<JObject>(); var exact = true;
        foreach (var clause in source.Properties()) {
            JObject? value = null; var complete = false;
            if (clause.Name == "fields" && clause.Value is JObject fields) {
                var selected = new List<JObject>(); complete = true;
                foreach (var field in fields.Properties()) {
                    if (field.Name == definitionField && field.Value is JObject condition &&
                        !condition.ContainsKey("arguments") && !condition.ContainsKey("is_null"))
                        selected.Add((JObject)condition.DeepClone());
                    else complete = false;
                }
                value = JoinConditions(selected,"all");
            } else if (clause.Name is "all" or "any" && clause.Value is JArray children) {
                var projected = children.Cast<JObject>().Select(c => DefinitionCondition(c,definitionField)).ToArray();
                complete = projected.All(c => c.Exact);
                if (clause.Name == "all" || projected.All(c => c.Condition != null))
                    value = JoinConditions(projected.Where(c => c.Condition != null).Select(c => c.Condition!),clause.Name);
            } else if (clause.Name == "not" && clause.Value is JObject child) {
                var projected = DefinitionCondition(child,definitionField);
                if (projected.Exact && projected.Condition != null) {
                    value = new JObject { ["not"] = projected.Condition }; complete = true;
                }
            }
            if (value != null) terms.Add(value);
            exact &= complete;
        }
        var result = JoinConditions(terms,"all");
        return (result,exact && result != null);
    }
    private static JObject? JoinConditions(IEnumerable<JObject> conditions,string operation)
    {
        var list = conditions.ToArray();
        return list.Length == 0 ? null : list.Length == 1 ? list[0] : new JObject { [operation] = new JArray(list) };
    }

    private IEnumerable<object> ReadRoot(string root, Dictionary<string,object?> args, IGameData source, ReadContext state,Func<GameNode,bool>? definitionFilter)
    {
        var mapArgument = root == "map" ? "id" : "mapId";
        if (args.TryGetValue(mapArgument,out var requestedMap) && requestedMap as string == "current")
            args[mapArgument] = state.Read("Game",null,"currentMapId") as string ?? throw ReadContext.Error("object_unavailable","There is no current map.");
        if (args.TryGetValue(mapArgument,out var selectedMap) && selectedMap is string mapId && !state.Maps().Any(m => m.Id == mapId))
            throw ReadContext.Error("object_unavailable","Selected map is not loaded.");
        if (root is "things" or "pawns")
        {
            if (source is not IQueryData query) throw ReadContext.Error("query_source_unavailable","This data source has no complete loaded-map/holder query capability.");
            return root == "things" ? query.QueryThings(args,state,definitionFilter).Cast<object>() : query.QueryPawns(args,state).Cast<object>();
        }
        if (root == "spawned_free_colonists")
        {
            var values = state.Colonists().Where(p => !args.TryGetValue("mapId",out var map) || p.MapId == map as string);
            if (args.TryGetValue("ids",out var ids)) values = values.Where(p => ((IEnumerable)ids!).Cast<string>().Contains(p.Id));
            if (args.TryGetValue("name",out var name)) values = values.Where(p => (state.Read("Pawn",p,"name") as string)?.IndexOf((string)name!,StringComparison.OrdinalIgnoreCase) >= 0);
            return values.Cast<object>();
        }
        if (root == "pawn") {
            var pawns = source is IQueryData query ? query.QueryPawns(new Dictionary<string,object?>(),state) : state.Colonists();
            var found = pawns.FirstOrDefault(p => p.Id == (string)args["id"]!);
            if (found == null) throw ReadContext.Error("object_unavailable","Pawn ID is not reachable on loaded maps.");
            return new object[] { found };
        }
        if (root == "thing_defs") return ((IEnumerable)state.NativeRoot("query_defs",args)!).Cast<object>();
        var field = roots[root]; var value = registry.Read("Query",state,field,args,state);
        return value is IEnumerable sequence && value is not string ? sequence.Cast<object>() : value == null ? Array.Empty<object>() : new[] { value };
    }
    private Selection Compile(string type,JObject options,int depth,bool root = false,string inputPath = "",Projection? projection = null)
    {
        var checkPath = inputPath;
        try {
        if (depth > 12 || ++compiledFields > 512) throw PublicFields.Error("Selection exceeds depth12/512 fields.");
        KeysAt(options,inputPath,root ? new[] { "from","args","where","select","aggregate","group_by","order_by","limit","offset" } : new[] { "args","where","select","aggregate","group_by","order_by","limit","offset","on" });
        var plan = new Selection { Type = type, Limit = Integer(options,"limit",20,0,100), Offset = Integer(options,"offset",0,0,8192) };
        if (options["where"] != null) {
            checkPath = SelectionInput.Pointer(inputPath, "where");
            plan.Predicate = conditions.Compile(type,registry.Predicate(type,options["where"]!));
            plan.Where = options["where"]!.DeepClone();
        }
        if (projection == null) {
            var specified = options["select"];
            var generated = specified == null || specified.Type == JTokenType.Boolean && (bool)specified;
            projection = new Projection {
                Token = generated ? (options["group_by"] != null ? new JArray() : Defaults(type)) : specified,
                Path = specified == null ? inputPath : SelectionInput.Pointer(inputPath,"select"), Generated = generated
            };
        }
        checkPath = projection.Path;
        var selected = projection.Fields ?? inputSyntax.Read(projection.Token!,projection.Path,projection.Generated);
        foreach (var entry in selected)
        {
            checkPath = entry.Path;
            if (++compiledFields > 512) throw PublicFields.Error("Selection exceeds 512 fields.");
            var field = registry.Field(type,entry.Name); var target = PublicFields.ValueType(field);
            var config = new JObject(); Projection? childProjection = null;
            if (entry.Children != null) childProjection = new Projection { Fields = entry.Children, Path = entry.Path };
            else if (entry.Value is JArray array) childProjection = new Projection { Token = array, Path = entry.Path };
            else if (entry.Value is JObject objectConfig) {
                var bare = target is IComplexGraphType && objectConfig.Count > 0 &&
                    objectConfig.Properties().All(p => registry.Fields(target.Name).ContainsKey(p.Name) && !SelectionInput.IsOption(p.Name));
                if (bare) childProjection = new Projection { Token = objectConfig, Path = entry.Path };
                else config = objectConfig;
            }
            else if (entry.Value?.Type != JTokenType.Boolean || !(bool)entry.Value) throw PublicFields.Error("Select fields with true, field paths or child-field/selection-option objects.");
            var args = ArgumentsAt(field,config["args"],config["args"] == null ? entry.Path : SelectionInput.Pointer(entry.Path,"args"),type+"."+entry.Name);
            if (target is ScalarGraphType) {
                if (childProjection != null) throw SelectionInput.Locate(PublicFields.Error("Scalar fields cannot have child selections."),entry.Path,target.Name);
                FieldKeys(config,entry.Path,target.Name,"args"); plan.Fields.Add((entry.Name,field,args,null));
            }
            else {
                if (!PublicFields.IsList(field)) FieldKeys(config,entry.Path,target.Name,"select","args","on");
                if (childProjection == null && field.Metadata.ContainsKey("default_reference") && (config["select"] == null || config["select"]!.Type == JTokenType.Boolean && (bool)config["select"]!))
                    childProjection = new Projection { Token = ReferenceDefaults(target.Name), Path = entry.Path, Generated = true };
                plan.Fields.Add((entry.Name,field,args,Compile(target.Name,config,depth+1,false,entry.Path,childProjection)));
            }
        }
        if (options["on"] != null)
        {
            checkPath = SelectionInput.Pointer(inputPath,"on");
            if (registry.Type(type) is not IAbstractGraphType abstractType || options["on"] is not JObject variants) throw PublicFields.Error("on requires an abstract type and concrete selection objects.");
            foreach (var variant in variants.Properties())
            {
                checkPath = SelectionInput.Pointer(SelectionInput.Pointer(inputPath,"on"),variant.Name);
                if (!abstractType.PossibleTypes.Any(t => t.Name == variant.Name) || variant.Value is not JObject body) throw PublicFields.Error("Unsupported concrete type " + variant.Name);
                KeysAt(body,checkPath,"select","on");
                plan.On.Add(variant.Name,Compile(variant.Name,body,depth+1,false,checkPath));
            }
        }
        if (options["group_by"] != null)
        {
            checkPath = SelectionInput.Pointer(inputPath,"group_by");
            if (options["group_by"] is not JArray groups || groups.Count is < 1 or > 12 || groups.Any(x => x.Type != JTokenType.String)) throw PublicFields.Error("group_by requires 1..12 scalar paths.");
            plan.GroupBy = groups.Values<string>().Select(x => x!).ToArray();
            if (plan.GroupBy.Distinct(StringComparer.Ordinal).Count() != plan.GroupBy.Length) throw PublicFields.Error("group_by paths must be unique.");
            for (var i=0;i<plan.GroupBy.Length;i++) {
                checkPath = SelectionInput.Pointer(SelectionInput.Pointer(inputPath,"group_by"),i.ToString());
                ScalarPath(type,plan.GroupBy[i]);
            }
        }
        if (options["aggregate"] != null)
        {
            checkPath = SelectionInput.Pointer(inputPath,"aggregate");
            if (options["aggregate"] is not JObject measures || measures.Count is < 1 or > 12) throw PublicFields.Error("aggregate requires 1..12 named measures.");
            foreach (var m in measures.Properties())
            {
                checkPath = SelectionInput.Pointer(SelectionInput.Pointer(inputPath,"aggregate"),m.Name);
                if (m.Value is not JObject spec) throw PublicFields.Error("Measure must be an object."); KeysAt(spec,checkPath,"op","field");
                var op = (string?)spec["op"]; var path = (string?)spec["field"];
                if (op == "count") { if (path != null) throw PublicFields.Error("count counts records; omit field."); }
                else {
                    if (op is not ("sum" or "min" or "max") || path == null) throw PublicFields.Error("Use count, sum, min or max with a scalar field.");
                    checkPath = SelectionInput.Pointer(checkPath,"field");
                    var (owner,field) = ScalarPath(type,path);
                    if (PublicFields.ValueType(field) is not (IntGraphType or FloatGraphType) || op == "sum" && !PublicFields.CanSum(owner,field)) throw PublicFields.Error("Aggregation " + op + " is not declared for " + path + ".");
                }
                plan.Measures.Add((m.Name,op!,path));
            }
        }
        if (plan.GroupBy.Length > 0 && plan.Measures.Count == 0) throw PublicFields.Error("group_by requires aggregate.");
        if (options["order_by"] != null)
        {
            checkPath = SelectionInput.Pointer(inputPath,"order_by");
            if (options["order_by"] is not JArray orders || orders.Count is < 1 or > 4) throw PublicFields.Error("order_by requires 1..4 field specifications.");
            foreach (var token in orders) {
                checkPath = SelectionInput.Pointer(SelectionInput.Pointer(inputPath,"order_by"),orders.IndexOf(token).ToString());
                if (token is not JObject spec) throw PublicFields.Error("Invalid order_by."); KeysAt(spec,checkPath,"field","direction");
                var path = (string?)spec["field"] ?? throw PublicFields.Error("order_by needs field.");
                if (plan.GroupBy.Length == 0) ScalarPath(type,path);
                else if (!plan.GroupBy.Contains(path) && !plan.Measures.Any(m => m.Name == path)) throw PublicFields.Error("Grouped order_by must name a group_by field or named aggregate.");
                var direction = (string?)spec["direction"] ?? "asc"; if (direction is not ("asc" or "desc")) throw PublicFields.Error("direction must be asc or desc.");
                plan.Order.Add((path,direction == "desc"));
            }
        }
        return plan;
        }
        catch (ExecutionError error) { throw SelectionInput.Locate(error,checkPath,type); }
    }
    private Dictionary<string,object?> ArgumentsAt(FieldType field,JToken? token,string path,string owner)
    {
        try { return registry.Arguments(field,token,owner); }
        catch (ExecutionError error) {
            if (token is JObject args) {
                var unknown = args.Properties().FirstOrDefault(p => field.Arguments?.Any(a => PublicFields.Name(a.Name)==p.Name) != true);
                if (unknown != null) path = SelectionInput.Pointer(path,unknown.Name);
            }
            throw SelectionInput.Locate(error,path,owner);
        }
    }
    private static void KeysAt(JObject value,string path,params string[] allowed)
    {
        var unknown = value.Properties().FirstOrDefault(p => !allowed.Contains(p.Name));
        if (unknown != null) throw SelectionInput.Error("Unknown option " + unknown.Name + ". Allowed: " + string.Join(", ",allowed) + ". Child fields and relation options cannot be mixed at this level.",SelectionInput.Pointer(path,unknown.Name));
    }
    private static void FieldKeys(JObject value,string path,string owner,params string[] allowed)
    {
        try { KeysAt(value,path,allowed); }
        catch (ExecutionError error) { throw SelectionInput.Locate(error,path,owner); }
    }
    private JArray Defaults(string type)
    {
        var fields = registry.Fields(type);
        // A selected object must not silently become {} just because it is a tracker/container.
        // The same structural rule covers every public type, without task/category presets.
        bool Usable(FieldType f) => !f.Metadata.ContainsKey("explicit_selection") && !(f.Arguments?.Any(a => a.Name is not ("where" or "first" or "offset" or "after") && a.DefaultValue == null) ?? false);
        var readable = fields.Where(p => Usable(p.Value)).ToArray();
        var scalar = readable.Where(p => PublicFields.IsScalar(p.Value) && p.Key != "description").Select(p => p.Key).ToArray();
        var names = scalar.Concat(readable.Where(p => PublicFields.IsList(p.Value)).Select(p => p.Key)).ToArray();
        if (scalar.Length == 0) names = readable.Select(p => p.Key).ToArray();
        return new JArray(names);
    }
    private JArray ReferenceDefaults(string type)
    {
        var fields = registry.Fields(type);
        var names = fields.Where(f => PublicFields.IsScalar(f.Value) && f.Value.Arguments?.Any(a => a.ResolvedType is NonNullGraphType) != true).Select(f => f.Key).ToList();
        if (!names.Any(n => n is "id" or "ref" or "def_name" or "key"))
            names.AddRange(fields.Where(f => f.Value.Metadata.ContainsKey("default_reference") && PublicFields.ValueType(f.Value).Name != type).Select(f => f.Key));
        return new JArray(names);
    }
    private (string Owner,FieldType Field) ScalarPath(string type,string path)
    {
        var parts = path.Split('.'); FieldType? field = null;
        for (var i=0;i<parts.Length;i++) {
            field = registry.Field(type,parts[i]);
            if (PublicFields.IsList(field) || field.Arguments?.Any(a => a.ResolvedType is NonNullGraphType) == true) throw PublicFields.Error("Group/order/measure paths cannot cross collections or require arguments: " + path);
            if (i == parts.Length-1) { if (!PublicFields.IsScalar(field)) throw PublicFields.Error("Expected scalar path " + path); break; }
            type = PublicFields.ValueType(field).Name;
        }
        return (type,field!);
    }
    private JToken PathValue(string type,object? value,string path,Run run)
    {
        run.Context.Check();
        if (value == null) return JValue.CreateNull();
        var lookup = (value,type,path);
        if (run.PathValues.TryGetValue(lookup,out var cached)) return cached;
        var key = type + ":" + path;
        if (!paths.TryGetValue(key,out var steps)) {
            steps = new();
            foreach (var part in path.Split('.')) { var field = registry.Field(type,part); steps.Add((type,field,registry.Arguments(field,null,path))); type = PublicFields.ValueType(field).Name; }
            paths.Add(key,steps);
        }
        foreach (var step in steps) {
            if (value == null) return JValue.CreateNull();
            value = registry.Read(step.Type,value,step.Field,step.Args,run.Context);
        }
        var result = value == null ? JValue.CreateNull() : new JValue(value);
        run.PathValues[lookup] = result;
        return result;
    }
    private JObject Collection(Selection plan,Func<IEnumerable<object>> read,Run run,string path)
    {
        var started = run.Context.ElapsedMilliseconds;
        var values = new List<object>(); var identities = new HashSet<string>(StringComparer.Ordinal); var scan = true;
        try {
            var scanned = 0;
            foreach (var value in read()) {
                run.Context.Check(); if (++scanned > 8192) throw ReadContext.Error("query_scan_limit","Collection exceeds 8192 source candidates. Map/definition restrictions and safe definition predicates narrow this source; ordinary instance filters and output offset/limit do not reduce the source scan. Keep the total unknown if a complete disjoint scan cannot be obtained within the task budget.");
                var id = value is GameEntity e ? "entity:" + e.Id : value is GameNode n && n.Kind is "Thing" or "ThingReference" ? n.Kind + ":" + n.Owner.Id + ":" + n.Key : null;
                if (id != null && !identities.Add(id)) continue;
                if (plan.Predicate != null) {
                    var match = plan.Predicate(value,run.Context);
                    if (match == null) throw ReadContext.Error("filter_unavailable","Candidate membership is unknown, not false.");
                    if (!match.Value) continue;
                }
                values.Add(value);
            }
        } catch (ExecutionError error) { scan = false; run.Fail(error,path); }
        catch (Exception error) when (ReadContext.Recoverable(error)) { scan = false; run.Fail(ReadContext.Error("field_unavailable","Collection read failed."),path); }
        var result = new JObject { ["scan_complete"] = scan,["matched_count"] = scan ? new JValue(values.Count) : JValue.CreateNull(),["known_matches"] = values.Count };
        var scannedAt = run.Context.ElapsedMilliseconds;
        var before = run.Issues.Count;
        if (plan.Measures.Count > 0) result["aggregates"] = Measures(plan,values,scan,run,path);
        var measuredAt = run.Context.ElapsedMilliseconds;
        var rows = new JArray(); bool ordered = true;
        try {
            if (plan.GroupBy.Length > 0)
            {
                var groups = new Dictionary<JToken,(JObject Key,List<object> Values,string Signature)>(new JTokenEqualityComparer());
                var members = new Dictionary<string,List<object>>(StringComparer.Ordinal);
                foreach (var value in values) {
                    var keyValues = new JArray(plan.GroupBy.Select(p => PathValue(plan.Type,value,p,run)));
                    if (!groups.TryGetValue(keyValues,out var group)) {
                        var key = new JObject(plan.GroupBy.Select((name,index) => new JProperty(name,keyValues[index].DeepClone())));
                        group = (key,new List<object>(),key.ToString(Formatting.None));
                        groups.Add(keyValues,group); members.Add(group.Signature,group.Values);
                    }
                    group.Values.Add(value);
                }
                var groupRows = groups.Values.OrderBy(g => g.Signature,StringComparer.Ordinal).Select((group,index) => new JObject { ["key"] = group.Key,["aggregates"] = Measures(plan,group.Values,scan,run,path + "/groups/" + index) }).ToList();
                if (plan.Order.Count > 0) groupRows.Sort((a,b) => {
                    foreach (var order in plan.Order) {
                        var left = a["key"]![order.Path] ?? a["aggregates"]![order.Path]!["value"]!;
                        var right = b["key"]![order.Path] ?? b["aggregates"]![order.Path]!["value"]!;
                        var cmp = Compare(left,right); if (cmp != 0) return order.Desc ? -cmp : cmp;
                    }
                    return StringComparer.Ordinal.Compare(a["key"]!.ToString(Formatting.None),b["key"]!.ToString(Formatting.None));
                });
                foreach (var row in groupRows.Skip(plan.Offset).Take(plan.Limit)) {
                    row["values"] = GroupValues(plan,members[row["key"]!.ToString(Formatting.None)],scan,run,path + "/rows/" + rows.Count + "/values");
                    rows.Add(row);
                }
                result["page"] = Page(groups.Count,rows.Count,plan,scan);
            }
            else
            {
                IEnumerable<object> output = values;
                if (plan.Order.Count > 0)
                {
                    var keyed = values.Select((v,i) => (Value:v,Index:i,Keys:plan.Order.Select(o => PathValue(plan.Type,v,o.Path,run)).ToArray())).ToList();
                    keyed.Sort((a,b) => { for (var i=0;i<plan.Order.Count;i++) { var cmp = Comparer<JToken>.Create(Compare).Compare(a.Keys[i],b.Keys[i]); if (cmp != 0) return plan.Order[i].Desc ? -cmp : cmp; } return a.Index.CompareTo(b.Index); });
                    output = keyed.Select(v => v.Value);
                }
                foreach (var value in output.Skip(plan.Offset).Take(plan.Limit)) rows.Add(Project(plan,value,run,path + "/rows/" + rows.Count));
                result["page"] = Page(values.Count,rows.Count,plan,scan);
            }
        } catch (ExecutionError error) { ordered = false; run.Fail(error,path); result["page"] = Page(values.Count,rows.Count,plan,false); }
        result["rows"] = rows; result["fields_complete"] = run.Issues.Count == before && ordered;
        if (path.Count(c => c == '/') == 2) run.Phases.Add(new JObject { ["path"] = path,["scan_ms"] = scannedAt-started,["aggregate_ms"] = measuredAt-scannedAt,["output_ms"] = run.Context.ElapsedMilliseconds-measuredAt,["known_matches"] = values.Count,["returned"] = rows.Count });
        return result;
    }
    private static int Compare(JToken a,JToken b)
    {
        if (a.Type == JTokenType.Null || b.Type == JTokenType.Null) return a.Type == b.Type ? 0 : a.Type == JTokenType.Null ? -1 : 1;
        if (a.Type is JTokenType.Integer or JTokenType.Float && b.Type is JTokenType.Integer or JTokenType.Float) return ((decimal)a).CompareTo((decimal)b);
        return StringComparer.Ordinal.Compare(a.ToString(),b.ToString());
    }
    private JObject Measures(Selection plan,List<object> values,bool scan,Run run,string path)
    {
        var measures = new JObject();
        foreach (var measure in plan.Measures)
        {
            var complete = scan; JToken value = JValue.CreateNull();
            try {
                if (measure.Op == "count") value = new JValue(values.Count);
                else {
                    var numbers = values.Select(v => PathValue(plan.Type,v,measure.Path!,run)).ToList();
                    if (numbers.Any(v => v.Type is not (JTokenType.Integer or JTokenType.Float))) throw ReadContext.Error("aggregate_unavailable","Measure contains null or nonnumeric values: " + measure.Path);
                    if (measure.Op == "sum") value = new JValue(numbers.Sum(v => (decimal)v));
                    else if (numbers.Count > 0) value = measure.Op == "min" ? new JValue(numbers.Min(v => (decimal)v)) : new JValue(numbers.Max(v => (decimal)v));
                }
            } catch (ExecutionError error) { complete = false; value = JValue.CreateNull(); run.Fail(error,path + "/aggregates/" + measure.Name); }
            var row = new JObject { ["value"] = complete ? value : JValue.CreateNull(),["complete"] = complete,["op"] = measure.Op,["field"] = measure.Path,["unit"] = measure.Op == "count" ? "records" : measure.Op == "sum" ? "stack_units" : "field_value" };
            if (!complete && measure.Op is "sum" or "count" && value.Type != JTokenType.Null) row["known_value"] = value;
            measures[measure.Name] = row;
        }
        return measures;
    }
    private JObject Project(Selection plan,object value,Run run,string path)
    {
        run.Check(); var result = new JObject();
        foreach (var selected in plan.Fields)
        {
            var at = path + "/" + selected.Name;
            try {
                var raw = registry.Read(plan.Type,value,selected.Field,selected.Args,run.Context);
                if (raw == null) result[selected.Name] = JValue.CreateNull();
                else if (selected.Child == null) result[selected.Name] = raw is IEnumerable && raw is not string ? JToken.FromObject(raw) : new JValue(raw);
                else if (PublicFields.IsList(selected.Field)) {
                    var collection = Collection(selected.Child,() => ((IEnumerable)raw).Cast<object>(),run,at);
                    var relation = plan.Type + "." + selected.Name;
                    if (!string.IsNullOrEmpty(selected.Field.Description)) run.RelationSemantics[relation] = selected.Field.Description;
                    collection["scope"] = new JObject {
                        ["relation"] = relation,
                        ["args"] = JObject.FromObject(selected.Args.ToDictionary(a => PublicFields.Name(a.Key),a => a.Value)),
                        ["where"] = selected.Child.Where.DeepClone(),
                    };
                    result[selected.Name] = collection;
                }
                else result[selected.Name] = Project(selected.Child,raw,run,at);
            } catch (ExecutionError error) { result[selected.Name] = JValue.CreateNull(); run.Fail(error,at); }
            catch (Exception error) when (ReadContext.Recoverable(error)) { result[selected.Name] = JValue.CreateNull(); run.Fail(ReadContext.Error("field_unavailable","Selected field failed."),at); }
        }
        if (plan.On.Count > 0 && value is GameNode node && plan.On.TryGetValue(node.Kind,out var variant))
            foreach (var p in Project(variant,value,run,path).Properties()) result[p.Name] = p.Value.DeepClone();
        return result;
    }
    private JToken GroupValues(Selection plan,List<object> values,bool complete,Run run,string path)
    {
        if (values.Count == 0 || plan.Fields.Count == 0 && plan.On.Count == 0) return new JObject();
        if (!complete) {
            run.Fail(ReadContext.Error("group_membership_incomplete","Unscanned members could have different values; shared values are unknown."),path);
            return JValue.CreateNull();
        }
        var first = values[0]; var result = Project(plan,first,run,path); var varied = new HashSet<string>();
        var originals = new Dictionary<string,object?>();
        foreach (var field in plan.Fields) {
            try { originals[field.Name] = registry.Read(plan.Type,first,field.Field,field.Args,run.Context); }
            catch (ExecutionError) { varied.Add(field.Name); } // Project already recorded the failed field.
        }
        foreach (var value in values.Skip(1)) {
            run.Context.Check(); JObject? projected = null;
            foreach (var field in plan.Fields) {
                if (varied.Contains(field.Name)) continue;
                try {
                    var next = registry.Read(plan.Type,value,field.Field,field.Args,run.Context);
                    if (SameValue(originals[field.Name],next)) continue;
                    projected ??= Project(plan,value,run,path);
                    if (!JToken.DeepEquals(result[field.Name],projected[field.Name]) || HasWindow(projected[field.Name])) Varies(field.Name);
                } catch (ExecutionError error) { result[field.Name] = JValue.CreateNull(); varied.Add(field.Name); run.Fail(error,path + "/" + field.Name); }
            }
            if (plan.On.Count > 0) {
                projected ??= Project(plan,value,run,path);
                foreach (var name in result.Properties().Select(p => p.Name).Concat(projected.Properties().Select(p => p.Name)).Distinct().ToArray())
                    if (!varied.Contains(name) && !JToken.DeepEquals(result[name],projected[name])) Varies(name);
            }
        }
        return result;
        void Varies(string name) {
            varied.Add(name); result[name] = JValue.CreateNull();
            run.Fail(ReadContext.Error("group_field_varies","Selected field differs within this group; no arbitrary representative is returned. Select shared fields, refine grouping, or request details."),path + "/" + name);
        }
    }
    private static bool SameValue(object? a,object? b)
    {
        if (ReferenceEquals(a,b)) return true;
        if (a is GameNode left && b is GameNode right) return left.Kind == right.Kind && left.Key == right.Key && left.Owner.Id == right.Owner.Id;
        if (a is GameEntity x && b is GameEntity y) return x.Id == y.Id;
        return a != null && a is not IEnumerable && a is not GameNode && a is not GameEntity ? a.Equals(b) : a is string text && b is string other && text == other;
    }
    private static bool HasWindow(JToken? value) => value is JObject obj && ((bool?)obj["page"]?["has_more"] == true || (bool?)obj["scan_complete"] == false || obj.Properties().Any(p => HasWindow(p.Value))) || value is JArray array && array.Any(HasWindow);
    private static JObject Page(int count,int returned,Selection plan,bool scan) => new JObject { ["offset"] = plan.Offset,["returned"] = returned,["has_more"] = scan ? new JValue(plan.Offset+returned<count) : JValue.CreateNull(),["next_offset"] = scan && returned > 0 && plan.Offset+returned<count ? new JValue(plan.Offset+returned) : JValue.CreateNull(),["consistency"] = "fresh_observation" };
    private static int Integer(JObject obj,string name,int fallback,int min,int max) => obj[name] == null ? fallback : obj[name]!.Type == JTokenType.Integer && (long)obj[name]! >= min && (long)obj[name]! <= max ? (int)obj[name]! : throw PublicFields.Error(name + " must be " + min + ".." + max);
    private static void Keys(JObject obj,params string[] allowed) { var unknown = obj.Properties().FirstOrDefault(p => !allowed.Contains(p.Name)); if (unknown != null) throw PublicFields.Error("Unknown option " + unknown.Name + ". Allowed: " + string.Join(", ",allowed)); }
}
