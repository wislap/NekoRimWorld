using System.Linq;
using GraphQL.Types;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>Small public null/membership normalization into the existing typed predicate engine.</summary>
internal static class ObjectReadPredicates
{
    internal static JObject Normalize(PublicFields fields,string type,JObject input)
    {
        var output=new JObject();var conjunction=new JArray();
        foreach(var property in input.Properties()) {
            if(property.Name is "all" or "any") { output[property.Name]=property.Value is JArray clauses?new JArray(clauses.Select(c=>Normalize(fields,type,(JObject)c))):property.Value.DeepClone();continue; }
            if(property.Name is "not" or "where") { output[property.Name]=Normalize(fields,type,(JObject)property.Value);continue; }
            if(property.Name is "on" or "reach") { output[property.Name]=property.Value.DeepClone();continue; }
            var owner=type;FieldType field=null!;
            foreach(var part in property.Name.Split('.')) { field=fields.Field(owner,part);owner=PublicFields.ValueType(field).Name; }
            if(property.Value is not JObject condition) { output[property.Name]=property.Value.DeepClone();continue; }
            var normalized=new JObject();
            // A direct child field map retains the same object vocabulary.
            if(fields.Type(owner) is IComplexGraphType && condition.Properties().All(p=>fields.Fields(owner).ContainsKey(p.Name))) {
                output[property.Name]=Normalize(fields,owner,condition);continue;
            }
            foreach(var operation in condition.Properties()) {
                if(operation.Name is "eq" or "ne" && operation.Value.Type==JTokenType.Null) { conjunction.Add(new JObject { [property.Name]=new JObject { ["is_null"]=operation.Name=="eq" } });continue; }
                if(operation.Name=="in" && operation.Value is JArray choices && choices.Any(v=>v.Type==JTokenType.Null)) {
                    var other=new JArray(choices.Where(v=>v.Type!=JTokenType.Null));var alternatives=new JArray(new JObject { [property.Name]=new JObject { ["is_null"]=true } });
                    if(other.Count>0)alternatives.Add(new JObject { [property.Name]=new JObject { ["in"]=other } });
                    conjunction.Add(new JObject { ["any"]=alternatives });continue;
                }
                if(operation.Name=="has") {
                    if(PublicFields.IsList(field) && fields.Type(owner) is ScalarGraphType) {
                        normalized["some"]=operation.Value is JArray list?new JObject { ["in"]=list.DeepClone() }:operation.Value.Type==JTokenType.Null?new JObject { ["is_null"]=true }:new JObject { ["eq"]=operation.Value.DeepClone() };
                    } else if(NativeEnumScalar.For(field)?.Flags==true)normalized["has_any"]=operation.Value.DeepClone();
                    else throw ReadContext.Error("invalid_condition","has is supported only for declared primitive collections or native flag fields.");
                    continue;
                }
                if(operation.Name=="where" && fields.Type(owner) is IComplexGraphType)normalized[operation.Name]=Normalize(fields,owner,(JObject)operation.Value);
                else if(operation.Name is "some" or "every" or "none" && fields.Type(owner) is IComplexGraphType)normalized[operation.Name]=Normalize(fields,owner,(JObject)operation.Value);
                else normalized[operation.Name]=operation.Value.DeepClone();
            }
            if(normalized.Count>0)output[property.Name]=normalized;
        }
        if(conjunction.Count>0) {
            if(output["all"] is JArray existing)foreach(var clause in conjunction)existing.Add(clause);
            else output["all"]=conjunction;
        }
        return output;
    }
    internal static JObject Scalar(JObject input) {
        var result=new JObject();var all=new JArray();
        foreach(var operation in input.Properties()) {
            if(operation.Name is "eq" or "ne" && operation.Value.Type==JTokenType.Null)all.Add(new JObject { ["is_null"]=operation.Name=="eq" });
            else if(operation.Name=="in" && operation.Value is JArray values && values.Any(v=>v.Type==JTokenType.Null)) {
                var others=new JArray(values.Where(v=>v.Type!=JTokenType.Null));var any=new JArray(new JObject { ["is_null"]=true });if(others.Count>0)any.Add(new JObject { ["in"]=others });all.Add(new JObject { ["any"]=any });
            } else result[operation.Name]=operation.Value.DeepClone();
        }
        if(all.Count>0) { if(result.Count>0)all.Insert(0,result);return new JObject { ["all"]=all }; }return result;
    }
}
