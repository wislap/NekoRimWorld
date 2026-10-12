using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GraphQL;
using GraphQL.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>Lossless, consultation-leased object reads. Persistent state contains IDs and DTOs only.</summary>
internal sealed partial class ObjectReadEngine
{
    private readonly PublicFields registry;
    private readonly SelectionConditions conditions;
    private readonly double budget;
    private readonly Dictionary<string,Lease> leases=new(StringComparer.Ordinal);
    private readonly Dictionary<string,Continuation> cursors=new(StringComparer.Ordinal);
    private const int PacketCharacters=24000;
    private sealed class Lease { internal string Epoch="",Colony="";internal DateTime Expires;internal long RetainedBytes;internal readonly Dictionary<string,Snapshot> Sources=new(StringComparer.Ordinal);internal readonly Dictionary<string,Snapshot> SourceIds=new(StringComparer.Ordinal);internal readonly Dictionary<string,JObject> Roles=new(StringComparer.Ordinal); }
    private sealed class Selection {
        internal string Field="",Type="";internal FieldType Definition=null!;internal readonly List<FieldType> Path=new();
        internal Dictionary<string,object?> Args=new();internal string[] Columns=Array.Empty<string>();internal List<Selection> Children=new();
        internal SelectionConditions.Predicate? Predicate;internal JObject Where=new();internal int Size=20;internal string? Cursor;internal int Index;
        internal ExecutionError? Error;
        internal bool ExplicitEmpty;
        internal bool Self;
        internal int[] InputPath=Array.Empty<int>();
    }
    private sealed class Locator { internal string Root="",Type="",Key="",SourceId="";internal JObject Scope=new();internal Locator? Parent;internal Selection? Relation; }
    private sealed class Member { internal string Key="",NativeKey="";internal WeakReference? Identity;internal JObject? Row;internal string State="pending"; }
    private sealed class Snapshot {
        internal string Id=Guid.NewGuid().ToString("N"),RootId="",Type="",Capture="",Signature="";
        internal Locator Locator=null!;internal Selection? Selection;internal JObject Where=new();internal SelectionConditions.Predicate? Predicate;
        internal readonly List<Member> Members=new();internal bool Complete;internal long CapturedTick;
        internal long CaptureFinishedTick;
        internal readonly Dictionary<string,Member> MemberIndex=new(StringComparer.Ordinal);
        internal readonly JObject Semantics=new();
        internal readonly List<QueryIssue> SourceIssues=new();
    }
    private sealed class Continuation {
        internal string Lease="",Epoch="",Signature="";internal Snapshot? Source;internal int Offset;
        internal string? Packet;internal string PacketStatus="ok";internal JObject Scope=new();internal long Tick;
        internal JObject Request=new();internal int[] InputPath=Array.Empty<int>();
        internal ReadTraversal? Traversal;internal JObject? CachedResult;
    }
    private sealed class Run {
        internal IGameData Source=null!;internal SessionDescriptor Session=null!;internal ReadContext Context=null!;
        internal Lease Lease=null!;internal string LeaseId="",Signature="";internal Func<long> Tick=null!;
        internal readonly Dictionary<string,object?> Lookups=new(StringComparer.Ordinal);internal readonly List<QueryIssue> Issues=new();
        internal readonly Dictionary<string,Dictionary<string,object>> LookupSources=new(StringComparer.Ordinal);
        internal readonly Dictionary<string,Dictionary<object,object>> LookupIdentities=new(StringComparer.Ordinal);
        internal readonly JObject SemanticRules=new();
        internal int CandidateReads,GetterReads;internal long Started;
        internal int ExplainedIssueCount;
        internal long LookupTick=-1;
        internal int LookupFrame=-1;
        internal Snapshot Root=null!;
        internal JObject Request=new();
    }
    private sealed class IdentityComparer : IEqualityComparer<object> {
        public new bool Equals(object? left,object? right)=>ReferenceEquals(left,right);
        public int GetHashCode(object value)=>RuntimeHelpers.GetHashCode(value);
    }
    internal ObjectReadEngine(Schema schema,SelectionConditions conditions,double budget) {
        registry=new PublicFields(schema,conditions);this.conditions=conditions;this.budget=budget;
    }
    internal JObject Catalog()=>ObjectReadContract.Catalog(registry);
    internal void Expire() { foreach(var id in leases.Where(p=>p.Value.Expires<DateTime.UtcNow).Select(p=>p.Key).ToArray())Release(id); }
    internal QueryOutcome Release(JObject arguments,SessionDescriptor session) {
        ObjectReadContract.Keys(arguments,"lease_id");var id=(string?)arguments["lease_id"]??throw ReadContext.Error("invalid_arguments","lease_id is required.");
        if(leases.TryGetValue(id,out var lease) && (lease.Epoch!=session.LoadEpoch || lease.Colony!=session.ColonyId))throw ReadContext.Error("cursor_stale_load","Cannot release a lease owned by another session.");
        Release(id);return new QueryOutcome { Data=new JObject { ["released"]=true,["lease_id"]=id } };
    }
    internal void Clear() { leases.Clear();cursors.Clear(); }
    internal void Release(string leaseId) {
        leases.Remove(leaseId);
        foreach(var key in cursors.Where(p=>p.Value.Lease==leaseId).Select(p=>p.Key).ToArray())cursors.Remove(key);
    }
    internal QueryOutcome Execute(JObject input,IGameData source,SessionDescriptor session) {
        using(var operation=Begin(input,source,session,()=>true,()=>session.GameTick)) { while(!operation.Advance()) { }return operation.Outcome!; }
    }
    internal IFrameQueryOperation Begin(JObject input,IGameData source,SessionDescriptor session,Func<bool> current,Func<long> tick) =>
        new ObjectReadOperation((publish,ready)=>ExecuteSteps((JObject)input.DeepClone(),source,session,tick,publish,ready),current,()=> { if(input["lease_id"]?.Type==JTokenType.String)Release((string)input["lease_id"]!); });
    private IEnumerable<int> ExecuteSteps(JObject input,IGameData source,SessionDescriptor session,Func<long> tick,Action<QueryOutcome> publish,Action<ReadContext> ready)
    {
        ObjectReadContract.Keys(input,"target","select","page","context","lease_id");
        var leaseId=(string?)input["lease_id"];
        if(string.IsNullOrWhiteSpace(leaseId) || leaseId!.Length>128)throw ReadContext.Error("invalid_arguments","lease_id must identify the current consultation.");
        if(input["target"]==null && input["select"]==null && input["page"] is JObject resumePage && resumePage["cursor"]?.Type==JTokenType.String) {
            ObjectReadContract.Keys(input,"page","lease_id");ObjectReadContract.Size(resumePage);
            var token=(string)resumePage["cursor"]!;
            var owner=GetLease(leaseId!,session);
            if(!cursors.TryGetValue(token,out var retained) || retained.Lease!=leaseId)throw ReadContext.Error("cursor_expired","Cursor has expired or belongs to another consultation.");
            if(retained.Epoch!=session.LoadEpoch)throw ReadContext.Error("cursor_stale_load","Cursor belongs to another load.");
            input=(JObject)retained.Request.DeepClone();input["lease_id"]=leaseId;
            var selectedPage=input["page"] as JObject??new JObject();
            selectedPage["cursor"]=token;
            if(resumePage["size"]!=null && (int)resumePage["size"]! != ObjectReadContract.Size(selectedPage))throw ReadContext.Error("cursor_selection_mismatch","A cursor inherits its record window; changed size requires a fresh read.");
            input["page"]=selectedPage;
        }
        var target=input["target"] as JObject??throw ReadContext.Error("invalid_arguments","target must be an object.");
        ObjectReadContract.Keys(target,"kind","scope","where");
        var kind=(string?)target["kind"]??"";
        if(!ObjectReadContract.Roots.TryGetValue(kind,out var type))throw ReadContext.Error("unknown_target","Unknown object kind: "+kind);
        var selections=Compile(type,input["select"],0);
        if(target["scope"]!=null && target["scope"] is not JObject || target["where"]!=null && target["where"] is not JObject || input["page"]!=null && input["page"] is not JObject)throw ReadContext.Error("invalid_arguments","scope, where and page must be objects.");
        var scope=target["scope"] as JObject??new JObject();ObjectReadContract.Keys(scope,"map","placement","map_visibility");
        if(scope.Properties().Any(p=>p.Value.Type!=JTokenType.String))throw ReadContext.Error("invalid_arguments","Scope values must be strings.");
        var page=input["page"] as JObject;var size=ObjectReadContract.Size(page);
        var signature=GameQueryEngine.Hash(new JObject { ["target"]=WithoutCursor(target),["select"]=WithoutCursor(input["select"]!),["context"]=input["context"]?.DeepClone()??new JObject(),["page_size"]=size }.ToString(Formatting.None));
        var lease=GetLease(leaseId,session);
        var context=new ReadContext(source,session,budget) { Conditions=conditions,ObjectReadMode=true,ObjectTick=tick };ready(context);
        var run=new Run { Source=source,Session=session,Context=context,Lease=lease,LeaseId=leaseId,Signature=signature,Tick=tick,Started=tick(),Request=(JObject)WithoutCursor(input) };
        void CompileIssues(IEnumerable<Selection> choices,List<object> path) {
            foreach(var selection in choices) {
                var at=path.Concat(new object[]{selection.Index}).ToList();
                if(selection.Error!=null)run.Issues.Add(new QueryIssue { Code=selection.Error.Code??"invalid_arguments",Message=selection.Error.Message,Path=new JArray(at) });
                CompileIssues(selection.Children,at.Concat(new object[]{"children"}).ToList());
            }
        }
        CompileIssues(selections,new List<object>{"select"});
        var continuationToken=(string?)page?["cursor"];
        string? NestedToken(IEnumerable<Selection> choices) { foreach(var choice in choices) { if(choice.Cursor!=null)return choice.Cursor;var nested=NestedToken(choice.Children);if(nested!=null)return nested; }return null; }
        var transportToken=continuationToken??NestedToken(selections);
        if(transportToken!=null) {
            var continuation=GetCursor(transportToken,run);
            if(continuation.CachedResult!=null) {
                var replay=(JObject)continuation.CachedResult.DeepClone();var status=(string?)replay["status"]??"ok";
                if(JsonDepth(replay)>24)publish(PacketPage(replay.ToString(Formatting.None),0,(JObject)replay["scope"]!,run,status));
                else publish(new QueryOutcome { Status=status,Data=replay,Issues=EnvelopeIssues(status) });yield break;
            }
        }
        if(continuationToken!=null) {
            var continuation=GetCursor(continuationToken,run);
            if(continuation.Packet!=null) { publish(PacketPage(continuation.Packet,continuation.Offset,continuation.Scope,run,continuation.PacketStatus));yield break; }
        }
        var rootCursor=transportToken;
        Snapshot? priorRoot=null;
        if(rootCursor!=null) {
            var retained=GetCursor(rootCursor,run).Source??throw ReadContext.Error("cursor_ancestor_mismatch","Cursor has no captured source.");
            priorRoot=lease.Sources.TryGetValue("root:"+retained.RootId,out var retainedRoot)?retainedRoot:throw ReadContext.Error("cursor_expired","Ancestor snapshot has expired.");
        }
        var resolved=priorRoot==null?(JObject)scope.DeepClone():(JObject)priorRoot.Locator.Scope.DeepClone();
        if(priorRoot==null && (resolved["map"]==null || (string?)resolved["map"]=="current")) {
            var currentMap=source.GameField("currentMapId") as string;
            if(currentMap==null && kind is not ("game" or "defs"))throw ReadContext.Error("object_unavailable","No current map is loaded.");
            resolved["map"]=currentMap;
        }
        resolved["placement"]??="any";resolved["map_visibility"]??="all";
        if((string?)resolved["placement"] is not ("any" or "spawned" or "held") || (string?)resolved["map_visibility"] is not ("all" or "visible"))throw ReadContext.Error("invalid_arguments","Invalid scope placement or map_visibility.");
        if(priorRoot==null && kind is not ("game" or "defs") && (string?)resolved["map"] is not ("all" or "all_loaded") && !source.Maps().Any(m=>m.Id==(string?)resolved["map"]))throw ReadContext.Error("object_unavailable","Selected map is not loaded.");
        resolved["applied"]=new JArray(kind is "game" or "defs"?Array.Empty<string>():kind is "things" or "pawns" or "pawn" or "colonists"?new[]{"map","placement","map_visibility"}:new[]{"map"});
        resolved["not_applicable"]=new JArray(kind is "game" or "defs"?new[]{"map","placement","map_visibility"}:kind is "things" or "pawns" or "pawn" or "colonists"?Array.Empty<string>():new[]{"placement","map_visibility"});
        if(kind=="cells") { resolved["applied"]=new JArray("map","map_visibility");resolved["not_applicable"]=new JArray("placement"); }
        var where=target["where"] as JObject??new JObject();var predicate=where.Count==0?null:conditions.Compile(type,Predicate(type,where));
        resolved["target_kind"]=kind;resolved["where_signature"]=GameQueryEngine.Hash(where.ToString(Formatting.None));
        if(kind=="cells") {
            if((string?)resolved["map"] is "all" or "all_loaded")throw ReadContext.Error("invalid_arguments","cells needs one selected map.");
            JArray Coordinates(string name) {
                var condition=where[name] as JObject;
                var values=condition?["in"] as JArray??(condition?["eq"]?.Type==JTokenType.Integer?new JArray(condition["eq"]!):null);
                if(values==null || values.Count==0 || values.Any(v=>v.Type!=JTokenType.Integer || (long)v<int.MinValue || (long)v>int.MaxValue))throw ReadContext.Error("invalid_arguments","cells requires explicit finite x/z eq or in coordinates.");
                return new JArray(values.Distinct(new JTokenEqualityComparer()));
            }
            resolved["cell_x"]=Coordinates("x");resolved["cell_z"]=Coordinates("z");
        }
        var rootLocator=new Locator { Root=kind,Type=type,Scope=resolved };
        run.Context.ObjectScopeMap=(string?)resolved["map"];
        Snapshot snapshot;
        if(priorRoot!=null) { snapshot=priorRoot;run.Root=snapshot; }
        else {
                snapshot=new Snapshot { Type=type,Locator=rootLocator,Capture=ObjectReadContract.Capture(type),Signature=signature,Where=where,Predicate=predicate,CapturedTick=tick() };
                snapshot.RootId=snapshot.Id;run.Root=snapshot;
                var sourceKey="root:"+snapshot.Id;
                lease.Sources[sourceKey]=snapshot;
                lease.SourceIds[snapshot.Id]=snapshot;
                ResolveRoles(input["context"],run,resolved,selections,where,type);
                foreach(var step in Capture(snapshot,ReadRoot(rootLocator,run),run))yield return step;
        }
        if(priorRoot!=null)ResolveRoles(input["context"],run,resolved,selections,where,type);
        var continued=transportToken==null?null:GetCursor(transportToken,run);
        var traversal=continued?.Traversal;
        if(traversal==null) {
            var selectedSource=continued?.Source??snapshot;
            traversal=CreateTraversal(selectedSource,continued?.Offset??0,selectedSource.Selection?.Size??size,selections,run);
        }
        foreach(var step in Traverse(traversal,run,publish,continued))yield return step;
    }
    private Lease GetLease(string id,SessionDescriptor session) {
        foreach(var stale in leases.Where(p=>p.Value.Expires<DateTime.UtcNow).Select(p=>p.Key).ToArray())Release(stale);
        if(leases.TryGetValue(id,out var lease)) {
            if(lease.Epoch!=session.LoadEpoch || lease.Colony!=session.ColonyId) { Release(id);throw ReadContext.Error("cursor_stale_load","Read lease belongs to another colony or load."); }
            return lease;
        }
        lease=new Lease { Epoch=session.LoadEpoch,Colony=session.ColonyId,Expires=DateTime.UtcNow.AddMinutes(5) };leases[id]=lease;return lease;
    }
    private Continuation GetCursor(string token,Run run) {
        if(!cursors.TryGetValue(token,out var cursor) || cursor.Lease!=run.LeaseId)throw ReadContext.Error("cursor_expired","Cursor has expired or belongs to another consultation.");
        if(cursor.Epoch!=run.Session.LoadEpoch)throw ReadContext.Error("cursor_stale_load","Cursor belongs to another load.");
        if(cursor.Signature!=run.Signature)throw ReadContext.Error("cursor_selection_mismatch","Cursor binds its target, selected columns, predicates, context and ancestors.");
        return cursor;
    }
    private QueryOutcome PacketPage(string text,int offset,JObject scope,Run run,string originalStatus="ok") {
        var count=Math.Min(PacketCharacters,text.Length-offset);var next=offset+count;string? cursor=null;
        if(next<text.Length && count>0 && char.IsHighSurrogate(text[next-1]) && char.IsLowSurrogate(text[next])) { count--;next--; }
        if(next<text.Length) { if(offset==0)Retain(run,text.Length*2L);cursor=Guid.NewGuid().ToString("N");cursors[cursor]=new Continuation { Lease=run.LeaseId,Epoch=run.Session.LoadEpoch,Signature=run.Signature,Packet=text,PacketStatus=originalStatus,Offset=next,Scope=(JObject)scope.DeepClone(),Tick=run.Tick(),Request=(JObject)run.Request.DeepClone() }; }
        var status=originalStatus;
        return new QueryOutcome { Status=status,Issues=EnvelopeIssues(status),Data=new JObject { ["version"]=ObjectReadContract.Version,["scope"]=scope.DeepClone(),["fragment"]=new JObject { ["encoding"]="json",["text"]=text.Substring(offset,count),["offset"]=offset,["total_chars"]=text.Length,["page"]=new JObject { ["has_more"]=cursor!=null,["cursor"]=cursor } } } };
    }
    private JObject TextWindow(string text,int offset,JObject template,Run run,string? valueId=null)=>
        TextWindowWithinBudget(text,offset,template,valueId,window=>DeliverySize(TextEnvelope(template,window)));
    private JObject TextWindowWithinBudget(string text,int offset,JObject template,string? valueId,Func<JObject,int> measure) {
        valueId??=Guid.NewGuid().ToString("N");
        var count=TextBoundary(text,offset,Math.Min(DeliveryBytes,text.Length-offset));
        JObject Window(int width) {
            var end=offset+width;var more=end<text.Length;
            var result=new JObject { ["value_snapshot_id"]=valueId,["text"]=text.Substring(offset,width),["offset"]=offset,["returned_chars"]=width,["total_chars"]=text.Length,["complete"]=offset==0 && !more,["end_of_value"]=!more,["has_more"]=more,["unit"]="UTF16_code_units" };
            var slot=template.Descendants().OfType<JObject>().Single(o=>(bool?)o["__text_window_slot__"]==true);
            foreach(var name in new[]{"encoding","json_type"})if(slot[name]!=null)result[name]=slot[name]!.DeepClone();
            return result;
        }
        while(count>0 && measure(Window(count))>DeliveryBytes)count=TextBoundary(text,offset,count/2);
        if(count==0 && offset<text.Length)throw ReadContext.Error("projection_block_too_large","Text scope exceeds the measured delivery window.");
        return Window(count);
    }
    private static int TextBoundary(string text,int offset,int count) {
        var next=offset+count;
        return next<text.Length && count>0 && char.IsHighSurrogate(text[next-1]) && char.IsLowSurrogate(text[next])?count-1:count;
    }
    private static JObject TextEnvelope(JObject template,JObject window) {
        var result=(JObject)template.DeepClone();
        var slot=result.Descendants().OfType<JObject>().Single(o=>(bool?)o["__text_window_slot__"]==true);
        slot.Replace(window.DeepClone());
        result["delivery"]=new JObject { ["kind"]="value_window",["complete"]=(bool)window["complete"]!,["source_page"]=template["page"]?.DeepClone(),["meaning"]="Another lossless window of the same sampled value; no new game getter." };
        result["page"]=new JObject { ["has_more"]=(bool?)window["has_more"]==true,["cursor"]=window["cursor"]?.DeepClone(),["returned_count"]=((JArray)result["rows"]!).Count };
        return result;
    }
    private static object[] TokenPath(JToken token,JToken root) {
        var path=new List<object>();var current=token;
        while(!ReferenceEquals(current,root)) {
            if(current.Parent is JProperty property) { path.Add(property.Name);current=property.Parent!; }
            else if(current.Parent is JArray array) { path.Add(array.IndexOf(current));current=array; }
            else throw ReadContext.Error("object_read_failed","Invalid DTO text ancestor.");
        }
        path.Reverse();return path.ToArray();
    }
    private List<Selection> Compile(string type,JToken? token,int depth,int[]? ancestry=null) {
        if(depth>12 || token is not JArray array)throw ReadContext.Error("invalid_arguments","select/children must be an array within depth12.");
        var result=new List<Selection>();var index=0;
        foreach(var item in array) {
            var inputIndex=index++;
            try {
            if(item is not JObject obj)throw ReadContext.Error("invalid_arguments","Each selection is an object.");
            ObjectReadContract.Keys(obj,"field","where","columns","children","page");
            if(obj["where"]!=null && obj["where"] is not JObject || obj["page"]!=null && obj["page"] is not JObject || obj["args"]!=null && obj["args"] is not JObject || obj["columns"]!=null && obj["columns"] is not JArray || obj["children"]!=null && obj["children"] is not JArray)throw ReadContext.Error("invalid_arguments","Selection where/page/args must be objects; columns/children must be arrays.");
            var fieldName=(string?)obj["field"]??throw ReadContext.Error("invalid_arguments","Selection requires field.");
            var selected=new Selection { Field=fieldName,Index=inputIndex,Size=ObjectReadContract.Size(obj["page"] as JObject),Cursor=(string?)obj["page"]?["cursor"],InputPath=(ancestry??Array.Empty<int>()).Concat(new[]{inputIndex}).ToArray(),Self=fieldName=="self" };
            if(fieldName.Contains('.'))throw ReadContext.Error("invalid_arguments","Read.field is a direct registered field; relationships use children.");
            var definition=selected.Self?new FieldType { Name="self",ResolvedType=registry.Type(type) }:registry.Field(type,fieldName);
            if(!selected.Self)selected.Path.Add(definition);
            if(PublicFields.ValueType(definition) is ScalarGraphType)throw ReadContext.Error("invalid_arguments",type+"."+fieldName+" is a value; select it in columns on self or its parent relationship.");
            var owner=PublicFields.ValueType(definition).Name;
            selected.Definition=definition;selected.Type=owner;
            // Object reads expose a lazy StatDef source; the legacy query's
            // named-list argument constraints remain on the legacy protocol.
            var args=obj["args"] as JObject;
            selected.Where=obj["where"] as JObject??new JObject();
            if(selected.Definition.Name=="stats" && args==null) {
                var clause=selected.Where["def_name"] as JObject;var names=clause?["in"] as JArray??(clause?["eq"]?.Type==JTokenType.String?new JArray(clause["eq"]):null);
                if(names?.Count==0)selected.ExplicitEmpty=true;
                if(names!=null && names.Any(n=>n.Type!=JTokenType.String))throw ReadContext.Error("invalid_arguments","Stat definition names must be strings.");
                args=names==null?new JObject():new JObject { ["def_names"]=names.DeepClone() };
            }
            selected.Args=selected.Definition.Name=="stats"?new Dictionary<string,object?>():registry.Arguments(selected.Definition,args,fieldName);
            if(selected.Definition.Name=="stats" && args?["def_names"] is JArray statNames)selected.Args["defNames"]=statNames.Values<string>().ToArray();
            if(selected.Definition.Arguments?.Any(a=>a.Name=="visibleOnly")==true && (args==null || args["visible_only"]==null))selected.Args["visibleOnly"]=false;
            if(registry.Type(owner) is IComplexGraphType) {
                selected.Predicate=selected.Where.Count==0?null:conditions.Compile(owner,Predicate(owner,selected.Where));
                if(obj["columns"] is JArray columns) {
                    if(columns.Any(c=>c.Type!=JTokenType.String))throw ReadContext.Error("invalid_arguments","columns contains direct registered scalar names.");
                    selected.Columns=columns.Values<string>().Select(c=>c!).Distinct(StringComparer.Ordinal).ToArray();
                    foreach(var column in selected.Columns)if(PublicFields.ValueType(registry.Field(owner,column)) is not ScalarGraphType)throw ReadContext.Error("invalid_arguments","Relationships use children, not columns: "+column);
                } else if(obj["columns"]!=null)throw ReadContext.Error("invalid_arguments","columns must be an array.");
                else selected.Columns=Array.Empty<string>();
                if(obj["children"]!=null)selected.Children=Compile(owner,obj["children"],depth+1,selected.InputPath);
            }
            result.Add(selected);
            } catch(ExecutionError error) { result.Add(new Selection { Field=(string?)(item as JObject)?["field"]??"",Index=inputIndex,Error=error }); }
        }
        return result;
    }
    private IEnumerable<object> ReadRoot(Locator locator,Run run) {
        var map=(string?)locator.Scope["map"];var allMaps=map is "all" or "all_loaded";
        var args=new Dictionary<string,object?> { ["mapId"]=allMaps?null:map,["includeHeld"]=(string?)locator.Scope["placement"]!="spawned",["visibleOnly"]=(string?)locator.Scope["map_visibility"]=="visible",["placementOnlyHeld"]=(string?)locator.Scope["placement"]=="held" };
        if(locator.Root=="game")return new object[]{run.Context};
        if(locator.Root=="colony")return new object[]{run.Context};
        if(locator.Root=="maps")return run.Source.Maps().Where(m=>allMaps || m.Id==map).Cast<object>();
        if(locator.Root=="colonists")return (run.Source is IObjectQueryData native?native.QueryObjectColonists(args,run.Context):run.Source.Colonists().Where(p=>allMaps || p.MapId==map)).Cast<object>();
        if(locator.Root is "pawn" or "pawns") {
            var pawns=run.Source is IObjectQueryData native?native.QueryObjectPawns(args,run.Context):run.Source is IQueryData query?query.QueryPawns(args,run.Context):run.Source.Colonists().Where(p=>allMaps || p.MapId==map);
            return pawns.Where(p=>(string?)locator.Scope["placement"]!="held" || run.Context.ReadNode(new GameNode("Thing",p.Id,p,p.Handle),"Spawned") is false).Cast<object>();
        }
        if(locator.Root=="things") {
            if(run.Source is not IQueryData query)throw ReadContext.Error("query_source_unavailable","Native things enumeration is unavailable.");
            return query.QueryThings(args,run.Context).Where(n=>(string?)locator.Scope["placement"]!="held" || run.Context.ReadNode(n,"Spawned") is false).Cast<object>();
        }
        if(locator.Root=="defs")return ((IEnumerable)run.Context.NativeRoot("query_defs",new Dictionary<string,object?>())!).Cast<object>();
        if(locator.Root=="cells")return ((IEnumerable)run.Context.NativeRoot("object_cells",new Dictionary<string,object?> { ["mapId"]=map,["xs"]=locator.Scope["cell_x"]!.Values<int>().ToArray(),["zs"]=locator.Scope["cell_z"]!.Values<int>().ToArray(),["visibleOnly"]=args["visibleOnly"] })!).Cast<object>();
        if(locator.Root=="situation")return new[]{run.Context.NativeRoot("situation",args)!};
        throw ReadContext.Error("unknown_target","Unsupported source.");
    }
    // GameNode.Key is unique only within its owner (every pawn's health is "pawn/health"). Member identity,
    // child-source keys, lookups and public anchors derive from this key, so it must be owner-qualified.
    private static string Key(object value)=>value is GameEntity entity?entity.Id:value is GameNode node?OwnedKey(node):"game";
    private static string OwnedKey(GameNode node) {
        var owner=node.Owner.Id;
        return node.Key==owner || node.Key.StartsWith(owner+"/",StringComparison.Ordinal)?node.Key:owner+"/"+node.Key;
    }
    private static object? Identity(object value)=>value is GameNode node?node.RecordIdentity:null;
    private IEnumerable<object> ResolveCollection(Locator locator,Run run) {
        if(locator.Parent==null)return ReadRoot(locator,run);
        var parent=Find(locator.Parent,run);if(parent==null)throw ReadContext.Error("object_unavailable","Ancestor is no longer available.");
        var raw=ReadSelection(parent,locator.Parent.Type,locator.Relation!,run);
        return raw==null?Array.Empty<object>():raw is IEnumerable sequence && raw is not string?sequence.Cast<object>():new[]{raw};
    }
    private object? Find(Locator locator,Run run) {
        var profiled=ObjectReadProfile.Start();try { return FindCore(locator,run); } finally { ObjectReadProfile.Stop(ObjectReadProfile.Part.Find,profiled); }
    }
    private object? FindCore(Locator locator,Run run) {
        var tick=run.Tick();
        if(run.LookupTick!=tick || run.LookupFrame!=run.Context.ObjectFrameVersion) {
            if(run.LookupTick>=0)run.Context.ResetObjectReadCache();
            run.Lookups.Clear();run.LookupSources.Clear();run.LookupIdentities.Clear();run.LookupTick=tick;run.LookupFrame=run.Context.ObjectFrameVersion;
        }
        var cacheKey=locator.Root+":"+locator.Type+":"+locator.SourceId+":"+locator.Key+":"+(locator.Parent?.Key??"")+":"+(locator.Relation?.Field??"");
        if(run.Lookups.TryGetValue(cacheKey,out var cached))return cached;
        var sourceKey=LookupKey(locator);
        if(!run.LookupSources.TryGetValue(sourceKey,out var source)) {
            var rebuildStarted=ObjectReadProfile.Start();if(ObjectReadProfile.Enabled)ObjectReadProfile.Rebuilds++;
            source=new Dictionary<string,object>(StringComparer.Ordinal);
            var identities=new Dictionary<object,object>(new IdentityComparer());
            foreach(var value in ResolveCollection(locator,run)) {
                run.CandidateReads++;if(ObjectReadProfile.Enabled)ObjectReadProfile.RebuildMembers++;var nativeKey=Key(value);source[nativeKey]=source.ContainsKey(nativeKey)?null!:value;
                if(Identity(value) is object identity)identities[identity]=value;
            }
            run.LookupSources[sourceKey]=source;
            run.LookupIdentities[sourceKey]=identities;ObjectReadProfile.Stop(ObjectReadProfile.Part.FindRebuild,rebuildStarted);
        }
        Member? captured=null;
        if(locator.SourceId.Length>0 && run.Lease.SourceIds.TryGetValue(locator.SourceId,out var snapshot))snapshot.MemberIndex.TryGetValue(locator.Key,out captured);
        object? found;
        if(captured?.Identity!=null) {
            var identity=captured.Identity.Target;
            found=identity!=null && run.LookupIdentities[sourceKey].TryGetValue(identity,out var same)?same:null;
        } else found=source.TryGetValue(captured?.NativeKey??locator.Key,out var match)?match:null;
        run.Lookups[cacheKey]=found;return found;
    }
    private static string LookupKey(Locator locator)=>locator.Root+":"+locator.Type+":"+locator.Scope.ToString(Formatting.None)+":"+(locator.Parent==null?"":LookupKey(locator.Parent)+":"+locator.Parent.SourceId+":"+locator.Parent.Key)+":"+(locator.Relation==null?"":locator.Relation.Index+":"+locator.Relation.Field+":"+JObject.FromObject(locator.Relation.Args).ToString(Formatting.None));
    private object? ReadSelection(object parent,string type,Selection selection,Run run) {
        object? value=parent;var owner=type;
        foreach(var field in selection.Path) {
            if(value==null)return null;
            run.GetterReads++;value=registry.Read(owner,value,field,field==selection.Definition?selection.Args:new Dictionary<string,object?>(),run.Context);owner=PublicFields.ValueType(field).Name;
        }
        return value;
    }
    private IEnumerable<int> Capture(Snapshot snapshot,IEnumerable<object> values,Run run) {
        var seen=new HashSet<string>(StringComparer.Ordinal);
        var sourceKey=LookupKey(snapshot.Locator);var localSource=new Dictionary<string,object>(StringComparer.Ordinal);
        var localIdentities=new Dictionary<object,object>(new IdentityComparer());
        var completed=true;
        using(var iterator=values.GetEnumerator())while(true) {
            var more=false;object? value=null;
            try { more=iterator.MoveNext();if(more)value=iterator.Current; }
            catch(Exception error) when(ReadContext.Recoverable(error)) {
                if(error is ExecutionError fatal && fatal.Code is "world_changed" or "wrong_thread")throw;
                completed=false;Issue(run,error,"source/"+snapshot.Type);snapshot.SourceIssues.Add(run.Issues.Last());
            }
            if(!more)break;
            run.Context.Check();run.CandidateReads++;var key=Key(value);
            if(snapshot.Capture!="local_identity" && !seen.Add(key))continue;
            var identity=Identity(value!);
            var member=new Member { Key=snapshot.Capture=="local_identity"?snapshot.Id+":"+snapshot.Members.Count:key,NativeKey=key,Identity=identity==null?null:new WeakReference(identity) };
            snapshot.Members.Add(member);
            snapshot.MemberIndex[member.Key]=member;
            Retain(run,member.Key.Length*2L+64);
            localSource[key]=localSource.ContainsKey(key)?null!:value!;
            if(identity!=null)localIdentities[identity]=value!;
            yield return 0;
        }
        snapshot.Complete=completed;
        snapshot.CaptureFinishedTick=run.Tick();
        run.LookupSources[sourceKey]=localSource;
        run.LookupIdentities[sourceKey]=localIdentities;
    }
    private bool? Match(SelectionConditions.Predicate? predicate,object value,Run run,string path) {
        if(predicate==null)return true;
        try { var match=predicate(value,run.Context);if(match==null)Issue(run,ReadContext.Error("predicate_unavailable","Candidate membership is unknown."),path);return match; }
        catch(Exception error) when(ReadContext.Recoverable(error)) { Issue(run,error,path);return null; }
    }
    private static JToken ColumnValue(object? raw,FieldType field) {
        if(raw==null)return JValue.CreateNull();
        var scalar=(ScalarGraphType)PublicFields.ValueType(field);
        if(PublicFields.IsList(field))return new JArray(((IEnumerable)raw).Cast<object?>().Select(value=>value==null?JValue.CreateNull():JToken.FromObject(scalar.Serialize(value)!)));
        return JToken.FromObject(scalar.Serialize(raw)!);
    }
    private HashSet<string> RequiredRoles(string type,JObject where,List<Selection> selections) {
        var result=new HashSet<string>(StringComparer.Ordinal);
        void Field(FieldType field) { if(field.Metadata.TryGetValue("object_roles",out var roles))foreach(var role in (string[])roles!)result.Add(role); }
        void Predicate(string owner,JToken rule) {
            if(rule is JArray clauses) { foreach(var clause in clauses)Predicate(owner,clause);return; }
            if(rule is not JObject obj)return;
            if(obj["fields"] is JObject fields && registry.Type(owner) is IComplexGraphType)foreach(var selected in fields.Properties()) {
                var field=registry.Fields(owner).Values.FirstOrDefault(f=>f.Name==selected.Name);
                if(field==null)continue;Field(field);Predicate(PublicFields.ValueType(field).Name,selected.Value);
            }
            foreach(var clause in obj.Properties().Where(p=>p.Name is "all" or "any" or "not" or "where" or "some" or "every" or "none" or "match"))Predicate(owner,clause.Value);
        }
        if(where.Count>0)Predicate(type,this.Predicate(type,where));
        void Selected(IEnumerable<Selection> choices) {
            foreach(var selection in choices) {
                if(selection.Error!=null)continue;
                if(selection.Columns.Length>0 || selection.Predicate!=null || !PublicFields.IsScalar(selection.Definition))Field(selection.Definition);
                if(registry.Type(selection.Type) is IComplexGraphType) {
                    foreach(var column in selection.Columns)Field(registry.Field(selection.Type,column));
                    if(selection.Where.Count>0)Predicate(selection.Type,this.Predicate(selection.Type,selection.Where));
                }
                Selected(selection.Children);
            }
        }
        Selected(selections);return result;
    }
    private void ResolveRoles(JToken? token,Run run,JObject scope,List<Selection> selected,JObject where,string type) {
        if(token==null)return;var roles=token as JObject??throw ReadContext.Error("invalid_arguments","context must be an object.");ObjectReadContract.Keys(roles,"actor","recipient");
        var required=RequiredRoles(type,where,selected);
        var pinned=run.Lease.Roles.TryGetValue(run.Root.Id,out var storedRoles)?storedRoles:new JObject();
        foreach(var role in roles.Properties()) {
            if(role.Value.Type==JTokenType.Null)continue;
            var reference=role.Value as JObject??throw ReadContext.Error("invalid_arguments","Role reference must be an object.");ObjectReadContract.Keys(reference,"id","name","map");
            if((reference["id"]==null)==(reference["name"]==null))throw ReadContext.Error("invalid_arguments","Role requires exactly one id or name.");
            if(reference.Properties().Any(p=>p.Value.Type!=JTokenType.String || string.IsNullOrWhiteSpace((string?)p.Value)))throw ReadContext.Error("invalid_arguments","Role id/name/map must be nonempty strings.");
            if(!required.Contains(role.Name))continue;
            var effective=pinned[role.Name] as JObject??reference;
            var locator=new Locator { Root="pawns",Type="Pawn",Scope=new JObject { ["map"]=scope["map"]?.DeepClone(),["placement"]="any",["map_visibility"]="all" } };if(effective["map"]!=null && (string?)effective["map"]!="current")locator.Scope["map"]=effective["map"]!.DeepClone();
            var matches=ReadRoot(locator,run).OfType<GameEntity>().Where(p=>effective["id"]!=null?p.Id==(string?)effective["id"]:(string?)run.Context.Read("Pawn",p,"name")== (string?)effective["name"]).ToArray();
            if(matches.Length!=1) { Issue(run,ReadContext.Error("context_unavailable","Role reference is missing or ambiguous: "+role.Name),"context/"+role.Name);continue; }
            pinned[role.Name]=new JObject { ["id"]=matches[0].Id,["map"]=locator.Scope["map"]?.DeepClone() };
            if(role.Name=="actor")run.Context.ObjectActor=matches[0];else run.Context.ObjectRecipient=matches[0];
        }
        run.Lease.Roles[run.Root.Id]=pinned;
    }
    private static JToken WithoutCursor(JToken token) {
        if(token is JObject obj)return new JObject(obj.Properties().Where(p=>p.Name!="cursor" && !(p.Name=="page" && p.Value is JObject page && page.Properties().All(x=>x.Name=="cursor" || x.Name=="size" && x.Value.Type==JTokenType.Integer && (int)x.Value==20))).OrderBy(p=>p.Name,StringComparer.Ordinal).Select(p=>new JProperty(p.Name,WithoutCursor(p.Value))));
        if(token is JArray array)return new JArray(array.Select(WithoutCursor));return token.DeepClone();
    }
    private static void Issue(Run run,Exception error,string path)=>run.Issues.Add(new QueryIssue { Code=(error as ExecutionError)?.Code??"field_unavailable",Message=error.Message,Path=new JArray(path) });
    private JObject Predicate(string type,JObject rule)=>registry.Predicate(type,ObjectReadPredicates.Normalize(registry,type,rule));
    private static void Rule(Run run,string owner,string column,FieldType field) { var key=owner+"."+column;var rule=run.Root.Semantics[key] as JObject??ObjectReadSemantics.Describe(owner,field);if(rule.Count>0) { run.Root.Semantics[key]=rule;run.SemanticRules[key]=rule.DeepClone(); } }
    private static QueryIssue[] EnvelopeIssues(string status)=>status=="ok"?Array.Empty<QueryIssue>():new[] { new QueryIssue { Code="object_read_partial",Message="Some requested facts or membership are unavailable. Full issues and field/source coverage are retained in the lossless object result.",Path=new JArray() } };
    private static int JsonDepth(JToken token)=>token is JContainer container && container.HasValues?1+container.Children().Max(JsonDepth):0;
    private static bool Incomplete(JToken value) {
        if(value is JArray array)return array.Any(Incomplete);
        if(value is not JObject obj)return false;
        if(obj["status"]?.Type==JTokenType.String && (string?)obj["status"]=="unavailable")return true;
        if(obj["coverage"] is JObject coverage && ((bool?)coverage["source_complete"]==false || (bool?)coverage["predicate_complete_for_visited"]==false || coverage["predicate_complete_for_visited"]==null && (bool?)coverage["predicate_complete"]==false))return true;
        if(obj["values"] is JObject fields && fields.Properties().Any(p=>p.Value is JObject state && (string?)state["status"]=="unavailable"))return true;
        return obj["rows"]!=null && Incomplete(obj["rows"]!) || obj["readings"]!=null && Incomplete(obj["readings"]!);
    }
    private void Retain(Run run,long bytes) {
        run.Lease.RetainedBytes+=bytes;
        if(run.Lease.RetainedBytes>64L*1024*1024) {
            Release(run.LeaseId);
            throw ReadContext.Error("snapshot_memory_limit","Exceptional read-lease memory limit reached; no truncated snapshot or completeness is returned.");
        }
    }
}
