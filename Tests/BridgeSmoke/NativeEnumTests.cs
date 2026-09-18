using System;
using System.Linq;
using System.Collections.Generic;
using GraphQL;
using GraphQL.Types;
using GraphQL.Resolvers;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;
internal static class NativeEnumTests
{
    internal static void Run()
    {
        var session = new SessionDescriptor("colony", "load", 1, 1);
        var engine = new GameQueryEngine();
        var catalog = engine.Execute("query_catalog", new JObject(), null!, session).Data!;
        var domains = (JObject)catalog["public_query"]!["value_domains"]!;
        Check(domains.Count == 12, "native enum field coverage");
        foreach (var pair in domains.Properties()) {
            var domain = new NativeEnumScalar(pair.Name);
            var root = new ObjectGraphType { Name = "Probe" };
            var field = new FieldType { Name = "value", ResolvedType = new StringGraphType(), Resolver = new FuncFieldResolver<object?>(c => c.Source is object[] box ? box[0] : c.Source) };
            field.Metadata["native_enum"] = domain; root.AddField(field);
            using var schema = new Schema { Query = root }; schema.Initialize();
            var conditions = new SelectionConditions(schema);
            SelectionConditions.Predicate Compile(string op, JToken value, bool sensitive=true) => conditions.Compile("Probe",new JObject {
                ["fields"] = new JObject { ["value"] = new JObject { [op] = value, ["case_sensitive"] = sensitive } }
            });
            var state = new ReadContext(null!, session, 10000);
            foreach (var name in ((JArray)pair.Value["values"]!).Values<string>()) {
                Check(Compile("eq", name!)(name, state)==true, pair.Name+" eq "+name);
                Check(Compile("ne", name!)(name, state)==false, pair.Name+" ne "+name);
                Check(Compile("in", new JArray(name!))(name,state)==true,pair.Name+" in "+name);
                Check(Compile("eq", name!.ToLowerInvariant(),false)(name,state)==true,pair.Name+" case fold");
                foreach (var op in new[]{"eq","ne","in"})
                    Check(Compile(op,op=="in" ? new JArray(name) : new JValue(name))("UnregisteredFutureValue",state)==null,pair.Name+" unknown " + op + " became known");
                if (name != name.ToLowerInvariant()) {
                    try { Compile("eq",name.ToLowerInvariant());throw new Exception("Default case sensitivity lost"); } catch(ExecutionError) { }
                }
            }
            foreach(var op in new[]{"eq","ne","in","contains"}) {
                try { Compile(op,op=="in" ? new JArray("Invented") : new JValue("Invented"));throw new Exception("Invalid literal/operator accepted for "+pair.Name); }
                catch(ExecutionError) { }
            }
            var nullField = conditions.Compile("Probe",new JObject { ["fields"] = new JObject { ["value"] = new JObject { ["is_null"] = true } } });
            Check(nullField(new object?[]{null},state)==true,pair.Name+" null availability");
            try { Compile("eq",JValue.CreateNull());throw new Exception("eq:null accepted"); } catch(ExecutionError) { }
            if (domain.Flags) {
                foreach (var op in new[]{"has_all","has_any"}) {
                    Check(Compile(op,"Meat")("UnknownFlag",state)==null,"unknown flags became known");
                    try { Compile(op,string.Join(",",Enumerable.Repeat("Meat",1000)));throw new Exception("Oversized flags accepted"); } catch(ExecutionError) { }
                }
                Check(Compile("eq",string.Join(",",Enumerable.Repeat("Meat",800)))("Meat",state)==true,"bounded repeated flags changed native union");
                Check(Compile("eq","Corpse, Meat")("Meat, Corpse",state)==true,"flag ordering");
                Check(Compile("has_all","Meat")("Meat, Corpse",state)==true,"has_all");
                Check(Compile("has_any","VegetableOrFruit")("Meat, Corpse",state)==false,"has_any");
                Check(Compile("has_any","None")("Meat, Corpse",state)==false,"empty any flags");
                Check(Compile("has_all","None")("Meat, Corpse",state)==true,"empty all flags");
            }
        }
        Check(!new NativeEnumScalar("Verse.ThingCategory").Valid("Corpse") && new NativeEnumScalar("RimWorld.FoodTypeFlags").Valid("Corpse"),"domain-local validity");
        var unread = new CountingData();
        var rejected = engine.Execute("query",JObject.Parse("{requests:[{from:'game'},{from:'things',where:{'definition.category':{eq:'Corpse'}},aggregate:{n:{op:'count'}}}]}"),unread,session);
        Check(rejected.Status=="failed" && unread.Reads==0,"Invalid enum batch performed reads");
        // Real public paths exercise attachment and translation, not just a hand-built scalar probe.
        var routes = new[] {
            ("things", "{'definition.category':{eq:'Invented'}}", "Verse.ThingCategory"),
            ("thing_defs", "{'ingestible.food_type':{has_any:'Invented'}}", "RimWorld.FoodTypeFlags"),
            ("thing_defs", "{'ingestible.drug_category':{eq:'Invented'}}", "RimWorld.DrugCategory"),
            ("things", "{all_comps:{some:{on:{CompQuality:{quality:{eq:'Invented'}}}}}}", "RimWorld.QualityCategory"),
            ("things", "{all_comps:{every:{on:{CompRottable:{stage:{eq:'Invented'}}}}}}", "RimWorld.RotStage"),
            ("things", "{'storage_group.settings.priority':{eq:'Invented'}}", "RimWorld.StoragePriority"),
            ("spawned_free_colonists", "{'profile.gender':{eq:'Invented'}}", "Verse.Gender"),
            ("spawned_free_colonists", "{'profile.backstories':{some:{slot:{eq:'Invented'}}}}", "RimWorld.BackstorySlot"),
            ("spawned_free_colonists", "{'profile.skills':{none:{passion:{eq:'Invented'}}}}", "RimWorld.Passion"),
            ("spawned_free_colonists", "{'status.current_job.targets':{some:{slot:{eq:'Invented'}}}}", "Verse.AI.TargetIndex"),
            ("spawned_free_colonists", "{'faction.player_relation':{eq:'Invented'}}", "RimWorld.FactionRelationKind"),
            ("spawned_free_colonists", "{'guest_status':{eq:'Invented'}}", "RimWorld.GuestStatus")
        };
        foreach (var route in routes) {
            var condition = JObject.Parse(route.Item2);
            foreach (var wrapped in new[]{condition, new JObject { ["where"]=condition.DeepClone() }, new JObject { ["any"]=new JArray(condition.DeepClone()) }}) {
                unread.Reads=0;
                var query=new JObject { ["requests"]=new JArray(new JObject { ["from"]="game" },new JObject { ["from"]=route.Item1,["where"]=wrapped }) };
                var outcome=engine.Execute("query",query,unread,session);
                Check(outcome.Status=="failed" && unread.Reads==0 && outcome.Message.Contains("Native_"+route.Item3.Replace('.','_')) && outcome.Message.Contains("Declared names"),"Real enum path missed validation: "+route+" / "+outcome.Message);
            }
            var description=engine.Execute("describe_query",new JObject { ["types"]=new JArray(route.Item3) },unread,session);
            Check(description.Status=="ok" && description.Data!.ToString().Contains(route.Item3),"Domain discovery failed: "+route.Item3);
        }
        unread.Reads=0;
        var nested=engine.Execute("query",JObject.Parse("{from:'spawned_free_colonists',select:{profile:{select:{skills:{where:{passion:{eq:'Invented'}},select:['def_name']}}}}}"),unread,session);
        Check(nested.Status=="failed" && unread.Reads==0 && nested.Message.Contains("Declared names"),"Nested projection enum validation missed");
        unread.Reads=0;
        var longFlags=engine.Execute("query",new JObject { ["from"]="thing_defs",["where"]=new JObject { ["ingestible.food_type"]=new JObject { ["has_any"]=string.Join(",",Enumerable.Repeat("Meat",1000)) } } },unread,session);
        Check(longFlags.Status=="failed" && unread.Reads==0,"Oversized flag literal performed reads");
        Console.WriteLine("Native enum tests passed: all declared values, operators, flags, case folding and unknown reads across12 domains.");
    }
    private sealed class CountingData : IGameData {
        internal int Reads;
        public IEnumerable<GameEntity> Maps() { Reads++; return Array.Empty<GameEntity>(); }
        public IEnumerable<GameEntity> Colonists() { Reads++; return Array.Empty<GameEntity>(); }
        public object? GameField(string field) { Reads++; return null; }
        public object? MapField(GameEntity e,string field) { Reads++; return null; }
        public object? ColonistField(GameEntity e,string field) { Reads++; return null; }
        public object? NodeField(GameNode n,string field,IReadOnlyDictionary<string,object?> args) { Reads++; return null; }
    }
    private static void Check(bool value,string message) { if(!value)throw new Exception(message); }
}
