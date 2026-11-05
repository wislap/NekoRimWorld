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
    internal static JArray? Selection(JObject args) {
        if (!args.HasValues) return null;
        if (args.Count != 1 || args["fields"] is not JArray fields || fields.Count < 1 || fields.Count > 64) throw new ArgumentException("Select 1..64 unique ledger fields, or use empty arguments for the legacy full snapshot.");
        foreach (var item in fields) if (item is not JObject row || row.Count != 2 || new[] { "object", "field" }.Any(name => row[name]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)row[name]) || ((string)row[name]!).Length > 256)) throw new ArgumentException("Each ledger selection needs an object and field identity.");
        if (fields.Select(item => Key((string)item["object"]!, (string)item["field"]!)).Distinct(StringComparer.Ordinal).Count() != fields.Count) throw new ArgumentException("Ledger fields must be unique.");
        return new JArray(fields.OfType<JObject>().OrderBy(row => (string)row["object"]!, StringComparer.Ordinal).ThenBy(row => (string)row["field"]!, StringComparer.Ordinal).Select(row => row.DeepClone()));
    }
    internal string? ManualId(string obj, string field) => (string?)Find(obj, field)?["manual_source"]?["manual_id"];
    internal JArray ManualIds(string obj) => new(entries.Values.Where(row => (string?)row["object"] == obj && ((string?)row["field"])?.StartsWith("area_flag:", StringComparison.Ordinal) == true && row["manual_source"] is JObject)
        .Select(row => (JObject)row["manual_source"]!).OrderBy(source => (int)source["cell"]!["z"]!).ThenBy(source => (int)source["cell"]!["x"]!)
        .Select(source => new JObject { ["x"] = source["cell"]!["x"]!.DeepClone(), ["z"] = source["cell"]!["z"]!.DeepClone(), ["manual_id"] = source["manual_id"]!.DeepClone() }));
    internal JObject[] AreaFields(string obj) => entries.Values.Where(row => (string?)row["object"] == obj && ((string?)row["field"])?.StartsWith("area_flag:", StringComparison.Ordinal) == true).Select(row => (JObject)row.DeepClone()).ToArray();
    internal JObject? RecordManualChange(string obj, string field, JObject evidence) {
        if (!entries.TryGetValue(Key(obj, field), out var prior)) return null;
        if (!field.StartsWith("area_flag:", StringComparison.Ordinal)) throw new ArgumentException("Manual area provenance requires a tracked cell field.");
        var source = (JObject)evidence.DeepClone();
        source["ownership"] = new JObject { ["changeset_id"] = prior["changeset_id"]!.DeepClone(), ["value"] = prior["value"]!.DeepClone() };
        if (!ValidManualSource(source, obj, field)) throw new ArgumentException("Invalid native manual area provenance.");
        var row = (JObject)prior.DeepClone(); row["manual_source"] = source;
        entries = new Dictionary<string, JObject>(entries, StringComparer.Ordinal) { [Key(obj, field)] = row };
        return (JObject)source.DeepClone();
    }
    private static bool ValidManualSource(JObject source, string obj, string field) {
        bool Id(JToken? value) => value?.Type == JTokenType.String && Guid.TryParseExact((string?)value, "N", out _);
        bool Flag(JToken? value) => value is JObject flag && flag["native_instance"]?.Type == JTokenType.String && flag["enabled"]?.Type == JTokenType.Boolean && (flag["manual_id"]?.Type == JTokenType.Null || Id(flag["manual_id"]));
        if (!Id(source["manual_id"]) || !Id(source["origin_load_epoch"]) || source["manual_order"]?.Type != JTokenType.Integer || (long)source["manual_order"]! < 1 || (string?)source["source"] != "user_designator" || (string?)source["object"] != obj || (string?)source["field"] != field || source["cell"] is not JObject cell || cell["x"]?.Type != JTokenType.Integer || cell["z"]?.Type != JTokenType.Integer || (string?)source["map_id"] is not string map || string.IsNullOrWhiteSpace(map) || source["area_kind"]?.Type != JTokenType.String || source["designator"]?.Type != JTokenType.String || source["tick"]?.Type != JTokenType.Integer || (long)source["tick"]! < 0 || !Flag(source["old_value"]) || !Flag(source["new_value"]) || !JToken.DeepEquals(source["new_value"]!["manual_id"], source["manual_id"]) || !JToken.DeepEquals(source["old_value"]!["native_instance"], source["new_value"]!["native_instance"]) || source["ownership"] is not JObject ownership || ownership["changeset_id"]?.Type != JTokenType.String || ownership["value"] == null) return false;
        return field == "area_flag:" + ((int)cell["x"]!).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + ((int)cell["z"]!).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    internal string Serialize() => new JArray(entries.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value.DeepClone())).ToString(Formatting.None);
    internal void Load(string? json) {
        var next = new Dictionary<string, JObject>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(json)) {
            foreach (var token in JArray.Parse(json!)) {
                if (token is not JObject row || row.Properties().Count() != (row["manual_source"] == null ? 6 : 7) || row["object"]?.Type != JTokenType.String || row["field"]?.Type != JTokenType.String || row["value"] == null || row["changeset_id"]?.Type != JTokenType.String || row["author"]?.Type != JTokenType.String || (string?)row["author"] != "planner" || row["tick"]?.Type != JTokenType.Integer || (long)row["tick"]! < 0 || new[] { "object", "field", "changeset_id" }.Any(name => string.IsNullOrWhiteSpace((string?)row[name]) || ((string)row[name]!).Length > 256) || row["manual_source"] != null && (row["manual_source"] is not JObject manual || !ValidManualSource(manual, (string)row["object"]!, (string)row["field"]!))) throw new ArgumentException("Invalid write ledger.");
                var key = Key((string)row["object"]!, (string)row["field"]!);
                if (next.ContainsKey(key)) throw new ArgumentException("Duplicate write ledger field.");
                next.Add(key, (JObject)row.DeepClone());
            }
        }
        entries = next;
    }
    internal JObject ReadSnapshot(bool valid, WriteTargetIndex targets, Func<object, bool> destroyed, Func<string, string, object?, JToken, IReadableWriteField?> resolve, JArray? fields = null) {
        var rows = new JArray();
        var selected = fields == null ? null : new HashSet<string>(fields.Select(field => Key((string)field["object"]!, (string)field["field"]!)), StringComparer.Ordinal);
        if (valid) foreach (var source in entries.Where(pair => selected == null || selected.Contains(pair.Key)).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value)) {
            var entry = (JObject)source.DeepClone();
            string id = (string)entry["object"]!, field = (string)entry["field"]!;
            var target = targets.Find(id);
            string status = "unresolved"; JToken? current = null; bool? mayChange = null;
            if (target != null && destroyed(target)) status = "destroyed";
            else try {
                var resolved = resolve(id, field, target, entry["value"]!);
                if (resolved == null) status = "unknown";
                else if (resolved.ObjectId != id || resolved.Field != field) status = "unresolved";
                else if (resolved.IsDestroyed) status = "destroyed";
                else { current = resolved.Read(); mayChange = resolved.MayChangeByGame; status = "observed"; }
            } catch (Exception error) when (ReadContext.Recoverable(error)) { status = "unresolved"; }
            entry["current_status"] = status;
            entry["current_value"] = current ?? JValue.CreateNull();
            entry["may_change_by_game"] = mayChange.HasValue ? new JValue(mayChange.Value) : JValue.CreateNull();
            rows.Add(entry);
        }
        var result = new JObject { ["ledger_status"] = valid ? "valid" : "invalid", ["complete"] = valid, ["entry_count"] = rows.Count, ["entries"] = rows };
        if (fields != null) { result["scope"] = new JObject { ["kind"] = "fields", ["fields"] = fields.DeepClone() }; result["missing_fields"] = new JArray(fields.Where(field => !entries.ContainsKey(Key((string)field["object"]!, (string)field["field"]!))).Select(field => field.DeepClone())); }
        return result;
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
        foreach (var field in fields) {
            var row = new JObject {
            ["object"] = field.Field.ObjectId, ["field"] = field.Field.Field, ["value"] = field.Value.DeepClone(),
            ["changeset_id"] = changeset, ["author"] = author, ["tick"] = tick };
            if (entries.TryGetValue(Key(field.Field.ObjectId, field.Field.Field), out var prior) && prior["manual_source"] is JObject source) row["manual_source"] = source.DeepClone();
            next[Key(field.Field.ObjectId, field.Field.Field)] = row;
        }
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
    private sealed class Undo { internal string Changeset = ""; internal List<AppliedWriteField> Fields = new(); internal List<JToken> Expected = new(); internal bool Used; }
    private sealed class UndoResolver : IWriteFieldResolver {
        private readonly Undo saved;
        internal UndoResolver(Undo saved) { this.saved = saved; }
        public IReadOnlyList<ResolvedWriteField> Resolve(JObject op) {
            int index = (int)op["index"]!; var field = saved.Fields[index];
            var structural = field.Field as IPreparedStructuralWrite;
            var target = structural != null ? structural.CreateUndo() : field.Field;
            return new[] { new ResolvedWriteField(target, saved.Expected[index], field.Original) };
        }
    }
    private readonly WriteLedger ledger;
    private readonly WriteTargetIndex targets;
    private string epoch = "";
    private readonly Dictionary<string, Receipt> receipts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Undo> undo = new(StringComparer.Ordinal);
    internal WriteCoordinator(WriteLedger ledger, WriteTargetIndex? targets = null) { this.ledger = ledger; this.targets = targets ?? new WriteTargetIndex(x => x is IPreparedWriteField f && f.IsDestroyed); }
    internal JObject UndoFields(string token) {
        if (!undo.TryGetValue(token, out var saved)) return new JObject { ["status"] = "unknown_token", ["complete"] = true, ["field_count"] = 0, ["fields"] = new JArray() };
        if (saved.Used) return new JObject { ["status"] = "already_undone", ["complete"] = true, ["field_count"] = 0, ["fields"] = new JArray() };
        return new JObject { ["status"] = "available", ["complete"] = true, ["field_count"] = saved.Fields.Count, ["fields"] = new JArray(saved.Fields.Select(f => new JObject { ["object"] = f.Field.ObjectId, ["field"] = f.Field.Field })) };
    }
    internal static JObject FieldIdentities(JObject changeset, IWriteFieldResolver resolver) {
        ColonyWriteContract.ValidateRequest("dry_run", changeset);
        var rows = new JArray(); int index = 0;
        foreach (JObject op in (JArray)changeset["ops"]!) {
            foreach (var resolved in resolver.Resolve(op)) rows.Add(new JObject { ["op"] = index, ["object"] = resolved.Field.ObjectId, ["field"] = resolved.Field.Field });
            index++;
        }
        return new JObject { ["complete"] = true, ["field_count"] = rows.Count, ["fields"] = rows };
    }
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
        if (mode == "commit" && receipts.TryGetValue(key, out var receipt)) {
            if (!JToken.DeepEquals(receipt.Request, request)) return QueryOutcome.Failure("idempotency_key_reused", "Idempotency key belongs to another request.");
            var replay = Copy(receipt.Outcome); replay.IsReplay = true; return replay;
        }
        if (!ready()) return QueryOutcome.Failure("world_unavailable", "The game is not ready for a transaction.");
        PruneConfirmedDestroyed();
        QueryOutcome Complete(IReadOnlyList<AppliedWriteField> fields) {
            if (mode == "dry_run") return new QueryOutcome { Data = new JObject { ["status"] = "ok", ["preview"] = new JArray(fields.Select(f => new JObject { ["op"] = f.Index, ["object"] = f.Field.ObjectId, ["field"] = f.Field.Field, ["expect"] = f.Original.DeepClone(), ["value"] = f.Value.DeepClone() })) } };
            var changeset = (string)request["changeset_id"]!;
            var token = Guid.NewGuid().ToString("N");
            var outcome = new QueryOutcome { Data = new JObject { ["status"] = "committed", ["changeset_id"] = changeset, ["load_epoch"] = epoch, ["readback"] = Readback(fields), ["undo_token"] = token } };
            // Prepare all potentially throwing allocations before publishing metadata.
            var savedUndo = new Undo { Changeset = changeset, Fields = fields.ToList(), Expected = fields.Select(f => (f.Field is IPreparedStructuralWrite structural ? structural.UndoExpected() : f.Value).DeepClone()).ToList() };
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
        using var operation = new AtomicWriteOperation(new UndoResolver(saved), request, currentEpoch, false, ready, Complete);
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
