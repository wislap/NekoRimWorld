using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class ColonyManagementTests
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (ProtocolException) { return; } throw new Exception("Invalid observation policy accepted."); }
    private static JObject Policy(string revision = "one", int samples = 2, int expiry = 100) => JObject.Parse("{source:'planner',observations:[{observation_id:'supply',revision:'" + revision + "',conditions:[{id:'food',kind:'food_policy',target_ids:['FoodPolicy_1'],max_samples:" + samples + ",expires_at_tick:" + expiry + ",sample_interval_ticks:1}]}]}");
    internal static void Run() {
        ColonyObservationContract.Validate(Policy());
        var invalid = Policy(); invalid["observations"]![0]!["conditions"]![0]!["max_samples"] = 0; Reject(() => ColonyObservationContract.Validate(invalid));
        invalid = Policy(); invalid["registrations"] = new JArray(); Reject(() => ColonyObservationContract.Validate(invalid));
        Reject(() => ColonyObservationContract.Validate(Policy(), true));
        invalid = Policy(); invalid["observations"]![0]!["conditions"]![0]!["kind"] = "construction"; invalid["observations"]![0]!["conditions"]![0]!["map_id"] = "Map_0"; Reject(() => ColonyObservationContract.Validate(invalid));
        int state = 1, reads = 0; bool unavailable = false; var events = new List<JObject>();
        bool Emit(JObject e) { events.Add(e); return true; }
        var manager = new ColonyObservations(_ => { reads++; if (unavailable) throw new ArgumentException("target unavailable"); return new JObject { ["objects"] = new JArray(new JObject { ["id"] = "FoodPolicy_1", ["observed"] = state }), ["complete"] = true }; }, _ => { });
        var ack = manager.Replace(Policy(), "connection", 0); ColonyObservationContract.Validate(ack, true); string receipt = (string)ack["registrations"]![0]!["receipt_id"]!;
        Require((string?)ack["registrations"]![0]!["status"] == "registered", "Native coverage was not registered.");
        manager.Advance("connection", 0, Emit); manager.Advance("connection", 1, Emit); Require(events.Count == 1 && reads == 2, "Unchanged native state emitted redundant events.");
        var repeated = manager.Replace(Policy(), "connection", 2); Require((string?)repeated["registrations"]![0]!["receipt_id"] == receipt, "Unchanged revision lost native receipt.");
        manager.Advance("connection", 2, Emit); Require((string?)events[1]["status"] == "exhausted", "Replacement reset the bounded sample budget.");
        manager.Replace(Policy(), "connection", 3); manager.Advance("connection", 3, Emit); Require(events.Count == 2, "Exhausted observation was resurrected by replacement.");
        var changed = Policy(); changed["observations"]![0]!["conditions"]![0]!["max_samples"] = 8; var conflict = manager.Replace(changed, "connection", 4); Require((string?)conflict["registrations"]![0]!["status"] == "rejected", "Revision changed without a new identity.");
        manager.Replace(Policy("two", 4), "connection", 5); unavailable = true; manager.Advance("connection", 5, Emit); Require((string?)events[2]["status"] == "unknown", "Unavailable native source claimed complete evidence.");
        unavailable = false; manager.Advance("connection", 6, Emit); Require((string?)events[3]["status"] == "changed", "Same native registration could not recover unchanged evidence after unknown coverage.");
        manager.Replace(JObject.Parse("{source:'planner',observations:[]}"), "connection", 7); manager.Advance("connection", 8, Emit); Require(events.Count == 4, "Revocation left active native observers.");
        manager.Replace(Policy("expiry", 9, 10), "connection", 9); manager.Advance("connection", 10, Emit); Require((string?)events[4]["status"] == "expired", "Expired observation retained coverage.");
        manager.Replace(Policy("fresh"), "connection", 11); manager.Advance("reconnected", 11, Emit); Require(events.Count == 5, "Observer survived connection identity change.");
        var reconnect = manager.Replace(Policy(), "reconnected", 12); Require((string?)reconnect["registrations"]![0]!["receipt_id"] != receipt, "New connection reused stale registration proof.");
        long wall = 0; int value = 1, sampled = 0; bool missing = false; var recoveryEvents = new List<JObject>();
        var recovery = new ColonyObservations(_ => { sampled++; if (missing) throw new ArgumentException("missing"); return new JObject { ["objects"] = new JArray(new JObject { ["value"] = value }), ["complete"] = true }; }, _ => { }, () => wall);
        bool Recover(JObject e) { recoveryEvents.Add(e); return true; }
        var pausedPolicy = Policy("paused", 8); pausedPolicy["observations"]![0]!["conditions"]![0]!["sample_interval_ticks"] = 100;
        recovery.Replace(pausedPolicy, "paused", 0);
        recovery.Advance("paused", 0, Recover, true);
        missing = true; wall = 2000; recovery.Advance("paused", 0, Recover, true);
        missing = false; wall = 4000; recovery.Advance("paused", 0, Recover, true);
        Require(sampled == 3 && (string?)recoveryEvents[1]["status"] == "unknown" && (string?)recoveryEvents[2]["status"] == "changed", "Paused unchanged state did not recover coverage after unknown.");
        value = 2; wall = 5999; recovery.Advance("paused", 0, Recover, true); Require(sampled == 3, "Paused polling ignored its wall-clock bound.");
        wall = 6000; recovery.Advance("paused", 0, Recover, true); Require(sampled == 4 && recoveryEvents.Count == 4, "Paused same-tick player change was not observed.");
        int delivered = 0, pressureReads = 0; var pressure = new ColonyObservations(_ => { pressureReads++; return new JObject { ["objects"] = new JArray(new JObject { ["id"] = "one" }), ["complete"] = true }; }, _ => { });
        pressure.Replace(Policy("pressure", 1), "pressure", 0); pressure.Advance("pressure", 0, _ => false);
        pressure.Advance("pressure", 0, _ => { delivered++; return true; });
        Require(delivered == 1 && pressureReads == 1, "Failed event delivery discarded its sample or consumed another read.");
        pressure.Advance("pressure", 1, _ => { delivered++; return true; }); Require(delivered == 2 && pressureReads == 1, "Single-sample budget performed a hidden registration read.");
        var multiple = Policy("multiple", 1); multiple["observations"]![0]!["conditions"]![0]!["target_ids"] = new JArray("one", "two");
        pressure.Replace(multiple, "pressure", 2); int before = delivered;
        pressure.Advance("pressure", 2, _ => { delivered++; return true; }); Require(delivered == before, "A partial target slice claimed full condition coverage.");
        pressure.Advance("pressure", 2, _ => { delivered++; return true; }); Require(delivered == before + 1, "A complete target set did not publish its bounded sample.");
        ColonyManagementContract.Validate("inspect_construction", JObject.Parse("{map:'Map_0',bounds:{min_x:0,min_z:0,width:64,height:64}}"));
        ColonyManagementContract.ValidateElement(JObject.Parse("{plan_id:'house',element_id:'wall',definition:'Wall',x:1,z:2,rotation:1,kind:'building'}"));
    }
}
