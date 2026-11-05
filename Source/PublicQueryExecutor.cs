using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GraphQL;
using GraphQL.Types;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>Executes planned requests: root reads, the bounded candidate scan, and collection assembly.</summary>
internal sealed class PublicQueryExecutor
{
    private readonly PublicFields registry;
    private readonly PublicQueryProjector projector;
    internal PublicQueryExecutor(PublicFields registry)
    {
        this.registry = registry;
        projector = new PublicQueryProjector(registry, Collection);
    }
    /// <summary>Reads one planned request's root collection into <paramref name="publish"/>.</summary>
    internal IEnumerable<int> RequestSteps(PublicQueryRequest plan,IGameData source,PublicQueryRun run,string path,Action<JObject> publish)
    {
        var state = run.Context;
        // Pushdown can discard only definitely false candidates. Unknown must reach the full AND predicate.
        Func<GameNode,bool>? filter = plan.DefinitionFilter == null ? null : n => {
            var match = plan.DefinitionFilter(n,state);
            if (match == null && plan.FullyPushed) throw ReadContext.Error("filter_unavailable","Definition membership is unknown.");
            return match != false;
        };
        foreach(var step in CollectionSteps(plan.Select, () => ReadRoot(plan.Root,plan.RootField,plan.Args,source,state,filter,plan.ExactIds), run,path,publish,
            plan.Root == "things" ? () => ThingsScanLimitMessage(plan.Args) : (Func<string>?)null)) yield return step;
    }
    private IEnumerable<object> ReadRoot(string root, FieldType rootField, Dictionary<string,object?> args, IGameData source, ReadContext state,Func<GameNode,bool>? definitionFilter,IReadOnlyCollection<string>? exactIds)
    {
        // This iterator's preparation belongs before the first checked game read.
        yield return ReadProgress.Instance;
        if (root is "thing_defs" or "thing_def") {
            if (source is not IThingData native) throw ReadContext.Error("unsupported_target", "Native Thing data is unavailable.");
            // Definition-only reads already have a typed native root and exact
            // candidate walker. No map resolution or general cache/signature work.
            var definitions = native.NativeRoot(root == "thing_defs" ? "query_defs" : root, args, state);
            yield return ReadProgress.Instance;
            if (definitions is IEnumerable sequence) foreach (var definition in sequence) yield return definition;
            else if (definitions != null) yield return definitions;
            yield break;
        }
        var mapArgument = root == "map" ? "id" : "mapId";
        if (args.TryGetValue(mapArgument,out var requestedMap) && requestedMap as string == "current") {
            args[mapArgument] = state.Read("Game",null,"currentMapId") as string ?? throw ReadContext.Error("object_unavailable","There is no current map.");
            // A completed native getter can consume this slice before the next getter.
            yield return ReadProgress.Instance;
        }
        if (args.TryGetValue(mapArgument,out var selectedMap) && selectedMap is string mapId) {
            if (!state.Maps().Any(m => m.Id == mapId)) throw ReadContext.Error("object_unavailable","Selected map is not loaded.");
            yield return ReadProgress.Instance;
        }
        var values = ReadResolvedRoot(root, rootField, args, source, state, definitionFilter, exactIds);
        yield return ReadProgress.Instance;
        foreach (var value in values) yield return value;
    }
    private IEnumerable<object> ReadResolvedRoot(string root, FieldType rootField, Dictionary<string,object?> args, IGameData source, ReadContext state,Func<GameNode,bool>? definitionFilter,IReadOnlyCollection<string>? exactIds)
    {
        if (root is "things" or "pawns")
        {
            if (source is not IQueryData query) throw ReadContext.Error("query_source_unavailable","This data source has no complete loaded-map/holder query capability.");
            return root == "things" ? query.QueryThings(args,state,definitionFilter,exactIds).Cast<object>() : query.QueryPawns(args,state).Cast<object>();
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
            if (found == null) return Array.Empty<object>();
            return new object[] { found };
        }
        if (root == "thing") {
            try { return new[] { state.NativeRoot(root, args)! }; }
            catch (ExecutionError error) when (error.Code == "object_not_found") { return Array.Empty<object>(); }
        }
        var field = rootField; var value = registry.Read("Query",state,field,args,state);
        return value is IEnumerable sequence && value is not string ? sequence.Cast<object>() : value == null ? Array.Empty<object>() : new[] { value };
    }
    private const string CollectionScanLimitMessage = "This collection exceeded 8192 source candidates, before ordinary instance filters and output ordering, grouping or pagination. Changing only those options does not reduce this source scan. Keep totals unknown; known_matches/known_value describe observed lower bounds, not the candidate limit. Any other path needs its own completeness check. Retain complete sibling results.";
    private static string ThingsScanLimitMessage(Dictionary<string,object?> args)
    {
        // ReadRoot resolves current before enumeration. Do not infer a second query plan from where.
        var oneMap = args.TryGetValue("mapId",out var map) && map is string;
        var oneDefinition = args.TryGetValue("defNames",out var names) && names is IEnumerable list &&
            list.Cast<object>().OfType<string>().Distinct(StringComparer.Ordinal).Take(2).Count()==1;
        if (oneMap && oneDefinition)
            return "This top-level things reading is already restricted to one resolved loaded map and one exact definition. Its source still exceeded 8192 candidates. Changing only limit, offset, order_by, group_by or ordinary instance filters does not reduce this source scan; splitting the same map/definition cannot partition it further. Keep the total unknown; known_matches/known_value describe observed lower bounds. Any other path needs its own completeness check. Retain complete sibling results.";
        return "This things source exceeded 8192 candidates before ordinary instance filters and output ordering, grouping or pagination. Changing only those options does not reduce this scan. Map/definition restrictions may narrow it only if they divide the intended population; a single map and definition cannot be split further along those dimensions. Keep totals unknown; known_matches/known_value describe observed lower bounds. Any other path needs its own completeness check. Retain complete sibling results.";
    }
    private JObject Collection(PublicQuerySelection plan,Func<IEnumerable<object>> read,PublicQueryRun run,string path)
    {
        JObject result=null!;
        foreach(var step in CollectionSteps(plan,read,run,path,value=>result=value)) { }
        return result;
    }
    private IEnumerable<int> ScanSteps(PublicQuerySelection plan,Func<IEnumerable<object>> read,PublicQueryRun run,string path,List<object> values,HashSet<string> identities,Action<bool> complete,Func<string>? scanLimitMessage)
    {
        IEnumerator<object>? iterator=null; bool scan=true; int scanned=0;
        string stage = "root_acquisition";
        void Failed(Exception error) {
            var failure = error as ExecutionError ?? ReadContext.Error("field_unavailable", "Collection read failed.");
            if (failure.Code == "deadline_exceeded") failure = ReadContext.Error(failure.Code, failure.Message + " Read stage: " + stage + ".");
            run.Fail(failure, path);
        }
        try { iterator=read().GetEnumerator(); }
        catch(Exception error) when(ReadContext.Recoverable(error)) { scan=false;Failed(error); }
        try {
            // Root acquisition may perform synchronous native initialization before
            // returning its lazy iterator. Let the frame runner end that slice
            // before a deadline check rejects the still-unread first candidate.
            if (scan) yield return 0;
            while(scan) {
                bool more=false; bool progress=false; object? value=null;
                try {
                    stage = "before_source_advance";
                    run.Context.Check();
                    stage = "source_iteration";
                    more=iterator!.MoveNext();
                    if(more) {
                        value=iterator.Current;
                        progress = ReadProgress.Is(value);
                        // Scheduler progress is not a candidate, count or evidence row.
                        if (!progress) {
                            if(++scanned>8192)throw ReadContext.Error("query_scan_limit",scanLimitMessage?.Invoke()??CollectionScanLimitMessage);
                            var id=value is GameEntity e?"entity:"+e.Id:value is GameNode n && n.Kind is "Thing" or "ThingReference"?n.Kind+":"+n.Owner.Id+":"+n.Key:null;
                            if(id==null || identities.Add(id)) {
                                stage = "selection_predicate";
                                var match=plan.Predicate?.Invoke(value,run.Context) ?? (plan.Predicate==null?(bool?)true:null);
                                if(match==null)throw ReadContext.Error("filter_unavailable","Candidate membership is unknown, not false.");
                                if(match.Value)values.Add(value);
                            }
                        }
                    }
                } catch(Exception error) when(ReadContext.Recoverable(error)) { scan=false;Failed(error); }
                if(!more)break;
                if(progress || (scanned&15)==0)yield return 0;
            }
        } finally { iterator?.Dispose(); }
        complete(scan);
    }
    private IEnumerable<int> CollectionSteps(PublicQuerySelection plan,Func<IEnumerable<object>> read,PublicQueryRun run,string path,Action<JObject> publish,Func<string>? scanLimitMessage = null)
    {
        var started = run.Context.ElapsedMilliseconds;
        var values = new List<object>(); var identities = new HashSet<string>(StringComparer.Ordinal); var scan = true;
        foreach(var step in ScanSteps(plan,read,run,path,values,identities,value=>scan=value,scanLimitMessage)) yield return step;
        var result = new JObject { ["scan_complete"] = scan,["matched_count"] = scan ? new JValue(values.Count) : JValue.CreateNull(),["known_matches"] = values.Count };
        var scannedAt = run.Context.ElapsedMilliseconds;
        yield return 0;
        var before = run.Issues.Count;
        if(plan.Measures.Count>0) {
            JObject aggregate=null!;
            foreach(var step in projector.MeasureSteps(plan,values,scan,run,path,value=>aggregate=value))yield return step;
            result["aggregates"]=aggregate;
        }
        var measuredAt = run.Context.ElapsedMilliseconds;
        var rows = new JArray(); bool ordered = true;
        using(var projecting=projector.OutputSteps(plan,values,scan,run,path,rows,result).GetEnumerator()) {
            while(true) {
                bool more=false;
                try { more=projecting.MoveNext(); }
                catch(ExecutionError error) { ordered=false;run.Fail(error,path);result["page"]=PublicQueryProjector.Page(values.Count,rows.Count,plan,false); }
                if(!more)break;
                yield return projecting.Current;
            }
        }
        // A zero-sized aggregate display is intentional, not a page awaiting retrieval.
        if (plan.Measures.Count > 0 && plan.Limit == 0) result.Remove("page");
        else result["rows"] = rows;
        result["fields_complete"] = run.Issues.Count == before && ordered;
        if (path.Count(c => c == '/') == 2) run.Phases.Add(new JObject { ["path"] = path,["scan_ms"] = scannedAt-started,["aggregate_ms"] = measuredAt-scannedAt,["output_ms"] = run.Context.ElapsedMilliseconds-measuredAt,["known_matches"] = values.Count,["returned"] = rows.Count });
        publish(result);
    }
}
