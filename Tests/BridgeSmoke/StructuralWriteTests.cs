using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class StructuralWriteTests
{
    private sealed class State { internal readonly List<string> Ids = new(); internal int Next, Setting; internal bool ThrowAfterAdd; }
    private sealed class Addition : IPreparedStructuralWrite, IWriteAggregateScope {
        private readonly State state; private string? id; private JObject? applied;
        internal Addition(State state) { this.state = state; }
        public string ObjectId => "table"; public string Field => "bills"; public bool MayChangeByGame => false;
        public bool IsDestroyed => false; public object NativeTarget => state;
        public string AggregateScope => "table"; public bool ChangesStructure => true;
        private JObject Order() => new() { ["bill_ids"] = new JArray(state.Ids) };
        public JToken Read() => id == null ? Order() : Result();
        private JObject Snapshot() { var value = Order(); value["created"] = new JObject { ["bill_id"] = id, ["setting"] = state.Setting }; return value; }
        private JObject Result() { var value = Order(); value["created"] = new JObject { ["bill_id"] = id }; return value; }
        public void Validate(JToken value) { if (value is not JObject) throw new ArgumentException(); }
        public void Write(JToken value) { id = "generated-" + ++state.Next; state.Ids.Add(id); if (state.ThrowAfterAdd) throw new InvalidOperationException(); applied = Snapshot(); }
        public JToken AppliedValue() => Result();
        // The coordinator must freeze this native guard at commit, rather than
        // resampling the player's later configuration when undo is requested.
        public JToken UndoExpected() => Snapshot();
        public void Restore(JToken original) { if (id != null) state.Ids.Remove(id); id = null; }
        public IPreparedWriteField CreateUndo() => new Removal(this);
        private sealed class Removal : IPreparedWriteField, IWriteAggregateScope {
            private readonly Addition add; private int index; private bool removed;
            internal Removal(Addition add) { this.add = add; }
            public string ObjectId => add.ObjectId; public string Field => add.Field; public bool MayChangeByGame => false;
            public bool IsDestroyed => false; public object NativeTarget => add.state;
            public string AggregateScope => add.AggregateScope; public bool ChangesStructure => true;
            public JToken Read() => removed ? add.Order() : add.Snapshot();
            public void Validate(JToken value) { if (value is not JObject) throw new ArgumentException(); }
            public void Write(JToken value) { index = add.state.Ids.IndexOf(add.id!); if (index < 0) throw new InvalidOperationException(); add.state.Ids.RemoveAt(index); removed = true; }
            public void Restore(JToken value) { if (removed) { add.state.Ids.Insert(index, add.id!); removed = false; } }
        }
    }
    private sealed class Resolver : IWriteFieldResolver {
        internal readonly State State = new();
        public IReadOnlyList<ResolvedWriteField> Resolve(JObject op) => new[] { new ResolvedWriteField((string?)op["op"] == "field" || (string?)op["op"] == "throw" ? new FieldProbe((string?)op["op"] == "throw") : new Addition(State), op["expect"]!, op["value"]!) };
    }
    private sealed class FieldProbe : IPreparedWriteField, IWriteAggregateScope {
        private readonly bool fail; private int value;
        internal FieldProbe(bool fail) { this.fail = fail; }
        public string ObjectId => "bill"; public string Field => "setting"; public bool MayChangeByGame => false; public bool IsDestroyed => false;
        public object? NativeTarget => null; public string AggregateScope => fail ? "other" : "table"; public bool ChangesStructure => false;
        public JToken Read() => new JValue(value); public void Validate(JToken value) { } public void Write(JToken value) { this.value = (int)value; if (fail) throw new InvalidOperationException(); } public void Restore(JToken value) { this.value = (int)value; }
    }
    private static JObject Request(string key) => new() { ["load_epoch"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ["author"] = "planner", ["changeset_id"] = "cs", ["idempotency_key"] = key, ["entry_id"] = "entry", ["ops"] = new JArray(new JObject { ["op"] = "add_production_bill", ["worktable"] = "table", ["evidence"] = "ev", ["expect"] = new JObject { ["bill_ids"] = new JArray() }, ["value"] = new JObject { ["recipe"] = "Meal" } }) };
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    internal static void Run() {
        var resolver = new Resolver(); var ledger = new WriteLedger(); var coordinator = new WriteCoordinator(ledger); coordinator.StartEpoch("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        QueryOutcome Run(string mode, JObject request) => coordinator.Execute(mode, request, resolver, () => "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", () => true, 1);
        var request = Request("a");
        Check((string?)Run("dry_run", request).Data!["status"] == "ok" && resolver.State.Next == 0, "dry run allocated identity");
        var overlap = (JObject)request.DeepClone(); ((JArray)overlap["ops"]!).Add(new JObject { ["op"] = "field", ["expect"] = 0, ["value"] = 1 });
        using (var atomic = new AtomicWriteOperation(resolver, overlap, () => "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)) {
            atomic.Advance(); Check(atomic.Outcome!.ErrorCode == "write_failed" && resolver.State.Next == 0, "overlapping structural and field writes were accepted");
        }
        using (var atomic = new AtomicWriteOperation(resolver, request, () => "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false, completed: _ => throw new InvalidOperationException("publication"))) {
            atomic.Advance(); Check(atomic.Outcome!.ErrorCode == "write_failed" && resolver.State.Ids.Count == 0, "metadata failure left structural change");
        }
        var mixed = (JObject)request.DeepClone(); ((JArray)mixed["ops"]!).Add(new JObject { ["op"] = "throw", ["expect"] = 0, ["value"] = 1 });
        using (var atomic = new AtomicWriteOperation(resolver, mixed, () => "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)) {
            atomic.Advance(); Check(atomic.Outcome!.ErrorCode == "write_failed" && resolver.State.Ids.Count == 0, "later field failure did not roll back the addition");
        }
        resolver.State.ThrowAfterAdd = true;
        Check(Run("commit", Request("failure")).ErrorCode == "write_failed" && resolver.State.Ids.Count == 0 && ledger.Count == 0, "failed addition was not rolled back");
        resolver.State.ThrowAfterAdd = false;
        var result = Run("commit", request); var token = (string)result.Data!["undo_token"]!;
        Check((string?)result.Data!["readback"]![0]!["value"]!["created"]!["bill_id"] == "generated-4" && resolver.State.Ids.Count == 1, "actual identity missing");
        Check(ledger.Find("table", "bills")!["value"]!["created"]!["setting"] == null && result.Data!["readback"]![0]!["value"]!["created"]!["setting"] == null, "undo-only mutable state leaked into the structural ledger");
        Check(JToken.DeepEquals(result.Data, Run("commit", request).Data) && resolver.State.Ids.Count == 1, "idempotent replay created another object");
        resolver.State.Setting = 1;
        Check((string?)Run("undo", new JObject { ["token"] = token }).Data!["status"] == "conflict" && resolver.State.Ids.Count == 1, "undo ignored configuration conflict");
        resolver.State.Setting = 0;
        Check((string?)Run("undo", new JObject { ["token"] = token }).Data!["status"] == "undone" && resolver.State.Ids.Count == 0, "undo did not remove created object");
        Console.WriteLine("Structural write tests passed.");
    }
}
