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
        value["scope"] = "observed_footprint_not_object_selection";
        return value;
    }

    internal static void LimitRows(JObject value, int limit)
    {
        foreach (var property in value.Properties().ToArray())
        {
            if (property.Value is not JArray rows) continue;
            var completeKey = property.Name + "_complete";
            var totalKey = property.Name + "_total";
            bool previouslyComplete = (bool?)value[completeKey] != false;
            if (value[totalKey] == null && previouslyComplete) value[totalKey] = rows.Count;
            value[completeKey] = previouslyComplete && rows.Count <= limit;
            if (rows.Count > limit) property.Value = new JArray(rows.Take(limit).Select(row => row.DeepClone()));
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
