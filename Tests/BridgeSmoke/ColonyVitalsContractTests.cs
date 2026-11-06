using System;
using System.Linq;
using GraphQL;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class ColonyVitalsContractTests
{
    internal static void Run()
    {
        ColonyVitalsContract.Validate(new JObject());
        ColonyVitalsContract.Validate(new JObject { ["map"] = "Map_0" });
        foreach (var args in new[] { new JObject { ["map"] = 0 }, new JObject { ["map"] = "" }, new JObject { ["domains"] = new JArray("food") }, new JObject { ["map"] = JValue.CreateNull() } }) {
            try { ColonyVitalsContract.Validate(args); throw new Exception("Invalid vitals args were accepted."); }
            catch (ExecutionError error) when (error.Code == "invalid_arguments") { }
        }
        var catalog = ColonyVitalsContract.Catalog();
        if ((string?)catalog["version"] != "colony_vitals.v1" || (string?)catalog["operation"] != "read_colony_vitals" || catalog["tools"] != null)
            throw new Exception("Colony vitals must be independently advertised, never a model-tool catalog.");
        if (!catalog["domains"]!.Select(d => (string?)d).SequenceEqual(new[] { "life", "food", "shelter", "labor" }))
            throw new Exception("Colony vitals v1 domains changed.");
        if (((JArray)WorldObservationContract.Catalog()["tools"]!).Count != 3)
            throw new Exception("Colony vitals changed the supported world tools.");

        if (ColonyVitalsContract.Days(10, 0) != null) throw new Exception("Days without consumers must be null.");
        if (ColonyVitalsContract.Days(6.65, 4.8) != 1.39) throw new Exception("Days must divide unforbidden nutrition by daily need.");

        var domain = new JObject();
        ColonyVitalsContract.SetWorst(domain, Enumerable.Range(0, 11).Select(i => new JObject { ["rank"] = i }));
        var worst = (JArray)domain["worst"]!;
        if (worst.Count != ColonyVitalsContract.MaxWorst || (int)worst[0]["rank"]! != 0 || (int)domain["worst_omitted"]! != 3)
            throw new Exception("Worst rows must keep caller order, cap at MaxWorst and report the omitted count.");
        ColonyVitalsContract.SetWorst(domain, Array.Empty<JObject>());
        if (((JArray)domain["worst"]!).Count != 0 || (int)domain["worst_omitted"]! != 0) throw new Exception("Empty worst rows must report zero omitted.");

        ColonyVitalsContract.Partial(domain, "work_type_missing:Cooking");
        ColonyVitalsContract.Partial(domain, "work_type_missing:Cooking");
        if ((string?)domain["status"] != "partial" || ((JArray)domain["reasons"]!).Count != 1) throw new Exception("Partial reasons must be recorded once.");
    }
}
