using System;
using System.Collections.Generic;
using System.Linq;
using GraphQL.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>Bounded relational reads over the existing executable field schema. No model-side joins or arithmetic.</summary>
internal sealed class PublicQuery
{
    internal const string Version = "public_query.v2";
    private readonly SelectionConditions conditions;
    private readonly PublicQueryPlanner planner;
    private readonly PublicQueryExecutor executor;
    internal PublicQuery(Schema schema, SelectionConditions conditions)
    {
        this.conditions = conditions;
        planner = new PublicQueryPlanner(schema, conditions);
        executor = new PublicQueryExecutor(planner.Registry);
    }
    internal JObject Catalog() => planner.Catalog();
    internal JArray Describe(IEnumerable<string> types) => planner.Describe(types);
    internal QueryOutcome Execute(JObject input, IGameData source, SessionDescriptor session, double budget, string schemaId)
    {
        QueryOutcome outcome=null!;
        foreach(var step in ExecuteSteps(input,source,session,budget,schemaId,value=>outcome=value)) { }
        return outcome;
    }
    internal IEnumerable<int> ExecuteSteps(JObject input, IGameData source, SessionDescriptor session, double budget, string schemaId, Action<QueryOutcome> publish,Action<ReadContext>? contextReady=null)
    {
        var direct = input["from"] != null && input["requests"] == null;
        if (direct) input = new JObject { ["requests"] = new JArray(input.DeepClone()) };
        if (!planner.TryPlan(input,direct,out var plans,out var failed)) { publish(failed); yield break; }
        var requests = (JArray)input["requests"]!;
        // Validate the whole batch before touching game state.
        var state = new ReadContext(source,session,budget) { Conditions = conditions }; var run = new PublicQueryRun(state); var results = new JArray();
        contextReady?.Invoke(state);
        foreach (var plan in plans)
        {
            var path = "/results/" + results.Count;
            JObject result = null!;
            foreach(var step in executor.RequestSteps(plan,source,run,path,value=>result=value)) yield return step;
            result["from"] = plan.Root;
            result["scope"] = new JObject { ["args"] = JObject.FromObject(plan.Args.ToDictionary(p => PublicFields.Name(p.Key),p => p.Value)), ["description"] = (plan.RootField.Description ?? PublicQueryPlanner.RootDescription(plan.Root)) + " The resolved args and requested where below define this reading; completeness is per aggregate/collection." };
            result["scope"]!["argument_semantics"] = new JObject((plan.RootField.Arguments?.ToArray() ?? Array.Empty<QueryArgument>())
                .Where(a => plan.Args.ContainsKey(a.Name) && !string.IsNullOrEmpty(a.Description))
                .Select(a => new JProperty(PublicFields.Name(a.Name),a.Description)));
            result["scope"]!["where"] = requests[results.Count]!["where"]?.DeepClone() ?? new JObject();
            results.Add(result);
        }
        var data = new JObject { ["results"] = results, ["relation_semantics"] = JObject.FromObject(run.RelationSemantics), ["sample"] = new JObject { ["load_epoch"] = session.LoadEpoch,["game_tick"] = session.GameTick,["sequence"] = session.SnapshotSequence } };
        if (data.ToString(Formatting.None).Length > 80000) { publish(QueryOutcome.Failure("query_result_too_large","Request fewer fields or displayed rows.")); yield break; }
        publish(new QueryOutcome { Status = run.Issues.Count == 0 ? "ok" : "partial", Data = data, Issues = run.Issues.Take(256).ToArray(), Execution = new JObject { ["schema_id"] = schemaId,["contract"] = Version,["requests"] = input["requests"]!.DeepClone(),["phases"] = run.Phases } });
    }
}
