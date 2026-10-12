using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GraphQL;
using GraphQL.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>Resumable selection tree. Frames hold keys, positions and DTOs, never native enumerators.</summary>
internal sealed partial class ObjectReadEngine
{
    private const int DeliveryBytes=80000;
    private const int TraversalSteps=1024;
    private sealed class ReadTraversal {
        internal string Id=Guid.NewGuid().ToString("N");internal Snapshot Source=null!;
        internal JObject Projection=new();internal readonly Stack<ReadFrame> Frames=new();
        internal readonly Queue<ReadFrame> DeferredSources=new();
        internal readonly Queue<TextDelivery> TextValues=new();
        internal readonly List<ReadFrame> ClosedSources=new();internal bool Finalized;
        internal readonly List<object[]> DerivedPaths=new();
        internal int LeafOffset;internal readonly Queue<JObject> PendingDelivery=new();
        internal readonly HashSet<string> SeenRules=new(StringComparer.Ordinal),SeenIssues=new(StringComparer.Ordinal);
        internal readonly Dictionary<string,ExplanationSource> ExplanationSources=new(StringComparer.Ordinal);
        internal bool HadIssues;
        internal string? ProbeKey;internal int ProbeDelta;
    }
    private sealed class TextDelivery { internal JToken Value=null!,Original=null!;internal string Id=Guid.NewGuid().ToString("N"),Name="";internal int Offset;internal JObject Template=null!,Destination=null!; }
    private sealed class ExplanationSource { internal Snapshot Source=null!;internal JObject Node=null!;internal object[] Path=Array.Empty<object>();internal int ExplainedIssueCount; }
    private sealed class ReadFrame {
        internal Snapshot Source=null!;internal Member? Member;internal JObject Node=null!;
        internal object[] Path=Array.Empty<object>();internal List<Selection> Choices=new();
        internal int Start,Index,Size,Returned,ColumnIndex,ChildIndex;internal bool Opened;
        internal long FinishedTick;
        internal Selection? Record;
    }
    private ReadTraversal CreateTraversal(Snapshot source,int offset,int size,List<Selection> choices,Run run) {
        var work=new ReadTraversal { Source=source,Projection=new JObject {
            ["version"]=ObjectReadContract.Version,["scope"]=PublishedScope(run.Root.Locator.Scope),["rows"]=new JArray(),["issues"]=new JArray(),
            ["sample"]=new JObject { ["load_epoch"]=run.Session.LoadEpoch,["started_tick"]=run.Started,["atomic"]=false }
        } };
        work.Frames.Push(new ReadFrame { Source=source,Node=work.Projection,Start=offset,Index=offset,Size=size,Choices=choices });
        return work;
    }
    private IEnumerable<int> Traverse(ReadTraversal work,Run run,Action<QueryOutcome> publish,Continuation? resumed) {
        var output=new JObject { ["version"]=ObjectReadContract.Version,["scope"]=PublishedScope(run.Root.Locator.Scope),["rows"]=new JArray(),["issues"]=new JArray(),["semantic_rules"]=new JObject() };
        var start=work.LeafOffset;var steps=0;var paused=false;var nextToken=Guid.NewGuid().ToString("N");
        var deliveredTexts=new HashSet<string>(StringComparer.Ordinal);
        // Pending atoms merge in place; the tracked compact size makes the budget probe O(atom), and a refused atom is undone exactly.
        var tracked=new ObjectReadPages.TrackedMerge(output);
        bool Drain() {
            while(work.PendingDelivery.Count>0) {
                var drainStarted=ObjectReadProfile.Start();
                string? before=null;JObject? shadow=null;
                if(ObjectReadProfile.VerifySizes) { before=output.ToString(Formatting.None);shadow=(JObject)output.DeepClone();ObjectReadPages.Merge(shadow,work.PendingDelivery.Peek()); }
                tracked.Apply(output,work.PendingDelivery.Peek());
                if(shadow!=null && shadow.ToString(Formatting.None)!=output.ToString(Formatting.None))throw new InvalidOperationException("Tracked merge diverged from Merge.");ObjectReadProfile.Stop(ObjectReadProfile.Part.Merge,drainStarted);
                var measureStarted=ObjectReadProfile.Start();var probeSize=ProbeSize(output,tracked.Size,work,run,start,steps,nextToken);ObjectReadProfile.Stop(ObjectReadProfile.Part.Measure,measureStarted);
                if(ObjectReadProfile.VerifySizes) {
                    var actual=DeliverySize(DeliveryEnvelope(output,work,run,start,steps,false,nextToken,true));
                    if(actual!=probeSize || DeliverySize(output)!=tracked.Size)throw new InvalidOperationException("Tracked delivery size diverged: "+probeSize+" vs "+actual+", body "+tracked.Size+" vs "+DeliverySize(output));
                }
                ObjectReadProfile.Stop(ObjectReadProfile.Part.Drain,drainStarted);
                if(ObjectReadProfile.Enabled)ObjectReadProfile.MeasuredBytes+=probeSize;
                if(probeSize>DeliveryBytes) {
                    tracked.Rollback();
                    if(before!=null && before!=output.ToString(Formatting.None))throw new InvalidOperationException("Tracked merge rollback did not restore the delivery.");
                    if(work.LeafOffset==start)throw ReadContext.Error("projection_block_too_large","One delivery atom and its required scope exceed the window.");
                    paused=true;return false;
                }
                tracked.Commit();var accepted=work.PendingDelivery.Dequeue();work.LeafOffset++;
                foreach(var value in accepted.Descendants().OfType<JObject>())
                    if(value["value_snapshot_id"]?.Type==JTokenType.String)deliveredTexts.Add((string)value["value_snapshot_id"]!);
            }
            return true;
        }
        bool Emit(object[] path,JToken? replacement=null) {
            var explained=ObjectReadProfile.Start();QueueExplanations(work,run);ObjectReadProfile.Stop(ObjectReadProfile.Part.Explanations,explained);
            var branched=ObjectReadProfile.Start();var atom=ObjectReadPages.Branch(work.Projection,path,replacement);ObjectReadProfile.Stop(ObjectReadProfile.Part.Branch,branched);
            work.PendingDelivery.Enqueue(atom);return Drain();
        }
        Drain();
        while(!paused && (work.Frames.Count>0 || work.DeferredSources.Count>0 || work.TextValues.Count>0) && steps++<TraversalSteps) {
            if(work.Frames.Count==0) {
                if(work.DeferredSources.Count>0)work.Frames.Push(work.DeferredSources.Dequeue());
                else {
                    var text=work.TextValues.Peek();
                    if(!deliveredTexts.Add(text.Id)) { paused=true;break; }
                    work.TextValues.Dequeue();
                    var window=ValueWindow(text.Value,text.Offset,text.Template,run,text.Id,work);
                    text.Offset+=(int)(window["returned_chars"]??window["returned_items"])!;
                    var branch=TextBranch(text.Template,window);
                    if((bool)window["has_more"]!)work.TextValues.Enqueue(text);
                    else text.Destination[text.Name]=text.Original.DeepClone();
                    work.PendingDelivery.Enqueue(branch);Drain();yield return 0;continue;
                }
            }
            run.Context.Check();var frame=work.Frames.Peek();if(ObjectReadProfile.Enabled)ObjectReadProfile.Steps++;
            if(frame.Member==null) {
                if(!frame.Opened) { frame.Opened=true;SourceMetadata(frame,run,false,work);Emit(frame.Path,MetadataOnly(frame.Node));yield return 0;continue; }
                if(frame.Index>=frame.Source.Members.Count || frame.Returned>=frame.Size) {
                    SourceMetadata(frame,run,true,work);work.Frames.Pop();Emit(frame.Path,MetadataOnly(frame.Node));
                    if(frame.Index<frame.Source.Members.Count) {
                        frame.Start=frame.Index;frame.Returned=0;frame.Opened=false;work.DeferredSources.Enqueue(frame);paused=true;
                    } else { frame.FinishedTick=run.Tick();work.ClosedSources.Add(frame); }
                    yield return 0;continue;
                }
                var member=frame.Source.Members[frame.Index++];
                if(member.State=="pending") {
                    object? native=null;try { native=Find(MemberLocator(frame.Source,member),run); }catch(Exception error) when(ReadContext.Recoverable(error)) { Issue(run,error,member.Key); }
                    if(native==null) { member.State="unavailable";Issue(run,ReadContext.Error("source_changed","The captured member is no longer in this source or cannot be validated."),member.Key); }
                    else { var match=Match(frame.Source.Predicate,native,run,member.Key);member.State=match==true?"matched":match==false?"excluded":"unknown"; }
                }
                if(member.State=="excluded") { yield return 0;continue; }
                member.Row??=NewRow(frame.Source,member,run);
                var row=RowHeader(member.Row);var rows=(JArray)frame.Node["rows"]!;var rowIndex=rows.Count;rows.Add(row);frame.Returned++;
                var rowPath=frame.Path.Concat(new object[]{"rows",rowIndex}).ToArray();
                if(member.State is "unknown" or "unavailable") { row["status"]=member.State=="unknown"?"membership_unknown":"unavailable";Emit(rowPath);yield return 0;continue; }
                work.Frames.Push(new ReadFrame { Source=frame.Source,Member=member,Node=row,Path=rowPath,Record=frame.Record,Choices=frame.Choices });
                Emit(rowPath,RowHeader(row));yield return 0;continue;
            }
            var record=frame.Record;
            if(record!=null && frame.ColumnIndex<record.Columns.Length) {
                var column=record.Columns[frame.ColumnIndex++];var cached=(JObject)frame.Member.Row!["values"]!;
                if(cached[column]==null) {
                    try {
                        var native=Find(MemberLocator(frame.Source,frame.Member),run)??throw ReadContext.Error("source_changed","Captured member or ancestor is no longer available.");
                        var field=registry.Field(frame.Source.Type,column);run.GetterReads++;
                        var started=run.Tick();run.Context.BeginSelectedGetter();var getterStarted=ObjectReadProfile.Start();
                        cached[column]=ColumnValue(registry.Read(frame.Source.Type,native,field,registry.Arguments(field,null,column),run.Context),field);ObjectReadProfile.Stop(ObjectReadProfile.Part.Getter,getterStarted);Rule(run,frame.Source.Type,column,field);
                        var sample=run.Context.LastValueSample??(started,run.Tick());
                        frame.Member.Row!["field_samples"]??=new JObject();frame.Member.Row!["field_samples"]![column]=new JObject { ["started_tick"]=sample.Item1,["finished_tick"]=sample.Item2,["atomic"]=false };
                        AddColumnBinding(frame.Member.Row!,column,field,native,run);
                    } catch(Exception error) when(ReadContext.Recoverable(error)) { cached[column]=new JObject { ["status"]="unavailable" };Issue(run,error,frame.Member.Key+"/"+column); }
                    Retain(run,cached[column]!.ToString(Formatting.None).Length*2L);
                }
                var value=cached[column]!.DeepClone();var path=frame.Path.Concat(new object[]{"values",column}).ToArray();
                ((JObject)frame.Node["values"]!)[column]=value;
                CopyColumnBinding(frame.Member.Row!,frame.Node,column);frame.Member.Row!["sample"]!["finished_tick"]=run.Tick();frame.Node["sample"]=frame.Member.Row!["sample"]!.DeepClone();
                if(NeedsValueWindow(value)) {
                    var template=ObjectReadPages.Branch(work.Projection,path,new JObject { ["__text_window_slot__"]=true });
                    value=ScheduleValue(value,template,(JObject)frame.Node["values"]!,column,work,run);
                    deliveredTexts.Add((string)value["value_snapshot_id"]!);frame.Node["values"]![column]=value;
                }
                Emit(path);yield return 0;continue;
            }
            if(frame.ChildIndex<frame.Choices.Count) {
                var selected=frame.Choices[frame.ChildIndex++];
                var reading=new JObject { ["field"]=selected.Field,["input_path"]=new JArray(selected.InputPath),["rows"]=new JArray(),["scope"]=new JObject { ["relation"]=frame.Source.Type+"."+selected.Field,["where_signature"]=GameQueryEngine.Hash(selected.Where.ToString(Formatting.None)) },["sample"]=new JObject { ["load_epoch"]=run.Session.LoadEpoch,["started_tick"]=run.Tick(),["atomic"]=false } };
                var readings=(JArray)frame.Node["readings"]!;var readingIndex=readings.Count;readings.Add(reading);
                var path=frame.Path.Concat(new object[]{"readings",readingIndex}).ToArray();
                if(selected.Definition?.Metadata.ContainsKey("object_derived")==true)work.DerivedPaths.Add(path);
                if(selected.Error!=null) { Issue(run,selected.Error,frame.Member.Key+"/"+selected.Field);reading["status"]="selection_error";reading["coverage"]=new JObject { ["fields_complete"]=false,["display_complete"]=true };Emit(path);yield return 0;continue; }
                if(selected.ExplicitEmpty) { reading["status"]="observed";reading["coverage"]=CompleteEmpty();reading["page"]=new JObject { ["has_more"]=false,["cursor"]=null,["returned_count"]=0 };Emit(path);yield return 0;continue; }
                var key=run.Root.Id+":"+frame.Member.Key+":"+selected.Index+":"+selected.Field;
                if(!run.Lease.Sources.TryGetValue(key,out var childSource)) {
                    object? raw=null;var failed=false;
                    try { var native=Find(MemberLocator(frame.Source,frame.Member),run)??throw ReadContext.Error("source_changed","Captured ancestor is no longer available.");raw=ReadSelection(native,frame.Source.Type,selected,run); }
                    catch(Exception error) when(ReadContext.Recoverable(error)) { failed=true;Issue(run,error,frame.Member.Key+"/"+selected.Field); }
                    if(raw==null) {
                        reading["status"]=failed?"unavailable":selected.Definition.Metadata.TryGetValue("object_null_status",out var nullStatus)?(string)nullStatus!:"observed_none";
                        reading["coverage"]=CompleteEmpty();if(failed) { reading["coverage"]!["source_complete"]=false;reading["coverage"]!["fields_complete"]=false; }
                        reading["page"]=new JObject { ["has_more"]=false,["cursor"]=null,["returned_count"]=0 };Emit(path);yield return 0;continue;
                    }
                    childSource=new Snapshot { RootId=run.Root.Id,Type=selected.Type,Locator=new Locator { Type=selected.Type,Scope=run.Root.Locator.Scope,Parent=MemberLocator(frame.Source,frame.Member),Relation=selected },Capture=ObjectReadContract.Capture(selected.Type,selected.Definition),Selection=selected,Predicate=selected.Predicate,Where=selected.Where,Signature=run.Signature,CapturedTick=run.Tick() };
                    run.Lease.Sources[key]=childSource;run.Lease.SourceIds[childSource.Id]=childSource;
                    var members=raw is IEnumerable sequence && raw is not string?sequence.Cast<object>():new[]{raw};
                    foreach(var step in Capture(childSource,members,run))yield return step;
                }
                Rule(run,frame.Source.Type,selected.Field,selected.Definition);
                work.Frames.Push(new ReadFrame { Source=childSource,Node=reading,Path=path,Size=selected.Size,Record=selected,Choices=selected.Children });
                yield return 0;continue;
            }
            frame.Member.State="observed";frame.Node["sample"]=frame.Member.Row!["sample"]!.DeepClone();work.Frames.Pop();
            Emit(frame.Path,RowHeader(frame.Node));yield return 0;
        }
        var finalizeStarted=ObjectReadProfile.Start();
        var complete=work.Frames.Count==0 && work.DeferredSources.Count==0 && work.TextValues.Count==0 && work.PendingDelivery.Count==0;
        if(complete && !work.Finalized) {
            work.Finalized=true;
            foreach(var closed in work.ClosedSources.OrderByDescending(f=>f.Path.Length)) {
                SourceMetadata(closed,run,true,work,true);Emit(closed.Path,MetadataOnly(closed.Node));
            }
            foreach(var derived in work.DerivedPaths)Emit(derived);
        }
        if(!complete)foreach(var active in work.Frames.Where(f=>f.Member==null).ToArray()) {
            SourceMetadata(active,run,false,work);
            if(!paused)Emit(active.Path,MetadataOnly(active.Node));
        }
        if(start==0 && complete)RestoreCovered(output);
        complete=work.Frames.Count==0 && work.DeferredSources.Count==0 && work.TextValues.Count==0 && work.PendingDelivery.Count==0;
        output=DeliveryEnvelope(output,work,run,start,steps,complete,nextToken);
        if(!complete) {
            cursors[nextToken]=new Continuation { Lease=run.LeaseId,Epoch=run.Session.LoadEpoch,Signature=run.Signature,Source=work.Source,Traversal=work,Request=(JObject)run.Request.DeepClone(),InputPath=work.Source.Selection?.InputPath??Array.Empty<int>() };
        }
        if(resumed!=null)resumed.CachedResult=(JObject)output.DeepClone();
        var encoded=output.ToString(Formatting.None);if(DeliverySize(output)>DeliveryBytes)throw ReadContext.Error("object_read_failed","Delivery exceeded its measured structural maximum.");
        ObjectReadProfile.Stop(ObjectReadProfile.Part.Finalize,finalizeStarted);if(ObjectReadProfile.Enabled)ObjectReadProfile.Deliveries++;
        if(JsonDepth(output)>24)publish(PacketPage(encoded,0,(JObject)output["scope"]!,run,(string)output["status"]!));
        else publish(new QueryOutcome { Status=(string)output["status"]!,Data=output,Issues=EnvelopeIssues((string)output["status"]!) });
    }
    private static Locator MemberLocator(Snapshot source,Member member)=>new() { Root=source.Locator.Root,Type=source.Type,Key=member.Key,Scope=source.Locator.Scope,Parent=source.Locator.Parent,Relation=source.Locator.Relation,SourceId=source.Id };
    private static JObject NewRow(Snapshot source,Member member,Run run)=>new() { ["anchor"]=new JObject { ["id"]=member.Key,["type"]=source.Type,["identity_scope"]=source.Capture=="local_identity"?"snapshot":"native" },["values"]=new JObject(),["readings"]=new JArray(),["membership_sample"]=new JObject { ["started_tick"]=source.CapturedTick,["finished_tick"]=source.CaptureFinishedTick,["verified_at_tick"]=run.Tick(),["atomic"]=false },["sample"]=new JObject { ["load_epoch"]=run.Session.LoadEpoch,["started_tick"]=run.Tick(),["atomic"]=false } };
    private static JObject RowHeader(JObject row)=>new(row.Properties().Where(p=>p.Name is not ("values" or "readings" or "field_samples" or "field_context" or "field_semantics")).Select(p=>new JProperty(p.Name,p.Value.DeepClone()))) { ["values"]=new JObject(),["readings"]=new JArray() };
    private static JObject MetadataOnly(JObject node)=>new(node.Properties().Where(p=>p.Name!="rows").Select(p=>new JProperty(p.Name,p.Value.DeepClone()))) { ["rows"]=new JArray() };
    private static JObject CompleteEmpty()=>new() { ["source_complete"]=true,["predicate_complete"]=true,["fields_complete"]=true,["display_complete"]=true,["returned_count"]=0 };
    private void SourceMetadata(ReadFrame frame,Run run,bool finished,ReadTraversal work,bool refresh=false) {
        var profiled=ObjectReadProfile.Start();try { SourceMetadataCore(frame,run,finished,work,refresh); } finally { ObjectReadProfile.Stop(ObjectReadProfile.Part.Metadata,profiled); }
    }
    private void SourceMetadataCore(ReadFrame frame,Run run,bool finished,ReadTraversal work,bool refresh) {
        var source=frame.Source;var node=frame.Node;var more=frame.Index<source.Members.Count;
        var started=(long?)node["sample"]?["started_tick"]??source.CapturedTick;
        var end=refresh?Math.Max(frame.FinishedTick,LatestSample(node)):run.Tick();
        node["sample"]=new JObject { ["load_epoch"]=run.Session.LoadEpoch,["started_tick"]=Math.Min(started,source.CapturedTick),["finished_tick"]=end,["atomic"]=false };
        node["snapshot_id"]=source.Id;node["status"]=frame.Path.Length==0?"ok":"observed";
        if(!work.ExplanationSources.TryGetValue(source.Id,out var explanation)) {
            explanation=new ExplanationSource { Source=source };work.ExplanationSources[source.Id]=explanation;
        }
        explanation.Node=node;explanation.Path=frame.Path;
        node["issues"]=new JArray();node["source_issue_count"]=source.SourceIssues.Count;
        var delivered=finished && !more;
        node["coverage"]=new JObject { ["source_complete"]=source.Complete,["predicate_complete"]=source.Predicate==null || source.Members.All(m=>m.State is not ("pending" or "unknown" or "unavailable")),["predicate_complete_for_visited"]=source.Predicate==null || source.Members.Skip(frame.Start).Take(frame.Index-frame.Start).All(m=>m.State is not ("pending" or "unknown" or "unavailable")),["fields_complete"]=finished && !Incomplete(node["rows"]!),["source_delivery_complete"]=delivered,["display_complete"]=delivered && RowsDisplayComplete((JArray)node["rows"]!),["captured_members"]=source.Members.Count,["returned_count"]=frame.Returned };
        node["page"]=new JObject { ["kind"]="source_window",["offset"]=refresh?0:frame.Start,["visited_through"]=frame.Index,["returned_count"]=refresh?((JArray)node["rows"]!).Count:frame.Returned,["window_complete"]=finished,["has_more"]=more };
    }
    private static long LatestSample(JObject node) {
        var latest=(long?)node["sample"]?["finished_tick"]??0;
        foreach(var row in ((JArray?)node["rows"]??new JArray()).OfType<JObject>()) {
            latest=Math.Max(latest,(long?)row["sample"]?["finished_tick"]??0);
            foreach(var child in ((JArray?)row["readings"]??new JArray()).OfType<JObject>())latest=Math.Max(latest,LatestSample(child));
        }
        return latest;
    }
    private static bool RowsDisplayComplete(JArray rows)=>rows.OfType<JObject>().All(row=>
        ((JObject?)row["values"]??new JObject()).Properties().All(p=>p.Value is not JObject text || text["text"]==null && text["items"]==null || (bool?)text["complete"]==true) &&
        ((JArray?)row["readings"]??new JArray()).OfType<JObject>().All(reading=>(bool?)reading["coverage"]?["display_complete"]==true));
    private static void AddColumnBinding(JObject row,string column,FieldType field,object native,Run run) {
        if(field.Metadata.TryGetValue("object_semantic_role",out var role)) { row["field_semantics"]??=new JObject();row["field_semantics"]![column]=new JObject { ["role"]=(string)role!,["subject_id"]=native is GameNode node?node.Owner.Id:native is GameEntity entity?entity.Id:(string?)row["anchor"]?["id"] }; }
        if(field.Metadata.TryGetValue("object_roles",out var roles)) { row["field_context"]??=new JObject();row["field_context"]![column]=new JObject(((string[])roles!).Select(name=>new JProperty(name,name=="actor"?run.Context.ObjectActor?.Id:run.Context.ObjectRecipient?.Id))); }
    }
    private static void CopyColumnBinding(JObject saved,JObject delivered,string column) {
        foreach(var name in new[]{"field_semantics","field_context","field_samples"})if(saved[name]?[column]!=null) { delivered[name]??=new JObject();delivered[name]![column]=saved[name]![column]!.DeepClone(); }
    }
    private static void RestoreCovered(JToken value) {
        if(value is JObject obj && obj["coverage"] is JObject coverage && coverage["source_display_complete"]!=null) {
            coverage["display_complete"]=coverage["source_display_complete"]!.DeepClone();coverage.Remove("source_display_complete");
        }
        if(value is JContainer container)foreach(var child in container.Children().ToArray())RestoreCovered(child);
    }
}
