using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class SituationContract
{
    internal const string Version = "situation.v1";
    internal const string Operation = "read_situation";
    // One game day (GenDate.TicksPerDay); kept literal so the contract has no game dependency.
    internal const int EventWindowTicks = 60000;

    internal static JObject Catalog() => new() {
        ["version"] = Version,
        ["operation"] = Operation,
        ["event_window_ticks"] = EventWindowTicks,
        ["parameters"] = new JObject {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JObject { ["map"] = WorldObservationContract.Text() }
        },
        ["description"] = "Current situation digest in native units: clock, one entry per active GameCondition, one entry per hostile Lord with visible members, and archived letters plus threat messages of the last game day. Identity only (labels, defs, ticks, subject IDs); read details with object/state reads. Dismissing a letter in the UI does not remove it from the archive."
    };

    internal static void Validate(JObject arguments)
    {
        if (arguments.Properties().Any(p => p.Name != "map")) throw ReadContext.Error("invalid_arguments", "Unknown situation argument");
        if (arguments["map"] != null && (arguments["map"]!.Type != JTokenType.String || arguments["map"]!.ToString().Length is < 1 or > 256))
            throw ReadContext.Error("invalid_arguments", "Invalid map reference");
    }
}
