using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

// A prepared field owns the native target only for the duration of one Advance.
// Implementations must validate without mutation and Restore must tolerate a Write
// that mutated the field before throwing. The staged coordinator uses this core
// through the existing game-thread scheduler; HELLO does not advertise writes.
internal interface IPreparedWriteField
{
    string ObjectId { get; }
    string Field { get; }
    bool MayChangeByGame { get; }
    bool IsDestroyed { get; }
    object? NativeTarget { get; }
    JToken Read();
    void Validate(JToken value);
    void Write(JToken value);
    void Restore(JToken original);
}

// Structural writes have a game-assigned result, rather than a requested field
// value. Their undo adapter compares that result before removing the object.
internal interface IPreparedStructuralWrite : IPreparedWriteField
{
    JToken AppliedValue();
    JToken UndoExpected();
    IPreparedWriteField CreateUndo();
}

// Reject structure changes that overlap any field of the same aggregate.
internal interface IWriteAggregateScope
{
    string AggregateScope { get; }
    bool ChangesStructure { get; }
}

internal sealed class ResolvedWriteField
{
    internal ResolvedWriteField(IPreparedWriteField field, JToken expected, JToken value) { Field = field; Expected = expected; Value = value; }
    internal IPreparedWriteField Field { get; }
    internal JToken Expected { get; }
    internal JToken Value { get; }
}
internal sealed class AppliedWriteField
{
    internal AppliedWriteField(int index, IPreparedWriteField field, JToken original, JToken value)
    { Index = index; Field = field; Original = original.DeepClone(); Value = value.DeepClone(); }
    internal int Index { get; }
    internal IPreparedWriteField Field { get; }
    internal JToken Original { get; }
    internal JToken Value { get; }
}
internal interface IWriteFieldResolver
{
    IReadOnlyList<ResolvedWriteField> Resolve(JObject operation);
}

internal sealed class AtomicWriteOperation : IFrameQueryOperation
{
    private readonly IWriteFieldResolver resolver;
    private readonly JObject request;
    private readonly Func<string> currentEpoch;
    private readonly bool dryRun;
    private readonly Func<bool> canExecute;
    private readonly Func<IReadOnlyList<AppliedWriteField>, QueryOutcome>? completed;
    private readonly int ownerThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
    private bool disposed;
    public QueryOutcome? Outcome { get; private set; }

    internal AtomicWriteOperation(IWriteFieldResolver resolver, JObject request, Func<string> currentEpoch, bool dryRun, Func<bool>? canExecute = null, Func<IReadOnlyList<AppliedWriteField>, QueryOutcome>? completed = null)
    { this.resolver = resolver; this.request = request; this.currentEpoch = currentEpoch; this.dryRun = dryRun; this.canExecute = canExecute ?? (() => true); this.completed = completed; }

    public bool Advance()
    {
        if (Outcome != null || disposed) return true;
        var prepared = new List<(int Index, IPreparedWriteField Field, JToken Original, JToken Value)>();
        var written = new List<(IPreparedWriteField Field, JToken Original)>();
        try {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != ownerThread) throw new InvalidOperationException("Transaction must run on its owning game thread.");
            if (!canExecute()) { Outcome = QueryOutcome.Failure("world_unavailable", "The game is not ready for a transaction."); return true; }
            if ((string?)request["load_epoch"] != currentEpoch()) {
                Outcome = QueryOutcome.Failure("stale_epoch", "The requested colony load is no longer active.", ProtocolStatuses.StaleSession);
                return true;
            }
            if (request["ops"] is not JArray ops || ops.Count < 1 || ops.Count > 64) throw new ArgumentException("Expected 1-64 operations.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var aggregates = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (var i = 0; i < ops.Count; i++) {
                if (ops[i] is not JObject op || op["expect"] == null || op["value"] == null) throw new ArgumentException("Malformed operation.");
                var fields = resolver.Resolve(op);
                if (fields.Count == 0 || fields.Count > 64) throw new ArgumentException("Operation must resolve to 1-64 fields.");
                foreach (var resolved in fields) {
                    var field = resolved.Field;
                    if (!keys.Add(field.ObjectId + "\0" + field.Field)) throw new ArgumentException("Duplicate target field.");
                    if (field is IWriteAggregateScope scoped) {
                        if (aggregates.TryGetValue(scoped.AggregateScope, out var structural) && (structural || scoped.ChangesStructure))
                            throw new ArgumentException("A structural change overlaps another operation on the same aggregate.");
                        aggregates[scoped.AggregateScope] = scoped.ChangesStructure;
                    }
                    var value = resolved.Value.DeepClone();
                    field.Validate(value);
                    var observed = field.Read().DeepClone();
                    if (!JToken.DeepEquals(observed, resolved.Expected)) {
                        Outcome = new QueryOutcome { Data = new JObject { ["status"] = "conflict", ["op"] = i, ["expected"] = resolved.Expected.DeepClone(), ["observed"] = observed, ["last_writer"] = JValue.CreateNull(), ["at_tick"] = JValue.CreateNull() } };
                        return true;
                    }
                    prepared.Add((i, field, observed, value));
                    if (prepared.Count > 64) throw new ArgumentException("A transaction may contain at most 64 fields.");
                }
            }
            if ((string?)request["load_epoch"] != currentEpoch()) throw new InvalidOperationException("The load epoch changed during validation.");
            var rows = new JArray();
            for (var i = 0; i < prepared.Count; i++) {
                var item = prepared[i];
                if (!dryRun) {
                    // Include the current field before Write: a setter may throw after mutation.
                    written.Add((item.Field, item.Original));
                    item.Field.Write(item.Value);
                    if (item.Field is IPreparedStructuralWrite structural) {
                        item.Value = structural.AppliedValue().DeepClone();
                        prepared[i] = item;
                    }
                }
                rows.Add(new JObject { ["op"] = item.Index, ["object"] = item.Field.ObjectId, ["field"] = item.Field.Field, ["expect"] = item.Original.DeepClone(), ["value"] = item.Value.DeepClone(), ["may_change_by_game"] = item.Field.MayChangeByGame });
            }
            if (!dryRun) foreach (var item in prepared) if (!JToken.DeepEquals(item.Field.Read(), item.Value)) throw new InvalidOperationException("Readback differs from the requested value.");
            if ((string?)request["load_epoch"] != currentEpoch()) throw new InvalidOperationException("The load epoch changed during execution.");
            Outcome = completed != null
                ? completed(prepared.ConvertAll(item => new AppliedWriteField(item.Index, item.Field, item.Original, item.Value)))
                : new QueryOutcome { Data = new JObject { ["status"] = dryRun ? "validated" : "applied", ["fields"] = rows } };
        } catch (Exception error) when (ReadContext.Recoverable(error)) {
            var rollbackFailed = false;
            for (var i = written.Count - 1; i >= 0; i--) {
                try { written[i].Field.Restore(written[i].Original); if (!JToken.DeepEquals(written[i].Field.Read(), written[i].Original)) rollbackFailed = true; }
                catch (Exception rollbackError) when (ReadContext.Recoverable(rollbackError)) { rollbackFailed = true; }
            }
            Outcome = QueryOutcome.Failure(rollbackFailed ? "rollback_failed" : "write_failed", error.Message);
        }
        return true;
    }

    public void Dispose() { disposed = true; }
}
