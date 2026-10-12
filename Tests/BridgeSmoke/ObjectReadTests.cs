using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class ObjectReadTests
{
    internal static void Run(string? capturePath=null)
    {
        var engine=new GameQueryEngine(1000);var source=new MutableData();
        var session=new SessionDescriptor(new string('2',32),new string('3',32),1,1);
        var captured=new JArray();
        QueryOutcome Read(JObject request) {
            var input=(JObject)request.DeepClone();var outcome=engine.Execute("read_objects",input,source,session);var json="";
            while(true) {
                var envelope=ProtocolEnvelope.CreateQueryResult(new ProtocolEnvelope { RequestId=new string('4',32) },new string('1',32),1,session,outcome.Status,outcome.Data,outcome.Issues,outcome.ErrorCode,outcome.Message,outcome.Execution);
                ProtocolCodec.Deserialize(ProtocolCodec.Serialize(envelope));
                if(outcome.Data?["fragment"] is not JObject fragment)break;
                Require((int)fragment["offset"]! == json.Length,"Internal wire fragment skipped DTO characters.",outcome);json+=(string)fragment["text"]!;
                if((bool?)fragment["page"]?["has_more"]!=true) { outcome.Data=JObject.Parse(json);break; }
                input["page"]??=new JObject();input["page"]!["cursor"]=fragment["page"]!["cursor"]!.DeepClone();outcome=engine.Execute("read_objects",input,source,session);
            }
            var lease=(string?)request["lease_id"];
            if(capturePath!=null && lease is "parts" or "text" or "long-explanation" or "array" or "nested-array" or "nested-large-array")
                captured.Add(new JObject { ["lease"]=lease,["request"]=request.DeepClone(),["data"]=outcome.Data?.DeepClone(),["status"]=outcome.Status });
            return outcome;
        }
        JObject Input(string selections,string lease="lease")=>JObject.Parse("{target:{kind:'colonists'},select:"+selections+",lease_id:'"+lease+"',page:{size:20}}");
        QueryOutcome Follow(QueryOutcome previous,string lease) {
            Require(previous.Data!.Descendants().OfType<JProperty>().Count(p=>p.Name=="cursor" && p.Value.Type==JTokenType.String && !string.IsNullOrEmpty((string?)p.Value))==1,"A semantic page exposes multiple continuation cursors.",previous);
            return Read(new JObject { ["lease_id"]=lease,["page"]=new JObject { ["cursor"]=previous.Data!["page"]!["cursor"]!.DeepClone() } });
        }
        var anchors=Read(Input("[]","anchor"));Require(anchors.Status=="ok" && source.ValueReads==0,"Identity-only read expanded business facts.",anchors);
        var input=Input("[{field:'self',columns:['name']}]");var first=Read(input);
        Require(first.Status=="ok" && first.Data?["rows"] is JArray rows && rows.Count==20 && source.ValueReads==20,"Root window did not execute only selected page getters.",first);
        var cursor=(string?)first.Data?["page"]?["cursor"];
        source.NamePrefix="changed:";var repeated=Read(input);
        Require((string?)repeated.Data?["rows"]?[0]?["readings"]?[0]?["rows"]?[0]?["values"]?["name"]=="changed:pawn0","Same no-cursor request reused stale values.",repeated);source.NamePrefix="";
        source.Pawns.RemoveAll(p=>p.Id=="pawn20");source.Pawns.Add(new GameEntity("new-pawn","map1",new object()));session.GameTick=2;
        var next=(JObject)input.DeepClone();next["page"]!["cursor"]=cursor;
        var second=Read(new JObject { ["lease_id"]="lease",["page"]=new JObject { ["cursor"]=cursor } });Require(second.Data?["rows"]?[0]?["status"]?.ToString()=="unavailable" && !second.Data!["rows"]!.ToString().Contains("new-pawn"),"Cursor-only read mixed new members or hid a deleted member.",second);
        Require(source.ValueReads==59,"Fresh/page-local values were unexpectedly reread.",second);
        var mismatch=(JObject)next.DeepClone();mismatch["select"]=new JArray(new JObject { ["field"]="id" });
        Require(Read(mismatch).ErrorCode=="cursor_selection_mismatch","Cursor did not bind selection.",Read(mismatch));
        engine.Execute("release_object_reads",new JObject { ["lease_id"]="lease" },source,session);
        Require(Read(next).ErrorCode=="cursor_expired","Released cursor was reused.",Read(next));

        source.ValueReads=0;source.PartReads=0;
        var anatomy=Input("[{field:'health',children:[{field:'body_parts',columns:['index','health','health'],page:{size:20}}]}]","parts");
        anatomy["target"]!["where"]=JObject.Parse("{id:{eq:'pawn0'}}");
        var parts=Read(anatomy);Require(parts.Status=="ok" && source.PartReads==40,"Anatomy expanded beyond selected page or duplicate columns.",parts);
        var health=(JObject)parts.Data!["rows"]![0]!["readings"]![0]!;
        var nested=(JObject)health["rows"]![0]!["readings"]![0]!;
        var allParts=new JObject();ObjectReadPages.Merge(allParts,parts.Data!);
        while((bool?)parts.Data?["delivery"]?["end_of_projection"]!=true) { parts=Follow(parts,"parts");Require(parts.Status=="ok","Unified anatomy cursor failed.",parts);ObjectReadPages.Merge(allParts,parts.Data!); }
        var total=((JArray)allParts["rows"]![0]!["readings"]![0]!["rows"]![0]!["readings"]![0]!["rows"]!).Count;
        Require(total==137 && source.PartReads==274,"More-than100 anatomy was truncated or reread.",parts);
        source.DuplicateTraitKeys=true;
        var twins=Input("[{field:'profile',children:[{field:'traits',columns:['suppressed'],page:{size:2}}]}]","trait-twins");twins["target"]!["where"]=JObject.Parse("{id:{eq:'pawn0'}}");
        var twinsResult=Read(twins);var twinRows=twinsResult.Data!["rows"]![0]!["readings"]![0]!["rows"]![0]!["readings"]![0]!["rows"]!;
        Require((bool?)twinRows[0]?["values"]?["suppressed"]==false && (bool?)twinRows[1]?["values"]?["suppressed"]==true,"Distinct native traits with equal keys shared getter values.",twinsResult);source.DuplicateTraitKeys=false;

        source.InventoryReads=0;var inventory=Input("[{field:'gear',children:[{field:'inventory',columns:['stack_count'],page:{size:20}}]}]","inventory");inventory["target"]!["where"]=JObject.Parse("{id:{eq:'pawn0'}}");
        var inventoryResult=Read(inventory);var allInventory=new JObject();
        while(true) {
            Require(inventoryResult.Status=="ok","Inventory window failed.",inventoryResult);
            ObjectReadPages.Merge(allInventory,inventoryResult.Data!);
            if((bool?)inventoryResult.Data?["delivery"]?["end_of_projection"]==true)break;
            inventoryResult=Follow(inventoryResult,"inventory");
        }
        var inventoryCount=((JArray)allInventory["rows"]![0]!["readings"]![0]!["rows"]![0]!["readings"]![0]!["rows"]!).Count;
        Require(inventoryCount==137 && source.InventoryReads==137,"Inventory beyond100 was lost or unselected columns were read.",inventoryResult);

        // Native child keys are owner-local ("pawn/health/summary" for every pawn). Several pawns in one
        // read must still keep their own nested facts, and the anchor must say whose fact it is.
        var people=Input("[{field:'health',children:[{field:'summary',columns:['pain']}]},{field:'profile',children:[{field:'skills',columns:['def_name','level']}]}]","people");
        people["target"]!["where"]=JObject.Parse("{id:{in:['pawn0','pawn1','pawn2']}}");
        foreach(var attempt in new[]{"fresh","same-lease"}) {
            var peopleResult=Read(people);var everyone=new JObject();
            while(true) {
                Require(peopleResult.Status=="ok","Multi-pawn nested read failed ("+attempt+").",peopleResult);
                ObjectReadPages.Merge(everyone,peopleResult.Data!);
                if((bool?)peopleResult.Data?["delivery"]?["end_of_projection"]==true)break;
                peopleResult=Follow(peopleResult,"people");
            }
            var pawnRows=(JArray)everyone["rows"]!;Require(pawnRows.Count==3,"Multi-pawn read lost roots ("+attempt+").",peopleResult);
            foreach(var row in pawnRows) {
                var id=(string)row["anchor"]!["id"]!;var index=int.Parse(id.Substring("pawn".Length));
                var summary=row["readings"]![0]!["rows"]![0]!["readings"]![0]!["rows"]![0]!;
                Require(Math.Abs((double)summary["values"]!["pain"]!-index*0.1)<1e-9,"Health summary of "+id+" came from another pawn ("+attempt+").",peopleResult);
                Require(((string)summary["anchor"]!["id"]!).StartsWith(id+"/",StringComparison.Ordinal),"Nested anchor does not identify its owner "+id+" ("+attempt+").",peopleResult);
                var skills=(JArray)row["readings"]![1]!["rows"]![0]!["readings"]![0]!["rows"]!;
                Require(skills.Count==2 && skills.All(s=>(int)s["values"]!["level"]! == MutableData.SkillLevel(index,(string)s["values"]!["def_name"]!)),"Skills of "+id+" came from another pawn ("+attempt+").",peopleResult);
            }
        }
        var carried=Input("[{field:'gear',children:[{field:'inventory',columns:['stack_count'],page:{size:2}}]}]","people-gear");
        carried["target"]!["where"]=JObject.Parse("{id:{in:['pawn0','pawn1']}}");
        var carriedResult=Read(carried);Require(carriedResult.Status=="ok","Multi-pawn inventory read failed.",carriedResult);
        foreach(var row in (JArray)carriedResult.Data!["rows"]!) {
            var id=(string)row["anchor"]!["id"]!;
            var items=(JArray)row["readings"]![0]!["rows"]![0]!["readings"]![0]!["rows"]!;
            Require(items.Count>0 && items.All(i=>((string)i["anchor"]!["id"]!).StartsWith(id+"-item",StringComparison.Ordinal)),"Inventory of "+id+" came from another pawn.",carriedResult);
        }

        source.TraitReads=0;
        var traits=Input("[{field:'profile',children:[{field:'traits',columns:['def_name','degree'],page:{size:20}}]}]","traits");traits["target"]!["where"]=JObject.Parse("{id:{eq:'pawn0'}}");
        var traitResult=Read(traits);Require(traitResult.Status=="ok" && source.TraitReads==40,"ID-less source eagerly expanded selected values beyond this page.",traitResult);
        var traitPage=(JObject)traitResult.Data!["rows"]![0]!["readings"]![0]!["rows"]![0]!["readings"]![0]!;
        source.TraitsReversed=true;
        traitResult=Follow(traitResult,"traits");Require(source.TraitReads==80 && (int?)traitResult.Data?["rows"]?[0]?["readings"]?[0]?["rows"]?[0]?["readings"]?[0]?["rows"]?[0]?["values"]?["degree"]==20,"ID-less continuation changed captured identities or expanded other pages.",traitResult);
        var removedCursor=traitResult.Data!["page"]!["cursor"]!.DeepClone();source.RemovedTraits.Add(40);
        var removedTrait=Read(new JObject { ["lease_id"]="traits",["page"]=new JObject { ["cursor"]=removedCursor } });
        Require((string?)removedTrait.Data?["rows"]?[0]?["readings"]?[0]?["rows"]?[0]?["readings"]?[0]?["rows"]?[0]?["status"]=="unavailable","Removed but still-live native trait was returned as a current member.",removedTrait);source.RemovedTraits.Clear();

        var shared=Input("[{field:'health',children:[{field:'body_parts',columns:['index'],page:{size:20}}]},{field:'self',columns:['name']}]","branches");shared["page"]!["size"]=2;
        shared["target"]!["where"]=JObject.Parse("{id:{in:['pawn0','pawn1']}}");
        var sharedFirst=Read(shared);Require(sharedFirst.Status=="ok","Multi-parent fixture failed.",sharedFirst);
        var beforeValueReads=source.ValueReads;var beforePartReads=source.PartReads;
        var sharedNext=Follow(sharedFirst,"branches");Require(sharedNext.Status=="ok" && source.ValueReads==beforeValueReads+1 && source.PartReads==beforePartReads+20,"Unified continuation failed to advance the explicitly selected sibling and next parent.",sharedNext);

        source.StatReads=0;var stats=Input("[{field:'stats',where:{def_name:{in:['MoveSpeed']}},columns:['def_name','value']} ]","stats");stats["target"]!["where"]=JObject.Parse("{id:{eq:'pawn0'}}");
        var statResult=Read(stats);Require(statResult.Status=="ok" && source.StatReads==1,"Named stats did not execute finite explicit getter set.",statResult);
        var statPage=Input("[{field:'stats',columns:['def_name','value']}]","stat-page");statPage["target"]!["where"]=JObject.Parse("{id:{eq:'pawn0'}}");
        var beforeStats=source.StatReads;var unfilteredStats=Read(statPage);
        Require(unfilteredStats.Status=="ok" && source.StatReads==beforeStats+20,"Unnamed stats computed beyond the selected page or were rejected.",unfilteredStats);
        var invalid=Input("[{field:'health',columns:['body_parts']},{field:'self',columns:['name']}]","local-error");invalid["target"]!["where"]=JObject.Parse("{id:{eq:'pawn0'}}");
        var localError=Read(invalid);Require(localError.Status=="partial" && (string?)localError.Data?["rows"]?[0]?["readings"]?[1]?["rows"]?[0]?["values"]?["name"]=="pawn0","Invalid local selection discarded independent facts.",localError);

        source.CurrentMap="map2";
        var pinned=Follow(sharedNext,"branches");Require((string?)pinned.Data?["scope"]?["map"]=="map1","Continuation re-resolved current map.",pinned);source.CurrentMap="map1";

        source.FailName=true;var unknown=Input("[]","unknown");unknown["target"]!["where"]=JObject.Parse("{name:{eq:'pawn0'}}");
        var unavailable=Read(unknown);Require(unavailable.Status=="partial" && (bool?)unavailable.Data?["coverage"]?["predicate_complete"]==false,"Unknown predicate became a complete empty set.",unavailable);source.FailName=false;
        source.LongName=string.Concat(Enumerable.Repeat("文👾",17000));var text=Input("[{field:'self',columns:['name']}]","text");text["target"]!["where"]=JObject.Parse("{id:{eq:'pawn0'}}");
        var textReads=source.ValueReads;var packet=Read(text);var expiredTextCursor=packet.Data!["page"]!["cursor"]!.DeepClone();var reconstructed="";var textSample=packet.Data!["rows"]![0]!["readings"]![0]!["rows"]![0]!["field_samples"]!["name"]!.DeepClone();
        while(true) {
            Require(JToken.DeepEquals(packet.Data!["rows"]![0]!["readings"]![0]!["rows"]![0]!["field_samples"]?["name"],textSample),"Text continuation lost its original value sample.",packet);
            var window=(JObject)packet.Data!["rows"]![0]!["readings"]![0]!["rows"]![0]!["values"]!["name"]!;
            Require((int)window["offset"]! == reconstructed.Length,"Text continuation skipped characters.",packet);var piece=(string)window["text"]!;
            Require(piece.Length==0 || !char.IsHighSurrogate(piece[piece.Length-1]),"Text window split surrogate pair.",packet);reconstructed+=piece;
            if((bool?)window["has_more"]!=true) { Require((bool?)window["complete"]==false && (bool?)window["end_of_value"]==true,"Tail text window claimed it contained the whole text.",packet);break; }
            packet=Follow(packet,"text");
        }
        Require(reconstructed==source.LongName && source.ValueReads==textReads+1,"Long text was lost or getters repeated.",packet);
        var bindings=JObject.Parse("{values:{a:1},field_samples:{a:{started_tick:1,finished_tick:1}},field_context:{a:{actor:'pawn0'}},field_semantics:{a:{role:'actor'}}}");
        ObjectReadPages.Merge(bindings,JObject.Parse("{values:{b:2},field_samples:{b:{started_tick:2,finished_tick:2}},field_context:{b:{recipient:'pawn1'}},field_semantics:{b:{role:'recipient'}}}"));
        Require(bindings["field_samples"]?["a"]!=null && bindings["field_samples"]?["b"]!=null && bindings["field_context"]?["a"]!=null && bindings["field_semantics"]?["a"]!=null,"Column metadata was replaced while values were accumulated.",packet);
        source.LongName=null;

        var big=Input("[{field:'profile',children:[{field:'traits',columns:['def_name','description'],page:{size:100}}]}]","delivery");big["page"]!["size"]=2;
        big["target"]!["where"]=JObject.Parse("{id:{in:['pawn0','pawn1']}}");
        source.TraitDescription=new string('x',2000);var beforeDeliveryTraits=source.TraitReads;var delivery=Read(big);var projected=new JObject();var sampledTraits=source.TraitReads;var pages=0;
        Require(sampledTraits>beforeDeliveryTraits && sampledTraits<beforeDeliveryTraits+400,"First delivery eagerly expanded the complete 2x100 selected tree.",delivery);
        while(true) {
            Require(delivery.Status=="ok" && delivery.Data!.ToString(Newtonsoft.Json.Formatting.None).Length<120000,"Structural delivery exceeded tool budget.",delivery);
            ObjectReadPages.Merge(projected,delivery.Data!);pages++;
            if((bool?)delivery.Data?["delivery"]?["end_of_projection"]==true) { Require((bool?)delivery.Data?["delivery"]?["complete"]==false,"Tail DTO page claimed it contained the full projection.",delivery);break; }
            big["page"]!["cursor"]=delivery.Data!["page"]!["cursor"]!.DeepClone();delivery=Read(big);
        }
        Require(pages>1 && source.TraitReads==beforeDeliveryTraits+548 && projected["rows"] is JArray deliveredRows && deliveredRows.Count==2,"Lazy delivery duplicated getters or lost roots: pages="+pages+" getter="+source.TraitReads+"/"+(beforeDeliveryTraits+548)+" roots="+(projected["rows"] as JArray)?.Count,delivery);
        Require(((JArray)projected["rows"]![0]!["readings"]![0]!["rows"]![0]!["readings"]![0]!["rows"]!).Count==137,"Structural delivery lost child rows beyond the display window.",delivery);source.TraitDescription="";

        var noParents=Input("[{field:'invented_field'}]","zero-error");noParents["target"]!["where"]=JObject.Parse("{id:{eq:'missing'}}");
        Require(Read(noParents).Status=="partial","Invalid selection disappeared with zero parents.",Read(noParents));
        var nullable=Input("[]","null-predicate");nullable["target"]!["where"]=JObject.Parse("{name:{eq:null}}");source.NullNames=true;
        Require(Read(nullable).Status=="ok" && ((JArray)Read(nullable).Data!["rows"]!).Count==20,"Public eq:null was rejected or mishandled.",Read(nullable));source.NullNames=false;
        var inNull=Input("[]","in-null");inNull["target"]!["where"]=JObject.Parse("{name:{in:[null,'pawn0']}}");
        Require(((JArray)Read(inNull).Data!["rows"]!).Count==1,"Public in with null changed membership.",Read(inNull));
        var nullRole=Input("[]","null-role");nullRole["context"]=JObject.Parse("{actor:null,recipient:null}");Require(Read(nullRole).Status=="ok","Null unused role was rejected.",Read(nullRole));
        var beforePawnLookups=source.RoleLookups;nullRole["context"]=JObject.Parse("{actor:{name:'pawn0'}}");Read(nullRole);Require(source.RoleLookups==beforePawnLookups,"Unused role caused population lookup.",Read(nullRole));
        var emptyStats=Input("[{field:'stats',where:{def_name:{in:[]}},columns:['value']}]","empty-stats");emptyStats["target"]!["where"]=JObject.Parse("{id:{eq:'pawn0'}}");
        var statBefore=source.StatReads;Require(Read(emptyStats).Status=="ok" && source.StatReads==statBefore,"Empty named stat selection executed or rejected.",Read(emptyStats));

        source.ReturnThing=true;var actor=JObject.Parse("{target:{kind:'things',scope:{placement:'held'}},select:[{field:'state',children:[{field:'condition',columns:['forbidden_to_actor']}]}],context:{actor:{id:'pawn0'}},lease_id:'actor',page:{size:20}}");
        var actorResult=Read(actor);Require(actorResult.Status=="ok" && source.LastActor=="pawn0" && !source.LastRoleHeldOnly,"Target held scope filtered spawned actor.",actorResult);
        var membership=JObject.Parse("{target:{kind:'things',where:{capabilities:{has:'fuel'}}},select:[],lease_id:'has',page:{size:20}}");Require(Read(membership).Status=="ok" && ((JArray)Read(membership).Data!["rows"]!).Count==1,"Public primitive has membership was rejected.",Read(membership));
        JArray Chain(int depth)=>new(new JObject { ["field"]="state",["children"]=new JArray(new JObject { ["field"]="containment",["children"]=new JArray(new JObject { ["field"]="contents",["columns"]=new JArray("stack_count"),["children"]=depth>0?Chain(depth-1):new JArray(),["page"]=new JObject { ["size"]=1 } }) }) });
        var deep=JObject.Parse("{target:{kind:'things'},select:[],lease_id:'deep',page:{size:20}}");deep["select"]=Chain(3);
        var deepResult=Read(deep);Require(deepResult.Status=="ok" && deepResult.Data!.ToString().Contains("stack_count"),"Deep source projection failed protocol round trip.",deepResult);source.ReturnThing=false;
        var situation=JObject.Parse("{target:{kind:'situation'},select:[{field:'clock',columns:['paused']},{field:'alerts',columns:['label']}],lease_id:'situation',page:{size:20}}");
        source.OnClockRead=()=>session.GameTick=100;source.OnAlertRead=()=>session.GameTick=300;
        var board=Read(situation);Require(board.Status=="partial" && (bool?)board.Data?["rows"]?[0]?["readings"]?[0]?["coverage"]?["source_complete"]==true && (bool?)board.Data?["rows"]?[0]?["readings"]?[1]?["coverage"]?["source_complete"]==false && (bool?)board.Data?["rows"]?[0]?["readings"]?[0]?["rows"]?[0]?["values"]?["paused"]==true,"Lazy unavailable alerts erased complete selected clock.",board);
        Require((long?)board.Data?["rows"]?[0]?["readings"]?[0]?["sample"]?["finished_tick"]==100 && (long?)board.Data?["sample"]?["finished_tick"]==300,"Closed source sample inherited a later sibling's delivery tick.",board);
        source.OnClockRead=null;source.OnAlertRead=null;
        var manyErrors=Input("[{field:'self',columns:['name']}]","many-errors");manyErrors["page"]!["size"]=100;
        manyErrors["target"]!["where"]=new JObject { ["id"]=new JObject { ["in"]=new JArray(source.Pawns.Take(100).Select(p=>p.Id)) } };
        source.FailName=true;source.FailNameMessage=string.Concat(Enumerable.Repeat("原生失败\"\n👾",60));
        var errorReads=source.ValueReads;var errors=Read(manyErrors);var errorProjection=new JObject();var errorPages=0;
        while(true) {
            Require(errors.ErrorCode==null && System.Text.Encoding.UTF8.GetByteCount(errors.Data!.ToString(Newtonsoft.Json.Formatting.None))<=80000,"Growing errors broke the complete delivery budget.",errors);
            ObjectReadPages.Merge(errorProjection,errors.Data!);errorPages++;
            if((bool?)errors.Data?["delivery"]?["end_of_projection"]==true)break;
            errors=Read(new JObject { ["lease_id"]="many-errors",["page"]=new JObject { ["cursor"]=errors.Data!["page"]!["cursor"]!.DeepClone() } });
        }
        Require(errorPages>1 && ((JArray)errorProjection["issues"]!).Count==100 && source.ValueReads==errorReads+100,"Error delivery lost records or repeated game getters: pages="+errorPages+" issues="+((JArray)errorProjection["issues"]!).Count+" getters="+(source.ValueReads-errorReads),errors);
        Require(errorProjection["issues"]!.All(issue=>(string?)issue["message"]==source.FailNameMessage),"Error messages were truncated or encoded.",errors);
        source.FailName=false;

        var executable=(GraphQL.Types.Schema)typeof(GameQueryEngine).GetField("schema",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(engine)!;
        var nameField=((GraphQL.Types.IComplexGraphType)executable.AllTypes["Pawn"]).GetField("name")!;
        var oldDescription=nameField.Description;var longDescription=string.Concat(Enumerable.Repeat("规则\"\n👾",5000));nameField.Description=longDescription;
        var explain=Input("[{field:'self',columns:['name']}]","long-explanation");explain["target"]!["where"]=JObject.Parse("{id:{eq:'pawn0'}}");
        var explanationReads=source.ValueReads;var explanation=Read(explain);var explained="";
        while(true) {
            Require(explanation.ErrorCode==null && System.Text.Encoding.UTF8.GetByteCount(explanation.Data!.ToString(Newtonsoft.Json.Formatting.None))<=80000,"Text explanation exceeded the complete package budget.",explanation);
            var window=(JObject)explanation.Data!["semantic_rules"]!["Pawn.name"]!["meaning"]!;
            Require((int)window["offset"]! == explained.Length,"Explanation continuation skipped a segment.",explanation);explained+=(string)window["text"]!;
            if((bool?)window["has_more"]!=true)break;
            explanation=Follow(explanation,"long-explanation");
        }
        Require(explained==longDescription && source.ValueReads==explanationReads+1,"Long explanation was lost or triggered another getter.",explanation);
        nameField.Description=oldDescription;
        source.ReturnThing=true;source.LargeCapabilities=true;
        var arrayRead=JObject.Parse("{target:{kind:'things'},select:[{field:'self',columns:['capabilities']}],lease_id:'array',page:{size:20}}");
        var arrayPage=Read(arrayRead);var arrayItems=new List<string>();
        while(true) {
            Require(arrayPage.ErrorCode==null && System.Text.Encoding.UTF8.GetByteCount(arrayPage.Data!.ToString(Newtonsoft.Json.Formatting.None))<=80000,"Array value broke the unified delivery budget.",arrayPage);
            var window=(JObject)arrayPage.Data!["rows"]![0]!["readings"]![0]!["rows"]![0]!["values"]!["capabilities"]!;
            Require((int)window["offset"]! == arrayItems.Count,"Array window skipped items.",arrayPage);
            arrayItems.AddRange(((JArray)window["items"]!).Values<string>()!);
            if((bool?)window["end_of_value"]==true)break;
            arrayPage=Follow(arrayPage,"array");
        }
        Require(arrayItems.SequenceEqual(Enumerable.Range(0,8000).Select(i=>"capability"+i)),"Array value lost or reordered items.",arrayPage);
        foreach(var large in new[]{false,true}) {
            source.LargeCapabilities=large;source.LongCapability=true;
            var lease=large?"nested-large-array":"nested-array";
            var nestedRequest=(JObject)arrayRead.DeepClone();nestedRequest["lease_id"]=lease;
            var nestedPage=Read(nestedRequest);var json="";var getters=source.ValueReads;
            while(true) {
                Require(nestedPage.ErrorCode==null && System.Text.Encoding.UTF8.GetByteCount(nestedPage.Data!.ToString(Newtonsoft.Json.Formatting.None))<=80000,"Nested JSON value exceeded the unified budget.",nestedPage);
                var window=(JObject)nestedPage.Data!["rows"]![0]!["readings"]![0]!["rows"]![0]!["values"]!["capabilities"]!;
                Require((string?)window["encoding"]=="json" && (int)window["offset"]! == json.Length,"Nested JSON window lost encoding or offset.",nestedPage);
                json+=(string)window["text"]!;
                if((bool?)nestedPage.Data["page"]?["has_more"]!=true)break;
                nestedPage=Follow(nestedPage,lease);
            }
            var restored=JArray.Parse(json);
            Require(restored.Count==(large?8000:1) && (string?)restored[0]==MutableData.LongCapabilityText && source.ValueReads==getters && (bool?)nestedPage.Data!["coverage"]?["display_complete"]==true,"Nested array was lost, reread, or failed to close parent coverage.",nestedPage);
        }
        source.LongCapability=false;
        source.LargeCapabilities=false;source.ReturnThing=false;
        var widePredicate=Input("[{field:'self',columns:['name']}]","wide-predicate");
        var ids=new JArray(Enumerable.Range(0,127).Select(i=>new string('x',1000)+i));ids.Add("pawn0");
        widePredicate["target"]!["where"]=new JObject { ["id"]=new JObject { ["in"]=ids } };
        var scoped=Read(widePredicate);
        Require(scoped.ErrorCode==null && ((JArray)scoped.Data!["rows"]!).Count==1 && scoped.Data["scope"]?["where"]==null && (string?)scoped.Data["scope"]?["where_signature"]!=null && System.Text.Encoding.UTF8.GetByteCount(scoped.Data.ToString(Newtonsoft.Json.Formatting.None))<=80000,"Large bound predicates leaked into the delivery scope.",scoped);

        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var reader=typeof(GameQueryEngine).GetField("objectReader",flags)!.GetValue(engine)!;
        var textEnvelope=typeof(ObjectReadEngine).GetMethod("TextEnvelope",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
        var tiny=JObject.Parse("{scope:{map:'map1'},rows:[],padding:'',slot:{__text_window_slot__:true}}");
        var unicode="👾"+new string('x',2047);
        JObject Probe(int width)=>new() { ["value_snapshot_id"]=new string('v',32),["text"]=unicode.Substring(0,width),["offset"]=0,["returned_chars"]=width,["total_chars"]=unicode.Length,["complete"]=false,["end_of_value"]=false,["has_more"]=true,["cursor"]=new string('c',32),["resume_at"]="look.page.cursor",["unit"]="UTF16_code_units" };
        var one=(JObject)textEnvelope.Invoke(null,new object[]{tiny,Probe(1)})!;
        var oneBytes=System.Text.Encoding.UTF8.GetByteCount(one.ToString(Newtonsoft.Json.Formatting.None));
        tiny["padding"]=new string('x',80000-oneBytes);
        var runType=typeof(ObjectReadEngine).GetNestedType("Run",System.Reflection.BindingFlags.NonPublic)!;
        var tinyRun=Activator.CreateInstance(runType,true)!;runType.GetField("Session",flags)!.SetValue(tinyRun,session);
        try {
            var result=(JObject)typeof(ObjectReadEngine).GetMethod("TextWindow",flags)!.Invoke(reader,new object[]{unicode,0,tiny,tinyRun,new string('v',32)})!;
            Require((int)result["returned_chars"]!>0,"A tight Unicode budget created a non-advancing cursor.",scoped);
        } catch(System.Reflection.TargetInvocationException error) when(error.InnerException is GraphQL.ExecutionError failure && failure.Code=="projection_block_too_large") {
            // Required scope cannot hold a complete Unicode character; a
            // non-advancing success/cursor must never be manufactured.
        }
        engine.ClearObjectReads();session.LoadEpoch=new string('4',32);
        var staleText=new JObject { ["lease_id"]="text",["page"]=new JObject { ["cursor"]=expiredTextCursor } };
        Require(Read(staleText).ErrorCode=="cursor_expired","Old load cursor was accepted.",Read(staleText));
        if(capturePath!=null)System.IO.File.WriteAllText(capturePath,captured.ToString(Newtonsoft.Json.Formatting.None));
        Console.WriteLine("Object-read mutable/selected/lossless tests passed.");
    }
    private static void Require(bool condition,string message,QueryOutcome outcome) { if(!condition)throw new InvalidOperationException(message+" "+outcome.ErrorCode+" "+outcome.Message+" "+outcome.Data); }
    private sealed class MutableData : IGameData,IQueryData,IThingData,IObjectQueryData
    {
        internal List<GameEntity> Pawns=Enumerable.Range(0,137).Select(i=>new GameEntity("pawn"+i,"map1",new object())).ToList();
        internal readonly object[] TraitIdentities=Enumerable.Range(0,137).Select(_=>new object()).ToArray();internal readonly HashSet<int> RemovedTraits=new();
        internal int ValueReads,PartReads,TraitReads,StatReads,InventoryReads,RoleLookups;internal bool FailName,TraitsReversed,NullNames,ReturnThing,LastRoleHeldOnly,DuplicateTraitKeys;internal string? LongName,LastActor;internal string CurrentMap="map1",NamePrefix="",TraitDescription="";
        internal Action? OnClockRead,OnAlertRead;internal string FailNameMessage="name unavailable";internal bool LargeCapabilities,LongCapability;
        internal static string LongCapabilityText=>string.Concat(Enumerable.Repeat("文\"\n👾",18000));
        public IEnumerable<GameEntity> Maps()=>new[]{new GameEntity("map1","map1",new object())};
        public IEnumerable<GameEntity> Colonists()=>Pawns;
        public IEnumerable<GameEntity> QueryPawns(IReadOnlyDictionary<string,object?> args,ReadContext context)=>Pawns;
        public IEnumerable<GameEntity> QueryObjectPawns(IReadOnlyDictionary<string,object?> args,ReadContext context) { RoleLookups++;LastRoleHeldOnly=args.TryGetValue("placementOnlyHeld",out var held) && held is true;return LastRoleHeldOnly?Array.Empty<GameEntity>():Pawns; }
        public IEnumerable<GameEntity> QueryObjectColonists(IReadOnlyDictionary<string,object?> args,ReadContext context)=>args.TryGetValue("placementOnlyHeld",out var held) && held is true?Array.Empty<GameEntity>():Pawns.Where(p=>!args.TryGetValue("mapId",out var map) || map==null || p.MapId==map as string);
        public IEnumerable<GameNode> QueryThings(IReadOnlyDictionary<string,object?> args,ReadContext context,Func<GameNode,bool>? filter=null,IReadOnlyCollection<string>? exactIds=null)=>ReturnThing?new[]{new GameNode("Thing","box0",new GameEntity("box0","map1",0),0)}:Array.Empty<GameNode>();
        public object? NativeRoot(string root,IReadOnlyDictionary<string,object?> args,ReadContext context)=>root=="situation"?new GameNode("ObjectSituation","map1/situation",new GameEntity("map1","map1",new object()),new object()):Array.Empty<GameNode>();
        public object? NativeField(GameNode node,string field,IReadOnlyDictionary<string,object?> args,ReadContext context) {
            if(node.Kind=="ObjectCondition" && field=="forbiddenToActor") { LastActor=context.ObjectActor?.Id;if(LastActor==null)throw ReadContext.Error("context_unavailable","actor required");return false; }
            return NodeField(node,field,args);
        }
        public object? GameField(string field)=>field=="currentMapId"?CurrentMap:null;
        public object? MapField(GameEntity map,string field)=>field=="id"?map.Id:null;
        public object? ColonistField(GameEntity pawn,string field) { if(field=="name") { ValueReads++;if(FailName)throw ReadContext.Error("field_unavailable",FailNameMessage);return NullNames?null:LongName??NamePrefix+pawn.Id; }return null; }
        public object? NodeField(GameNode node,string field,IReadOnlyDictionary<string,object?> arguments) {
            if(node.Kind=="ObjectSituation" && field=="clock")return new GameNode("ObjectSituationClock",node.Key+"/clock",node.Owner,new object());
            if(node.Kind=="ObjectSituation" && field=="alerts") { OnAlertRead?.Invoke();return UnavailableAlerts(); }
            if(node.Kind=="ObjectSituationClock" && field=="paused") { OnClockRead?.Invoke();return true; }
            // Child keys follow the native Child(): parent.Key+"/"+key, where every Pawn node is "pawn".
            // They are unique only within their owner, exactly like the game implementation.
            if(node.Kind=="Pawn" && field=="health")return new GameNode("Health",node.Key+"/health",node.Owner,new object());
            if(node.Kind=="Pawn" && field=="profile")return new GameNode("Profile",node.Key+"/profile",node.Owner,new object());
            if(node.Kind=="Pawn" && field=="gear")return new GameNode("ObjectGear",node.Key+"/gear",node.Owner,new object());
            if(node.Kind=="Health" && field=="summary")return new GameNode("HealthSummary",node.Key+"/summary",node.Owner,new object());
            if(node.Kind=="HealthSummary" && field=="pain")return OwnerIndex(node)*0.1;
            if(node.Kind=="Profile" && field=="skills")return new[]{"Medicine","Cooking"}.Select(n=>new GameNode("Skill",node.Key+"/"+n,node.Owner,n));
            if(node.Kind=="Skill") { if(field=="defName")return node.Handle;if(field=="level")return SkillLevel(OwnerIndex(node),(string)node.Handle!); }
            if(node.Kind=="Thing" && field=="state")return new GameNode("ObjectState",node.Key+"/state",node.Owner,node.Handle);
            if(node.Kind=="ObjectState" && field=="condition")return new GameNode("ObjectCondition",node.Key+"/condition",node.Owner,node.Handle);
            if(node.Kind=="ObjectState" && field=="containment")return new GameNode("ObjectContainment",node.Key+"/containment",node.Owner,node.Handle);
            if(node.Kind=="ObjectContainment" && field=="contents") { var i=(int)node.Handle!+1;return new[]{new GameNode("Thing","box"+i,new GameEntity("box"+i,"map1",i),i)}; }
            if(node.Kind=="Thing" && field=="Spawned")return false;
            if(node.Kind=="Thing" && field=="capabilities")return LongCapability?Enumerable.Range(0,LargeCapabilities?8000:1).Select(i=>i==0?LongCapabilityText:"capability"+i):LargeCapabilities?Enumerable.Range(0,8000).Select(i=>"capability"+i):new[]{"fuel","bill_giver"};
            if(node.Kind=="Thing" && field=="id")return node.Key;
            if(node.Kind=="ObjectGear" && field=="inventory")return Enumerable.Range(0,137).Select(i=>new GameNode("Thing",node.Owner.Id+"-item"+i,new GameEntity(node.Owner.Id+"-item"+i,"map1",i),i));
            if(node.Kind=="Thing" && field=="stackCount") { InventoryReads++;return 1; }
            if(node.Kind=="Profile" && field=="traits")return (TraitsReversed?Enumerable.Range(0,137).Reverse():Enumerable.Range(0,137)).Where(i=>!RemovedTraits.Contains(i)).Select(i=>new GameNode("Trait",node.Key+"/trait"+(DuplicateTraitKeys?"same":i.ToString()),node.Owner,i,TraitIdentities[i]));
            if(node.Kind=="Trait") { TraitReads++;return field=="defName"?"Trait"+node.Handle:field=="degree"?node.Handle:field=="description"?TraitDescription:field=="suppressed"?(int)node.Handle!%2!=0:null; }
            if(node.Kind=="Pawn" && field=="stats")return (arguments.TryGetValue("defNames",out var selected)?(IEnumerable<object?>)selected!:Enumerable.Range(0,137).Select(i=>(object?)("Stat"+i))).Select(n=>new GameNode("Stat",node.Key+"/"+n,node.Owner,n));
            if(node.Kind=="Stat") { if(field=="defName")return node.Handle;if(field=="value") { StatReads++;return 4d; } }
            if(node.Kind=="Health" && field=="bodyParts")return Enumerable.Range(0,137).Select(i=>new GameNode("BodyPart",node.Owner.Id+"/part"+i,node.Owner,i));
            if(node.Kind=="BodyPart") { PartReads++;return field=="index"?node.Handle:field=="health"?10d:null; }
            if(node.Kind=="Pawn" && field=="id")return node.Owner.Id;
            return null;
        }
        private static int OwnerIndex(GameNode node)=>int.Parse(node.Owner.Id.Substring("pawn".Length));
        internal static int SkillLevel(int pawn,string skill)=>pawn+(skill=="Medicine"?10:0);
        private static IEnumerable<GameNode> UnavailableAlerts() { if(DateTime.UtcNow.Ticks>0)throw ReadContext.Error("alert_cache_unavailable","Native alert cache not ready.");yield break; }
    }
}
