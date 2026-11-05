using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GraphQL;
using GraphQL.Types;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>
/// Plans public queries without game reads: the root catalog, selection
/// compilation and validation, and definition/ID pushdown.
/// </summary>
internal sealed class PublicQueryPlanner
{
    private readonly PublicFields registry;
    private readonly SelectionConditions conditions;
    private readonly Dictionary<string, FieldType> roots;
    private int compiledFields;
    private SelectionInput inputSyntax = new();
    internal PublicFields Registry => registry;
    internal PublicQueryPlanner(Schema schema, SelectionConditions conditions)
    {
        registry = new PublicFields(schema,conditions); this.conditions = conditions;
        roots = schema.Query!.Fields.ToDictionary(f => f.Name == "colonists" ? "spawned_free_colonists" : PublicFields.Name(f.Name),f => f,StringComparer.Ordinal);
        roots["pawns"] = new FieldType { Name = "pawns", ResolvedType = registry.Type("Pawn"), Description = "Pawn instances enumerated from selected loaded maps and their nested holders, including humans, animals and mechanoids regardless of faction. No fog filter. This is object enumeration, not pathfinding access; excludes world caravans.", Arguments = new QueryArguments(GameArguments.Create("mapId",new StringGraphType(),"Loaded map ID; omitted means all loaded maps.")) };
    }
    internal static FieldType ThingsRoot(IGraphType thing) => new FieldType { Name = "things", ResolvedType = thing, Description = "Things on the selected loaded maps, with holder contents only when include_held is true. Spawned storage-cell items remain map things when include_held is false. Includes buildings/pawns/items; no caravans or off-map world objects.", Arguments = new QueryArguments(
            GameArguments.Create("mapId",new StringGraphType(),"Loaded map ID; omitted means all loaded maps."),
            GameArguments.Create("includeHeld",new BooleanGraphType(),"Traverse nested holder contents in addition to spawned map Things. false retains spawned map Things, including storage-cell contents. This selects current containment, not player ownership or movement history.",defaultValue:true),
            GameArguments.Create("includeUnreal",new BooleanGraphType(),"When including held Things, false excludes a Thing whose immediate holder has unreal contents. Descendant holders are still traversed and their Things checked separately. Spawned map Things are unaffected.",defaultValue:false),
            GameArguments.Create("visibleOnly",new BooleanGraphType(),"Exclude Things whose root held position is fogged.",defaultValue:true),
            GameArguments.Ids("defNames"),
            GameArguments.Create("capabilities", new ListGraphType(new StringGraphType()), "Match any native capability before projection/aggregation; intersects other selectors.",
                new JObject { ["minItems"] = 1, ["maxItems"] = WorldObservationContract.Capabilities.Length, ["items"] = new JObject { ["enum"] = new JArray(WorldObservationContract.Capabilities) } })) };

