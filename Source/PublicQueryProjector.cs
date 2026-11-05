using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GraphQL;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>Shapes scanned values into output: grouping, ordering, paging, field projection and measures.</summary>
internal sealed class PublicQueryProjector
{
    private readonly PublicFields registry;
    private readonly Func<PublicQuerySelection,Func<IEnumerable<object>>,PublicQueryRun,string,JObject> collection;
    /// <param name="collection">Reads a nested collection field; supplied by the executor that owns scanning.</param>
    internal PublicQueryProjector(PublicFields registry, Func<PublicQuerySelection,Func<IEnumerable<object>>,PublicQueryRun,string,JObject> collection)
    {
        this.registry = registry;
        this.collection = collection;
    }
    private JToken PathValue(string type,object? value,string path,PublicQueryRun run)
    {
        run.Context.Check();
        if (value == null) return JValue.CreateNull();
        var lookup = (value,type,path);
        if (run.PathValues.TryGetValue(lookup,out var cached)) return cached;
        var key = type + ":" + path;
        if (!run.PathSteps.TryGetValue(key,out var steps)) {
            steps = new();
            foreach (var part in path.Split('.')) { var field = registry.Field(type,part); steps.Add((type,field,registry.Arguments(field,null,path))); type = PublicFields.ValueType(field).Name; }
            run.PathSteps.Add(key,steps);
        }
        foreach (var step in steps) {
            if (value == null) return JValue.CreateNull();
            value = registry.Read(step.Type,value,step.Field,step.Args,run.Context);
        }
        var result = value == null ? JValue.CreateNull() : new JValue(value);
        run.PathValues[lookup] = result;
        return result;
    }
    internal IEnumerable<int> OutputSteps(PublicQuerySelection plan,List<object> values,bool scan,PublicQueryRun run,string path,JArray rows,JObject result)
    {
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
                    yield return 0;
                }
                var groupRows = new List<JObject>();
                foreach(var group in groups.Values.OrderBy(g=>g.Signature,StringComparer.Ordinal)) {
                    JObject aggregate=null!;
                    foreach(var step in MeasureSteps(plan,group.Values,scan,run,path+"/groups/"+groupRows.Count,value=>aggregate=value))yield return step;
                    groupRows.Add(new JObject { ["key"]=group.Key,["aggregates"]=aggregate });
                }
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
                    rows.Add(row);yield return 0;
                }
                result["page"] = Page(groups.Count,rows.Count,plan,scan);
            }
            else
            {
                IEnumerable<object> output = values;
                if (plan.Order.Count > 0)
                {
                    var keyed=new List<(object Value,int Index,JToken[] Keys)>();
                    foreach(var value in values) { keyed.Add((value,keyed.Count,plan.Order.Select(o=>PathValue(plan.Type,value,o.Path,run)).ToArray()));yield return 0; }
                    keyed.Sort((a,b) => { for (var i=0;i<plan.Order.Count;i++) { var cmp = Comparer<JToken>.Create(Compare).Compare(a.Keys[i],b.Keys[i]); if (cmp != 0) return plan.Order[i].Desc ? -cmp : cmp; } return a.Index.CompareTo(b.Index); });
                    output = keyed.Select(v => v.Value);
                }
                foreach (var value in output.Skip(plan.Offset).Take(plan.Limit)) { rows.Add(Project(plan,value,run,path + "/rows/" + rows.Count));yield return 0; }
                result["page"] = Page(values.Count,rows.Count,plan,scan);
            }

    }
    private static int Compare(JToken a,JToken b)
    {
        if (a.Type == JTokenType.Null || b.Type == JTokenType.Null) return a.Type == b.Type ? 0 : a.Type == JTokenType.Null ? -1 : 1;
        if (a.Type is JTokenType.Integer or JTokenType.Float && b.Type is JTokenType.Integer or JTokenType.Float) return ((decimal)a).CompareTo((decimal)b);
        return StringComparer.Ordinal.Compare(a.ToString(),b.ToString());
    }
    internal IEnumerable<int> MeasureSteps(PublicQuerySelection plan,List<object> values,bool scan,PublicQueryRun run,string path,Action<JObject> publish)
    {
        var measures = new JObject();
        foreach (var measure in plan.Measures)
        {
            var complete = scan; JToken value = JValue.CreateNull();
            List<JToken>? projected=null; ExecutionError? projectionError=null;
            if(measure.Op!="count") {
                projected=new List<JToken>();
                foreach(var item in values) {
                    try { projected.Add(PathValue(plan.Type,item,measure.Path!,run)); }
                    catch(ExecutionError error) { projectionError=error;break; }
                    yield return 0;
                }
            }
            try {
                if(projectionError!=null)throw projectionError;
                if (measure.Op == "count") value = new JValue(values.Count);
                else {
                    var numbers = projected!;
                    if (numbers.Any(v => v.Type is not (JTokenType.Integer or JTokenType.Float))) throw ReadContext.Error("aggregate_unavailable","Measure contains null or nonnumeric values: " + measure.Path);
                    if (measure.Op == "sum") value = new JValue(numbers.Sum(v => (decimal)v));
                    else if (numbers.Count > 0) value = measure.Op == "min" ? new JValue(numbers.Min(v => (decimal)v)) : new JValue(numbers.Max(v => (decimal)v));
                }
            } catch (ExecutionError error) { complete = false; value = JValue.CreateNull(); run.Fail(error,path + "/aggregates/" + measure.Name); }
            var row = new JObject { ["value"] = complete ? value : JValue.CreateNull(),["complete"] = complete,["op"] = measure.Op,["field"] = measure.Path,["unit"] = measure.Op == "count" ? "records" : measure.Op == "sum" ? "stack_units" : "field_value" };
            if (!complete && measure.Op is "sum" or "count" && value.Type != JTokenType.Null) row["known_value"] = value;
            measures[measure.Name] = row;
        }
        publish(measures);
    }
    private JObject Project(PublicQuerySelection plan,object value,PublicQueryRun run,string path)
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
                    var collection = this.collection(selected.Child,() => ((IEnumerable)raw).Cast<object>(),run,at);
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
    private JToken GroupValues(PublicQuerySelection plan,List<object> values,bool complete,PublicQueryRun run,string path)
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
    internal static JObject Page(int count,int returned,PublicQuerySelection plan,bool scan) => new JObject { ["offset"] = plan.Offset,["returned"] = returned,["has_more"] = scan ? new JValue(plan.Offset+returned<count) : JValue.CreateNull(),["next_offset"] = scan && returned > 0 && plan.Offset+returned<count ? new JValue(plan.Offset+returned) : JValue.CreateNull(),["consistency"] = "fresh_observation" };
}
