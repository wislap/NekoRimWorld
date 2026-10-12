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
        ["capabilities"] = new JArray("alerts_cached", "event_identity", "quest_state"),
        ["parameters"] = new JObject {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JObject { ["map"] = WorldObservationContract.Text() }
        },
        ["description"] = "Current situation digest in native units: clock, active GameConditions, hostile Lords with visible members, cached game UI alerts, and archived letters plus threat messages of the last game day. Directly linked pending/ongoing Quest events remain beyond that window while archived. Stable native IDs and explicit relations support consolidation; a shared target or Quest alone does not establish the same issue. Cached alerts are global, not selected-map complete. Read details with object/state reads."
    };

    internal static bool RetainEvent(int now, int created, string? questState) =>
        now - created <= EventWindowTicks || questState is "NotYetAccepted" or "Ongoing";

    internal static void Validate(JObject arguments)
    {
        if (arguments.Properties().Any(p => p.Name != "map")) throw ReadContext.Error("invalid_arguments", "Unknown situation argument");
        if (arguments["map"] != null && (arguments["map"]!.Type != JTokenType.String || arguments["map"]!.ToString().Length is < 1 or > 256))
            throw ReadContext.Error("invalid_arguments", "Invalid map reference");
    }
}
