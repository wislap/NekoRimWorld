using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class AtomicWriteOperationTests
{
    private sealed class FieldProbe : IPreparedWriteField {
        public string ObjectId { get; set; } = ""; public string Field => "value"; public bool MayChangeByGame => false;
        public int Value, Writes; public bool FailWrite, FailValidation, FailRestore, IgnoreWrite; public Action<JToken>? Guard;
        public JToken Read() => new JValue(Value);
        public void Validate(JToken value) { Guard?.Invoke(value); if (FailValidation) throw new ArgumentException("invalid"); }
        public void Write(JToken value) { if (!IgnoreWrite) Value = (int)value; Writes++; if (FailWrite) throw new InvalidOperationException("after mutation"); }
        public void Restore(JToken original) { if (FailRestore) throw new InvalidOperationException("restore failed"); Value = (int)original; }
    }
    private sealed class Resolver : IWriteFieldResolver {
        public readonly Dictionary<string, FieldProbe> Fields = new();
        public IReadOnlyList<ResolvedWriteField> Resolve(JObject op) {
            if ((string?)op["thing"] == "both") return new[] {
                new ResolvedWriteField(Fields["a"], op["expect"]!["a"]!, op["value"]!["a"]!),
                new ResolvedWriteField(Fields["b"], op["expect"]!["b"]!, op["value"]!["b"]!) };
            return new[] { new ResolvedWriteField(Fields[(string)op["thing"]!], op["expect"]!, op["value"]!) };
        }
    }
    private static JObject Request(int expected2 = 2) => new JObject {
        ["load_epoch"] = "epoch", ["ops"] = new JArray(
            new JObject { ["thing"] = "a", ["expect"] = 1, ["value"] = 3 },
            new JObject { ["thing"] = "b", ["expect"] = expected2, ["value"] = 4 }) };
    internal static void Run() {
        NativeSafety();
        var a = new FieldProbe { ObjectId = "a", Value = 1 }; var b = new FieldProbe { ObjectId = "b", Value = 2 };
        var resolver = new Resolver(); resolver.Fields.Add("a", a); resolver.Fields.Add("b", b);
        using (var op = new AtomicWriteOperation(resolver, Request(9), () => "epoch", false)) {
            Require(op.Advance() && (string?)op.Outcome!.Data!["status"] == "conflict" && a.Writes == 0 && b.Writes == 0, "Conflict wrote fields or yielded.");
            Require(op.Outcome!.Data!["last_writer"]!.Type == JTokenType.Null && op.Outcome!.Data!["at_tick"]!.Type == JTokenType.Null, "Unknown conflict provenance invented.");
        }
        var combined = new JObject { ["load_epoch"] = "epoch", ["ops"] = new JArray(new JObject { ["thing"] = "both", ["expect"] = new JObject { ["a"] = 1, ["b"] = 9 }, ["value"] = new JObject { ["a"] = 3, ["b"] = 4 } }) };
        using (var op = new AtomicWriteOperation(resolver, combined, () => "epoch", false)) { Require(op.Advance() && (string?)op.Outcome!.Data!["status"] == "conflict" && a.Writes == 0 && b.Writes == 0, "Second field conflict in one operation wrote the first."); }
        b.Guard = _ => NativeWriteSafety.RequireWorkPriority(0, 1, false, 1);
        using (var op = new AtomicWriteOperation(resolver, Request(), () => "epoch", false)) { Require(op.Advance() && op.Outcome!.ErrorCode == "write_failed" && a.Writes == 0 && b.Writes == 0, "Work safety rejection wrote fields."); }
        b.Guard = _ => NativeWriteSafety.RequireStorageItem(true, true, true);
        using (var op = new AtomicWriteOperation(resolver, Request(), () => "epoch", false)) { Require(op.Advance() && op.Outcome!.ErrorCode == "write_failed" && a.Writes == 0 && b.Writes == 0, "Storage safety rejection wrote fields."); }
        b.Guard = null;
        b.FailValidation = true;
        using (var op = new AtomicWriteOperation(resolver, Request(), () => "epoch", false)) { Require(op.Advance() && a.Writes == 0 && b.Writes == 0, "Validation wrote fields."); }
        b.FailValidation = false; b.FailWrite = true;
        using (var op = new AtomicWriteOperation(resolver, Request(), () => "epoch", false)) { Require(op.Advance() && a.Value == 1 && b.Value == 2 && op.Outcome!.ErrorCode == "write_failed", "Exception did not restore all fields."); }
        b.FailRestore = true;
        using (var op = new AtomicWriteOperation(resolver, Request(), () => "epoch", false)) { Require(op.Advance() && op.Outcome!.ErrorCode == "rollback_failed", "Rollback failure was not reported."); }
        b.FailRestore = false; b.Value = 2;
        b.FailWrite = false; b.IgnoreWrite = true;
        using (var op = new AtomicWriteOperation(resolver, Request(), () => "epoch", false)) { Require(op.Advance() && a.Value == 1 && b.Value == 2 && op.Outcome!.ErrorCode == "write_failed", "Readback mismatch did not restore all fields."); }
        b.IgnoreWrite = false;
        using (var op = new AtomicWriteOperation(resolver, Request(), () => "epoch", false, () => false)) { Require(op.Advance() && op.Outcome!.ErrorCode == "world_unavailable" && a.Value == 1 && b.Value == 2, "World guard wrote fields."); }
        using (var op = new AtomicWriteOperation(resolver, Request(), () => "other", false)) { Require(op.Advance() && op.Outcome!.ErrorCode == "stale_epoch" && a.Value == 1 && b.Value == 2, "Epoch mismatch wrote fields."); }
        using (var op = new AtomicWriteOperation(resolver, Request(), () => "epoch", false)) { Require(op.Advance() && a.Value == 3 && b.Value == 4, "Success did not complete in one advance."); }
        var wrongThread = new AtomicWriteOperation(resolver, Request(), () => "epoch", false);
        var thread = new System.Threading.Thread(() => wrongThread.Advance()); thread.Start(); thread.Join();
        Require(wrongThread.Outcome!.ErrorCode == "write_failed" && a.Value == 3 && b.Value == 4, "Wrong thread executed transaction.");
        Console.WriteLine("Atomic write tests passed: conflict, validation, rollback, epoch and single advance.");
    }
    private static void NativeSafety()
    {
        void Reject(Action action) {
            try { action(); throw new Exception("Unsafe native setter was accepted."); }
            catch (ArgumentException) { }
        }
        NativeWriteSafety.RequireWorkPriority(1, 2, true, 1);
        NativeWriteSafety.RequireWorkPriority(0, 1, false, 0);
        Reject(() => NativeWriteSafety.RequireWorkPriority(1, 0, true, 0));
        Reject(() => NativeWriteSafety.RequireWorkPriority(1, 0, false, 1));
        Reject(() => NativeWriteSafety.RequireWorkPriority(0, 1, true, 0));
        Reject(() => NativeWriteSafety.RequireWorkPriority(0, 1, false, 1));
        NativeWriteSafety.RequireStorageItem(true, false, true);
        Reject(() => NativeWriteSafety.RequireStorageItem(false, false, true));
        Reject(() => NativeWriteSafety.RequireStorageItem(true, true, true));
        Reject(() => NativeWriteSafety.RequireStorageItem(true, false, false));
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
