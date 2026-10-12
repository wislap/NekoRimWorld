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
        // value_snapshot_id strings carried by tokens this offer placed; everything already in the
        // destination came from an accepted offer, so these complete the delivered-text set.
        private List<string>? placed;
        private int committed;
        internal int Size { get; private set; }
        internal TrackedMerge(JObject destination) { Size=committed=Bytes(destination); }
        internal void Apply(JObject destination,JObject source,List<string> texts) { texts.Clear();placed=texts;undo.Clear();Merge(destination,source);placed=null; }
        internal void Commit() { undo.Clear();committed=Size; }
        internal void Rollback() {
            for(var i=undo.Count-1;i>=0;i--)undo[i]();
            undo.Clear();buckets.Clear();Size=committed;
        }
        internal static int Bytes(JToken value) {
            // Same compact serialization as ToString(Formatting.None), counted as UTF-8 without building the text.
            var counter=Counter.Shared;counter.Reset();
            using(var writer=new Newtonsoft.Json.JsonTextWriter(counter) { Formatting=Newtonsoft.Json.Formatting.None,CloseOutput=false })value.WriteTo(writer);
            counter.Flush();return counter.Total;
        }
        /// <summary>Counts the UTF-8 bytes Encoding.UTF8 would produce, lone surrogates included.</summary>
        private sealed class Counter : System.IO.TextWriter {
            [ThreadStatic] private static Counter? shared;
            internal static Counter Shared=>shared??=new Counter();
            private bool high;
            internal int Total;
            internal void Reset() { Total=0;high=false; }
            public override System.Text.Encoding Encoding=>System.Text.Encoding.UTF8;
            public override void Write(char value) {
                if(high) {
                    high=false;
                    if(char.IsLowSurrogate(value)) { Total+=4;return; }
                    Total+=3;
                }
                if(value<0x80)Total++;
                else if(value<0x800)Total+=2;
                else if(char.IsHighSurrogate(value))high=true;
                else Total+=3;
            }
            public override void Write(char[] buffer,int index,int count) { for(var i=index;i<index+count;i++)Write(buffer[i]); }
            public override void Write(string? value) { if(value!=null)foreach(var character in value)Write(character); }
            public override void Flush() { if(high) { Total+=3;high=false; } }
        }
        // Equal values serialize identically, so an unchanged member is left untouched (and never cloned).
        private void Copy(JObject target,string name,JToken value) {
            if(target.TryGetValue(name,out var old) && JToken.DeepEquals(old,value))return;
            Set(target,name,value.DeepClone());
        }
        private void Set(JObject target,string name,JToken value) {
            if(placed!=null) { if(name=="value_snapshot_id" && value.Type==JTokenType.String)placed.Add((string)value!);Within(value,placed); }
            if(target.TryGetValue(name,out var old)) {
                // Equal values serialize identically; the replaced token is never referenced elsewhere.
                Size+=Bytes(value)-Bytes(old!);target[name]=value;undo.Add(()=>target[name]=old);
            } else {
                Size+=Bytes(new JValue(name))+1+Bytes(value)+(target.Count>0?1:0);target[name]=value;undo.Add(()=>target.Remove(name));
            }
        }
        private void Add(JArray target,JToken value) {
            if(placed!=null)Within(value,placed);
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
        private JObject EnsureObject(JObject destination,string name) {
            var to=destination[name] as JObject;if(to==null) { to=new JObject();Set(destination,name,to); }
            return to;
        }
        private JArray EnsureArray(JObject destination,string name) {
            var to=destination[name] as JArray;if(to==null) { to=new JArray();Set(destination,name,to); }
            return to;
        }
        private JObject? Match(JArray to,Dictionary<string,List<JToken>> index,string kind,JObject row)=>
            index.TryGetValue(Key(to,row),out var candidates)?(JObject?)candidates.FirstOrDefault(r=>kind=="rows"?JToken.DeepEquals(r["anchor"],row["anchor"]):
                JToken.DeepEquals(r["input_path"],row["input_path"]) && JToken.DeepEquals(r["field"],row["field"])):null;
        private void Merge(JObject destination,JObject source) {
            foreach(var property in source.Properties()) {
                if(property.Name is "values" or "semantic_rules" or "field_samples" or "field_context" or "field_semantics" && property.Value is JObject values) {
                    var to=EnsureObject(destination,property.Name);
                    foreach(var value in values.Properties())Copy(to,value.Name,value.Value);continue;
                }
                if(property.Name is "rows" or "readings" && property.Value is JArray array) {
                    var to=EnsureArray(destination,property.Name);var index=Index(to,property.Name);
                    foreach(var row in array.OfType<JObject>()) {
                        var match=Match(to,index,property.Name,row);
                        if(match==null)Add(to,row.DeepClone());else Merge(match,row);
                    }
                    continue;
                }
                if(property.Name=="issues" && property.Value is JArray issues) {
                    var to=EnsureArray(destination,"issues");var index=Index(to,"issues");
                    foreach(var issue in issues)
                        if(!index.TryGetValue(Key(to,issue),out var same) || !same.Any(existing=>JToken.DeepEquals(existing,issue)))Add(to,issue.DeepClone());
                    continue;
                }
                Copy(destination,property.Name,property.Value);
            }
        }
        /// <summary>
        /// Exactly Apply(destination,Branch(projection,path,replacement)) without materializing the
        /// branch; texts receives the ids of tokens it placed, as Apply does. Returns false,
        /// changing nothing, for shapes outside the row/reading/column paths it mirrors.
        /// </summary>
        internal bool ApplyBranch(JObject destination,JObject projection,object[] path,JToken? replacement,List<string> texts) {
            texts.Clear();
            if(!Fusable(projection,path,0,replacement))return false;
            undo.Clear();placed=texts;
            if(path.Length==0)Merge(destination,(JObject)(replacement??projection));
            else MergeBranch(destination,projection,path,0,replacement);
            placed=null;return true;
        }
        // DeepEquals(present,CloneBranch's adjusted coverage) without building it; Copy would skip.
        private static bool SameAdjusted(JObject present,JObject coverage) {
            var count=coverage.Count+(coverage.ContainsKey("source_display_complete")?0:1)+(coverage.ContainsKey("display_complete")?0:1);
            if(present.Count!=count)return false;
            foreach(var property in coverage.Properties()) {
                if(property.Name is "display_complete" or "source_display_complete")continue;
                if(!present.TryGetValue(property.Name,out var value) || !JToken.DeepEquals(value,property.Value))return false;
            }
            return present["display_complete"] is JValue { Type: JTokenType.Boolean } shown && !(bool)shown &&
                present.TryGetValue("source_display_complete",out var source) && JToken.DeepEquals(source,coverage["display_complete"]??JValue.CreateNull());
        }
        private static bool Reserved(string name)=>name is "rows" or "readings" or "values" or "issues" or "semantic_rules" or
            "field_samples" or "field_context" or "field_semantics" or "coverage" or "value_snapshot_id";
        private static bool Fusable(JToken token,object[] path,int index,JToken? replacement) {
            if(index==path.Length)return (replacement??token) is JObject;
            if(token is not JObject obj || path[index] is not string name || !obj.TryGetValue(name,out var child))return false;
            foreach(var property in obj.Properties())
                if(property.Name is "field_samples" or "field_context" or "field_semantics" && property.Value is not JObject)return false;
            if(name is "rows" or "readings")
                return child is JArray array && index+1<path.Length && path[index+1] is int item && item>=0 && item<array.Count &&
                    array[item] is JObject element && Fusable(element,path,index+2,replacement);
            return name=="values" && child is JObject values && index+2==path.Length && path[index+1] is string column &&
                values.ContainsKey(column) && values.Properties().All(p=>!Reserved(p.Name));
        }
        private static void Text(JObject value,List<string> texts) {
            if(value["value_snapshot_id"]?.Type==JTokenType.String)texts.Add((string)value["value_snapshot_id"]!);
        }
        private static void Within(JToken value,List<string> texts) {
            if(value is JObject self)Text(self,texts);
            if(value is JContainer container)foreach(var item in container.Descendants())if(item is JObject inner)Text(inner,texts);
        }
        // Mirrors CloneBranch property by property, feeding Merge the virtual branch in the same order.
        private void MergeBranch(JObject destination,JObject obj,object[] path,int index,JToken? replacement) {
            var name=(string)path[index];
            foreach(var property in obj.Properties()) {
                var key=property.Name;
                if(key==name && name=="values") {
                    var to=EnsureObject(destination,"values");var column=(string)path[index+1];
                    foreach(var value in ((JObject)property.Value).Properties()) {
                        var virtualValue=value.Name==column?replacement??value.Value:value.Value;
                        Copy(to,value.Name,virtualValue);
                    }
                    continue;
                }
                if(key==name) {
                    var to=EnsureArray(destination,name);var lookup=Index(to,name);
                    var element=(JObject)((JArray)property.Value)[(int)path[index+1]];
                    var terminal=index+2==path.Length;var row=terminal?(JObject)(replacement??element):element;
                    var match=Match(to,lookup,name,row);
                    if(match==null) { var clone=CloneBranch(element,path,index+2,replacement);Add(to,clone); }
                    else if(terminal) { Merge(match,row); }
                    else MergeBranch(match,element,path,index+2,replacement);
                    continue;
                }
                if(key is "field_samples" or "field_context" or "field_semantics") {
                    var to=EnsureObject(destination,key);
                    if(name=="values" && index+1<path.Length && path[index+1] is string column && property.Value[column]!=null) {
                        Copy(to,column,property.Value[column]!);
                    }
                    continue;
                }
                if(key is "rows" or "readings" or "issues") { var to=EnsureArray(destination,key);Index(to,key);continue; }
                if(key is "values" or "semantic_rules") { EnsureObject(destination,key);continue; }
                if(key=="coverage" && property.Value is JObject coverage) {
                    if(destination[key] is JObject present && SameAdjusted(present,coverage))continue;
                    var adjusted=(JObject)coverage.DeepClone();
                    adjusted["source_display_complete"]=adjusted["display_complete"]?.DeepClone();adjusted["display_complete"]=false;
                    Copy(destination,key,adjusted);continue;
                }
                Copy(destination,key,property.Value);
            }
        }
    }
    /// <summary>value_snapshot_id strings below a delivered branch root, as Drain records them.</summary>
    internal static void Texts(JObject branch,List<string> texts) {
        texts.Clear();
        foreach(var value in branch.Descendants().OfType<JObject>())
            if(value["value_snapshot_id"]?.Type==JTokenType.String)texts.Add((string)value["value_snapshot_id"]!);
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
