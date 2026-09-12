using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal sealed class VerseGameData : IGameData, IThingData, IResourceData
{
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
        var map = (Map)entity.Handle;
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
