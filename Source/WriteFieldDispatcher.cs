using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

// One catalog operation kind and the resolver that turns a request for it into
// prepared fields. Each operation is independently resolved and validated; the
// transaction engine has no operation-specific branches.
internal sealed class WriteOperationBinding
{
    internal WriteOperationBinding(string op, Func<JObject, IReadOnlyList<ResolvedWriteField>> resolve) { Op = op; Resolve = resolve; }
    internal string Op { get; }
    internal Func<JObject, IReadOnlyList<ResolvedWriteField>> Resolve { get; }
    internal static WriteOperationBinding Single(string op, Func<JObject, IPreparedWriteField> resolve) =>
        new(op, operation => new[] { new ResolvedWriteField(resolve(operation), operation["expect"]!, operation["value"]!) });
}

// The write operations of one native domain, and the reader that recovers the
// same field implementations from ledger entries without validating or writing.
internal interface IWriteFieldSet
{
    IEnumerable<WriteOperationBinding> Operations { get; }
    IReadableWriteField? ResolveLedger(string id, string field, object? target, JToken value);
}

internal sealed class WriteFieldDispatcher : IWriteFieldResolver
{
    private readonly Dictionary<string, WriteOperationBinding> bindings;
    internal WriteFieldDispatcher(IEnumerable<IWriteFieldSet> sets, IEnumerable<string> catalogOperations)
    {
        bindings = sets.SelectMany(set => set.Operations).ToDictionary(binding => binding.Op, StringComparer.Ordinal);
        if (!new HashSet<string>(catalogOperations, StringComparer.Ordinal).SetEquals(bindings.Keys)) throw new InvalidOperationException("Write catalog and field resolvers declare different operations.");
    }
    public IReadOnlyList<ResolvedWriteField> Resolve(JObject operation)
    {
        var kind = (string?)operation["op"];
        if (kind == null || !bindings.TryGetValue(kind, out var binding)) throw new ArgumentException("Unsupported write operation.");
        return binding.Resolve(operation);
    }
}

// Ledger fields are recovered by the first domain, in registration order, that
// recognizes the recorded identity.
internal sealed class LedgerFieldRouter
{
    private readonly IReadOnlyList<IWriteFieldSet> sets;
    internal LedgerFieldRouter(IReadOnlyList<IWriteFieldSet> sets) { this.sets = sets; }
    internal IReadableWriteField? Resolve(string id, string field, object? target, JToken value)
    {
        foreach (var set in sets) {
            var resolved = set.ResolveLedger(id, field, target, value);
            if (resolved != null) return resolved;
        }
        return null;
    }
}
