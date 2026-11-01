using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
namespace Neko.RimWorld;
internal static class EventContractTests {
    internal static void Run() {
        foreach (var fixture in new[] { "colony-events-v1", "colony-pause-policy-v1" }) {
        var vectors = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, fixture, "vectors.json")));
        foreach (var row in (JArray)vectors["valid"]!) ProtocolCodec.Deserialize(Encoding.UTF8.GetBytes(row.ToString()));
        foreach (var row in (JArray)vectors["invalid"]!) { try { ProtocolCodec.Deserialize(Encoding.UTF8.GetBytes(row.ToString())); throw new Exception("Invalid event vector accepted"); } catch (ProtocolException) { } }
        }
        var clock1 = MonotonicEvidence.Capture(); var clock2 = MonotonicEvidence.Capture();
        if ((string?)clock1["domain"] != (string?)clock2["domain"] || (long?)clock1["frequency"] <= 0 ||
            (long?)clock1["frequency"] != (long?)clock2["frequency"] || (long?)clock2["counter"] < (long?)clock1["counter"])
            throw new Exception("Native monotonic clock domain changed or moved backwards");
        var pauseVectors = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "colony-pause-state-v1", "vectors.json")));
        var evidencePolicy = new PausePolicy();
        var unboundState = evidencePolicy.State("", "colony", "epoch", 100, true, true, false, false);
        if ((string?)unboundState["owner"] != "unknown" || (string?)unboundState["connection_epoch"] != "" ||
            (string?)unboundState["colony_id"] != "colony" || (string?)unboundState["load_epoch"] != "epoch" ||
            (bool?)unboundState["speed_paused"] != true || (long?)unboundState["pause_transition_revision"] != 0)
            throw new Exception("Internal pause state unavailable before bridge connection");
        if (!JToken.DeepEquals(evidencePolicy.Evidence("", "colony", "epoch", 100, true, true, false, false), pauseVectors["unavailable"]))
            throw new Exception("Unbound pause evidence must be unavailable");
        evidencePolicy.Bind("connection", "colony", "epoch"); evidencePolicy.Observe("planner");
        if (!JToken.DeepEquals(evidencePolicy.Evidence("connection", "colony", "epoch", 100, true, true, false, false), pauseVectors["observed"]))
            throw new Exception("Pause evidence does not match authority vector");
        if (!JToken.DeepEquals(evidencePolicy.Evidence("other", "colony", "epoch", 100, true, true, false, false), pauseVectors["unavailable"]))
            throw new Exception("Mismatched pause binding exposed prior owner");
        evidencePolicy.Bind("new", "colony", "epoch");
        if ((string?)evidencePolicy.Evidence("new", "colony", "epoch", 101, true, true, true, true)["owner"] != "unknown")
            throw new Exception("Reconnection retained prior owner");
        if (PausePolicy.ObservedSource(false, true) != "window" || PausePolicy.ObservedSource(true, true) != "unknown" || PausePolicy.ObservedSource(false, false) != "unknown") throw new Exception("Ambiguous pause source misclassified");
        var policy = new PausePolicy(); policy.Bind("c1", "colony", "epoch1");
        long initialRevision = policy.Revision;
        policy.Bind("c1", "colony", "epoch1");
        if (policy.Revision != initialRevision) throw new Exception("Unchanged binding advanced pause revision");
        policy.Observe("policy"); long firstPolicyRevision = policy.Revision;
        long firstTransition = policy.TransitionRevision;
        policy.Observe("none"); policy.Observe("policy");
        if (policy.Revision <= firstPolicyRevision || policy.TransitionRevision <= firstTransition) throw new Exception("Same-owner pause generation was reused");
        long beforeBindTransition = policy.TransitionRevision;
        policy.Bind("new", "colony", "epoch1");
        if (policy.TransitionRevision != beforeBindTransition) throw new Exception("Binding changed pause transition identity");
        policy.Bind("c1", "colony", "epoch1");
        if (policy.Matches("ThreatBig")) throw new Exception("Default policy was enabled");
        policy.Set(new[] { "ThreatBig", "NegativeEvent" });
        if (!policy.Matches("ThreatBig") || policy.Matches("ThreatSmall")) throw new Exception("Definition matching failed");
        policy.Observe("planner"); policy.Bind("c1", "colony", "epoch2"); if (policy.Matches("ThreatBig") || policy.CanResume) throw new Exception("Epoch did not clear policy and ownership");
        policy.Set(new[] { "ThreatBig" }); policy.Observe("planner"); policy.Bind("c2", "colony", "epoch2"); if (policy.Matches("ThreatBig") || policy.CanResume) throw new Exception("Connection did not clear policy and ownership");
        policy.Set(new[] { "ThreatBig" }); policy.Bind("", "colony", "epoch2"); if (policy.Matches("ThreatBig")) throw new Exception("Disconnected policy active");
        foreach (var owner in new[] { "user", "unknown" }) { policy.Observe(owner); if (policy.CanResume) throw new Exception("Unsafe resume"); }
        foreach (var owner in new[] { "planner", "frontdesk", "reflex", "automatic_letter", "policy" }) { policy.Observe(owner); if (!policy.CanResume) throw new Exception("Agent pause cannot resume"); }
        policy.Bind("bound", "colony", "epoch3");
        foreach (var owner in new[] { "user", "unknown", "policy", "none", "window", "unexpected" }) {
            policy.Observe(owner);
            if (policy.CanResumeExpected(owner) || policy.CanResumeExpected("frontdesk")) throw new Exception("Guarded unsafe resume");
        }
        foreach (var owner in new[] { "planner", "frontdesk", "reflex", "automatic_letter" }) {
            policy.Observe(owner);
            if (!policy.CanResumeExpected(owner)) throw new Exception("Guarded agent resume rejected");
            if (policy.CanResumeExpected(owner == "planner" ? "frontdesk" : "planner")) throw new Exception("Changed owner accepted");
        }
        policy.Bind("next", "colony", "epoch3");
        if (policy.CanResumeExpected("frontdesk")) throw new Exception("Old connection pause accepted");
    }
}
