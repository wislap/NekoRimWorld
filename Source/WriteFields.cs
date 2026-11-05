using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

// Some operations compare a complete native collection before projecting its
// changed members onto ordinary fields. Keep that original CAS conflict shape.
internal sealed class WritePreconditionConflictException : Exception
{
    internal JToken Expected { get; }
    internal JToken Observed { get; }
    internal WritePreconditionConflictException(JToken expected, JToken observed) : base("The complete native precondition changed.") { Expected = expected.DeepClone(); Observed = observed.DeepClone(); }
}

// A native field identity and its current value, as observed by ledger snapshots.
internal interface IReadableWriteField
{
    string ObjectId { get; }
    string Field { get; }
    bool MayChangeByGame { get; }
    bool IsDestroyed { get; }
    JToken Read();
}

// A prepared field owns the native target only for the duration of one Advance.
// Implementations must validate without mutation and Restore must tolerate a Write
// that mutated the field before throwing. The staged coordinator uses this core
// through the existing game-thread scheduler; HELLO does not advertise writes.
internal interface IPreparedWriteField : IReadableWriteField
{
    object? NativeTarget { get; }
    void Validate(JToken value);
    void Write(JToken value);
    void Restore(JToken original);
}

// Structural writes have a game-assigned result, rather than a requested field
// value; readback compares the field with that result.
internal interface IStructuralWriteField : IPreparedWriteField
{
    JToken AppliedValue();
}

// A committed structural write whose undo adapter compares the recorded result
// before removing the object.
internal interface IPreparedStructuralWrite : IStructuralWriteField
{
    JToken UndoExpected();
    IPreparedWriteField CreateUndo();
}

// Reject structure changes that overlap any field of the same aggregate.
internal interface IWriteAggregateScope
{
    string AggregateScope { get; }
    bool ChangesStructure { get; }
}

// Native owners can reject incompatible spatial claims before any mutation,
// while leaving unrelated elements in the same bounded transaction.
internal interface IWriteConflictScope
{
    bool ConflictsWith(IPreparedWriteField other);
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
