using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

// Cheap raw-name candidate selection before the normal query projection. No cache:
// Mod-added definitions and localized labels are read from the current collection.
internal static class DefinitionCandidates
{
    internal const int MaxNames = 64;
    internal static IEnumerable<T> Exact<T>(IEnumerable<T> source, IEnumerable<string> requested,
        Func<T, string> name, Func<T, string?> label, Action? check = null)
    {
        var names = requested.ToArray();
        if (names.Length == 0 || names.Length > MaxNames || names.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 128))
            throw ReadContext.Error("invalid_arguments", "exact_names requires 1..64 nonempty names of at most 128 characters.");
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        int visited = 0;
        foreach (var item in source)
        {
            if ((visited++ & 127) == 0) check?.Invoke();
            if (wanted.Contains(name(item)) || label(item) is string text && wanted.Contains(text)) yield return item;
        }
        check?.Invoke();
    }

    internal static JObject Suggestions(IEnumerable<(string Name, string? Label)> source, string requested,
        Action? check = null, int limit = 8)
    {
        var matches = new List<(int Rank, string Name, string? Label)>(); int visited = 0;
        foreach (var item in source)
        {
            if ((visited++ & 127) == 0) check?.Invoke();
            int rank = string.Equals(item.Label, requested, StringComparison.OrdinalIgnoreCase) ? 0 : Distance(item.Name, requested, 3);
            if (rank <= 3) matches.Add((rank, item.Name, item.Label));
        }
        check?.Invoke();
        var ordered = matches.OrderBy(m => m.Rank).ThenBy(m => m.Name, StringComparer.Ordinal).ToArray();
        return new JObject {
            ["definition_kind"] = "StatDef", ["matches"] = new JArray(ordered.Take(limit).Select(m => new JObject { ["definition"] = m.Name, ["label"] = m.Label })),
            ["truncated"] = ordered.Length > limit,
            ["meaning"] = "Actual loaded StatDef names/labels, ranked by exact label or bounded name edit distance. Suggestions are not evaluated values or automatic substitutions."
        };
    }

    private static int Distance(string left, string right, int bound)
    {
        if (Math.Abs(left.Length - right.Length) > bound) return bound + 1;
        var prior = Enumerable.Range(0, right.Length + 1).ToArray(); var next = new int[right.Length + 1];
        for (int i = 1; i <= left.Length; i++)
        {
            next[0] = i; int best = i;
            for (int j = 1; j <= right.Length; j++) {
                next[j] = Math.Min(Math.Min(prior[j] + 1, next[j - 1] + 1), prior[j - 1] + (char.ToUpperInvariant(left[i - 1]) == char.ToUpperInvariant(right[j - 1]) ? 0 : 1));
                best = Math.Min(best, next[j]);
            }
            if (best > bound) return bound + 1;
            var swap = prior; prior = next; next = swap;
        }
        return prior[right.Length];
    }
}
