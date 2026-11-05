using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

// One target per update; native registration qualification does not read a sample.
internal sealed class ColonyObservations
{
    private readonly Func<JObject, JObject> read;
    private readonly Action<JObject> qualify;
    private readonly Func<long> milliseconds;
    private readonly Func<JObject, IFrameQueryOperation?>? beginRead;
    internal ColonyObservations(Func<JObject, JObject> read, Action<JObject> qualify, Func<long>? milliseconds = null, Func<JObject, IFrameQueryOperation?>? beginRead = null) { this.read = read; this.qualify = qualify; this.beginRead = beginRead; this.milliseconds = milliseconds ?? (() => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency)); }
    private sealed class Condition {
        internal string ObservationId = "", Id = "", ReceiptId = ""; internal JToken Revision = JValue.CreateNull(); internal JObject Spec = new();
        internal int Remaining, Interval, TargetCursor; internal long Next, NextWall, StartedTick;
        internal readonly JArray Rows = new(); internal string? Fingerprint, PendingFingerprint; internal JObject? PendingEvent; internal bool PendingTerminal;
        internal IFrameQueryOperation? ActiveRead;
    }
    private sealed class Registration { internal JObject Row = new(); internal string Receipt = ""; internal List<Condition> Conditions = new(); }
    private readonly List<Condition> conditions = new(); private readonly Dictionary<string, Registration> registrationsByRevision = new(StringComparer.Ordinal); private string connection = ""; private int cursor;
    internal void Clear() { foreach (var c in registrationsByRevision.Values.SelectMany(r => r.Conditions)) ResetRead(c); conditions.Clear(); registrationsByRevision.Clear(); cursor = 0; connection = ""; }
    private static void ResetRead(Condition c) { c.ActiveRead?.Dispose(); c.ActiveRead = null; c.TargetCursor = 0; c.Rows.Clear(); }
    internal JObject Replace(JObject request, string connectionEpoch, long tick) {
        ColonyObservationContract.Validate(request); if (connection != connectionEpoch) Clear();
        var normalized = (JArray)request["observations"]!.DeepClone(); var acknowledgements = new JArray(); var accepted = new List<Condition>();
        foreach (JObject row in normalized) {
            foreach (JObject spec in (JArray)row["conditions"]!) spec["sample_interval_ticks"] ??= 250;
            var covered = new JArray(); string receipt = Guid.NewGuid().ToString("N"); string? reason = null; var staged = new List<Condition>();
            string key = (string)row["observation_id"]! + "\0" + row["revision"]!.ToString(Newtonsoft.Json.Formatting.None);
            if (registrationsByRevision.TryGetValue(key, out var prior)) {
                if (!JToken.DeepEquals(prior.Row, row)) reason = "An observation revision is immutable; submit a new revision for changed conditions.";
                else if (prior.Conditions.Any(c => c.Remaining < 0 || (long)c.Spec["expires_at_tick"]! <= tick)) reason = "This observation revision has expired or exhausted its budget; submit a new revision.";
                else { receipt = prior.Receipt; staged = prior.Conditions; foreach (var c in staged) covered.Add(c.Id); }
            } else if (registrationsByRevision.Count >= 4096) reason = "Observation revision capacity reached for this connection.";
            if (reason == null && staged.Count == 0) foreach (JObject spec in (JArray)row["conditions"]!) {
                try {
                    if ((long)spec["expires_at_tick"]! <= tick || (long)spec["expires_at_tick"]! > tick + 600000) throw new ArgumentException("Observation lifetime must be within the next 600000 ticks.");
                    qualify(spec);
                    var c = new Condition { ObservationId = (string)row["observation_id"]!, Revision = row["revision"]!.DeepClone(), Id = (string)spec["id"]!, ReceiptId = receipt, Spec = spec, Remaining = (int)spec["max_samples"]!, Interval = (int)spec["sample_interval_ticks"]!, Next = tick };
                    staged.Add(c); covered.Add(c.Id);
                } catch (Exception e) when (ReadContext.Recoverable(e)) { reason = e.Message; break; }
            }
            var ack = new JObject { ["observation_id"] = row["observation_id"]!.DeepClone(), ["revision"] = row["revision"]!.DeepClone(), ["status"] = reason == null ? "registered" : "rejected", ["covered_condition_ids"] = reason == null ? covered : new JArray(), ["receipt_id"] = receipt };
            if (reason != null) ack["reason"] = reason;
            else { accepted.AddRange(staged); if (!registrationsByRevision.ContainsKey(key)) registrationsByRevision.Add(key, new Registration { Row = (JObject)row.DeepClone(), Receipt = receipt, Conditions = staged }); }
            acknowledgements.Add(ack);
        }
        foreach (var old in conditions.Where(c => !accepted.Contains(c))) { ResetRead(old); old.PendingEvent = null; }
        conditions.Clear(); conditions.AddRange(accepted); cursor = 0; connection = connectionEpoch;
        return new JObject { ["source"] = "planner", ["observations"] = normalized, ["registrations"] = acknowledgements };
    }
    internal void Advance(string currentConnection, long tick, Func<JObject, bool> emit, bool paused = false) {
        if (connection != currentConnection) { Clear(); return; } if (conditions.Count == 0) return;
        cursor %= conditions.Count; var c = conditions.FirstOrDefault(item => item.ActiveRead != null) ?? conditions[cursor]; cursor = (cursor + 1) % conditions.Count;
        if (c.PendingEvent != null) { if (!c.PendingTerminal && tick >= (long)c.Spec["expires_at_tick"]!) { ResetRead(c); Queue(c, tick, "expired", new JObject(), null, true); } Deliver(c, emit); return; }
        if (tick >= (long)c.Spec["expires_at_tick"]!) { ResetRead(c); Queue(c, tick, "expired", new JObject(), null, true); Deliver(c, emit); return; }
        long wall = milliseconds(); if (tick < c.Next && (!paused || wall < c.NextWall)) return;
        if (c.Remaining <= 0) { Queue(c, tick, "exhausted", new JObject(), null, true); Deliver(c, emit); return; }
        if (c.TargetCursor == 0 && c.ActiveRead == null) c.StartedTick = tick;
        try {
            var targets = (JArray)c.Spec["target_ids"]!; var one = (JObject)c.Spec.DeepClone(); one["target_ids"] = new JArray(targets[c.TargetCursor].DeepClone());
            JObject sample;
            c.ActiveRead ??= beginRead?.Invoke(one);
            if (c.ActiveRead != null) { if (!c.ActiveRead.Advance()) return; var outcome = c.ActiveRead.Outcome; c.ActiveRead.Dispose(); c.ActiveRead = null; if (outcome?.Data == null || outcome.Status != ProtocolStatuses.Ok && outcome.Status != ProtocolStatuses.Partial) throw new ArgumentException(outcome?.Message ?? "Native observation source returned unavailable coverage."); sample = outcome.Data; }
            else sample = read(one);
            if (sample["objects"] is not JArray rows) throw new ArgumentException("Native observation source omitted its target rows.");
            if (sample["complete"]?.Value<bool>() != true) { var partial = (JObject)sample.DeepClone(); partial["complete"] = false; c.Remaining--; ResetRead(c); c.Next = tick + c.Interval; c.NextWall = wall + 2000; c.Fingerprint = null; Queue(c, tick, "unknown", partial, null, false); Deliver(c, emit); return; }
            foreach (var row in rows) c.Rows.Add(row.DeepClone()); c.TargetCursor++;
            if (c.TargetCursor < targets.Count) return;
            var observation = new JObject { ["objects"] = c.Rows.DeepClone(), ["complete"] = true }; if (c.Spec["map_id"] != null) observation["map_id"] = c.Spec["map_id"]!.DeepClone(); if (c.Spec["plan_id"] != null) observation["plan_id"] = c.Spec["plan_id"]!.DeepClone();
            c.Remaining--; c.TargetCursor = 0; c.Rows.Clear(); c.Next = tick + c.Interval; c.NextWall = wall + 2000;
            var serialized = observation.ToString(Newtonsoft.Json.Formatting.None); var fingerprint = GameQueryEngine.Hash(serialized);
            if (Encoding.UTF8.GetByteCount(serialized) > 262144) { c.Fingerprint = null; Queue(c, tick, "unknown", new JObject { ["reason"] = "observation_result_too_large", ["requires_read"] = true }, null, false); }
            else if (fingerprint != c.Fingerprint) Queue(c, tick, "changed", observation, fingerprint, false);
        } catch (Exception e) when (ReadContext.Recoverable(e)) { c.Remaining--; ResetRead(c); c.Next = tick + c.Interval; c.NextWall = wall + 2000; c.Fingerprint = null; Queue(c, tick, "unknown", new JObject { ["reason"] = e.Message }, null, false); }
        Deliver(c, emit);
    }
    private static void Queue(Condition c, long tick, string status, JObject observation, string? fingerprint, bool terminal) {
        c.PendingFingerprint = fingerprint; c.PendingTerminal = terminal;
        c.PendingEvent = new JObject { ["kind"] = "observation", ["observation_id"] = c.ObservationId, ["revision"] = c.Revision.DeepClone(), ["condition_id"] = c.Id, ["receipt_id"] = c.ReceiptId, ["status"] = status, ["sample_tick"] = tick, ["observation"] = observation, ["coverage"] = new JObject { ["target_ids"] = c.Spec["target_ids"]!.DeepClone(), ["complete"] = status == "changed", ["remaining_samples"] = c.Remaining, ["started_tick"] = c.StartedTick, ["finished_tick"] = tick, ["atomic"] = false } };
    }
    private void Deliver(Condition c, Func<JObject, bool> emit) { if (c.PendingEvent == null || !emit((JObject)c.PendingEvent.DeepClone())) return; c.Fingerprint = c.PendingFingerprint; c.PendingEvent = null; if (c.PendingTerminal) { c.Remaining = -1; conditions.Remove(c); } }
}
