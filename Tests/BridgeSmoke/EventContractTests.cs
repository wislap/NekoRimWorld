using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
namespace Neko.RimWorld;
internal static class EventContractTests {
    internal static void Run() {
        var vectors = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "colony-events-v1", "vectors.json")));
        foreach (var row in (JArray)vectors["valid"]!) ProtocolCodec.Deserialize(Encoding.UTF8.GetBytes(row.ToString()));
        foreach (var row in (JArray)vectors["invalid"]!) { try { ProtocolCodec.Deserialize(Encoding.UTF8.GetBytes(row.ToString())); throw new Exception("Invalid event vector accepted"); } catch (ProtocolException) { } }
        if (PausePolicy.ObservedSource(false, true) != "window" || PausePolicy.ObservedSource(true, true) != "unknown" || PausePolicy.ObservedSource(false, false) != "unknown") throw new Exception("Ambiguous pause source misclassified");
        var policy = new PausePolicy(); policy.Bind("c1", "colony", "epoch1");
        if (policy.Matches("ThreatBig")) throw new Exception("Default policy was enabled");
        policy.Set(new[] { "ThreatBig", "NegativeEvent" });
        if (!policy.Matches("ThreatBig") || policy.Matches("ThreatSmall")) throw new Exception("Definition matching failed");
        policy.Observe("planner"); policy.Bind("c1", "colony", "epoch2"); if (policy.Matches("ThreatBig") || policy.CanResume) throw new Exception("Epoch did not clear policy and ownership");
        policy.Set(new[] { "ThreatBig" }); policy.Observe("planner"); policy.Bind("c2", "colony", "epoch2"); if (policy.Matches("ThreatBig") || policy.CanResume) throw new Exception("Connection did not clear policy and ownership");
        policy.Set(new[] { "ThreatBig" }); policy.Bind("", "colony", "epoch2"); if (policy.Matches("ThreatBig")) throw new Exception("Disconnected policy active");
        foreach (var owner in new[] { "user", "unknown" }) { policy.Observe(owner); if (policy.CanResume) throw new Exception("Unsafe resume"); }
        foreach (var owner in new[] { "planner", "frontdesk", "reflex", "automatic_letter" }) { policy.Observe(owner); if (!policy.CanResume) throw new Exception("Agent pause cannot resume"); }
    }
}
