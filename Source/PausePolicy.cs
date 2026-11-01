using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
namespace Neko.RimWorld;

// Game-thread state, scoped to an authenticated connection and loaded colony.
internal sealed class PausePolicy
{
    private string binding = "";
    private readonly HashSet<string> definitions = new(StringComparer.Ordinal);
    internal string Owner { get; private set; } = "unknown";
    internal long Revision { get; private set; }
    internal long TransitionRevision { get; private set; }
    internal void Bind(string connection, string colony, string epoch) {
        var next = connection.Length == 0 ? "" : connection + ":" + colony + ":" + epoch;
        if (binding != next) { binding = next; definitions.Clear(); Owner = "unknown"; Revision = checked(Revision + 1); }
    }
    internal JObject State(string connection, string colony, string epoch, int tick,
                           bool paused, bool speedPaused, bool forcePaused, bool windowPaused) =>
        new JObject { ["connection_epoch"] = connection, ["colony_id"] = colony, ["load_epoch"] = epoch,
            ["sampled_tick"] = tick, ["owner"] = Owner, ["pause_revision"] = Revision,
            ["pause_transition_revision"] = TransitionRevision, ["paused"] = paused,
            ["speed_paused"] = speedPaused, ["force_paused"] = forcePaused, ["windows_force_paused"] = windowPaused };
    internal JObject Evidence(string connection, string colony, string epoch, int tick,
                              bool paused, bool speedPaused, bool forcePaused, bool windowPaused) {
        if (binding.Length == 0 || string.IsNullOrEmpty(connection) || string.IsNullOrEmpty(colony) || string.IsNullOrEmpty(epoch) ||
            binding != connection + ":" + colony + ":" + epoch)
            return new JObject { ["status"] = "unavailable" };
        var state = State(connection, colony, epoch, tick, paused, speedPaused, forcePaused, windowPaused);
        state["status"] = "observed";
        return state;
    }
    internal void Set(IEnumerable<string> values) { definitions.Clear(); foreach (var value in values) definitions.Add(value); }
    internal bool Matches(string definition) => binding.Length != 0 && definitions.Contains(definition);
    internal void Observe(string owner) { Owner = owner; Revision = checked(Revision + 1); TransitionRevision = checked(TransitionRevision + 1); }
    internal static string ObservedSource(bool speedChanged, bool windowChanged) => !speedChanged && windowChanged ? "window" : "unknown";
    internal bool CanResume => Owner != "user" && Owner != "unknown";
    // Guarded requests must name the currently observed agent owner. Policy
    // faults require explicit operator recovery, even if the caller is planner.
    internal bool CanResumeExpected(string expected) => binding.Length != 0 &&
        Owner == expected && IsResumableOwner(expected);
    internal static bool IsResumableOwner(string owner) => owner is "planner" or "frontdesk" or "reflex" or "automatic_letter";
}
