using System;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

// The two assemblies run in the same game process and share Stopwatch's native clock.
internal static class MonotonicEvidence
{
    private static readonly string Domain = DomainId();
    private static string DomainId() {
        using var process = Process.GetCurrentProcess();
        return process.Id.ToString() + ":" + process.StartTime.ToUniversalTime().Ticks.ToString();
    }
    internal static JObject Capture() => new JObject {
        ["domain"] = Domain, ["frequency"] = Stopwatch.Frequency,
        ["counter"] = Stopwatch.GetTimestamp(), ["source"] = "stopwatch"
    };
}
