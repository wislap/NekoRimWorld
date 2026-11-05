using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class ColonyObservationContract
{
    internal const string Capability = "colony_observation.v1";
    internal static JObject Catalog() => new() { ["version"] = Capability, ["capability"] = Capability, ["transport"] = "policy_request", ["source"] = "planner", ["replacement"] = "entire_connection_load_scope", ["supported_kinds"] = new JArray("construction", "production", "food_policy", "resources"), ["condition_schema"] = JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'id':{'type':'string','minLength':1},'kind':{'type':'string','enum':['construction','production','food_policy','resources']},'map_id':{'type':'string','minLength':1},'plan_id':{'type':'string','minLength':1},'target_ids':{'type':'array','minItems':1,'maxItems':64,'uniqueItems':true,'items':{'type':'string','minLength':1}},'max_samples':{'type':'integer','minimum':1,'maximum':1000},'expires_at_tick':{'type':'integer','minimum':1},'sample_interval_ticks':{'type':'integer','minimum':1,'maximum':60000,'default':250}},'required':['id','kind','target_ids','max_samples','expires_at_tick']}") , ["limits"] = new JObject { ["observations"] = 16, ["conditions_per_observation"] = 8, ["targets"] = 64, ["samples"] = 1000, ["lifetime_ticks"] = 600000, ["default_interval_ticks"] = 250, ["paused_interval_ms"] = 2000, ["event_bytes"] = 262144, ["target_reads_per_update"] = 1 } };
    internal static void Validate(JObject value, bool result = false) {
        void Bad() => throw new ProtocolException(ProtocolErrorCodes.InvalidPayload, "Invalid planner observation policy.");
        bool Text(JToken? t) => t?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string?)t) && ((string)t!).Length <= 256;
        if ((string?)value["source"] != "planner" || value.Properties().Any(p => p.Name != "source" && p.Name != "observations" && (!result || p.Name != "registrations")) || value["observations"] is not JArray observations || observations.Count > 16) { Bad(); return; }
        var ids = new HashSet<string>(StringComparer.Ordinal); int targets = 0;
        foreach (var token in observations) {
            if (token is not JObject row || row.Properties().Count() != 3 || !Text(row["observation_id"]) || !ids.Add((string)row["observation_id"]!) || row["revision"] == null || (row["revision"]!.Type != JTokenType.Integer && !Text(row["revision"])) || row["conditions"] is not JArray conditions || conditions.Count < 1 || conditions.Count > 8) { Bad(); continue; }
            var conditionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in conditions) {
                if (c is not JObject o || o.Properties().Any(p => !new[] { "id", "kind", "map_id", "plan_id", "target_ids", "max_samples", "expires_at_tick", "sample_interval_ticks" }.Contains(p.Name)) || !Text(o["id"]) || !conditionIds.Add((string)o["id"]!) || !new[] { "construction", "production", "food_policy", "resources" }.Contains((string?)o["kind"]) || o["target_ids"] is not JArray ts || ts.Count == 0 || ts.Any(t => !Text(t)) || ts.Values<string>().Distinct(StringComparer.Ordinal).Count() != ts.Count || o["max_samples"]?.Type != JTokenType.Integer || (long)o["max_samples"]! < 1 || (long)o["max_samples"]! > 1000 || o["expires_at_tick"]?.Type != JTokenType.Integer || (long)o["expires_at_tick"]! < 1 || o["map_id"] != null && !Text(o["map_id"]) || o["kind"]?.Value<string>() != "food_policy" && !Text(o["map_id"]) || o["kind"]?.Value<string>() == "construction" && !Text(o["plan_id"]) || o["sample_interval_ticks"] != null && (o["sample_interval_ticks"]!.Type != JTokenType.Integer || (long)o["sample_interval_ticks"]! < 1 || (long)o["sample_interval_ticks"]! > 60000)) { Bad(); continue; }
                targets += ts.Count;
            }
        }
        if (targets > 64 || result && value["registrations"] is not JArray) Bad();
        if (result && value["registrations"] is JArray acknowledgements) {
            if (acknowledgements.Count != observations.Count) Bad();
            for (int index = 0; index < acknowledgements.Count; index++) {
                if (acknowledgements[index] is not JObject ack || observations[index] is not JObject observation || ack.Properties().Any(p => !new[] { "observation_id", "revision", "status", "covered_condition_ids", "receipt_id", "reason" }.Contains(p.Name)) || !JToken.DeepEquals(ack["observation_id"], observation["observation_id"]) || !JToken.DeepEquals(ack["revision"], observation["revision"]) || !Text(ack["receipt_id"]) || !Guid.TryParseExact((string?)ack["receipt_id"], "N", out _) || ack["covered_condition_ids"] is not JArray covered || !new[] { "registered", "rejected" }.Contains((string?)ack["status"])) { Bad(); continue; }
                var expected = (string?)ack["status"] == "registered" ? ((JArray)observation["conditions"]!).Select(c => (string?)c["id"]).ToArray() : Array.Empty<string?>();
                if (!covered.Values<string>().SequenceEqual(expected) || (string?)ack["status"] == "rejected" && !Text(ack["reason"])) Bad();
            }
        }
    }
}
