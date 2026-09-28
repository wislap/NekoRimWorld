using Newtonsoft.Json.Linq;
using System.Linq;
namespace Neko.RimWorld;

// Presentation of detached observations only. Never infer absence or usability.
internal static class WorldPresentation
{
    internal static void ObjectScope(JObject summary, JToken? selection)
    {
        summary["scope"] = new JObject {
            ["selection"] = selection?.DeepClone(),
            ["include_held"] = true, ["visible_only"] = true,
            ["counts_apply_to"] = "matching_objects_in_observed_footprint",
            ["meaning"] = "All object counts and groups below apply to this selection. Zero does not establish absence outside the selection. Environment covers the observed footprint independently of this object filter."
        };
    }

    internal static void FinalizeChanges(JObject result, bool changed)
    {
        if (!changed) return;
        result["changed_during_read"] = true;
        result["status"] = "partial";
        foreach (var key in new[] { "environment", "path_environment" })
        {
            if (result[key] is not JObject environment) continue;
            if (environment["coverage"] is not JObject) environment["coverage"] = new JObject();
            environment["coverage"]!["changes_detected"] = true;
            environment["coverage"]!["native_attack_checks_complete"] = false;
        }
    }

    internal static JObject Environment(JObject value)
    {
        if (value["roof_support_objects"] is JArray supports)
        {
            foreach (var token in supports)
                if (token is JObject reference)
                {
                    // These are location references, not a second full state listing.
                    reference.Remove("runtime_type");
                    reference.Remove("holder_type");
                    reference.Remove("holder_chain");
                    reference.Remove("capabilities");
                }
            value["roof_support_objects_format"] = "Identity/location references; component and containment metadata omitted. Use inspect_state for full object state. No support rows removed by this formatting; roof_support_objects_total still describes the observed total.";
        }
        if (value["scope"] == null) value["scope"] = "observed_footprint_not_object_selection";
        value["field_semantics"] = new JObject {
            ["native_danger_for_actor"] = "Native region temperature/vacuum classification only; None does not establish absence of fire, gas, enemies or overall danger. Danger.Some in a path policy is a traversal setting, not an observed hazard.",
            ["path_environment_bands_complete"] = "Completeness of the retained band list only; path_cell_coverage separately reports unsampled path indices."
        };
        return value;
    }

    internal static JObject AttackForPath(JObject corridor, System.Collections.Generic.IEnumerable<JObject> affectedPathCells, bool sourceOnPath)
    {
        var row = (JObject)corridor.DeepClone();
        row["scope"] = "observed_path_cells";
        if (row["affected_observed_cells"] != null) {
            var cells = affectedPathCells.ToArray();
            row["affected_observed_cells"] = cells.Length;
            row["example_cells"] = new JArray(cells.Take(8).Select(c => c.DeepClone()));
            row["source_inside_footprint"] = sourceOnPath;
        }
        return row;
    }

    internal static JObject PathCoverage(System.Collections.Generic.IEnumerable<int> expected, System.Collections.Generic.IEnumerable<int> observed)
    {
        var all = expected.Distinct().OrderBy(i => i).ToArray();
        var seen = new System.Collections.Generic.HashSet<int>(observed);
        var missing = all.Where(i => !seen.Contains(i)).ToArray(); var ranges = new JArray(); JObject? range = null;
        foreach (var i in missing) {
            if (range == null || i != (int)range["last_index"]! + 1) { range = new JObject { ["first_index"] = i, ["last_index"] = i }; ranges.Add(range); }
            else range["last_index"] = i;
        }
        return new JObject { ["expected_path_nodes"] = all.Length, ["observed_path_nodes"] = all.Length - missing.Length,
            ["complete"] = missing.Length == 0, ["missing_index_ranges"] = ranges,
            ["meaning"] = "Path-node sampling coverage, separate from retained list completeness. Missing indices may be hidden, unread or failed; consult footprint visibility/read_errors. No clear/safe inference for missing cells." };
    }

    internal static void NullField(JObject value, string field, string state, string reason)
    {
        if (value[field]?.Type != JTokenType.Null) return;
        if (value["field_states"] is not JObject states) value["field_states"] = states = new JObject();
        states[field] = new JObject { ["status"] = state, ["reason"] = reason };
    }

    internal static void Stats(JObject stats)
    {
        var values = (JArray)stats["values"]!; int observed = values.Count(v => (string?)v["status"] == "observed");
        stats["requested_count"] = values.Count; stats["observed_count"] = observed;
        stats["complete"] = observed == values.Count;
        stats["status"] = observed == values.Count ? "observed" : observed > 0 ? "partial" : "unavailable";
    }

    internal static void LimitRows(JObject value, int limit, params string[] completeTables)
    {
        foreach (var property in value.Properties().ToArray())
        {
            if (property.Value is not JArray rows) continue;
            var completeKey = property.Name + "_complete";
            var totalKey = property.Name + "_total";
            bool previouslyComplete = (bool?)value[completeKey] != false;
            if (value[totalKey] == null && previouslyComplete) value[totalKey] = rows.Count;
            bool keepTable = completeTables.Contains(property.Name);
            value[completeKey] = previouslyComplete && (keepTable || rows.Count <= limit);
            if (!keepTable && rows.Count > limit) property.Value = new JArray(rows.Take(limit).Select(row => row.DeepClone()));
        }
    }

    internal static void Work(JObject value)
    {
        value["work_types_meaning"] = "disabled is the current native work-type disabled state, not its cause or assigned priority; priority 0 means not assigned for autonomous work. Neither is a complete manual-job eligibility check.";
    }

    internal static void Reservations(JObject value, JToken target, bool cell = false)
    {
        value["scope"] = new JObject {
            ["target"] = target.DeepClone(),
            ["direction"] = "reservations_on_target",
            ["reservations_claimed_by_target"] = "not_queried",
            ["meaning"] = cell
                ? "Records target this cell. Reservations of objects in the cell are separate. Job targets describe the work, not the reserved destination."
                : "Normal/physical records target this object; destination records target its occupied cells. Empty lists say nothing about reservations this Pawn claims elsewhere. For destinations, cell is reserved; job.targets may be elsewhere."
        };
    }
}
