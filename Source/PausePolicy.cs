using System;
using System.Collections.Generic;
namespace Neko.RimWorld;

// Game-thread state, scoped to an authenticated connection and loaded colony.
internal sealed class PausePolicy
{
    private string binding = "";
    private readonly HashSet<string> definitions = new(StringComparer.Ordinal);
    internal string Owner { get; private set; } = "unknown";
    internal void Bind(string connection, string colony, string epoch) {
        var next = connection.Length == 0 ? "" : connection + ":" + colony + ":" + epoch;
        if (binding != next) { binding = next; definitions.Clear(); Owner = "unknown"; }
    }
    internal void Set(IEnumerable<string> values) { definitions.Clear(); foreach (var value in values) definitions.Add(value); }
    internal bool Matches(string definition) => binding.Length != 0 && definitions.Contains(definition);
    internal void Observe(string owner) => Owner = owner;
    internal static string ObservedSource(bool speedChanged, bool windowChanged) => !speedChanged && windowChanged ? "window" : "unknown";
    internal bool CanResume => Owner != "user" && Owner != "unknown";
    // Guarded requests must name the currently observed agent owner. Policy
    // faults require explicit operator recovery, even if the caller is planner.
    internal bool CanResumeExpected(string expected) => binding.Length != 0 &&
        Owner == expected && IsResumableOwner(expected);
    internal static bool IsResumableOwner(string owner) => owner is "planner" or "frontdesk" or "reflex" or "automatic_letter";
}
