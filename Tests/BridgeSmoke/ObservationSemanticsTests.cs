using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class ObservationSemanticsTests
{
    internal static void Run()
    {
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        var corridor = JObject.Parse("{'source':{'id':'enemy'},'status':'partial','attack_trackers_complete':false,'affected_observed_cells':103,'example_cells':[{'x':9,'z':9}],'path_indices':[1,2],'source_inside_footprint':true}");
        var before = corridor.DeepClone();
        var projected = WorldPresentation.AttackForPath(corridor, new[] { JObject.Parse("{'x':1,'z':0}"), JObject.Parse("{'x':2,'z':0}") }, false);
        Require((int?)projected["affected_observed_cells"] == 2 && (bool?)projected["source_inside_footprint"] == false, "Path attack retained corridor population");
        Require((int?)projected["example_cells"]?[0]?["x"] == 1 && (string?)projected["status"] == "partial", "Path projection lost samples or partial status");
        Require(JToken.DeepEquals(corridor, before), "Path projection mutated corridor evidence");
        var outside = WorldPresentation.AttackForPath(corridor, Array.Empty<JObject>(), false);
        Require((int?)outside["affected_observed_cells"] == 0 && ((JArray)outside["example_cells"]!).Count == 0, "Off-path hits leaked into path count");
        var unknown = WorldPresentation.AttackForPath(JObject.Parse("{'status':'unavailable_attack'}"), Array.Empty<JObject>(), false);
        Require(unknown["affected_observed_cells"] == null, "Unknown attacks became exact zero");

        var coverage = WorldPresentation.PathCoverage(Enumerable.Range(0, 5), new[] { 0, 1, 4 });
        Require((bool?)coverage["complete"] == false && (int?)coverage["observed_path_nodes"] == 3 && (int?)coverage["missing_index_ranges"]?[0]?["first_index"] == 2 && (int?)coverage["missing_index_ranges"]?[0]?["last_index"] == 3, "Hidden path gap lost");
        var env = JObject.Parse("{'scope':'observed_path_cells','path_environment_bands_complete':true}"); WorldPresentation.Environment(env);
        Require((string?)env["scope"] == "observed_path_cells" && ((string?)env["field_semantics"]?["native_danger_for_actor"])?.Contains("temperature/vacuum") == true, "Band danger semantics/scope lost");

        var condition = JObject.Parse("{'forbidden':null,'forbidden_to_actor':null,'burning':false}");
        WorldPresentation.NullField(condition, "forbidden", "not_applicable", "forbiddable_component_absent");
        WorldPresentation.NullField(condition, "forbidden_to_actor", "not_computed", "actor_required");
        WorldPresentation.NullField(condition, "burning", "unavailable", "must_not_apply");
        Require(condition["forbidden"]!.Type == JTokenType.Null && (string?)condition["field_states"]?["forbidden"]?["status"] == "not_applicable" && (string?)condition["field_states"]?["forbidden_to_actor"]?["status"] == "not_computed" && condition["field_states"]?["burning"] == null, "Null applicability changed native values");
        var stats = JObject.Parse("{'values':[{'definition':'MoveSpeed','status':'observed','value':4.4},{'definition':'Imaginary','status':'unknown_definition'}]}");
        var values = stats["values"]!.DeepClone(); WorldPresentation.Stats(stats);
        Require((string?)stats["status"] == "partial" && (int?)stats["observed_count"] == 1 && JToken.DeepEquals(values, stats["values"]), "Mixed stats lost observed values");
        var allFailed = JObject.Parse("{'values':[{'status':'unknown_definition'},{'status':'unsupported_mod_stat_worker'}]}"); WorldPresentation.Stats(allFailed);
        Require((string?)allFailed["status"] == "unavailable", "All failed stats not unavailable");

        var defs = new List<(string Name, string? Label)> { ("Bed", "单人床"), ("Other", "bed"), ("MedicineIndustrial", "医药"), ("Bedroll", "睡袋") };
        var exact = DefinitionCandidates.Exact(defs, new[] { "BED", "bed" }, d => d.Name, d => d.Label).ToArray();
        Require(exact.Length == 2 && exact[0].Name == "Bed" && exact[1].Name == "Other", "Canonical/label collision or casing lost");
        Require(DefinitionCandidates.Exact(defs, new[] { "医药" }, d => d.Name, d => d.Label).Single().Name == "MedicineIndustrial", "Localized label unresolved");
        Require(!DefinitionCandidates.Exact(defs, new[] { "Med" }, d => d.Name, d => d.Label).Any(), "Exact name became substring");
        defs.Add(("NewModDef", "BED"));
        Require(DefinitionCandidates.Exact(defs, new[] { "bed" }, d => d.Name, d => d.Label).Count() == 3, "New Mod definition hidden by stale cache");
        int visits = 0; var large = Enumerable.Range(0, 20000).Select(i => (Name: "Def" + i, Label: (string?)null));
        Require(DefinitionCandidates.Exact(large, new[] { "Def19999" }, d => d.Name, d => d.Label, () => visits++).Single().Name == "Def19999" && visits < 200, "Candidate path performed per-definition projection work");
        bool stopped = false;
        try { DefinitionCandidates.Exact(large, new[] { "Def19999" }, d => d.Name, d => d.Label, () => throw new InvalidOperationException("deadline")).ToArray(); } catch (InvalidOperationException) { stopped = true; }
        Require(stopped, "Deadline swallowed into empty resolution");
        var suggestion = DefinitionCandidates.Suggestions(new[] { ("ModStat", (string?)"特殊值"), ("MoveSpeed", (string?)"移动速度") }, "特殊值");
        Require((string?)suggestion["matches"]?[0]?["definition"] == "ModStat", "Stat candidates hardcoded to vanilla or ignored labels");
        var names = WorldObservationContract.Capabilities;
        Require(!names.Contains("production") && names.Contains("bill_giver") && WorldObservationContract.CapabilityMeanings().ToString().Contains("surgery"), "Retired capability alias remains or real bill-giver meaning lost");
        Console.WriteLine("Observation semantics tests passed: projected attack scopes, missing path indices, null applicability, mixed stats, dynamic exact definition candidates and bill aliases.");
    }
}
