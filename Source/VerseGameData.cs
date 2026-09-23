using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal sealed class VerseGameData : IGameData, IThingData, IResourceData, IQueryData
{
    public IEnumerable<GameNode> QueryThings(IReadOnlyDictionary<string, object?> args, ReadContext context, Func<GameNode,bool>? definitionFilter = null, IReadOnlyCollection<string>? exactIds = null) => ThingData.QueryThings(args, context,definitionFilter,exactIds:exactIds);
    public IEnumerable<GameEntity> QueryPawns(IReadOnlyDictionary<string, object?> args, ReadContext context)
    {
        foreach (var node in ThingData.QueryThings(new Dictionary<string,object?>(args) { ["includeHeld"] = true, ["visibleOnly"] = false },context,requestGroup:ThingRequestGroup.Pawn))
            if (node.Handle is Pawn pawn) yield return new GameEntity(pawn.GetUniqueLoadID(),pawn.MapHeld?.GetUniqueLoadID(),pawn);
    }
    public bool ResourceDefinitionExists(string name, ReadContext context) => VerseResourceData.DefinitionExists(name, context);
    public IEnumerable<ResourceItem> ResourceItems(string mapId, string scope, bool visibleOnly, string[]? definitions, ReadContext context) => VerseResourceData.Items(mapId, scope, visibleOnly, definitions, context);
    public object? NativeRoot(string root, IReadOnlyDictionary<string, object?> args, ReadContext context) => ThingData.Root(root, args, context);
    public object? NativeField(GameNode node, string field, IReadOnlyDictionary<string, object?> args, ReadContext context) => ThingData.Kinds.Contains(node.Kind) ? ThingData.Read(node, field, args, context) : NodeField(node, field, args);
    public IEnumerable<GameEntity> Maps()
    {
        foreach (var map in Find.Maps)
            if (map != null && !map.Disposed) yield return new GameEntity(map.GetUniqueLoadID(), null, map);
    }
    public IEnumerable<GameEntity> Colonists()
    {
        foreach (var map in Find.Maps)
        {
            if (map == null || map.Disposed) continue;
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
                if (pawn != null && !pawn.Destroyed)
                    yield return new GameEntity(pawn.GetUniqueLoadID(), map.GetUniqueLoadID(), pawn);
        }
    }
    public object? GameField(string field) => field switch
    {
        "paused" => Find.TickManager?.Paused,
        "currentMapId" => Find.CurrentMap?.GetUniqueLoadID(),
        _ => throw new ArgumentException("Unknown game field."),
    };
    public object? MapField(GameEntity entity, string field)
    {
        return ReadMap((Map)entity.Handle,field);
    }
    internal static object? ReadMap(Map map,string field)
    {
        if (map.Disposed) throw new InvalidOperationException();
        return field switch
        {
            "name" => map.Parent?.LabelCap.ToString() ?? $"Map {map.Index}",
            "isCurrent" => ReferenceEquals(map, Find.CurrentMap),
            "isPlayerHome" => map.IsPlayerHome,
            "colonistCount" => map.mapPawns.FreeColonistsSpawned.Count,
            "hasVisibleThreat" => GenHostility.AnyHostileActiveThreatToPlayer(map, false, false),
            _ => throw new ArgumentException("Unknown map field."),
        };
    }
    public object? ColonistField(GameEntity entity, string field)
    {
        var pawn = (Pawn)entity.Handle;
        if (pawn.Destroyed) throw new InvalidOperationException();
        return field == "name" ? pawn.LabelShort : throw new ArgumentException("Unknown pawn identity field.");
    }
    public object? NodeField(GameNode node, string field, IReadOnlyDictionary<string, object?> arguments) =>
        PawnDetailsData.Read(node, field, arguments);
}
