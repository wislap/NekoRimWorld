using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

// Save-scoped latest values only. The serialized representation is independent of
// the transient receipt and undo state and is replaced atomically after each commit.
internal sealed class WriteLedger
{
    private Dictionary<string, JObject> entries = new(StringComparer.Ordinal);
    private static string Key(string obj, string field) => obj + "\0" + field;
    internal int Count => entries.Count;
    internal JObject? Find(string obj, string field) => entries.TryGetValue(Key(obj, field), out var value) ? (JObject)value.DeepClone() : null;
    internal string Serialize() => new JArray(entries.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value.DeepClone())).ToString(Formatting.None);
    internal void Load(string? json) {
        var next = new Dictionary<string, JObject>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(json)) {
            foreach (var token in JArray.Parse(json!)) {
                if (token is not JObject row || row.Properties().Count() != 6 || row["object"]?.Type != JTokenType.String || row["field"]?.Type != JTokenType.String || row["value"] == null || row["changeset_id"]?.Type != JTokenType.String || row["author"]?.Type != JTokenType.String || (string?)row["author"] != "planner" || row["tick"]?.Type != JTokenType.Integer || (long)row["tick"]! < 0 || new[] { "object", "field", "changeset_id" }.Any(name => string.IsNullOrWhiteSpace((string?)row[name]) || ((string)row[name]!).Length > 256)) throw new ArgumentException("Invalid write ledger.");
                var key = Key((string)row["object"]!, (string)row["field"]!);
                if (next.ContainsKey(key)) throw new ArgumentException("Duplicate write ledger field.");
                next.Add(key, (JObject)row.DeepClone());
            }
        }
        entries = next;
    }
    internal void RemoveConfirmedDestroyed(Func<string, bool> destroyed) {
        var next = new Dictionary<string, JObject>(entries, StringComparer.Ordinal);
        foreach (var item in entries) if (destroyed((string)item.Value["object"]!)) next.Remove(item.Key);
        entries = next;
    }
    internal Dictionary<string, JObject> Snapshot() => entries;
    internal void Restore(Dictionary<string, JObject> snapshot) { entries = snapshot; }
    internal void Record(IReadOnlyList<AppliedWriteField> fields, string changeset, string author, int tick) {
        var next = new Dictionary<string, JObject>(entries, StringComparer.Ordinal);
        foreach (var field in fields) next[Key(field.Field.ObjectId, field.Field.Field)] = new JObject {
            ["object"] = field.Field.ObjectId, ["field"] = field.Field.Field, ["value"] = field.Value.DeepClone(),
            ["changeset_id"] = changeset, ["author"] = author, ["tick"] = tick };
        entries = next;
    }
}

// Separates Scribe's scalar storage from parsing; malformed saves keep their
// original bytes and disable writing without preventing ordinary game loading.
internal sealed class WriteLedgerSaveState
{
    internal string Data = string.Empty;
    internal bool Valid { get; private set; } = true;
    internal void Loaded(WriteLedger ledger) {
        try { ledger.Load(Data); Valid = true; }
        catch (Exception error) when (ReadContext.Recoverable(error)) { Valid = false; }
    }
    internal void PrepareSave(WriteLedger ledger) { if (Valid) Data = ledger.Serialize(); }
}