    internal JObject Catalog() {
        var commonTypes = new[] { "Thing", "ThingDef", "Pawn", "Map", "Game" };
        var fieldIndex = registry.CompactDirectory(roots.Keys.Select(RootType));
        var catalog = new JObject {
        ["version"] = PublicQuery.Version,
        ["parameters"] = JObject.Parse(@"{'type':'object','properties':{'requests':{'type':'array','minItems':1,'maxItems':8,'items':{'type':'object','properties':{'from':{'type':'string'},'args':{'type':'object'},'where':{'type':'object'},'select':{'description':'Array of canonical field paths, or object mapping fields to true or nested selection options. Collections do not need items/nodes wrappers.','anyOf':[{'type':'array','items':{'type':'string'}},{'type':'object'}]},'aggregate':{'type':'object','description':'Named measures, e.g. {quantity:{op:sum,field:stack_count},records:{op:count}}. Operates on all matches before output limits; sum only for declared additive fields.'},'group_by':{'type':'array','items':{'type':'string'},'maxItems':12},'order_by':{'type':'array','items':{'type':'object','properties':{'field':{'type':'string'},'direction':{'type':'string','enum':['asc','desc']}},'required':['field'],'additionalProperties':false}},'limit':{'type':'integer','minimum':0,'maximum':100},'offset':{'type':'integer','minimum':0,'maximum':8192}},'required':['from'],'additionalProperties':false}}},'required':['requests'],'additionalProperties':false}"),
        ["roots"] = new JArray(roots.Select(p => new JObject { ["from"] = p.Key, ["type"] = RootType(p.Key), ["description"] = p.Value.Description ?? RootDescription(p.Key), ["args"] = new JArray((p.Value.Arguments?.ToArray() ?? Array.Empty<QueryArgument>()).Where(a => a.Name is not ("first" or "after" or "offset" or "where")).Select(a => new JObject { ["name"] = PublicFields.Name(a.Name), ["required"] = a.ResolvedType is NonNullGraphType, ["default"] = a.DefaultValue == null ? JValue.CreateNull() : JToken.FromObject(a.DefaultValue) })) })),
        ["rules"] = "Selection: use the canonical fields of the current type. A field on a related type is not a field on its parent. select arrays may combine string field paths and field-selection objects; duplicate or overlapping selections are rejected. String paths include, for example [\"definition.def_name\",\"stack_count\"]. An object maps each field to true, a child field array, or an options object. Equivalent child selections are {\"definition\":[\"def_name\",\"label\"]} and {\"definition\":{\"select\":[\"def_name\",\"label\"]}}. Use explicit select when configuring relation options. A nonempty object containing only known child field names, with no option keywords, is also a child selection. Do not mix child fields and relation options at the same level; unknown names are rejected rather than guessed. Single related objects accept select/args/on; collections also accept where/aggregate/group_by/order_by/limit/offset. Use on as an object keyed by supported concrete component types, never a type-name string. Each value accepts a field array, a pure field map, or explicit select/on options, using the same selection syntax as related objects. Collection selections do not need items/nodes transport wrappers.\nConditions: where is {field.path:{eq:value}}; clauses AND, with all/any/not for composition. Scalar ops: eq/ne/in/gt/gte/lt/lte/contains/starts_with/ends_with/is_null; case_sensitive:false for text. A condition path cannot cross a collection: use some/every/none with a nested where condition. Single-object conditions accept a child-field map or where:{...}; do not mix child fields and condition options. For example definition:{category:{eq:Item}} and definition.category:{eq:Item} select the same membership. reach:{field:parent,some:{...},include_self:true} traverses a declared same-type relation with cycle/depth limits. Do not infer condition paths from selection paths.\nDefaults: selected objects return readable scalars and parameter-free collections; objects without scalars expose related fields. Long descriptions, explicit contents and argument-dependent scans require selection. Collections default to limit20. Pawn inventory/equipment/apparel/carried are direct Thing collections; contents reads a Thing container. Their populations remain distinct, with the same collection operations; tracker traversal is internal. Map selectors accept map_id:current (map root id:current), resolved to the actual ID in scope without a preliminary map lookup. Native visible-record defaults remain in effect.\nMeasurements: group_by names scalar grouping fields. aggregate defines result aliases, not new source fields: quantity in {quantity:{op:sum,field:stack_count}} is not Thing.quantity. Count measures matching records, not automatically humans, stacks or distinct definitions; sum(stack_count) measures stack units. Aggregates cover all matches before display limits. With aggregate and limit:0, rows/page are omitted because no detail was requested; aggregate completeness is unchanged. Grouped select returns only shared values in rows.values. A varying instance field is null with group_field_varies; selecting stack_count is not a substitute for summing it. Keep overlapping query totals separate; never add the ResourceCounter readout to physical counts.\nValidation: a failed batch executes no requests and reports one primary error per invalid request plus the indices that passed validation; passing validation is not a game observation. Evidence: assess each required fact using its effective scope and own completeness. Top-level partial does not invalidate a complete aggregate when only unrelated display fields failed. An incomplete aggregate is not an exact total. Complete aggregates do not prove every detail/group row was displayed: check that collection's page.has_more before claiming a complete list. An unselected field was not read. A returned null must be interpreted using its field semantics and issue path, not treated as zero. A complete zero-match collection is empty only under its own scope and predicate. rows=[] alone can be an undisplayed page with a nonzero complete aggregate. scan_complete describes source coverage; fields_complete describes selected reads; page describes display coverage. Empty results establish no matches for the actual predicate, not the absence of a broader user concept; verify uncertain predicate semantics, without routinely requerying every empty set. Continue only for an unmet requested fact, a relevant unknown or conflicting evidence; preserve known facts when a different field is unavailable. Every request/continuation is a fresh observation. Native capability flags, content categories and cached readout membership have different meanings; no task/category presets are applied.",
        ["common_fields"] = registry.Describe(commonTypes),
        ["field_index"] = fieldIndex,
        ["value_domains"] = registry.ValueDomains(),
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
        catalog["rules"] = catalog["rules"]!.ToString() + " Fields with a value domain follow its operators and declared names; invalid names fail before reads, not as zero matches. Native flags accept comma-separated names and has_all/has_any; case_sensitive:false permits ordinal case folding. describe_query also accepts the listed domain names.";
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
    internal static string RootDescription(string name) => name switch {
        "spawned_free_colonists" => "Free colonists currently spawned on selected loaded maps, using the native FreeColonistsSpawned collection. A missing match establishes only nonmembership in this filtered population, not absence of colonist identity.",
        "thing_defs" => "Loaded ThingDef definitions, not physical quantities. Filter on declared native properties, not label substring alone.",
        "map" => "One loaded map selected by args.id, with storage and resource-counter relations. This is a map, not a collection of its contents.",
        "maps" => "Loaded map instances; conditions select maps, not their Things or Pawns.",
        "game" => "Current game/session state, not a map-object population.",
        "pawn" => "One Pawn found by stable ID in the loaded-map and holder enumeration, including humans, animals and mechanoids; no fog filter. Not a free-colonist identity check.",
        "thing" => "One native Thing instance selected by ID, including held instances; its definition and containment relations are separate selected facts.",
        "thing_def" => "One loaded ThingDef selected by exact name. A definition existing does not establish any physical instance or quantity.",
        _ => "Existing public " + name + " root; exact identifiers use args, conditions use where."
    };
    internal JArray Describe(IEnumerable<string> types) => registry.Describe(types);
    private string RootType(string name) => name is "things" ? "Thing" : name is "pawns" or "spawned_free_colonists" ? "Pawn" : PublicFields.ValueType(roots[name]).Name;
    /// <summary>Validates and compiles the whole batch before any game read; a failed batch executes nothing.</summary>
    internal bool TryPlan(JObject input, bool direct, out List<PublicQueryRequest> plans, out QueryOutcome failure)
    {
        Keys(input,"requests");
        if (input["requests"] is not JArray requests || requests.Count is < 1 or > 8) throw PublicFields.Error("requests must contain 1..8 queries.");
        compiledFields = 0;
        inputSyntax = new SelectionInput();
        plans = new List<PublicQueryRequest>();
        failure = null!;
        var validationErrors = new List<QueryIssue>(); var validIndices = new JArray();
        for (var requestIndex=0;requestIndex<requests.Count;requestIndex++)
        {
            var token=requests[requestIndex];
            var inputPath = direct ? "" : "/requests/" + requestIndex;
            try {
            if (token is not JObject obj || obj["from"]?.Type != JTokenType.String) throw PublicFields.Error("Each request needs from.");
            var name = (string)obj["from"]!;
            if (!roots.TryGetValue(name,out var root)) throw PublicFields.Error("Unknown from. Available: " + string.Join(", ",roots.Keys));
            var arguments = ArgumentsAt(root, obj["args"], obj["args"] == null ? inputPath : SelectionInput.Pointer(inputPath, "args"), RootType(name));
            var selection = Compile(RootType(name),obj,0,true,inputPath);
            // exact_names already selects this complete canonical-name/label union.
            // Drop only a validated, provably identical duplicate predicate.
            if (name == "thing_defs" && ExactNamesCoverPredicate(arguments, obj["where"])) selection.Predicate = null;
            SelectionConditions.Predicate? definitionFilter = null;
            var fullyPushed = false;
            if (name == "things" && obj["where"] is JObject where)
            {
                var projected = DefinitionCondition(registry.Predicate("Thing",where),registry.Field("Thing","definition").Name);
                fullyPushed = projected.Exact;
                if (projected.Condition != null) definitionFilter = conditions.Compile("ThingDef",projected.Condition);
            }
            if (fullyPushed) selection.Predicate = null; // The data source applies the entire validated predicate once per definition.
            plans.Add(new PublicQueryRequest(name,root,arguments,selection,definitionFilter,fullyPushed,
                name == "things" ? ExactThingIds(obj["where"] as JObject) : null));
            validIndices.Add(requestIndex);
            } catch (ExecutionError error) {
                var located=SelectionInput.Locate(error,inputPath,"request");
                var path=(string)located.Data["input_path"]!;
                validationErrors.Add(new QueryIssue { Code=located.Code ?? "invalid_query",Message=located.Message,
                    Path=new JArray(path.Split('/').Skip(1).Select(p=>p.Replace("~1","/").Replace("~0","~"))) });
            }
        }
        if (validationErrors.Count>0) {
            var message=string.Join("\n",validationErrors.Select(e=>e.Message));
            if (!direct) message="No requests executed. Requests passing validation: ["+string.Join(",",validIndices)+"]. Fix the listed requests and resubmit the intended batch.\n"+message;
            if (message.Length>2048) message="No requests executed. Requests passing validation: ["+string.Join(",",validIndices)+"]. See issues for all "+validationErrors.Count+" primary request errors; fix them and resubmit the intended batch.";
            var failed=QueryOutcome.Failure(validationErrors[0].Code,message);
            failed.Issues=validationErrors.ToArray();
            failed.Execution=new JObject { ["validation"]=new JObject { ["executed"]=false,["valid_request_indices"]=validIndices,["errors"]=validationErrors.Count } };
            failure=failed; return false;
        }
        return true;
    }
    private static bool ExactNamesCoverPredicate(IReadOnlyDictionary<string,object?> args, JToken? where)
    {
        if (!args.TryGetValue("exactNames", out var raw) || raw is not IEnumerable sequence || raw is string
            || where is not JObject predicate || predicate.Count != 1 || predicate["any"] is not JArray branches || branches.Count == 0) return false;
        var requested = sequence.Cast<object>().ToArray();
        if (requested.Length == 0 || requested.Any(value => value is not string)) return false;
        var wanted = new HashSet<string>(requested.Cast<string>(), StringComparer.OrdinalIgnoreCase);
        var covered = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal) {
            ["def_name"] = new(StringComparer.OrdinalIgnoreCase), ["label"] = new(StringComparer.OrdinalIgnoreCase)
        };
        foreach (var branch in branches) {
            if (branch is not JObject term || term.Count != 1) return false;
            var field = term.Properties().Single();
            if (!covered.TryGetValue(field.Name, out var names) || field.Value is not JObject comparison || comparison.Count != 2
                || comparison["case_sensitive"]?.Type != JTokenType.Boolean || (bool)comparison["case_sensitive"]! != false
                || comparison["eq"]?.Type != JTokenType.String || comparison["eq"] is not JValue exact || exact.Value is not string text || !wanted.Contains(text)) return false;
            names.Add(text);
        }
        return covered.Values.All(names => names.SetEquals(wanted));
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

    // Only a directly supplied, exact ID clause is a safe candidate restriction.
    // Full predicate evaluation remains in Collection; do not infer IDs through
    // any/not or case-insensitive comparisons.
    private static IReadOnlyCollection<string>? ExactThingIds(JObject? where)
    {
        if (where?["id"] is not JObject condition || (bool?)condition["case_sensitive"] == false) return null;
        HashSet<string>? ids = null;
        if (condition["eq"]?.Type == JTokenType.String)
            ids = new HashSet<string>(new[] { (string)condition["eq"]! }, StringComparer.Ordinal);
        if (condition["in"] is JArray values && values.All(v => v.Type == JTokenType.String)) {
            var included = new HashSet<string>(values.Values<string>()!, StringComparer.Ordinal);
            if (ids == null) ids = included; else ids.IntersectWith(included);
        }
        return ids;
    }

    private bool IsBareSelection(string type,JObject body) => registry.Type(type) is IComplexGraphType && body.Count>0 &&
        body.Properties().All(p=>registry.Fields(type).ContainsKey(p.Name) && !SelectionInput.IsOption(p.Name));

    private PublicQuerySelection Compile(string type,JObject options,int depth,bool root = false,string inputPath = "",PublicQueryProjection? projection = null)
    {
        var checkPath = inputPath;
        try {
        if (depth > 12 || ++compiledFields > 512) throw PublicFields.Error("Selection exceeds depth12/512 fields.");
        KeysAt(options,inputPath,root ? new[] { "from","args","where","select","aggregate","group_by","order_by","limit","offset" } : new[] { "args","where","select","aggregate","group_by","order_by","limit","offset","on" });
        var plan = new PublicQuerySelection { Type = type, Limit = Integer(options,"limit",20,0,100), Offset = Integer(options,"offset",0,0,8192) };
        // Resolve the discriminator structure before reporting dependent base-field errors.
        if (options["on"] != null) {
            checkPath=SelectionInput.Pointer(inputPath,"on");
            if (registry.Type(type) is not IAbstractGraphType abstractType) throw PublicFields.Error("on is only available on abstract component types; " + type + " is not abstract.");
            if (options["on"] is not JObject variants) throw PublicFields.Error("on must be an object mapping concrete type names to selections, for example on:{TypeName:{select:[field]}}. Allowed types: " + string.Join(", ",abstractType.PossibleTypes.Select(t=>t.Name)));
            foreach (var variant in variants.Properties()) {
                checkPath=SelectionInput.Pointer(SelectionInput.Pointer(inputPath,"on"),variant.Name);
                if (!abstractType.PossibleTypes.Any(t=>t.Name==variant.Name)) throw PublicFields.Error("Unknown concrete type " + variant.Name + ". Allowed types: " + string.Join(", ",abstractType.PossibleTypes.Select(t=>t.Name)));
                var body=variant.Value as JObject;
                var bare=body != null && IsBareSelection(variant.Name,body);
                if (variant.Value is JArray || bare)
                    plan.On.Add(variant.Name,Compile(variant.Name,new JObject(),depth+1,false,checkPath,new PublicQueryProjection { Token=variant.Value,Path=checkPath }));
                else {
                    if (body==null) throw PublicFields.Error("Selection for " + variant.Name + " must be a field array, a field map, or an object with select/on; the type exists but the supplied selection has the wrong shape.");
                    KeysAt(body,checkPath,"select","on");
                    plan.On.Add(variant.Name,Compile(variant.Name,body,depth+1,false,checkPath));
                }
            }
        }
        if (options["where"] != null) {
            checkPath = SelectionInput.Pointer(inputPath, "where");
            plan.Predicate = conditions.Compile(type,registry.Predicate(type,options["where"]!));
            plan.Where = options["where"]!.DeepClone();
        }
        if (projection == null) {
            var specified = options["select"];
            var generated = specified == null || specified.Type == JTokenType.Boolean && (bool)specified;
            projection = new PublicQueryProjection {
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
            var config = new JObject(); PublicQueryProjection? childProjection = null;
            if (entry.Children != null) childProjection = new PublicQueryProjection { Fields = entry.Children, Path = entry.Path };
            else if (entry.Value is JArray array) childProjection = new PublicQueryProjection { Token = array, Path = entry.Path };
            else if (entry.Value is JObject objectConfig) {
                var bare = IsBareSelection(target.Name,objectConfig);
                if (bare) childProjection = new PublicQueryProjection { Token = objectConfig, Path = entry.Path };
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
                    childProjection = new PublicQueryProjection { Token = ReferenceDefaults(target.Name), Path = entry.Path, Generated = true };
                plan.Fields.Add((entry.Name,field,args,Compile(target.Name,config,depth+1,false,entry.Path,childProjection)));
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
    private static int Integer(JObject obj,string name,int fallback,int min,int max) => obj[name] == null ? fallback : obj[name]!.Type == JTokenType.Integer && (long)obj[name]! >= min && (long)obj[name]! <= max ? (int)obj[name]! : throw PublicFields.Error(name + " must be " + min + ".." + max);
    private static void Keys(JObject obj,params string[] allowed) { var unknown = obj.Properties().FirstOrDefault(p => !allowed.Contains(p.Name)); if (unknown != null) throw PublicFields.Error("Unknown option " + unknown.Name + ". Allowed: " + string.Join(", ",allowed)); }
}
