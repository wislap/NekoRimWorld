using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

// Internal planner background capability. Never part of world.tools.
internal static class ColonyVitalsContract
{
    internal const string Version = "colony_vitals.v1";
    internal const string Operation = "read_colony_vitals";
    internal const int MaxWorst = 8;
    internal static readonly string[] Domains = { "life", "food", "shelter", "labor" };
    internal static readonly string[] KeyWork = { "Cooking", "Doctor", "Growing", "Construction", "Hauling" };

    internal static JObject Catalog() => new() {
        ["version"] = Version, ["operation"] = Operation,
        ["purpose"] = "Read-only native colony vitals (life, food, shelter, labor) prepared as planner background; not a model tool. Facts only: levels and thresholds belong to the consumer.",
        ["domains"] = new JArray(Domains), ["max_worst"] = MaxWorst,
        ["parameters"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JObject { ["map"] = new JObject { ["type"] = "string" } } }
    };

    internal static void Validate(JObject args)
    {
        if (args.Properties().Any(p => p.Name != "map") || args["map"] != null && (args["map"]!.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)args["map"])))
            throw ReadContext.Error("invalid_arguments", "read_colony_vitals accepts only an optional nonempty map string.");
    }

    // Days of supply; null when nobody eats, so no division hides an empty colony.
    internal static double? Days(double nutrition, double dailyNeed) => dailyNeed > 0 ? System.Math.Round(nutrition / dailyNeed, 2) : null;

    // Callers pass rows already in severity order; the omitted count is reported, never dropped silently.
    internal static void SetWorst(JObject domain, IEnumerable<JObject> ordered)
    {
        var rows = ordered.ToArray();
        domain["worst"] = new JArray(rows.Take(MaxWorst));
        domain["worst_omitted"] = System.Math.Max(0, rows.Length - MaxWorst);
    }

    internal static void Partial(JObject domain, string reason)
    {
        domain["status"] = "partial";
        var reasons = domain["reasons"] as JArray ?? new JArray();
        domain["reasons"] = reasons;
        if (!reasons.Any(r => (string?)r == reason)) reasons.Add(reason);
    }
}