// Receipts and undo references are epoch-scoped. The save-scoped identity
// index is owned by the game component and retained independently.
internal sealed class WriteCoordinator
{
    private sealed class Receipt { internal JObject Request = null!; internal QueryOutcome Outcome = null!; }
    private sealed class Undo { internal string Changeset = ""; internal List<AppliedWriteField> Fields = new(); internal bool Used; }
    private sealed class UndoResolver : IWriteFieldResolver {
        private readonly List<AppliedWriteField> fields;
        internal UndoResolver(List<AppliedWriteField> fields) { this.fields = fields; }
        public IReadOnlyList<ResolvedWriteField> Resolve(JObject op) {
            var field = fields[(int)op["index"]!];
            return new[] { new ResolvedWriteField(field.Field, field.Value, field.Original) };
        }
    }
    private readonly WriteLedger ledger;
    private readonly WriteTargetIndex targets;
    private string epoch = "";
    private readonly Dictionary<string, Receipt> receipts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Undo> undo = new(StringComparer.Ordinal);
    internal WriteCoordinator(WriteLedger ledger, WriteTargetIndex? targets = null) { this.ledger = ledger; this.targets = targets ?? new WriteTargetIndex(x => x is IPreparedWriteField f && f.IsDestroyed); }
    internal void StartEpoch(string value) { epoch = value; receipts.Clear(); undo.Clear(); }
    internal void PruneConfirmedDestroyed() => targets.Prune(ledger);
    private static QueryOutcome Copy(QueryOutcome o) => new() { Status = o.Status, Data = (JObject?)o.Data?.DeepClone(), ErrorCode = o.ErrorCode, Message = o.Message, Issues = o.Issues };
    private static JArray Readback(IReadOnlyList<AppliedWriteField> fields) => new(fields.Select(f => new JObject { ["object"] = f.Field.ObjectId, ["field"] = f.Field.Field, ["value"] = f.Value.DeepClone() }));
    internal QueryOutcome Execute(string mode, JObject request, IWriteFieldResolver resolver, Func<string> currentEpoch, Func<bool> ready, int tick) {
        try { ColonyWriteContract.ValidateRequest(mode, request); }
        catch (ProtocolException error) { return QueryOutcome.Failure(error.ErrorCode, error.Message); }
        if (epoch != currentEpoch() || ((mode == "commit" || mode == "dry_run") && (string?)request["load_epoch"] != epoch)) return QueryOutcome.Failure("stale_epoch", "The requested colony load is no longer active.", ProtocolStatuses.StaleSession);
        if (mode == "undo") {
            if (!ready()) return QueryOutcome.Failure("world_unavailable", "The game is not ready for a transaction.");
            PruneConfirmedDestroyed();
            return UndoNow((string?)request["token"] ?? "", currentEpoch, ready, tick);
        }
        if (mode != "commit" && mode != "dry_run") return QueryOutcome.Failure("unsupported", "Writing mode is unavailable.", ProtocolStatuses.Unsupported);
        if ((string?)request["author"] != "planner") return QueryOutcome.Failure("invalid_author", "Only planner may submit a changeset.");
        var key = (string?)request["idempotency_key"] ?? "";
        if (mode == "commit" && receipts.TryGetValue(key, out var receipt)) return JToken.DeepEquals(receipt.Request, request) ? Copy(receipt.Outcome) : QueryOutcome.Failure("idempotency_key_reused", "Idempotency key belongs to another request.");
        if (!ready()) return QueryOutcome.Failure("world_unavailable", "The game is not ready for a transaction.");
        PruneConfirmedDestroyed();
        QueryOutcome Complete(IReadOnlyList<AppliedWriteField> fields) {
            if (mode == "dry_run") return new QueryOutcome { Data = new JObject { ["status"] = "ok", ["preview"] = new JArray(fields.Select(f => new JObject { ["op"] = f.Index, ["object"] = f.Field.ObjectId, ["field"] = f.Field.Field, ["expect"] = f.Original.DeepClone(), ["value"] = f.Value.DeepClone() })) } };
            var changeset = (string)request["changeset_id"]!;
            var token = Guid.NewGuid().ToString("N");
            var outcome = new QueryOutcome { Data = new JObject { ["status"] = "committed", ["changeset_id"] = changeset, ["load_epoch"] = epoch, ["readback"] = Readback(fields), ["undo_token"] = token } };
            // Prepare all potentially throwing allocations before publishing metadata.
            var savedUndo = new Undo { Changeset = changeset, Fields = fields.ToList() };
            var savedReceipt = new Receipt { Request = (JObject)request.DeepClone(), Outcome = Copy(outcome) };
            var prior = ledger.Snapshot();
            var priorTargets = targets.Snapshot();
            try {
                ledger.Record(fields, changeset, "planner", tick);
                targets.BindMany(fields.Select(field => new KeyValuePair<string, object?>(field.Field.ObjectId, field.Field.NativeTarget)));
                undo.Add(token, savedUndo); receipts.Add(key, savedReceipt);
                if (epoch != currentEpoch() || !ready()) throw new InvalidOperationException("World changed during transaction publication.");
            }
            catch { ledger.Restore(prior); targets.Restore(priorTargets); undo.Remove(token); receipts.Remove(key); throw; }
            return outcome;
        }
        using var operation = new AtomicWriteOperation(resolver, request, currentEpoch, mode == "dry_run", ready, Complete);
        operation.Advance();
        var result = operation.Outcome!;
        if (mode == "commit" && !receipts.ContainsKey(key)) receipts.Add(key, new Receipt { Request = (JObject)request.DeepClone(), Outcome = Copy(result) });
        return result;
    }
    private QueryOutcome UndoNow(string token, Func<string> currentEpoch, Func<bool> ready, int tick) {
        if (!undo.TryGetValue(token, out var saved)) return new QueryOutcome { Data = new JObject { ["status"] = "unknown_token" } };
        if (saved.Used) return new QueryOutcome { Data = new JObject { ["status"] = "already_undone" } };
        var ops = new JArray();
        for (var i = saved.Fields.Count - 1; i >= 0; i--) ops.Add(new JObject { ["index"] = i, ["expect"] = saved.Fields[i].Value.DeepClone(), ["value"] = saved.Fields[i].Original.DeepClone() });
        var request = new JObject { ["load_epoch"] = epoch, ["ops"] = ops };
        QueryOutcome Complete(IReadOnlyList<AppliedWriteField> fields) {
            var result = new QueryOutcome { Data = new JObject { ["status"] = "undone", ["changeset_id"] = saved.Changeset, ["load_epoch"] = epoch, ["readback"] = Readback(fields) } };
            var prior = ledger.Snapshot();
            try {
                ledger.Record(fields, saved.Changeset, "planner", tick);
                if (epoch != currentEpoch() || !ready()) throw new InvalidOperationException("World changed during undo publication.");
                saved.Used = true;
            } catch { ledger.Restore(prior); throw; }
            return result;
        }
        using var operation = new AtomicWriteOperation(new UndoResolver(saved.Fields), request, currentEpoch, false, ready, Complete);
        operation.Advance(); return operation.Outcome!;
    }
}

// Created and advanced by the game component on the same game thread.
internal sealed class CoordinatedWriteOperation : IFrameQueryOperation
{
    private readonly Func<QueryOutcome> run;
    private readonly int ownerThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
    private bool disposed;
    public QueryOutcome? Outcome { get; private set; }
    internal CoordinatedWriteOperation(Func<QueryOutcome> run) { this.run = run; }
    public bool Advance() {
        if (disposed || Outcome != null) return true;
        if (System.Threading.Thread.CurrentThread.ManagedThreadId != ownerThread) {
            Outcome = QueryOutcome.Failure("write_failed", "Transaction must run on its owning game thread."); return true;
        }
        try { Outcome = run(); }
        catch (Exception error) when (ReadContext.Recoverable(error)) { Outcome = QueryOutcome.Failure("write_failed", error.Message); }
        return true;
    }
    public void Dispose() { disposed = true; }
}

internal static class WriteReceiptSession
{
    internal static SessionDescriptor AtTick(SessionDescriptor sample, long tick) =>
        new(sample.ColonyId, sample.LoadEpoch, tick, sample.SnapshotSequence);
}
