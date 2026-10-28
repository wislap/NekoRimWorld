using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;
internal static class WriteLedgerTests {
    private sealed class Probe : IPreparedWriteField {
        internal int Value, Writes; internal string Id = "";
        internal bool Destroyed;
        public string ObjectId => Id; public string Field => "value"; public bool MayChangeByGame => false;
        public bool IsDestroyed => Destroyed;
        public object? NativeTarget => this;
        public JToken Read() => new JValue(Value); public void Validate(JToken value) { if (value.Type != JTokenType.Integer) throw new ArgumentException(); }
        public void Write(JToken value) { Value = (int)value; Writes++; } public void Restore(JToken value) { Value = (int)value; }
    }
    private sealed class Resolver : IWriteFieldResolver {
        internal readonly Dictionary<string, Probe> fields = new();
        public IReadOnlyList<ResolvedWriteField> Resolve(JObject op) => new[] { new ResolvedWriteField(fields[(string)op["thing"]!], op["expect"]!, op["value"]!) };
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static Func<ProtocolEnvelope, QueryOutcome> CreatePump(out Func<int> writes) {
        var resolver = new Resolver(); var a = new Probe { Id = "a", Value = 1 }; resolver.fields.Add("a", a);
        var coordinator = new WriteCoordinator(new WriteLedger()); coordinator.StartEpoch("66666666666666666666666666666666");
        writes = () => a.Writes;
        return request => coordinator.Execute(request.Payload.Mode!, request.Payload.Arguments!, resolver, () => "66666666666666666666666666666666", () => true, 99);
    }
    private static JObject Request(string key, int a, int b) => new() { ["load_epoch"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ["author"] = "planner", ["changeset_id"] = "cs", ["idempotency_key"] = key, ["entry_id"] = "entry", ["ops"] = new JArray(
        new JObject { ["op"] = "set_forbidden", ["evidence"] = "ev-a", ["thing"] = "a", ["expect"] = a, ["value"] = a + 2 }, new JObject { ["op"] = "set_forbidden", ["evidence"] = "ev-b", ["thing"] = "b", ["expect"] = b, ["value"] = b + 2 }) };
    internal static void Run() {
        var ledger = new WriteLedger(); var targets = new WriteTargetIndex(x => ((Probe)x).Destroyed); var coordinator = new WriteCoordinator(ledger, targets); coordinator.StartEpoch("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var resolver = new Resolver(); var a = new Probe { Id = "a", Value = 1 }; var b = new Probe { Id = "b", Value = 2 }; resolver.fields.Add("a", a); resolver.fields.Add("b", b);
        QueryOutcome Call(string mode, JObject request) => coordinator.Execute(mode, request, resolver, () => "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", () => true, 42);
        var request = Request("key", 1, 2); var result = Call("commit", request); var token = (string)result.Data!["undo_token"]!;
        var queuedSession = new SessionDescriptor("colony", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 41, 7);
        var receiptSession = WriteReceiptSession.AtTick(queuedSession, 42);
        Check(receiptSession.GameTick == (long)ledger.Find("a", "value")!["tick"]! && receiptSession.SnapshotSequence == 7 && receiptSession.LoadEpoch == queuedSession.LoadEpoch && queuedSession.GameTick == 41, "write receipt did not use execution tick");
        Check(a.Value == 3 && b.Value == 4 && ledger.Count == 2, "commit failed");
        Check(JToken.DeepEquals(result.Data, Call("commit", request).Data) && a.Writes == 1 && b.Writes == 1, "duplicate wrote twice");
        Check(JToken.DeepEquals(result.Data, coordinator.Execute("commit", request, resolver, () => "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", () => false, 44).Data), "cached receipt depends on world readiness");
        Check(Call("commit", Request("key", 3, 4)).ErrorCode == "idempotency_key_reused", "key reuse accepted");
        var copy = new WriteLedger(); copy.Load(ledger.Serialize()); Check(copy.Count == 2 && (int)copy.Find("a", "value")!["value"]! == 3, "ledger roundtrip");
        // Model the result of a reference roundtrip: identity resolves to a new
        // loaded object, while a missing reference remains unknown.
        var loadedA = new Probe { Id = "a", Value = 3 };
        var loadedTargets = new WriteTargetIndex(x => ((Probe)x).Destroyed);
        var serializedRefs = new JArray(targets.Entries().Select(pair => new JObject { ["id"] = pair.Key, ["ref"] = ((Probe)pair.Value).Id })).ToString();
        var loadedRefs = JArray.Parse(serializedRefs);
        loadedTargets.BindMany(loadedRefs.Select(row => new KeyValuePair<string, object?>((string)row["id"]!, (string?)row["ref"] == "a" ? loadedA : null)));
        loadedTargets.Prune(copy); Check(copy.Count == 2, "unresolved target treated as destroyed");
        loadedA.Destroyed = true; loadedTargets.Prune(copy);
        Check(copy.Find("a", "value") == null && copy.Find("b", "value") != null, "loaded reference destruction was not pruned");
        var save = new WriteLedgerSaveState(); save.PrepareSave(ledger); var saved = save.Data;
        var loaded = new WriteLedger(); save.Loaded(loaded); Check(save.Valid && loaded.Serialize() == saved, "save adapter roundtrip failed");
        var oldSave = new WriteLedgerSaveState(); oldSave.Loaded(new WriteLedger()); Check(oldSave.Valid, "old save without ledger rejected");
        save.Data = "invalid json"; save.Loaded(loaded); save.PrepareSave(loaded);
        Check(!save.Valid && save.Data == "invalid json" && loaded.Count == 2, "invalid save data was not preserved");
        try { copy.Load("[{\"object\":\"bad\"}]"); throw new Exception("malformed ledger accepted"); } catch (ArgumentException) { }
        Check(copy.Count == 1, "invalid ledger changed existing entries");
        b.Value = 99; var failedUndo = Call("undo", new JObject { ["token"] = token }); Check((string?)failedUndo.Data!["status"] == "conflict" && a.Value == 3 && b.Value == 99, "undo conflict changed fields");
        b.Value = 4; Check((string?)Call("undo", new JObject { ["token"] = token }).Data!["status"] == "undone" && a.Value == 1 && b.Value == 2, "undo failed");
        Check((string?)Call("undo", new JObject { ["token"] = token }).Data!["status"] == "already_undone", "repeat undo accepted");
        Check((string?)Call("undo", new JObject { ["token"] = "missing" }).Data!["status"] == "unknown_token", "unknown token accepted");
        Check(ledger.Count == 2 && (int)ledger.Find("a", "value")!["value"]! == 1, "latest ledger value not updated");
        a.Destroyed = true; coordinator.PruneConfirmedDestroyed(); Check(ledger.Count == 1, "destroyed entry retained"); a.Destroyed = false;
        var calls = 0; var before = ledger.Serialize(); var beforeTargets = targets.Snapshot();
        var publication = coordinator.Execute("commit", Request("publication", 1, 2), resolver, () => "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", () => ++calls < 3, 43);
        Check(publication.ErrorCode == "write_failed" && a.Value == 1 && b.Value == 2 && ledger.Serialize() == before && ReferenceEquals(beforeTargets, targets.Snapshot()), "publication failure left fields or metadata changed");
        coordinator.StartEpoch("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"); Check(coordinator.Execute("commit", request, resolver, () => "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", () => true, 43).ErrorCode == "stale_epoch", "old epoch accepted");
        Check((string?)coordinator.Execute("undo", new JObject { ["token"] = token }, resolver, () => "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", () => true, 43).Data!["status"] == "unknown_token", "undo survived epoch");
        var next = Request("key", 1, 2); next["load_epoch"] = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        Check((string?)coordinator.Execute("commit", next, resolver, () => "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", () => true, 44).Data!["status"] == "committed", "key did not expire with epoch");
        Console.WriteLine("Write ledger tests passed.");
    }
}
