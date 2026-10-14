using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class QueryEngineTests
{
    private static readonly SessionDescriptor Session = new(new string('2', 32), new string('3', 32), 100, 7);

    public static void ExportCatalog(string path) => File.WriteAllText(path, new GameQueryEngine(1000).Execute("query_catalog",new JObject(),new FakeData(),Session).Data!.ToString());

    public static void Run()
    {
        ThoughtReferenceIdentity();
        var engine = new GameQueryEngine(1000);
        var source = new FakeData();
        QueryOutcome Query(string json) => engine.Execute("query",JObject.Parse(json),source,Session);
        var catalog = engine.Execute("query_catalog",new JObject(),source,Session);
        Require(catalog.Status=="ok" && source.Reads.Count==0,"Catalog read game state");
        Require(catalog.Data!.Properties().Select(p=>p.Name).OrderBy(x=>x).SequenceEqual(new[]{"public_query","schema_id","selection_contract","spatial_query","world_observation"}),"Catalog exposed retired entries");
        foreach(var operation in new[]{"graphql","describe_schema","resource_report","template_query"}) {
            var rejected=engine.Execute(operation,JObject.Parse("{document:'{game{paused}}',requests:[]}"),source,Session);
            Require(rejected.Status=="unsupported" && rejected.ErrorCode=="unsupported_operation" && source.Reads.Count==0,"Retired operation read game state: "+operation);
        }
        var result=Query("{requests:[{from:'pawn',args:{id:'pawn1'},select:['id','name','status','mood','health','profile']},{from:'colony',select:['game','maps']}]}");
        Ok(result,"Current pawn sections and colony overview");
        var pawn=result.Data!["results"]![0]!["rows"]![0]!;
        foreach(var section in new[]{"status","mood","health","profile"}) Require(pawn[section] is JObject,"Requested section absent: "+section);
        Require(source.Threads.All(t=>t==Thread.CurrentThread.ManagedThreadId),"Getter left caller thread");
        source=new FakeData();
        result=Query("{requests:[{from:'pawn',args:{id:'pawn1'},select:['mood.level']},{from:'pawn',args:{id:'pawn1'},select:['mood.level']}]}");
        Ok(result,"Current overlapping field requests");
        Require(source.Reads.Count(x=>x=="pawn1:Mood.level")==1 && source.Reads.Count(x=>x=="Colonists")==1,"Shared field/identity cache reread native state");
        source=new FakeData();
        result=Query("{from:'spawned_free_colonists',args:{name:'Walter',map_id:'map1'},select:['id','name']}");
        Ok(result,"Current colonist selection");
        Require((int?)result.Data?["results"]?[0]?["matched_count"]==1,"Name/map selectors changed");
        source=new FakeData();
        result=Query("{from:'spawned_free_colonists',select:['id'],limit:1,offset:1}");
        Require((string?)result.Data?["results"]?[0]?["rows"]?[0]?["id"]=="pawn2" && (bool?)result.Data?["results"]?[0]?["page"]?["has_more"]==true,"Current offset paging changed");
        source=new FakeData();
        result=Query("{from:'pawn',args:{id:'pawn1'},select:{health:{select:{conditions:{select:['id','severity'],where:{severity:{gt:1}},limit:1}}}}}");
        Require(result.Status=="ok" && (int?)result.Data?["results"]?[0]?["rows"]?[0]?["health"]?["conditions"]?["matched_count"]==2,"Nested health filter no longer precedes window");
        source=new FakeData {FailCondition=true};
        result=Query("{from:'pawn',args:{id:'pawn1'},select:{health:{select:{conditions:{select:['id','severity'],limit:1}}}}}");
        Require(result.Status=="partial" && result.Issues.Length>0,"Unavailable condition became successful");
        source=new FakeData {MissingMood=true};
        result=Query("{from:'pawn',args:{id:'pawn1'},select:['mood.level']}");
        Require(result.Status=="ok" && result.Data?["results"]?[0]?["rows"]?[0]?["mood"]?.Type==JTokenType.Null,"Applicable null became a failure");
        source=new FakeData {EmptyConditions=true};
        result=Query("{from:'pawn',args:{id:'pawn1'},select:['health.conditions']}");
        Require(result.Status=="ok" && (int?)result.Data?["results"]?[0]?["rows"]?[0]?["health"]?["conditions"]?["matched_count"]==0,"Empty conditions became unavailable");
        source=new FakeData();
        result=Query("{requests:[{from:'pawn',args:{id:'pawn1'},select:{stats:{args:{def_names:['MoveSpeed']},select:['def_name','value']}}},{from:'pawn',args:{id:'pawn1'},select:{stats:{args:{def_names:['WorkSpeedGlobal']},select:['def_name','value']}}}]}");
        Ok(result,"Stat arguments use separate cache entries");
        Require((string?)result.Data?["results"]?[0]?["rows"]?[0]?["stats"]?["rows"]?[0]?["def_name"]=="MoveSpeed" && (string?)result.Data?["results"]?[1]?["rows"]?[0]?["stats"]?["rows"]?[0]?["def_name"]=="WorkSpeedGlobal","Distinct StatDef arguments collided");
        Require(!source.Reads.Any(x=>x.Contains(":Health") || x.Contains(":Profile")),"Stat request eagerly read unrelated sections");
        source=new FakeData();
        result=Query("{from:'pawn',args:{id:'pawn1'},select:{stat_definitions:{args:{search:'Move'},select:['def_name','label']}}}");
        Require(result.Status=="ok" && (string?)result.Data?["results"]?[0]?["rows"]?[0]?["stat_definitions"]?["rows"]?[0]?["def_name"]=="MoveSpeed" && !source.Reads.Any(x=>x.Contains(":Stat.") || x.EndsWith(":Pawn.stats")),"Stat discovery calculated values or lost identity");
        source=new FakeData();
        result=Query("{requests:[{from:'pawn',args:{id:'pawn1'},select:{health:{select:{conditions:{args:{visible_only:true},aggregate:{n:{op:'count'}},limit:0}}}}},{from:'pawn',args:{id:'pawn1'},select:{health:{select:{conditions:{args:{visible_only:false},aggregate:{n:{op:'count'}},limit:0}}}}}]}");
        Require(result.Status=="ok" && (int?)result.Data?["results"]?[0]?["rows"]?[0]?["health"]?["conditions"]?["aggregates"]?["n"]?["value"]==3 && (int?)result.Data?["results"]?[1]?["rows"]?[0]?["health"]?["conditions"]?["aggregates"]?["n"]?["value"]==4 && !source.Reads.Any(x=>x.Contains(":Condition.")),"Visibility arguments collided or count read per-record detail");
        source=new FakeData();
        result=Query("{from:'pawn',args:{id:'pawn1'},select:{health:{select:{conditions:{select:['id','part.key','part.parent_key'],limit:1},body_parts:{select:['key','parent_key']}}}}}");
        Ok(result,"Native condition/body-part relationships");
        var health=result.Data!["results"]![0]!["rows"]![0]!["health"]!;
        var part=health["conditions"]!["rows"]![0]!["part"]!;
        Require((string?)part["key"]=="pawn1-part1" && (string?)part["parent_key"]=="pawn1-part0" && health["body_parts"]!["rows"]!.Any(x=>(string?)x["key"]==(string?)part["key"]),"Body-part identities or relationship changed");
        source=new FakeData();
        result=Query("{from:'pawn',args:{id:'pawn1'},select:{mood:{select:{thought_groups:{select:{member_count:true,members:{select:['ref','kind','other_pawn_id'],limit:1}},limit:1}}}}}");
        Ok(result,"Thought groups with independent member windows");
        var group=result.Data!["results"]![0]!["rows"]![0]!["mood"]!["thought_groups"]!["rows"]![0]!;
        Require((int?)group["member_count"]==2 && (int?)group["members"]?["matched_count"]==2 && (bool?)group["members"]?["page"]?["has_more"]==true,"Thought group member count/window changed");
        var referenceRequest=JObject.Parse("{from:'pawn',args:{id:'pawn1'},select:{mood:{select:{memories:{select:['ref','reference_scope'],limit:1}}}}}");
        var refs=engine.Execute("query",referenceRequest,source,Session);
        var laterRefs=engine.Execute("query",referenceRequest,source,new SessionDescriptor(Session.ColonyId,Session.LoadEpoch,101,8));
        var currentRef=refs.Data!["results"]![0]!["rows"]![0]!["mood"]!["memories"]!["rows"]![0]!;
        var laterRef=laterRefs.Data!["results"]![0]!["rows"]![0]!["mood"]!["memories"]!["rows"]![0]!;
        Require((string?)currentRef["reference_scope"]=="observation" && (string?)currentRef["ref"]!=(string?)laterRef["ref"],"Thought references persisted across observation samples");
        source=new FakeData {FailMood=true};
        result=Query("{from:'pawn',args:{id:'pawn1'},select:['mood.level','health.summary.health_percent']}");
        Require(result.Status=="partial" && (double?)result.Data?["results"]?[0]?["rows"]?[0]?["health"]?["summary"]?["health_percent"]==0.75,"Failed mood discarded independent health");
        source=new FakeData {OversizedConditions=true};
        result=Query("{from:'pawn',args:{id:'pawn1'},select:['health.conditions.id']}");
        Require(result.Status=="partial" && result.Issues.Any(i=>i.Code=="node_scan_limit"),"Unbounded nested source bypassed guard");
        foreach(var request in new[]{"{from:'pawn',args:{id:'pawn1',first:1},select:['id']}","{from:'pawn',args:{id:'pawn1'},select:['invented']}","{from:'spawned_free_colonists',limit:101,select:['id']}","{from:'pawn',args:{id:'pawn1'},select:{health:{select:{conditions:{limit:101,select:['id']}}}}}","{from:'pawn',args:{id:'pawn1'},select:{health:{select:{conditions:{offset:-1,select:['id']}}}}}","{from:'pawn',args:{id:'pawn1'},select:{health:{select:{conditions:{offset:8193,select:['id']}}}}}","{from:'pawn',args:{id:'pawn1'},select:{stats:{args:{def_names:[]},select:['value']}}}"}) {
            source=new FakeData();result=Query(request);
            Require(result.Status=="failed" && source.Reads.Count==0,"Invalid current query performed reads");
        }
        Console.WriteLine("Query engine tests passed: retired operations perform zero reads; current getters, sections, cache, filters, offset paging, partial/null/empty and pre-read rejection.");
    }
    private static void ThoughtReferenceIdentity()
    {
        var source = new FakeData();
        var pawn = new GameEntity("pawn1", "map1", new object());
        var sharedThought = new object();
        var memory = new GameNode("Thought", "mood/memories/0", pawn, sharedThought);
        var member = new GameNode("Thought", "mood/groups/1/members/2", pawn, sharedThought);
        var different = new GameNode("Thought", "mood/memories/0", pawn, new object());
        var context = new ReadContext(source, Session, 1000);
        var reference = (string?)context.ReadNode(memory, "ref");
        Require(!string.IsNullOrEmpty(reference) && reference == (string?)context.ReadNode(member, "ref"), "same Thought instance received different refs through memory and group paths");
        Require(reference != (string?)context.ReadNode(different, "ref"), "distinct Thought handles sharing a path received the same ref");
        Require(reference == (string?)context.ReadNode(memory, "ref"), "repeated read changed a Thought ref within the observation");
        var next = new ReadContext(source, new SessionDescriptor(Session.ColonyId, Session.LoadEpoch, 101, 8), 1000);
        Require(reference != (string?)next.ReadNode(memory, "ref"), "Thought ref survived a snapshot change");
        Require((string?)context.ReadNode(memory, "referenceScope") == "observation" && source.Reads.Count == 0, "detached reference metadata performed game reads");
    }

    private static void Ok(QueryOutcome result,string scenario) => Require(result.Status=="ok",scenario+": "+result.Status+" "+result.Message+" "+string.Join(";",result.Issues.Select(i=>i.Message)));
    private static void Require(bool pass,string message) { if(!pass)throw new InvalidOperationException(message); }

    private sealed class FakeData : IGameData
    {
        public readonly List<string> Reads = new();
        public readonly List<int> Threads = new();
        public bool FailMood, FailCondition, MissingMood, EmptyConditions, OversizedConditions;
        private void Mark(string key) { Reads.Add(key); Threads.Add(Thread.CurrentThread.ManagedThreadId); }
        public IEnumerable<GameEntity> Maps() { Mark("Maps"); yield return new GameEntity("map1", null, this); }
        public IEnumerable<GameEntity> Colonists() { Mark("Colonists"); for (var i = 1; i <= 3; i++) yield return new GameEntity("pawn" + i, "map1", this); }
        public object? GameField(string field) { Mark("Game." + field); return field == "paused" ? (object)false : "map1"; }
        public object? MapField(GameEntity map, string field) { Mark("Map." + field); return field switch { "name" => "Home", "colonistCount" => 3, _ => (object)false }; }
        public object? ColonistField(GameEntity pawn, string field)
        {
            Mark(pawn.Id + ":Pawn." + field);
            if (field != "name") throw new InvalidOperationException("Pawn root may only read name directly.");
            return pawn.Id == "pawn1" ? "Walter" : "Colonist " + pawn.Id;
        }
        public object? NodeField(GameNode node, string field, IReadOnlyDictionary<string, object?> arguments)
        {
            Mark(node.Owner.Id + ":" + node.Kind + "." + field);
            var index = node.Handle is int value ? value : 0;
            switch (node.Kind)
            {
                case "Pawn":
                    if (field == "mood" && MissingMood) return null;
                    if (field == "equipment") return Child(node, "Pawn_EquipmentTracker");
                    if (field == "apparel") return Child(node, "Pawn_ApparelTracker");
                    if (field == "inventory") return Child(node, "Pawn_InventoryTracker");
                    if (field == "carryTracker") return Child(node, "Pawn_CarryTracker");
                    if (field == "statDefinitions") return Many(node, "StatDefinition", 1);
                    if (field == "stats") return ((IEnumerable)arguments["defNames"]!).Cast<object>()
                        .Select(x => new GameNode("Stat", node.Key + "/Stat/" + x, node.Owner, x.ToString())).ToArray();
                    return Child(node, char.ToUpperInvariant(field[0]) + field.Substring(1));
                case "Status": return field switch { "currentJob" => Child(node, "Job"), "position" => Child(node, "Position"), "needs" => Many(node, "Need", 2), "mentalState" => null, _ => (object)false };
                case "Position": return field == "mapId" ? node.Owner.MapId : (object)5;
                case "Job": return field switch { "id" => "job1", "defName" => "Wait", "report" => "Standing", "targets" => Many(node, "JobTarget", 1), _ => null };
                case "JobTarget": return field switch { "slot" => "A", "kind" => "thing", "thingId" => "thing1", "mapId" => "map1", _ => (object)5 };
                case "Need": return field switch { "defName" => index == 0 ? "Food" : "Rest", "label" => index == 0 ? "Food" : "Rest", "maxLevel" => 1.0, _ => 0.6 };
                case "Mood":
                    if (field == "level" && FailMood) throw new InvalidOperationException("simulated mood unavailable");
                    return field switch { "level" => 0.6, "instant" => 0.65, "breakThresholds" => Child(node, "Thresholds"), "thoughtGroups" => Many(node, "ThoughtGroup", 2), "memories" => Many(node, "Thought", 3), _ => null };
                case "Thresholds": return field switch { "minor" => 0.35, "major" => 0.2, _ => 0.05 };
                case "ThoughtGroup": return field switch { "defName" => "Comfortable" + index, "label" => "Comfortable", "stageIndex" => index, "moodOffset" => 5.0, "memberCount" => 2, "members" => Many(node, "Thought", 2), _ => null };
                case "Thought": return field switch { "ref" => node.Key, "defName" => "AteFineMeal", "label" => "Ate fine meal", "description" => "A good meal.", "stageIndex" => 0, "moodOffset" => 5.0, "kind" => "memory", "otherPawnId" => "pawn2", "ageTicks" => 5, "durationTicks" => 60000, _ => null };
                case "Health": return field switch { "summary" => Child(node, "HealthSummary"), "capacities" => Many(node, "Capacity", 2), "conditions" => Many(node, "Condition", EmptyConditions ? 0 : OversizedConditions ? 8193 : arguments.TryGetValue("visibleOnly", out var visible) && visible is false ? 4 : 3), "bodyParts" => Many(node, "BodyPart", 3), _ => null };
                case "HealthSummary": return field switch { "healthPercent" => 0.75, "pain" => 0.1, "bleedRate" => 0.0, _ => (object)false };
                case "Capacity": return field switch { "defName" => index == 0 ? "Moving" : "Consciousness", "label" => index == 0 ? "Moving" : "Consciousness", _ => (object)0.8 };
                case "Condition":
                    if (field == "severity" && FailCondition) throw new InvalidOperationException("simulated condition unavailable");
                    return field switch {
                        "id" => node.Owner.Id + "-condition" + index, "defName" => "Bruise", "label" => "Bruise " + index,
                        "description" => "An injury.", "severity" => 0.1 + index, "severityLabel" => "10%", "visible" => true,
                        "part" => Child(node, "BodyPart", 1), "tending" => Child(node, "Tending"), "immunity" => null, _ => null
                    };
                case "BodyPart": return field switch { "key" => node.Owner.Id + "-part" + index, "defName" => index == 0 ? "Torso" : "Arm", "label" => index == 0 ? "Torso" : "Arm " + index, "index" => index, "parentKey" => index == 0 ? null : node.Owner.Id + "-part0", "health" => 20.0, "maxHealth" => 30.0, _ => null };
                case "Tending": return field switch { "isTended" => true, "quality" => 0.8, _ => 1000 };
                case "Immunity": return field == "level" ? (object)0.5 : false;
                case "Pawn_EquipmentTracker": return field == "Primary" ? null : Many(node, "Thing", 0);
                case "Pawn_ApparelTracker": return Many(node, "Thing", 2);
                case "Pawn_InventoryTracker": return Child(node, "ThingOwner");
                case "Pawn_CarryTracker": return null;
                case "ThingOwner": return field == "items" ? Many(node, "Thing", 1) : null;
                case "Thing": return field switch { "id" => node.Owner.Id + "-item" + index, "runtimeType" => "RimWorld.Apparel", "def" or "Stuff" => Child(node, "ThingDef"), "stackCount" => 1, "HitPoints" => 50, "MaxHitPoints" => 100, _ => null };
                case "ThingDef": return field switch { "defName" => "Shirt", "label" => "Cloth shirt", "category" => "Item", "thingClass" => "RimWorld.Apparel", _ => null };
                case "Profile": return field switch { "age" => Child(node, "Age"), "backstories" => Many(node, "Backstory", 2), "traits" => Many(node, "Trait", 2), "skills" => Many(node, "Skill", 3), _ => null };
                case "Age": return field switch { "biologicalYears" => 30.5, "chronologicalYears" => 130.5, _ => "Adult" };
                case "Backstory": return field switch { "slot" => index == 0 ? "childhood" : "adulthood", "defName" => "Farmer" + index, "title" => "Farmer", _ => "Grew food." };
                case "Trait": return field switch { "defName" => "Kind", "label" => "Kind", "degree" => 0, "suppressed" => false, _ => null };
                case "Skill": return field switch { "defName" => "Plants" + index, "label" => "Plants", "level" => 10, "passion" => "Major", "aptitude" => 1, "totallyDisabled" or "permanentlyDisabled" => false, "xpSinceLastLevel" => 25.0, "xpRequiredForLevelUp" => 1000.0, _ => null };
                case "Stat": return field switch { "defName" => node.Handle as string, "label" => "Selected stat", "value" => 4.0, _ => "4" };
                case "StatDefinition": return field switch { "defName" => "MoveSpeed", "label" => "Move speed", _ => "Movement cells per second." };
                default: throw new InvalidOperationException("Unexpected node kind " + node.Kind + "." + field);
            }
        }
        private static GameNode Child(GameNode parent, string kind, int index = 0) => new(kind, parent.Key + "/" + kind + "/" + index, parent.Owner, index);
        private static IEnumerable<GameNode> Many(GameNode parent, string kind, int count)
        { for (var i = 0; i < count; i++) yield return Child(parent, kind, i); }
    }
}
