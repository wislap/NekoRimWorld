using System;
using System.Collections.Generic;
using System.Linq;

namespace Neko.RimWorld;

// Save-scoped identity links only; values remain exclusively in WriteLedger.
internal sealed class WriteTargetIndex
{
    private Dictionary<string, object> targets = new(StringComparer.Ordinal);
    private readonly Func<object, bool> isDestroyed;
    internal WriteTargetIndex(Func<object, bool> isDestroyed) { this.isDestroyed = isDestroyed; }
    internal Dictionary<string, object> Snapshot() => targets;
    internal void Restore(Dictionary<string, object> snapshot) { targets = snapshot; }
    internal void BindMany(IEnumerable<KeyValuePair<string, object?>> values) {
        var next = new Dictionary<string, object>(targets, StringComparer.Ordinal);
        foreach (var value in values) if (value.Value != null) next[value.Key] = value.Value;
        targets = next;
    }
    internal IReadOnlyList<KeyValuePair<string, object>> Entries() => targets.ToArray();
    internal void Prune(WriteLedger ledger) {
        if (targets.Count == 0) return;
        var removed = new HashSet<string>(targets.Where(x => isDestroyed(x.Value)).Select(x => x.Key), StringComparer.Ordinal);
        if (removed.Count == 0) return;
        ledger.RemoveConfirmedDestroyed(removed.Contains);
        var next = new Dictionary<string, object>(targets, StringComparer.Ordinal);
        foreach (var id in removed) next.Remove(id);
        targets = next;
    }
}
