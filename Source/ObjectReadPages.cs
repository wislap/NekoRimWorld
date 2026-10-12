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
    /// <summary>
    /// Merge applied in place with exact compact UTF-8 size accounting and undo.
    /// Semantics are Merge's; matching uses hashed buckets verified by DeepEquals.
    /// </summary>
    internal sealed class TrackedMerge {
        private sealed class Reference : IEqualityComparer<JArray> {
            public bool Equals(JArray? left,JArray? right)=>ReferenceEquals(left,right);
            public int GetHashCode(JArray value)=>System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }
        private readonly Dictionary<JArray,Dictionary<string,List<JToken>>> buckets=new(new Reference());
        private readonly List<Action> undo=new();
        private int committed;
        internal int Size { get; private set; }
        internal TrackedMerge(JObject destination) { Size=committed=Bytes(destination); }
        internal void Apply(JObject destination,JObject source) { undo.Clear();Merge(destination,source); }
        internal void Commit() { undo.Clear();committed=Size; }
        internal void Rollback() {
            for(var i=undo.Count-1;i>=0;i--)undo[i]();
            undo.Clear();buckets.Clear();Size=committed;
        }
        internal static int Bytes(JToken value)=>System.Text.Encoding.UTF8.GetByteCount(value.ToString(Newtonsoft.Json.Formatting.None));
        // Equal values serialize identically, so an unchanged member is left untouched (and never cloned).
        private void Copy(JObject target,string name,JToken value) {
            if(target.TryGetValue(name,out var old) && JToken.DeepEquals(old,value))return;
            Set(target,name,value.DeepClone());
        }
        private void Set(JObject target,string name,JToken value) {
            if(target.TryGetValue(name,out var old)) {
                // Equal values serialize identically; the replaced token is never referenced elsewhere.
                Size+=Bytes(value)-Bytes(old!);target[name]=value;undo.Add(()=>target[name]=old);
            } else {
                Size+=Bytes(new JValue(name))+1+Bytes(value)+(target.Count>0?1:0);target[name]=value;undo.Add(()=>target.Remove(name));
            }
        }
        private void Add(JArray target,JToken value) {
            Size+=Bytes(value)+(target.Count>0?1:0);target.Add(value);undo.Add(()=>target.RemoveAt(target.Count-1));
            if(buckets.TryGetValue(target,out var index))Register(index,Key(target,value),value);
        }
        private static void Register(Dictionary<string,List<JToken>> index,string key,JToken value) {
            if(!index.TryGetValue(key,out var list))index[key]=list=new List<JToken>();
            list.Add(value);
        }
        // Coarse keys invariant under DeepEquals (same value type, order-insensitive objects).
        private static string Part(JToken? value)=>value is JValue { Type: JTokenType.String } text?"s:"+(string)text!:"*";
        private string Key(JArray owner,JToken value)=>KeyKind(owner) switch {
            "rows"=>Part((value as JObject)?["anchor"] is JObject anchor?anchor["id"]:null),
            "readings"=>Part((value as JObject)?["field"]),
            _=>Part((value as JObject)?["issue_id"])
        };
        private readonly Dictionary<JArray,string> kinds=new(new Reference());
        private string KeyKind(JArray owner)=>kinds.TryGetValue(owner,out var kind)?kind:"issues";
        private Dictionary<string,List<JToken>> Index(JArray target,string kind) {
            kinds[target]=kind;
            if(buckets.TryGetValue(target,out var index))return index;
            index=new Dictionary<string,List<JToken>>(StringComparer.Ordinal);
            foreach(var item in target)if(kind=="issues" || item is JObject)Register(index,Key(target,item),item);
            buckets[target]=index;return index;
        }
        private void Merge(JObject destination,JObject source) {
            foreach(var property in source.Properties()) {
                if(property.Name is "values" or "semantic_rules" or "field_samples" or "field_context" or "field_semantics" && property.Value is JObject values) {
                    var to=destination[property.Name] as JObject;if(to==null) { to=new JObject();Set(destination,property.Name,to); }
                    foreach(var value in values.Properties())Copy(to,value.Name,value.Value);continue;
                }
                if(property.Name is "rows" or "readings" && property.Value is JArray array) {
                    var to=destination[property.Name] as JArray;if(to==null) { to=new JArray();Set(destination,property.Name,to); }
                    var index=Index(to,property.Name);
                    foreach(var row in array.OfType<JObject>()) {
                        JObject? match=null;
                        if(index.TryGetValue(Key(to,row),out var candidates))
                            match=(JObject?)candidates.FirstOrDefault(r=>property.Name=="rows"?JToken.DeepEquals(r["anchor"],row["anchor"]):
                                JToken.DeepEquals(r["input_path"],row["input_path"]) && JToken.DeepEquals(r["field"],row["field"]));
                        if(match==null)Add(to,row.DeepClone());else Merge(match,row);
                    }
                    continue;
                }
                if(property.Name=="issues" && property.Value is JArray issues) {
                    var to=destination["issues"] as JArray;if(to==null) { to=new JArray();Set(destination,"issues",to); }
                    var index=Index(to,"issues");
                    foreach(var issue in issues)
                        if(!index.TryGetValue(Key(to,issue),out var same) || !same.Any(existing=>JToken.DeepEquals(existing,issue)))Add(to,issue.DeepClone());
                    continue;
                }
                Copy(destination,property.Name,property.Value);
            }
        }
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
