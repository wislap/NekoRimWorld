using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
namespace Neko.RimWorld;
internal static class WorldPresentationTests
{
    internal static void Run()
    {
        void Require(bool ok, string reason) { if (!ok) throw new Exception(reason); }
        var filter = JObject.Parse("{'capabilities':['medicine']}");
        var counts = JObject.Parse("{'records':1,'capabilities':[{'capability':'sleeping_place','records':0}],'scan_complete':false}");
        WorldPresentation.ObjectScope(counts, filter);
        Require((bool?)counts["scope"]?["include_held"] == true && (bool?)counts["scope"]?["visible_only"] == true, "Native visibility policy must use the existing evidence flags");
        filter["capabilities"] = new JArray("sleeping_place");
        Require((string?)counts["scope"]?["selection"]?["capabilities"]?[0] == "medicine", "Scope must be detached from mutable request");
        Require((int?)counts["records"] == 1 && (bool?)counts["scan_complete"] == false, "Scope changed observed facts");
        WorldPresentation.ObjectScope(counts, null);
        Require(counts["scope"]!["selection"]!.Type == JTokenType.Null, "Unfiltered scope not explicit");

        var reference = JObject.Parse("{'id':'wall','position':{'x':1,'z':2},'occupied_rect':{'min_x':1},'faction':null,'pawn_membership':null,'holder_chain':{'rows':[]},'runtime_type':'Wall','holder_type':'Map','capabilities':['building'],'mod_extra':'preserve'}");
        var env = new JObject { ["roof_support_objects"] = new JArray(reference), ["roof_support_objects_total"] = 101, ["coverage"] = new JObject { ["complete"] = false }, ["attack_influences"] = new JArray(new JObject { ["status"] = "partial", ["path_indices"] = new JArray(0, 1) }) };
        var original = (JObject)env.DeepClone();
        WorldPresentation.Environment(env);
        var compact = (JObject)env["roof_support_objects"]![0]!;
        Require(compact["holder_chain"] == null && (string?)compact["mod_extra"] == "preserve", "Compaction removed undeclared fields");
        foreach (var key in new[] { "id", "position", "occupied_rect", "faction", "pawn_membership" }) Require(JToken.DeepEquals(compact[key], original["roof_support_objects"]![0]![key]), "Identity/location changed: " + key);
        foreach (var key in new[] { "roof_support_objects_total", "coverage", "attack_influences" }) Require(JToken.DeepEquals(env[key], original[key]), "Environment facts changed: " + key);
        var once = (JObject)env.DeepClone(); WorldPresentation.Environment(env);
        Require(JToken.DeepEquals(once, env), "Formatting is not idempotent");

        var reservations = JObject.Parse("{'normal':[],'physical_interaction':[],'destinations':[{'cell':{'x':1,'z':2},'job':{'targets':[{'x':8,'z':9}]}}]}");
        var records = reservations["destinations"]!.DeepClone();
        WorldPresentation.Reservations(reservations, new JObject { ["id"] = "bed" });
        Require(JToken.DeepEquals(records, reservations["destinations"]), "Reserved cell confused with job target");
        Require((string?)reservations["scope"]?["reservations_claimed_by_target"] == "not_queried", "Target-side emptiness presented as claimant-side absence");
        WorldPresentation.Reservations(reservations, new JObject { ["x"] = 1, ["z"] = 2 }, true);
        Require(reservations["scope"]?["target"]?["id"] == null, "Cell target changed into object target");
        var work = JObject.Parse("{'work_types':[{'disabled':false,'priority':0}],'queued_jobs':[]}");
        var facts = work["work_types"]!.DeepClone(); WorldPresentation.Work(work);
        Require(JToken.DeepEquals(facts, work["work_types"]), "Presentation changed native work settings");

        var rows = new List<JObject>(); for (int i=0; i<3; i++) rows.Add(new JObject { ["id"] = i, ["text"] = new string('x', 600) });
        var large = new JObject { ["environment"] = new JObject { ["roof_support_objects"] = new JArray(new JObject { ["id"] = "wall", ["holder_chain"] = new string('x', 3000) }) }, ["objects_summary"] = new JObject { ["records"] = 3 } };
        var small = (JObject)large.DeepClone(); WorldPresentation.Environment((JObject)small["environment"]!);
        WorldResultPaging.Put(large, rows, 0, 3, "cursor", 3000);
        WorldResultPaging.Put(small, rows, 0, 3, "cursor", 3000);
        Require((int?)large["page"]?["returned"] == 1 && (int?)small["page"]?["returned"] == 3, "Compaction must precede page fitting");
        Require((int?)small["objects_summary"]?["records"] == 3, "Page fitting changed aggregate");
        var bounded = JObject.Parse("{'skills':[1,2,3],'inventory':[1,2],'inventory_total':7,'inventory_complete':false,'conditions':null}");
        WorldPresentation.LimitRows(bounded, 1);
        Require((int?)bounded["skills_total"] == 3 && (bool?)bounded["skills_complete"] == false && ((JArray)bounded["skills"]!).Count == 1, "Nested display limit lost total/coverage");
        Require((int?)bounded["inventory_total"] == 7 && (bool?)bounded["inventory_complete"] == false, "Existing partial collection marked complete");
        Require(bounded["conditions"]!.Type == JTokenType.Null, "Unavailable list became empty");
        var completeTables = JObject.Parse("{'skills':[1,2,3],'capacities':[1,2],'work_types':[1,2,3,4],'body_parts':[1,2,3],'inventory':[1,2],'inventory_total':7,'inventory_complete':false}");
        WorldPresentation.LimitRows(completeTables, 1, "skills", "capacities", "work_types");
        Require(((JArray)completeTables["skills"]!).Count == 3 && (bool?)completeTables["skills_complete"] == true, "Low detail hid skills");
        Require(((JArray)completeTables["capacities"]!).Count == 2 && ((JArray)completeTables["work_types"]!).Count == 4, "Low detail hid capacity/work tables");
        Require(((JArray)completeTables["body_parts"]!).Count == 1 && (bool?)completeTables["body_parts_complete"] == false, "Long details were not bounded");
        Require((int?)completeTables["inventory_total"] == 7 && (bool?)completeTables["inventory_complete"] == false, "Prior incomplete state lost");
        var partialTable = JObject.Parse("{'skills':[1,2],'skills_total':7,'skills_complete':false}");
        WorldPresentation.LimitRows(partialTable, 1, "skills");
        Require((bool?)partialTable["skills_complete"] == false && (int?)partialTable["skills_total"] == 7, "Existing failed table marked complete");
        Console.WriteLine("World presentation tests passed: scoped counts, target direction, unknowns, preserved facts, idempotence and page fitting.");
    }
}
