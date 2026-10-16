using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

// Internal consultation preparation capability. Never part of world.tools.
internal static class SituationSnapshotContract
{
    internal const string Version = "situation_snapshot.v1";
    internal const string Operation = "read_situation";
    internal static JObject Catalog() => new() {
        ["version"] = Version, ["operation"] = Operation,
        ["purpose"] = "Read-only native situation background prepared before consultation; not a model tool.",
        ["parameters"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JObject { ["map"] = new JObject { ["type"] = "string" } } }
    };
    internal static void Validate(JObject args)
    {
        if (args.Properties().Any(p => p.Name != "map") || args["map"] != null && (args["map"]!.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)args["map"])))
            throw ReadContext.Error("invalid_arguments", "read_situation accepts only an optional nonempty map string.");
    }
}
