using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

// Wire structure only. No executable capability is advertised until a native executor exists.
public static class ColonyWriteContract
{
    public const string Capability = "colony_write.v1";
    private static bool Identifier(JToken? value) => value?.Type == JTokenType.String && value.Value<string>() is string text && !string.IsNullOrWhiteSpace(text) && text.Length <= 256;
    private static bool CanonicalEpoch(JToken? value) => value?.Type == JTokenType.String &&
        Guid.TryParseExact((string?)value, "N", out var parsed) && (string?)value == parsed.ToString("N");
    public static ProtocolEnvelope UnavailableResult(ProtocolEnvelope request, SessionDescriptor current, bool sessionUnavailable) {
        bool stale = sessionUnavailable || request.ColonyId != current.ColonyId || request.LoadEpoch != current.LoadEpoch ||
            ((request.Payload.Mode == "commit" || request.Payload.Mode == "dry_run") && (string?)request.Payload.Arguments?["load_epoch"] != current.LoadEpoch);
        return ProtocolEnvelope.Create(ProtocolMessageTypes.WriteResult, request.ConnectionEpoch, 0, new ProtocolPayload {
            Status = stale ? ProtocolStatuses.StaleSession : ProtocolStatuses.Unsupported,
            ErrorCode = stale ? "stale_epoch" : "unsupported", Message = stale ? "The requested colony load is no longer active." : "Writing is not available.", Issues = Array.Empty<QueryIssue>() }, current, request.RequestId);
    }
    public static void ValidateResult(ProtocolPayload payload) {
        if (payload.Status != ProtocolStatuses.Ok) {
            if (payload.Status != ProtocolStatuses.Failed && payload.Status != ProtocolStatuses.StaleSession && payload.Status != ProtocolStatuses.Unsupported && payload.Status != ProtocolStatuses.Busy) throw new ProtocolException(ProtocolErrorCodes.InvalidPayload, "Invalid write result status.");
            if (payload.Issues == null || payload.Issues.Length != 0) throw new ProtocolException(ProtocolErrorCodes.InvalidPayload, "Write failure cannot carry query issues.");
            return;
        }
        var data = payload.Data ?? throw new ProtocolException(ProtocolErrorCodes.InvalidPayload, "write result data required.");
        void Bad() => throw new ProtocolException(ProtocolErrorCodes.InvalidPayload, "Invalid write receipt.");
        void Fields(params string[] fields) { if (data.Properties().Any(p => !fields.Contains(p.Name)) || fields.Any(f => data[f] == null)) Bad(); }
        void Text(string field) { if (data[field]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)data[field])) Bad(); }
        void Rows(string field, bool preview) {
            if (data[field] is not JArray rows) { Bad(); return; }
            foreach (var item in rows) {
                if (item is not JObject row) { Bad(); continue; }
                var names = preview ? new[] { "op", "object", "field", "expect", "value" } : new[] { "object", "field", "value" };
                if (row.Properties().Any(p => !names.Contains(p.Name)) || names.Any(n => row.Property(n) == null)) Bad();
                foreach (var name in new[] { "object", "field" }) {
                    var value = row[name];
                    if (value?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)value) || ((string)value!).Length > 256) Bad();
                }
                if (preview && (row["op"]?.Type != JTokenType.Integer || (long)row["op"]! < 0)) Bad();
            }
        }
        if (data["status"] == null) {
            if (data.Property("expected_owner") != null) Fields("paused", "role", "expected_owner");
            else Fields("paused", "role");
            if (data["paused"]?.Type != JTokenType.Boolean) Bad();
            Text("role");
            if ((string?)data["role"] != "planner" && (string?)data["role"] != "frontdesk" && (string?)data["role"] != "policy") Bad();
            if ((string?)data["role"] == "policy" && (bool)data["paused"]! == false) Bad();
            if (data.Property("expected_owner") != null && ((bool)data["paused"]! || (string?)data["role"] != "planner" || !PausePolicy.IsResumableOwner((string?)data["expected_owner"] ?? ""))) Bad();
            return;
        }
        switch ((string?)data["status"]) {
            case "ok": Fields("status", "preview"); Rows("preview", true); break;
            case "committed": Fields("status", "changeset_id", "load_epoch", "readback", "undo_token"); Text("changeset_id"); if (!CanonicalEpoch(data["load_epoch"])) Bad(); Text("undo_token"); Rows("readback", false); break;
            case "undone": Fields("status", "changeset_id", "load_epoch", "readback"); Text("changeset_id"); if (!CanonicalEpoch(data["load_epoch"])) Bad(); Rows("readback", false); break;
            case "conflict": Fields("status", "op", "expected", "observed", "last_writer", "at_tick"); if (data["op"]?.Type != JTokenType.Integer || (long)data["op"]! < 0 || (data["at_tick"]?.Type != JTokenType.Null && (data["at_tick"]?.Type != JTokenType.Integer || (long)data["at_tick"]! < 0))) Bad(); if (data["last_writer"]?.Type != JTokenType.Null) Text("last_writer"); break;
            case "unknown_token":
            case "already_undone": Fields("status"); break;
            case "stale_epoch": Fields("status", "load_epoch"); if (!CanonicalEpoch(data["load_epoch"])) Bad(); break;
            default: Bad(); break;
        }
    }
    public static void ValidateRequest(string? mode, JObject? args)
    {
        void Bad(string message) => throw new ProtocolException(ProtocolErrorCodes.InvalidPayload, message);
        void Keys(JObject obj, params string[] names) {
            if (obj.Properties().Any(p => !names.Contains(p.Name)) || names.Any(n => obj[n] == null)) Bad("Missing or unknown write argument.");
        }
        void Text(JToken? token) {
            if (token?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)token) || ((string)token!).Length > 256) Bad("Invalid write identifier.");
        }
        if (args == null) { Bad("arguments must be an object."); return; }
        if (mode == "dry_run" || mode == "commit") {
            Keys(args, "changeset_id", "author", "entry_id", "load_epoch", "idempotency_key", "ops");
            foreach (var k in new[] { "changeset_id", "author", "entry_id", "load_epoch", "idempotency_key" }) Text(args[k]);
            if ((string?)args["author"] != "planner") Bad("Only planner can author a changeset.");
            if (!CanonicalEpoch(args["load_epoch"])) Bad("load_epoch must be a canonical UUID.");
            if (args["ops"] is not JArray ops || ops.Count < 1 || ops.Count > 64) { Bad("ops must contain 1-64 operations."); return; }
            foreach (var token in ops) {
                if (token is not JObject op) { Bad("operation must be an object."); return; }
                Text(op["op"]); Text(op["evidence"]);
                if (op["expect"] == null || op["value"] == null) Bad("operation needs expect and value.");
                var targets = op.Properties().Where(p => p.Name != "op" && p.Name != "evidence" && p.Name != "expect" && p.Name != "value").ToArray();
                if (!targets.Any(p => new[] { "pawn", "bill", "thing", "worktable", "storage", "research", "map" }.Contains(p.Name)) || targets.Any(p => p.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)p.Value))) Bad("operation target must contain nonempty text fields.");
                if ((string?)op["op"] == "add_production_bill") {
                    if (op.Properties().Count() != 5 || !Identifier(op["worktable"]) || op["expect"] is not JObject order || order.Properties().Count() != 1 || order["bill_ids"] is not JArray ids || ids.Any(id => !Identifier(id)) || ids.Select(id => id.Value<string>()).Distinct(StringComparer.Ordinal).Count() != ids.Count || op["value"] is not JObject recipe || recipe.Properties().Count() != 1 || !Identifier(recipe["recipe"])) Bad("Invalid production bill operation.");
                } else if (op["expect"] is JObject || op["value"] is JObject) {
                    if (op["expect"] is not JObject e || op["value"] is not JObject v || !e.HasValues || !e.Properties().Select(p => p.Name).OrderBy(x => x).SequenceEqual(v.Properties().Select(p => p.Name).OrderBy(x => x))) Bad("expect and value object fields must match.");
                }
                if (op.Properties().Count() > 16) Bad("Too many operation fields.");
            }
        } else if (mode == "undo") { Keys(args, "token"); Text(args["token"]); }
        else if (mode == "set_paused") {
            if (args.Property("expected_owner") != null) Keys(args, "paused", "role", "expected_owner");
            else Keys(args, "paused", "role");
            if (args["paused"]?.Type != JTokenType.Boolean) Bad("paused must be boolean.");
            Text(args["role"]);
            if ((string?)args["role"] != "planner" && (string?)args["role"] != "frontdesk" && (string?)args["role"] != "policy") Bad("Unknown pause role.");
            if (((string?)args["role"] == "frontdesk" || (string?)args["role"] == "policy") && (bool)args["paused"]! == false) Bad("This role cannot resume.");
            if (args.Property("expected_owner") != null && ((bool)args["paused"]! || (string?)args["role"] != "planner" || !PausePolicy.IsResumableOwner((string?)args["expected_owner"] ?? ""))) Bad("Invalid expected pause owner.");
        }
        else Bad("Unknown write mode.");
    }
}
