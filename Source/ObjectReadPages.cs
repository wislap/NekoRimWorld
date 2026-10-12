using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>Lossless structural delivery of already sampled DTOs; never performs game reads.</summary>
internal static class ObjectReadPages
{
    internal static JObject Branch(JObject projection,object[] path,JToken? replacement=null)=>
        (JObject)CloneBranch(projection,path,0,replacement);
    private static JToken CloneBranch(JToken token,object[] path,int index,JToken? replacement) {
        if(index==path.Length)return replacement?.DeepClone()??token.DeepClone();
        if(token is JArray array)return new JArray(CloneBranch(array[(int)path[index]],path,index+1,replacement));
        var obj=(JObject)token;var name=(string)path[index];var result=new JObject();
        foreach(var property in obj.Properties()) {
            if(property.Name==name) { result[property.Name]=CloneBranch(property.Value,path,index+1,replacement);continue; }
            if(property.Name is "field_samples" or "field_context" or "field_semantics") {
                var selected=new JObject();
                if(name=="values" && index+1<path.Length && path[index+1] is string column && property.Value[column]!=null)
                    selected[column]=property.Value[column]!.DeepClone();
                result[property.Name]=selected;continue;
            }
            if(property.Name is "rows" or "readings" or "values" or "issues" or "semantic_rules") {
                if(property.Name is "values" or "semantic_rules")result[property.Name]=new JObject();
                else result[property.Name]=new JArray();
                continue;
            }
            result[property.Name]=property.Value.DeepClone();
        }
        if(result["coverage"] is JObject coverage) {
            coverage["source_display_complete"]=coverage["display_complete"]?.DeepClone();coverage["display_complete"]=false;
        }
        return result;
    }
    internal static void Merge(JObject destination,JObject source) {
        foreach(var property in source.Properties()) {
            if(property.Name is "values" or "semantic_rules" or "field_samples" or "field_context" or "field_semantics" && property.Value is JObject values) {
                var to=destination[property.Name] as JObject;if(to==null) { to=new JObject();destination[property.Name]=to; }
                foreach(var value in values.Properties())to[value.Name]=value.Value.DeepClone();continue;
            }
            if(property.Name is "rows" or "readings" && property.Value is JArray array) {
                var to=destination[property.Name] as JArray;if(to==null) { to=new JArray();destination[property.Name]=to; }
                foreach(var row in array.OfType<JObject>()) {
                    var match=property.Name=="rows"?to.OfType<JObject>().FirstOrDefault(r=>JToken.DeepEquals(r["anchor"],row["anchor"])):
                        to.OfType<JObject>().FirstOrDefault(r=>JToken.DeepEquals(r["input_path"],row["input_path"]) && JToken.DeepEquals(r["field"],row["field"]));
                    if(match==null)to.Add(row.DeepClone());else Merge(match,row);
                }
                continue;
            }
            if(property.Name=="issues" && property.Value is JArray issues) {
                var to=destination["issues"] as JArray;if(to==null) { to=new JArray();destination["issues"]=to; }
                foreach(var issue in issues)if(!to.Any(existing=>JToken.DeepEquals(existing,issue)))to.Add(issue.DeepClone());continue;
            }
            destination[property.Name]=property.Value.DeepClone();
        }
    }
}
