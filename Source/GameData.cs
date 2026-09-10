using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Runtime.CompilerServices;
using GraphQL;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal sealed class GameEntity
{
    public GameEntity(string id, string? mapId, object handle) { Id = id; MapId = mapId; Handle = handle; }
    public string Id { get; }
    public string? MapId { get; }
    public object Handle { get; }
}

/// <summary>A query-local source handle. Never serialized or retained across queries.</summary>
internal sealed class GameNode
{
    public GameNode(string kind, string key, GameEntity owner, object? handle)
    { Kind = kind; Key = key; Owner = owner; Handle = handle; }
    public string Kind { get; }
    public string Key { get; }
    public GameEntity Owner { get; }
    public object? Handle { get; }
}

internal interface IGameData
{
    IEnumerable<GameEntity> Maps();
    IEnumerable<GameEntity> Colonists();
    object? GameField(string field);
    object? MapField(GameEntity map, string field);
    object? ColonistField(GameEntity pawn, string field);
    object? NodeField(GameNode node, string field, IReadOnlyDictionary<string, object?> arguments);
}

internal sealed class NodeSlice
{
    public List<GameNode> Items { get; set; } = new();
    public int Total { get; set; }
    public int Offset { get; set; }
    public int Returned => Items.Count;
    public bool HasMore => Offset + Returned < Total;
    public int? NextOffset => HasMore ? Offset + Returned : null;
    public bool Complete => true;
}

internal sealed class ReadContext
{
    private readonly IGameData source;
    private readonly int threadId = Thread.CurrentThread.ManagedThreadId;
    private readonly double budgetMilliseconds;
    private Stopwatch? clock;
    private List<GameEntity>? maps;
    private List<GameEntity>? colonists;
    private readonly Dictionary<string, object?> values = new();
    private readonly Dictionary<object, int> nativeReferences = new(new ReferenceComparer());
    public string NativeReference(object value)
    {
        Check();
        if (!nativeReferences.TryGetValue(value, out var id)) { id = nativeReferences.Count; nativeReferences.Add(value, id); }
        return Session.LoadEpoch + ":" + Session.SnapshotSequence + ":object:" + id;
    }
    private readonly Dictionary<object, int> thoughtReferences = new(new ReferenceComparer());
    public ReadContext(IGameData source, SessionDescriptor session, double budgetMilliseconds)
    { this.source = source; Session = session; this.budgetMilliseconds = budgetMilliseconds; }
    public SessionDescriptor Session { get; }
    public void Check()
    {
        if (Thread.CurrentThread.ManagedThreadId != threadId)
            throw Error("wrong_thread", "Game reads must remain on the calling game thread.");
        clock ??= Stopwatch.StartNew();
        if (clock.Elapsed.TotalMilliseconds >= budgetMilliseconds)
            throw Error("deadline_exceeded", "The game read deadline was reached; narrow the query.");
    }
    public List<GameEntity> Maps() => maps ??= ReadList(source.Maps(), 32);
    public List<GameEntity> Colonists() => colonists ??= ReadList(source.Colonists(), 8192);
    private List<GameEntity> ReadList(IEnumerable<GameEntity> input, int limit)
    {
        Check();
        var result = new List<GameEntity>();
        foreach (var entity in input)
        {
            Check();
            if (result.Count >= limit) throw Error("entity_scan_limit", "The identity scan limit was reached.");
            result.Add(entity);
        }
        return result.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
    }
    public object? Read(string kind, GameEntity? entity, string field)
    {
        if (entity != null && field == "id") return entity.Id;
        if (entity != null && field == "mapId") return entity.MapId;
        return Cached(new JArray(kind, entity?.Id ?? "", field).ToString(Formatting.None), () => kind == "Game" ? source.GameField(field) :
            kind == "Map" ? source.MapField(entity!, field) : source.ColonistField(entity!, field));
    }
    public object? NativeRoot(string root, IReadOnlyDictionary<string, object?> args) => Cached("root:" + root + ":" + JObject.FromObject(args).ToString(Formatting.None), () => source is IThingData native ? native.NativeRoot(root, args, this) : throw Error("unsupported_target", "Native Thing data is unavailable."));
    public object? ReadNode(GameNode node, string field, IReadOnlyDictionary<string, object?>? arguments = null)
    {
        if (node.Kind == "Thought" && field == "ref")
        {
            var identity = node.Handle ?? node;
            if (!thoughtReferences.TryGetValue(identity, out var localId))
            { localId = thoughtReferences.Count; thoughtReferences.Add(identity, localId); }
            return Session.LoadEpoch + ":" + Session.SnapshotSequence + ":" + node.Owner.Id + ":thought:" + localId;
        }
        if (node.Kind == "Thought" && field == "referenceScope") return "observation";
        var args = arguments ?? new Dictionary<string, object?>();
        var key = new JArray(node.Owner.Id, node.Kind, node.Key, field,
            JObject.FromObject(args.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value)))
            .ToString(Formatting.None);
        return Cached(key, () => {
            var value = source is IThingData native ? native.NativeField(node, field, args, this) : source.NodeField(node, field, args);
            if (value is IEnumerable<GameNode> sequence)
            {
                var rows = new List<GameNode>();
                foreach (var child in sequence)
                {
                    Check();
                    if (rows.Count >= 8192) throw Error("node_scan_limit", "Nested collection exceeds 8192 records.");
                    rows.Add(child);
                }
                return rows;
            }
            return value;
        });
    }
    public NodeSlice? Slice(GameNode node, string field, int first, int offset, IReadOnlyDictionary<string, object?> args)
    {
        var rows = ReadNode(node, field, args) as List<GameNode>;
        if (rows == null) return null;
        return new NodeSlice { Items = rows.Skip(offset).Take(first).ToList(), Total = rows.Count, Offset = offset };
    }
    private object? Cached(string key, Func<object?> read)
    {
        if (Thread.CurrentThread.ManagedThreadId != threadId) throw Error("wrong_thread", "Invalid game thread.");
        if (values.TryGetValue(key, out var cached)) return cached;
        Check();
        try
        {
            var value = read();
            if (value is float f && (float.IsNaN(f) || float.IsInfinity(f)) || value is double d && (double.IsNaN(d) || double.IsInfinity(d)))
                throw Error("invalid_number", "The game returned a non-finite value.");
            if (value is string text && text.Length > 4096)
                throw Error("field_too_large", "The field exceeds its text limit.");
            values[key] = value;
            return value;
        }
        catch (ExecutionError) { throw; }
        catch (Exception exception) when (Recoverable(exception))
        { throw Error("field_unavailable", "The requested game field is unavailable."); }
    }
    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }
    internal static bool Recoverable(Exception exception) => exception is not OutOfMemoryException &&
        exception is not AccessViolationException && exception is not System.Threading.ThreadAbortException;
    internal static ExecutionError Error(string code, string message) => new(message) { Code = code };
}

internal sealed class ColonistPage
{
    public List<GameEntity> Nodes { get; set; } = new();
    public bool HasNextPage { get; set; }
    public string? EndCursor { get; set; }
}
