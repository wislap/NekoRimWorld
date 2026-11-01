using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;
internal static class AttentionFactsTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static JObject Facts(string alerts, string conditions = "[]", string threats = "[]", string status = "ok") => JObject.Parse("{alerts:{status:'"+status+"',rows:"+alerts+"},conditions:{status:'ok',rows:"+conditions+"},threats:{status:'ok',rows:"+threats+"}}");
    internal static void Run()
    {
        Check(!AttentionFacts.CapabilityAvailable(true, false) && !AttentionFacts.CapabilityAvailable(false, true) && AttentionFacts.CapabilityAvailable(true, true), "attention capability requires both hooks");
        var publication = new AttentionPublication();
        var baseline = new JObject { ["revision"] = 1 };
        var events = new System.Collections.Generic.List<JObject> { new JObject { ["id"] = "a" }, new JObject { ["id"] = "b" }, new JObject { ["id"] = "c" } };
        publication.Begin(events, baseline);
        var delivered = new System.Collections.Generic.List<string>();
        bool fail = true;
        bool Emit(JObject value) { var id = (string)value["id"]!; if (id == "b" && fail) { fail = false; return false; } delivered.Add(id); return true; }
        Check(!publication.Publish(Emit, out var pending) && pending == null && publication.Pending && delivered.Count == 1, "partial publication stays pending");
        Check(publication.Publish(Emit, out var committed) && ReferenceEquals(committed, baseline) && !publication.Pending && string.Join(",", delivered) == "a,b,c", "retry must resume at failed event and commit once");
        var empty = Facts("[]");
        var alert = "{id:'a',runtime_type:'RimWorld.Alert_Test',priority:'High',targets_complete:true,targets:[{id:'p1',map_id:'m'}]}";
        var first = Facts("["+alert+"]");
        Check(AttentionFacts.Compare(empty, first, 250, "m")[0]["edge"]!.ToString() == "appeared", "alert appearance");
        var next = Facts("[{id:'a',runtime_type:'RimWorld.Alert_Test',priority:'Critical',targets_complete:true,targets:[{id:'p1',map_id:'m'},{id:'p2',map_id:'m'}]}]", "[{id:'c',def:'ColdSnap'}]", "[{id:'t',kind:'hostile_group'}]");
        var changes = AttentionFacts.Compare(first, next, 500, "m");
        Check(changes.Count == 4 && (string?)changes[0]["edge"] == "priority_changed" && (string?)changes[1]["edge"] == "targets_changed" && ((JArray)changes[1]["added"]!).Count == 1, "priority, target, condition and threat appearances");
        var incomplete = Facts("[]", status: "partial");
        Check(AttentionFacts.Compare(next, incomplete, 750, "m").Find(c => (string?)c["category"] == "alerts" && (string?)c["edge"] == "disappeared") == null, "partial must not imply disappearance");
        var retained = AttentionFacts.Merge(next, incomplete);
        Check(AttentionFacts.Compare(retained, empty, 1000, "m").Find(c => (string?)c["category"] == "alerts" && (string?)c["edge"] == "disappeared") != null, "recovery proves disappearance");
        Check(AttentionFacts.Compare(next, empty, 1000, "m").Count == 3, "all three categories disappear");
        var partialTargets = Facts("[{id:'a',priority:'Critical',targets_complete:false,targets:[{id:'p1',map_id:'m'}]}]");
        Check(AttentionFacts.Compare(next, partialTargets, 750, "m").Find(c => (string?)c["edge"] == "targets_changed") == null, "truncated targets cannot prove removal");
        var merged = AttentionFacts.Merge(next, partialTargets);
        Check(AttentionFacts.Compare(merged, first, 1000, "m").Find(c => (string?)c["edge"] == "targets_changed") != null, "target removal after complete recovery");
        var section = JObject.Parse("{status:'ok',source:'native',items:[{id:'a',runtime_type:'Alert',priority:'High',report:{targets:[{id:'p',map_id:'m',x:1,z:2}]}}]}");
        var projected = AttentionFacts.Project("alerts", section);
        Check((string?)projected["rows"]?[0]?["targets"]?[0]?["id"] == "p" && projected["rows"]?[0]?["targets"]?[0]?["x"]?.Type == JTokenType.Null, "target stable identity");
        var oversized = new JObject { ["status"] = "ok", ["source"] = "native", ["items"] = new JArray() };
        var oversizedItems = (JArray)oversized["items"]!;
        for (int i = 0; i <= AttentionFacts.MaxRows; i++) oversizedItems.Add(new JObject { ["id"] = "c" + i, ["def"] = "ColdSnap" });
        var bounded = AttentionFacts.Project("conditions", oversized);
        Check((string?)bounded["status"] == "partial" && (bool?)bounded["truncated"] == true && ((JArray)bounded["rows"]!).Count == AttentionFacts.MaxRows, "bounded partition");
        var vectors = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "colony-attention-v1", "vectors.json")));
        foreach (var value in (JArray)vectors["valid"]!) ProtocolCodec.Deserialize(System.Text.Encoding.UTF8.GetBytes(value.ToString()));
    }
}
