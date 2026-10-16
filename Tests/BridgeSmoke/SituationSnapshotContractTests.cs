using System;
using GraphQL;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class SituationSnapshotContractTests
{
    internal static void Run()
    {
        SituationSnapshotContract.Validate(new JObject());
        SituationSnapshotContract.Validate(new JObject { ["map"] = "current" });
        SituationSnapshotContract.Validate(new JObject { ["map"] = "Map_42" });
        foreach (var args in new[] { new JObject { ["map"] = 4 }, new JObject { ["map"] = " " }, new JObject { ["detail"] = "all" }, new JObject { ["map"] = JValue.CreateNull() } }) {
            try { SituationSnapshotContract.Validate(args); throw new Exception("Invalid situation args were accepted."); }
            catch (ExecutionError error) when (error.Code == "invalid_arguments") { }
        }
        var catalog = SituationSnapshotContract.Catalog();
        if ((string?)catalog["version"] != "situation_snapshot.v1" || (string?)catalog["operation"] != "read_situation" || catalog["tools"] != null)
            throw new Exception("Situation preparation must be independently advertised, never a model-tool catalog.");
        if (((JArray)WorldObservationContract.Catalog()["tools"]!).Count != 3)
            throw new Exception("Situation preparation changed the supported world tools.");
    }
}
