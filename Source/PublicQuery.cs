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
    internal const string Version = "public_query.v1";
    private readonly PublicFields registry;
    private readonly SelectionConditions conditions;
    private readonly Dictionary<string, FieldType> roots;
    private int compiledFields;
    private readonly Dictionary<string,List<(string Type,FieldType Field,Dictionary<string,object?> Args)>> paths = new();
    private sealed class Selection
    {
        public string Type = "";
        public List<(string Name, FieldType Field, Dictionary<string, object?> Args, Selection? Child)> Fields = new();
        public Dictionary<string, Selection> On = new();
        public SelectionConditions.Predicate? Predicate;
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
        public Run(ReadContext context) { Context = context; }
        public void Check() { Context.Check(); if (++Objects > 20000) throw ReadContext.Error("query_limit","Query exceeds 20000 projected objects; narrow fields or rows."); }
        public void Fail(ExecutionError error,string path) => Issues.Add(new QueryIssue { Code = error.Code ?? "field_unavailable", Message = error.Message, Path = new JArray(path.Split('/').Where(s => s.Length > 0)) });
    }
    internal PublicQuery(Schema schema, SelectionConditions conditions)
    {
        registry = new PublicFields(schema,conditions); this.conditions = conditions;
        roots = schema.Query!.Fields.ToDictionary(f => f.Name == "colonists" ? "spawned_free_colonists" : PublicFields.Name(f.Name),f => f,StringComparer.Ordinal);
        roots["things"] = new FieldType { Name = "things", ResolvedType = registry.Type("Thing"), Description = "Things on loaded maps and their holder trees. Default all loaded maps, include_held=true, visible_only=true. Includes buildings/pawns/items: filter definition.category when needed. No caravans or off-map world objects.", Arguments = new QueryArguments(
            GameArguments.Create("mapId",new StringGraphType(),"Loaded map ID; omitted means all loaded maps."),
            GameArguments.Create("includeHeld",new BooleanGraphType(),"Include recursively held Things on selected loaded maps.",defaultValue:true),
            GameArguments.Create("includeUnreal",new BooleanGraphType(),"Include native unreal contents (e.g. minified inner object/corpse pawn). Default false prevents counting those alongside their wrappers.",defaultValue:false),
            GameArguments.Create("visibleOnly",new BooleanGraphType(),"Exclude Things whose root held position is fogged.",defaultValue:true),
            GameArguments.Ids("defNames")) };
        roots["pawns"] = new FieldType { Name = "pawns", ResolvedType = registry.Type("Pawn"), Description = "All pawns reachable on selected loaded maps, including held pawns, prisoners and visitors. Does not include world caravans.", Arguments = new QueryArguments(GameArguments.Create("mapId",new StringGraphType(),"Loaded map ID; omitted means all loaded maps.")) };
    }
    internal JObject Catalog() {
        var catalog = new JObject {
        ["version"] = Version,
        ["parameters"] = JObject.Parse(@"{'type':'object','properties':{'requests':{'type':'array','minItems':1,'maxItems':8,'items':{'type':'object','properties':{'from':{'type':'string'},'args':{'type':'object'},'where':{'type':'object'},'select':{'description':'Array of canonical field paths, or object mapping fields to true or nested selection options. Collections do not need items/nodes wrappers.','anyOf':[{'type':'array','items':{'type':'string'}},{'type':'object'}]},'aggregate':{'type':'object','description':'Named measures, e.g. {quantity:{op:sum,field:stack_count},records:{op:count}}. Operates on all matches before output limits; sum only for declared additive fields.'},'group_by':{'type':'array','items':{'type':'string'},'maxItems':12},'order_by':{'type':'array','items':{'type':'object','properties':{'field':{'type':'string'},'direction':{'type':'string','enum':['asc','desc']}},'required':['field'],'additionalProperties':false}},'limit':{'type':'integer','minimum':0,'maximum':100},'offset':{'type':'integer','minimum':0,'maximum':8192}},'required':['from'],'additionalProperties':false}}},'required':['requests'],'additionalProperties':false}"),
        ["roots"] = new JArray(roots.Select(p => new JObject { ["from"] = p.Key, ["type"] = RootType(p.Key), ["description"] = p.Value.Description ?? RootDescription(p.Key), ["args"] = new JArray((p.Value.Arguments?.ToArray() ?? Array.Empty<QueryArgument>()).Where(a => a.Name is not ("first" or "after" or "offset" or "where")).Select(a => new JObject { ["name"] = PublicFields.Name(a.Name), ["required"] = a.ResolvedType is NonNullGraphType, ["default"] = a.DefaultValue == null ? JValue.CreateNull() : JToken.FromObject(a.DefaultValue) })) })),
        ["rules"] = "One vocabulary for selection/filter/group/order and returned fields. where is {field.path:{eq:value}}, clauses AND; all/any/not combine conditions. Scalar ops eq/ne/in/gt/gte/lt/lte/contains/starts_with/ends_with/is_null; case_sensitive:false for text. Collection conditions use some/every/none with another where object. Object conditions use where. Use on for supported concrete component types. reach:{field:parent,some:{...},include_self:true} traverses a declared same-type relation with cycle/depth limits. Nested select options: select, args, where, aggregate, group_by, order_by, limit, offset, on. Selected objects default to their scalar fields and parameter-free collections; objects with no scalars expose their related fields. Long description text and argument-dependent scans require explicit selection. Collections use limit20 by default. Aggregates cover all matches, never just displayed rows. Count is records, sum(stack_count) is item units. Group rows may overlap only if querying overlapping sources separately; never add independent query totals. Missing/failed is not zero. scan_complete differs from fields_complete and page.has_more. Every request and continuation is a fresh observation, not a retained snapshot. ResourceCounter is a separate native readout with its own scope/filter/update cadence; never add it to physical counts. UI-visible record defaults remain in effect. Map selectors accept map_id:current (map root: id:current), resolved once to the actual map ID returned in scope; no preliminary game/maps lookup is needed. No task/category presets.",
        ["common_fields"] = registry.Describe(new[] { "Thing","ThingDef","Pawn","Map","Game" }),
        ["field_index"] = registry.CompactDirectory(roots.Keys.Select(RootType)),
        ["examples"] = JArray.Parse(@"[
            {'requests':[{'from':'things','where':{'definition.category':{'eq':'Item'}},'group_by':['definition.def_name','definition.label'],'aggregate':{'quantity':{'op':'sum','field':'stack_count'},'records':{'op':'count'}}}]},
            {'requests':[{'from':'spawned_free_colonists','select':{'id':true,'name':true,'status':{'select':['downed','current_job.report']},'health':{'select':{'summary':true,'conditions':{'limit':20}}},'inventory':{'select':{'inner_container':{'select':{'items':{'select':['id','definition.def_name','stack_count']}}}}}}}]}
        ]")
        };
        var properties = (JObject)catalog["parameters"]!["properties"]!["requests"]!["items"]!["properties"]!;
        ((JArray)properties["select"]!["anyOf"]!).Add(new JObject { ["const"] = true });
        properties["from"]!["enum"] = new JArray(roots.Keys);
        var arguments = new JObject();
        foreach (var root in roots.Values)
            foreach (var argument in root.Arguments?.ToArray() ?? Array.Empty<QueryArgument>())
                if (argument.Name is not ("where" or "first" or "after" or "offset")) arguments[PublicFields.Name(argument.Name)] = PublicFields.ArgumentSchema(argument);
        properties["args"] = new JObject { ["type"] = "object",["properties"] = arguments,["additionalProperties"] = false,["description"] = "Root arguments go ONLY here. Each root accepts the subset in its directory; map_id/current (map root id/current) avoids a preliminary map lookup." };
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
        Keys(input,"requests");
        if (input["requests"] is not JArray requests || requests.Count is < 1 or > 8) throw PublicFields.Error("requests must contain 1..8 queries.");
        compiledFields = 0;
        paths.Clear();
        var plans = new List<(string Root,Dictionary<string,object?> Args,Selection Select,SelectionConditions.Predicate? DefinitionFilter)>();
        foreach (var token in requests)
        {
            if (token is not JObject obj || obj["from"]?.Type != JTokenType.String) throw PublicFields.Error("Each request needs from.");
            var name = (string)obj["from"]!;
            if (!roots.TryGetValue(name,out var root)) throw PublicFields.Error("Unknown from. Available: " + string.Join(", ",roots.Keys));
            var arguments = registry.Arguments(root,obj["args"],name);
            SelectionConditions.Predicate? definitionFilter = null;
            if (name == "things" && obj["where"] is JObject where)
            {
                var definitionWhere = new JObject(where.Properties().Where(p => p.Name.StartsWith("definition.",StringComparison.Ordinal)).Select(p => new JProperty(p.Name.Substring(11),p.Value.DeepClone())));
                if (definitionWhere.Count > 0) definitionFilter = conditions.Compile("ThingDef",registry.Predicate("ThingDef",definitionWhere));
            }
            plans.Add((name,arguments,Compile(RootType(name),obj,0,true),definitionFilter));
        }
        // Validate the whole batch before touching game state.
        var state = new ReadContext(source,session,budget) { Conditions = conditions }; var run = new Run(state); var results = new JArray();
        foreach (var plan in plans)
        {
            var path = "/results/" + results.Count;
            // Pushdown can discard only definitely false candidates. Unknown must reach the full AND predicate.
            Func<GameNode,bool>? filter = plan.DefinitionFilter == null ? null : n => plan.DefinitionFilter(n,state) != false;
            var result = Collection(plan.Select, () => ReadRoot(plan.Root,plan.Args,source,state,filter), run,path);
            result["from"] = plan.Root;
            result["scope"] = new JObject { ["args"] = JObject.FromObject(plan.Args.ToDictionary(p => PublicFields.Name(p.Key),p => p.Value)), ["description"] = roots[plan.Root].Description ?? RootDescription(plan.Root) };
            result["scope"]!["where"] = requests[results.Count]!["where"]?.DeepClone() ?? new JObject();
            results.Add(result);
        }
        var data = new JObject { ["results"] = results, ["sample"] = new JObject { ["load_epoch"] = session.LoadEpoch,["game_tick"] = session.GameTick,["sequence"] = session.SnapshotSequence } };
        if (data.ToString(Formatting.None).Length > 80000) return QueryOutcome.Failure("query_result_too_large","Request fewer fields or displayed rows.");
        return new QueryOutcome { Status = run.Issues.Count == 0 ? "ok" : "partial", Data = data, Issues = run.Issues.Take(256).ToArray(), Execution = new JObject { ["schema_id"] = schemaId,["contract"] = Version,["requests"] = input["requests"]!.DeepClone() } };
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
    private Selection Compile(string type,JObject options,int depth,bool root = false)
    {
        if (depth > 12 || ++compiledFields > 512) throw PublicFields.Error("Selection exceeds depth12/512 fields.");
        Keys(options,root ? new[] { "from","args","where","select","aggregate","group_by","order_by","limit","offset" } : new[] { "args","where","select","aggregate","group_by","order_by","limit","offset","on" });
        var plan = new Selection { Type = type, Limit = Integer(options,"limit",20,0,100), Offset = Integer(options,"offset",0,0,8192) };
        if (options["where"] != null) plan.Predicate = conditions.Compile(type,registry.Predicate(type,options["where"]!));
        var specified = options["select"];
        if (specified?.Type == JTokenType.Boolean && (bool)specified) specified = null;
        var selected = Normalize(specified ?? (options["group_by"] != null ? new JArray() : Defaults(type)));
        foreach (var p in selected.Properties())
        {
            if (++compiledFields > 512) throw PublicFields.Error("Selection exceeds 512 fields.");
            var field = registry.Field(type,p.Name); var target = PublicFields.ValueType(field);
            var config = p.Value is JObject objectConfig ? (JObject)objectConfig.DeepClone() : p.Value is JArray names ? new JObject { ["select"] = names.DeepClone() } : p.Value.Type == JTokenType.Boolean && (bool)p.Value ? new JObject() : throw PublicFields.Error("select values must be true, field paths or selection options.");
            var args = registry.Arguments(field,config["args"],type + "." + p.Name);
            if (target is ScalarGraphType) { Keys(config,"args"); plan.Fields.Add((p.Name,field,args,null)); }
            else {
                if (!PublicFields.IsList(field)) Keys(config,"select","args","on");
                if (field.Metadata.ContainsKey("default_reference") && (config["select"] == null || config["select"]!.Type == JTokenType.Boolean && (bool)config["select"]!))
                    config["select"] = ReferenceDefaults(target.Name);
                plan.Fields.Add((p.Name,field,args,Compile(target.Name,config,depth+1)));
            }
        }
        if (options["on"] != null)
        {
            if (registry.Type(type) is not IAbstractGraphType abstractType || options["on"] is not JObject variants) throw PublicFields.Error("on requires an abstract type and concrete selection objects.");
            foreach (var variant in variants.Properties())
            {
                if (!abstractType.PossibleTypes.Any(t => t.Name == variant.Name) || variant.Value is not JObject body) throw PublicFields.Error("Unsupported concrete type " + variant.Name);
                Keys(body,"select","on");
                plan.On.Add(variant.Name,Compile(variant.Name,body,depth+1));
            }
        }
        if (options["group_by"] != null)
        {
            if (options["group_by"] is not JArray groups || groups.Count is < 1 or > 12 || groups.Any(x => x.Type != JTokenType.String)) throw PublicFields.Error("group_by requires 1..12 scalar paths.");
            plan.GroupBy = groups.Values<string>().Select(x => x!).ToArray();
            if (plan.GroupBy.Distinct(StringComparer.Ordinal).Count() != plan.GroupBy.Length) throw PublicFields.Error("group_by paths must be unique.");
            foreach (var path in plan.GroupBy) ScalarPath(type,path);
        }
        if (options["aggregate"] != null)
        {
            if (options["aggregate"] is not JObject measures || measures.Count is < 1 or > 12) throw PublicFields.Error("aggregate requires 1..12 named measures.");
            foreach (var m in measures.Properties())
            {
                if (m.Value is not JObject spec) throw PublicFields.Error("Measure must be an object."); Keys(spec,"op","field");
                var op = (string?)spec["op"]; var path = (string?)spec["field"];
                if (op == "count") { if (path != null) throw PublicFields.Error("count counts records; omit field."); }
                else {
                    if (op is not ("sum" or "min" or "max") || path == null) throw PublicFields.Error("Use count, sum, min or max with a scalar field.");
                    var (owner,field) = ScalarPath(type,path);
                    if (PublicFields.ValueType(field) is not (IntGraphType or FloatGraphType) || op == "sum" && !PublicFields.CanSum(owner,field)) throw PublicFields.Error("Aggregation " + op + " is not declared for " + path + ".");
                }
                plan.Measures.Add((m.Name,op!,path));
            }
        }
        if (plan.GroupBy.Length > 0 && plan.Measures.Count == 0) throw PublicFields.Error("group_by requires aggregate.");
        if (options["order_by"] != null)
        {
            if (options["order_by"] is not JArray orders || orders.Count is < 1 or > 4) throw PublicFields.Error("order_by requires 1..4 field specifications.");
            foreach (var token in orders) {
                if (token is not JObject spec) throw PublicFields.Error("Invalid order_by."); Keys(spec,"field","direction");
                var path = (string?)spec["field"] ?? throw PublicFields.Error("order_by needs field.");
                if (plan.GroupBy.Length == 0) ScalarPath(type,path);
                else if (!plan.GroupBy.Contains(path) && !plan.Measures.Any(m => m.Name == path)) throw PublicFields.Error("Grouped order_by must name a group_by field or named aggregate.");
                var direction = (string?)spec["direction"] ?? "asc"; if (direction is not ("asc" or "desc")) throw PublicFields.Error("direction must be asc or desc.");
                plan.Order.Add((path,direction == "desc"));
            }
        }
        if (plan.GroupBy.Length > 0 && options["select"] != null) {
            var permitted = Normalize(new JArray(plan.GroupBy));
            bool Covered(JObject actual,JObject allowed) => actual.Properties().All(p => allowed[p.Name] != null && (p.Value is JValue ? allowed[p.Name] is JValue : p.Value["select"] is JObject nested && allowed[p.Name]?["select"] is JObject accepted && Covered(nested,accepted)));
            if (!Covered(selected,permitted)) throw PublicFields.Error("Grouped select may contain only grouping keys; use a separate detail request for individual rows.");
        }
        return plan;
    }
    private JArray Defaults(string type)
    {
        var fields = registry.Fields(type);
        // A selected object must not silently become {} just because it is a tracker/container.
        // The same structural rule covers every public type, without task/category presets.
        bool Usable(FieldType f) => !(f.Arguments?.Any(a => a.Name is not ("where" or "first" or "offset" or "after") && a.DefaultValue == null) ?? false);
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
    private static JObject Normalize(JToken token)
    {
        if (token is JObject obj) return (JObject)obj.DeepClone();
        if (token is not JArray array || array.Any(x => x.Type != JTokenType.String)) throw PublicFields.Error("select must be field paths or an object.");
        var result = new JObject();
        foreach (var path in array.Values<string>()) {
            var parts = path!.Split('.'); var node = result;
            for (var i=0;i<parts.Length;i++) {
                if (i == parts.Length-1) { if (node[parts[i]] != null) throw PublicFields.Error("Duplicate/overlapping selected path " + path); node[parts[i]] = true; }
                else { if (node[parts[i]] is JValue) throw PublicFields.Error("Overlapping selected path " + path); node[parts[i]] ??= new JObject { ["select"] = new JObject() }; node = (JObject)node[parts[i]]!["select"]!; }
            }
        }
        return result;
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
        return value == null ? JValue.CreateNull() : JToken.FromObject(value);
    }
    private JObject Collection(Selection plan,Func<IEnumerable<object>> read,Run run,string path)
    {
        var values = new List<object>(); var identities = new HashSet<string>(StringComparer.Ordinal); var scan = true;
        try {
            var scanned = 0;
            foreach (var value in read()) {
                run.Context.Check(); if (++scanned > 8192) throw ReadContext.Error("query_scan_limit","Collection exceeds 8192 candidates; narrow range or definitions.");
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
        var before = run.Issues.Count;
        if (plan.Measures.Count > 0) result["aggregates"] = Measures(plan,values,scan,run,path);
        var rows = new JArray(); bool ordered = true;
        try {
            if (plan.GroupBy.Length > 0)
            {
                var groups = new SortedDictionary<string,(JObject Key,List<object> Values)>(StringComparer.Ordinal);
                foreach (var value in values) {
                    var key = new JObject(plan.GroupBy.Select(p => new JProperty(p,PathValue(plan.Type,value,p,run)))); var encoded = key.ToString(Formatting.None);
                    if (!groups.TryGetValue(encoded,out var group)) { group = (key,new List<object>()); groups.Add(encoded,group); } group.Values.Add(value);
                }
                var groupRows = groups.Values.Select((group,index) => new JObject { ["key"] = group.Key,["aggregates"] = Measures(plan,group.Values,scan,run,path + "/groups/" + index) }).ToList();
                if (plan.Order.Count > 0) groupRows.Sort((a,b) => {
                    foreach (var order in plan.Order) {
                        var left = a["key"]![order.Path] ?? a["aggregates"]![order.Path]!["value"]!;
                        var right = b["key"]![order.Path] ?? b["aggregates"]![order.Path]!["value"]!;
                        var cmp = Compare(left,right); if (cmp != 0) return order.Desc ? -cmp : cmp;
                    }
                    return StringComparer.Ordinal.Compare(a["key"]!.ToString(Formatting.None),b["key"]!.ToString(Formatting.None));
                });
                foreach (var row in groupRows.Skip(plan.Offset).Take(plan.Limit)) rows.Add(row);
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
                else if (selected.Child == null) result[selected.Name] = JToken.FromObject(raw);
                else if (PublicFields.IsList(selected.Field)) result[selected.Name] = Collection(selected.Child,() => ((IEnumerable)raw).Cast<object>(),run,at);
                else result[selected.Name] = Project(selected.Child,raw,run,at);
            } catch (ExecutionError error) { result[selected.Name] = JValue.CreateNull(); run.Fail(error,at); }
            catch (Exception error) when (ReadContext.Recoverable(error)) { result[selected.Name] = JValue.CreateNull(); run.Fail(ReadContext.Error("field_unavailable","Selected field failed."),at); }
        }
        if (plan.On.Count > 0 && value is GameNode node && plan.On.TryGetValue(node.Kind,out var variant))
            foreach (var p in Project(variant,value,run,path).Properties()) result[p.Name] = p.Value.DeepClone();
        return result;
    }
    private static JObject Page(int count,int returned,Selection plan,bool scan) => new JObject { ["offset"] = plan.Offset,["returned"] = returned,["has_more"] = scan ? new JValue(plan.Offset+returned<count) : JValue.CreateNull(),["next_offset"] = scan && returned > 0 && plan.Offset+returned<count ? new JValue(plan.Offset+returned) : JValue.CreateNull(),["consistency"] = "fresh_observation" };
    private static int Integer(JObject obj,string name,int fallback,int min,int max) => obj[name] == null ? fallback : obj[name]!.Type == JTokenType.Integer && (long)obj[name]! >= min && (long)obj[name]! <= max ? (int)obj[name]! : throw PublicFields.Error(name + " must be " + min + ".." + max);
    private static void Keys(JObject obj,params string[] allowed) { var unknown = obj.Properties().FirstOrDefault(p => !allowed.Contains(p.Name)); if (unknown != null) throw PublicFields.Error("Unknown option " + unknown.Name + ". Allowed: " + string.Join(", ",allowed)); }
}
