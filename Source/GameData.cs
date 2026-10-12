using System;
using System.Collections.Generic;
using System.Collections;
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
    public GameNode(string kind, string key, GameEntity owner, object? handle, object? recordIdentity = null)
    { Kind = kind; Key = key; Owner = owner; Handle = handle; RecordIdentity = recordIdentity; }
    public string Kind { get; }
    public string Key { get; }
    public GameEntity Owner { get; }
    public object? Handle { get; }
    // Only a native record identity, never a temporary query wrapper. The
    // object reader may weakly pin it, then verifies membership on resumption.
    public object? RecordIdentity { get; }
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

internal interface IObjectQueryData
{
    IEnumerable<GameEntity> QueryObjectPawns(IReadOnlyDictionary<string,object?> args,ReadContext context);
    IEnumerable<GameEntity> QueryObjectColonists(IReadOnlyDictionary<string,object?> args,ReadContext context);
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
    private Stopwatch? lifetime;
    private List<GameEntity>? maps;
    private List<GameEntity>? colonists;
    private readonly Dictionary<object, object?> values = new();
    private readonly Dictionary<object,(long Started,long Finished)> valueSamples=new();
    private readonly Dictionary<object,string> argumentKeys = new(new ReferenceComparer());
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
    // The object reader owns collection snapshots and lossless text delivery;
    // legacy slice/text caps must not silently constrain that separate contract.
    internal bool ObjectReadMode { get; set; }
    internal GameEntity? ObjectActor { get; set; }
    internal GameEntity? ObjectRecipient { get; set; }
    internal int NativeGetterReads { get; private set; }
    internal string? ObjectScopeMap { get; set; }
    internal void ResetObjectReadCache() { if(ObjectReadMode) { values.Clear();valueSamples.Clear(); } }
    internal int ObjectFrameVersion { get; private set; }
    internal Func<long>? ObjectTick { get; set; }
    internal (long Started,long Finished)? LastValueSample { get; private set; }
    internal void BeginSelectedGetter()=>LastValueSample=null;
    internal double ElapsedMilliseconds => lifetime?.Elapsed.TotalMilliseconds ?? 0;
    public SelectionConditions? Conditions { get; set; }
    public Dictionary<string, JObject> ResolvedFields { get; } = new();
    public void TraceField(IResolveFieldContext context)
    {
        if (context.ResponsePath == null) return; // Direct condition/capture reads have no response path.
        var path = ""; foreach (var part in context.ResponsePath) path = ResultEvidence.Path(path, part.ToString()!);
        if (path.Length == 0) return;
        var info = new JObject { ["native"] = context.ParentType.Name + "." + context.FieldDefinition.Name };
        if (context.Arguments?.Count > 0) info["arguments"] = JObject.FromObject(context.Arguments.ToDictionary(p => p.Key, p => p.Value.Value));
        if (context.Source is NodeSlice slice) info["window"] = new JObject { ["total"] = slice.Total, ["offset"] = slice.Offset, ["returned"] = slice.Returned, ["has_more"] = slice.HasMore };
        if (context.Source is ThingPage page) info["window"] = new JObject { ["returned"] = page.Nodes.Count, ["has_more"] = page.HasNextPage };
        if (context.Source is ColonistPage colonistsPage) info["window"] = new JObject { ["returned"] = colonistsPage.Nodes.Count, ["has_more"] = colonistsPage.HasNextPage };
        if (context.Source is GameEntity entity) info["subject"] = new JObject { ["id"] = entity.Id, ["map_id"] = entity.MapId };
        if (context.Source is GameNode node) info["subject"] = new JObject { ["owner_id"] = node.Owner.Id, ["map_id"] = node.Owner.MapId, ["type"] = node.Kind, ["key"] = node.Key };
        ResolvedFields[path] = info;
    }
    private readonly Dictionary<string, SelectionConditions.Predicate> predicates = new();
    public Func<object?, bool> Matcher(string type, object? where)
    {
        if (where == null) return _ => true;
        var key = type + ":" + JsonConvert.SerializeObject(where);
        if (!predicates.TryGetValue(key, out var predicate))
            predicates[key] = predicate = (Conditions ?? throw Error("filter_unavailable", "Condition schema unavailable.")).Compile(type, where);
        return value => {
            Check();
            return predicate(value, this) ?? throw Error("filter_unavailable", "A selected field could not be read; membership is unknown, not false.");
        };
    }
    public bool Matches(string type, object? value, object? where) => Matcher(type, where)(value);
    public IEnumerable<object> Filter(IEnumerable values, string type, object? where)
    {
        var matches = Matcher(type, where);
        var count = 0;
        foreach (var value in values) { Check(); if (++count > 8192 && !ObjectReadMode) throw Error("node_scan_limit", "Collection exceeds 8192 records."); if (matches(value)) yield return value; }
    }
    internal void RestartSlice()
    {
        if (Thread.CurrentThread.ManagedThreadId != threadId) throw Error("wrong_thread", "Spatial slice must remain on the game thread.");
        clock = Stopwatch.StartNew();
        lifetime ??= Stopwatch.StartNew();
        if(ObjectReadMode)ObjectFrameVersion++;
    }
    public void Check()
    {
        if (Thread.CurrentThread.ManagedThreadId != threadId)
            throw Error("wrong_thread", "Game reads must remain on the calling game thread.");
        clock ??= Stopwatch.StartNew();
        lifetime ??= Stopwatch.StartNew();
        if (clock.Elapsed.TotalMilliseconds >= budgetMilliseconds)
            throw Error("deadline_exceeded", "The game read deadline was reached; narrow the query.");
    }
    public List<GameEntity> Maps() => maps ??= ReadList(ObjectReadMode && ObjectScopeMap is not (null or "all" or "all_loaded")?source.Maps().Where(m=>m.Id==ObjectScopeMap):source.Maps(), 32);
    public List<GameEntity> Colonists() => colonists ??= ReadList(source.Colonists(), 8192);
    private List<GameEntity> ReadList(IEnumerable<GameEntity> input, int limit)
    {
        Check();
        var result = new List<GameEntity>();
        foreach (var entity in input)
        {
            Check();
            if (!ObjectReadMode && result.Count >= limit) throw Error("entity_scan_limit", "The identity scan limit was reached.");
            result.Add(entity);
        }
        return result.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
    }
    public object? Read(string kind, GameEntity? entity, string field)
    {
        if (entity != null && field == "id") return entity.Id;
        if (entity != null && field == "mapId") return entity.MapId;
        return Cached((kind, entity?.Id ?? "", field), () => kind == "Game" ? source.GameField(field) :
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
        // Query arguments are immutable for a read. Avoid JSON allocation for every scalar on every object.
        var signature = "";
        if (args.Count > 0 && !argumentKeys.TryGetValue(args,out signature)) {
            signature = JObject.FromObject(args.OrderBy(x => x.Key,StringComparer.Ordinal).ToDictionary(x => x.Key,x => x.Value)).ToString(Formatting.None);
            argumentKeys[args] = signature;
        }
        var recordKey=ObjectReadMode && node.RecordIdentity!=null?NativeReference(node.RecordIdentity):node.Key;
        var key = (node.Owner.Id,node.Kind,recordKey,field,signature);
        return Cached(key, () => {
            var value = source is IThingData native ? native.NativeField(node, field, args, this) : source.NodeField(node, field, args);
            if (!ObjectReadMode && value is IEnumerable<GameNode> sequence)
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
    public NodeSlice? Slice(GameNode node, string field, int first, int offset, IReadOnlyDictionary<string, object?> args, string elementType)
    {
        var rawArgs = args.Where(p => p.Key != "where").ToDictionary(p => p.Key, p => p.Value);
        var rows = ReadNode(node, field, rawArgs) as List<GameNode>;
        if (rows == null) return null;
        rows = Filter(rows, elementType, args.TryGetValue("where", out var where) ? where : null).Cast<GameNode>().ToList();
        return new NodeSlice { Items = rows.Skip(offset).Take(first).ToList(), Total = rows.Count, Offset = offset };
    }
    internal object? ProjectContents(GameNode node,string[] path,bool single)
    {
        object? value = node;
        foreach (var field in path) {
            Check();
            if (value == null) return null; // An absent intermediate tracker/container is not an observed empty collection.
            if (value is not GameNode current) throw Error("field_unavailable","Invalid container projection.");
            value = ReadNode(current,field);
        }
        return single ? value == null ? Array.Empty<GameNode>() : new[] { (GameNode)value } : value;
    }
    private object? Cached(object key, Func<object?> read)
    {
        if (Thread.CurrentThread.ManagedThreadId != threadId) throw Error("wrong_thread", "Invalid game thread.");
        if (values.TryGetValue(key, out var cached)) { if(ObjectReadMode && valueSamples.TryGetValue(key,out var sample))LastValueSample=sample;return cached; }
        Check();
        try
        {
            var started=ObjectTick?.Invoke()??Session.GameTick;
            NativeGetterReads++;
            var value = read();
            if (value is float f && (float.IsNaN(f) || float.IsInfinity(f)) || value is double d && (double.IsNaN(d) || double.IsInfinity(d)))
                throw Error("invalid_number", "The game returned a non-finite value.");
            if (!ObjectReadMode && value is string text && text.Length > 4096)
                throw Error("field_too_large", "The field exceeds its text limit.");
            values[key] = value;
            if(ObjectReadMode) { var sample=(started,ObjectTick?.Invoke()??Session.GameTick);valueSamples[key]=sample;LastValueSample=sample; }
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
