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
        Check(domains.Count == 10, "native enum field coverage");
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
                Check(Compile("eq", name)("UnregisteredFutureValue",state)==null,pair.Name+" unknown read became false");
            }
            foreach(var op in new[]{"eq","ne","in","contains"}) {
                try { Compile(op,op=="in" ? new JArray("Invented") : new JValue("Invented"));throw new Exception("Invalid literal/operator accepted for "+pair.Name); }
                catch(ExecutionError) { }
            }
            var nullField = conditions.Compile("Probe",new JObject { ["fields"] = new JObject { ["value"] = new JObject { ["is_null"] = true } } });
            Check(nullField(new object?[]{null},state)==true,pair.Name+" null availability");
            try { Compile("eq",JValue.CreateNull());throw new Exception("eq:null accepted"); } catch(ExecutionError) { }
            if (domain.Flags) {
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
        Console.WriteLine("Native enum tests passed: all declared values, operators, flags, case folding and unknown reads across10 domains.");
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
