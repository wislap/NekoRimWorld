using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

/// <summary>Bounded, game-thread-only projection of live Verse state.</summary>
internal static class ColonyQueries
{
    public const string ColonyDigest = "colony_digest";

    private const int MaxMaps = 32;
    private const int MaxPawnsPerMap = 256;
    private const int MaxIdLength = 256;
    private const int MaxLabelLength = 256;
    private const int MaxDefNameLength = 128;

    public static QueryDataDto BuildColonyDigest(long snapshotSequence, long deadline)
    {
        var omissions = new List<QueryOmissionDto>();
        var ticks = Find.TickManager;
        var gameTick = Math.Max(0, ticks?.TicksGame ?? 0);
        var paused = ticks?.Paused ?? true;
        var currentMap = Expired(deadline) ? null : Find.CurrentMap;
        if (currentMap?.Disposed == true)
        {
            currentMap = null;
            Omit(omissions, "maps", "disposed_current_map");
        }

        string? currentMapId = null;
        var expiredBeforeMaps = Expired(deadline);
        var maps = expiredBeforeMaps ? new List<Map>() : Find.Maps ?? new List<Map>();
        if (expiredBeforeMaps) Omit(omissions, "maps", "deadline_exceeded");

        var mapDtos = new List<MapDigestDto>();
        var visibleThreat = false;
        var mapLimit = Math.Min(maps.Count, MaxMaps);
        Omit(omissions, "maps", "map_limit_reached", maps.Count - mapLimit);
        for (var index = 0; index < mapLimit; index++)
        {
            if (Expired(deadline))
            {
                Omit(omissions, "maps", "deadline_exceeded", mapLimit - index);
                break;
            }

            var map = maps[index];
            if (map == null)
            {
                Omit(omissions, "maps", "null_map");
                continue;
            }

            try
            {
                if (map.Disposed)
                {
                    Omit(omissions, "maps", "disposed_map");
                    continue;
                }

                var dto = ReadMap(map, ReferenceEquals(map, currentMap), deadline, omissions, out var threat);
                if (dto != null)
                {
                    mapDtos.Add(dto);
                    visibleThreat |= threat;
                    if (dto.IsCurrent) currentMapId = dto.Id;
                }
            }
            catch (Exception exception) when (Recoverable(exception))
            {
                Omit(omissions, "maps", "map_read_failed");
            }
        }

        var digest = new ColonyDigestDto
        {
            GameTick = gameTick,
            Paused = paused,
            SnapshotSequence = snapshotSequence,
            HasVisibleThreat = visibleThreat,
            CurrentMapId = currentMapId,
            MapCount = maps.Count,
            Maps = mapDtos.ToArray(),
        };
        return QueryDataDto.FromColonyDigest(digest, omissions.ToArray());
    }

    public static QueryDataDto CreateFailureDigest(long sequence, long gameTick, bool paused) =>
        QueryDataDto.FromColonyDigest(
            new ColonyDigestDto
            {
                GameTick = Math.Max(0L, gameTick),
                Paused = paused,
                SnapshotSequence = sequence,
                MapCount = 0,
                Maps = Array.Empty<MapDigestDto>(),
            },
            new[] { new QueryOmissionDto
                { Scope = "query", Code = "snapshot_read_failed", OmittedCount = 1 } });

    private static MapDigestDto? ReadMap(Map map, bool isCurrent, long deadline,
        List<QueryOmissionDto> omissions, out bool visibleThreat)
    {
        visibleThreat = false;
        var id = StableId(map.GetUniqueLoadID(), omissions, "maps");
        if (id == null) return null;
        var index = map.Index;
        var label = RequiredText(map.Parent?.LabelCap, $"Map {index}", MaxLabelLength, omissions);

        if (!Expired(deadline))
        {
            try
            {
                visibleThreat = GenHostility.AnyHostileActiveThreatToPlayer(map, false, false);
            }
            catch (Exception exception) when (Recoverable(exception))
            {
                Omit(omissions, "threats", "threat_read_failed");
            }
        }
        else Omit(omissions, "threats", "deadline_exceeded");

        List<Pawn>? pawns = null;
        if (!Expired(deadline))
        {
            try { pawns = map.mapPawns?.FreeColonistsSpawned; }
            catch (Exception exception) when (Recoverable(exception))
            { Omit(omissions, "pawns", "pawn_list_read_failed"); }
        }
        else Omit(omissions, "pawns", "deadline_exceeded");

        var pawnDtos = new List<PawnDigestDto>();
        var pawnCount = pawns?.Count ?? 0;
        var pawnLimit = Math.Min(pawnCount, MaxPawnsPerMap);
        Omit(omissions, "pawns", "pawn_limit_reached", pawnCount - pawnLimit);
        for (var indexInMap = 0; indexInMap < pawnLimit; indexInMap++)
        {
            if (Expired(deadline))
            {
                Omit(omissions, "pawns", "deadline_exceeded", pawnLimit - indexInMap);
                break;
            }

            var pawn = pawns![indexInMap];
            if (pawn == null)
            {
                Omit(omissions, "pawns", "null_pawn");
                continue;
            }

            try
            {
                if (pawn.Destroyed) Omit(omissions, "pawns", "destroyed_pawn");
                else
                {
                    var dto = ReadPawn(pawn, omissions);
                    if (dto != null) pawnDtos.Add(dto);
                }
            }
            catch (Exception exception) when (Recoverable(exception))
            {
                Omit(omissions, "pawns", "pawn_read_failed");
            }
        }

        return new MapDigestDto
        {
            Id = id,
            Index = index,
            Label = label,
            IsCurrent = isCurrent,
            IsPlayerHome = map.IsPlayerHome,
            HasVisibleThreat = visibleThreat,
            FreeColonistCount = pawnCount,
            FreeColonistsSpawned = pawnDtos.ToArray(),
        };
    }

    private static PawnDigestDto? ReadPawn(Pawn pawn, List<QueryOmissionDto> omissions)
    {
        var id = StableId(pawn.GetUniqueLoadID(), omissions, "pawns");
        if (id == null) return null;
        var health = pawn.health?.summaryHealth;
        if (health == null) Omit(omissions, "pawns", "health_unavailable");
        var needs = pawn.needs;
        return new PawnDigestDto
        {
            Id = id,
            Label = RequiredText(pawn.LabelShort, id, MaxLabelLength, omissions),
            Downed = pawn.Downed,
            Drafted = pawn.Drafted,
            InMentalState = pawn.InMentalState,
            HealthPercent = Fraction(health?.SummaryHealthPercent, "health_percent", omissions),
            Job = OptionalText(pawn.CurJobDef?.defName, MaxDefNameLength, omissions),
            MentalState = OptionalText(pawn.MentalStateDef?.defName, MaxDefNameLength, omissions),
            Mood = Fraction(needs?.mood?.CurLevelPercentage, "mood", omissions),
            Food = Fraction(needs?.food?.CurLevelPercentage, "food", omissions),
            Rest = Fraction(needs?.rest?.CurLevelPercentage, "rest", omissions),
        };
    }

    private static string? StableId(string? value, List<QueryOmissionDto> omissions, string scope)
    {
        if (!string.IsNullOrWhiteSpace(value) && value!.Length <= MaxIdLength) return value;
        Omit(omissions, scope, "invalid_stable_id");
        return null;
    }

    private static string RequiredText(string? value, string fallback, int limit,
        List<QueryOmissionDto> omissions) =>
        OptionalText(string.IsNullOrWhiteSpace(value) ? fallback : value, limit, omissions)!;

    private static string? OptionalText(string? value, int limit,
        List<QueryOmissionDto> omissions)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value!.Length <= limit) return value;
        Omit(omissions, "strings", "string_truncated", value.Length - limit);
        return value.Substring(0, limit);
    }

    private static double? Fraction(float? value, string field,
        List<QueryOmissionDto> omissions)
    {
        if (!value.HasValue) return null;
        var number = (double)value.Value;
        if (double.IsNaN(number) || double.IsInfinity(number) || number < 0d || number > 1d)
        {
            Omit(omissions, field, "invalid_fraction");
            return null;
        }
        return Math.Round(number, 4);
    }

    private static void Omit(List<QueryOmissionDto> omissions, string scope,
        string code, int count = 1)
    {
        if (count <= 0) return;
        foreach (var omission in omissions)
        {
            if (omission.Scope != scope || omission.Code != code) continue;
            omission.OmittedCount = count > int.MaxValue - omission.OmittedCount
                ? int.MaxValue
                : omission.OmittedCount + count;
            return;
        }
        omissions.Add(new QueryOmissionDto { Scope = scope, Code = code, OmittedCount = count });
    }

    private static bool Expired(long deadline) => Stopwatch.GetTimestamp() >= deadline;
    private static bool Recoverable(Exception exception) =>
        exception is not OutOfMemoryException &&
        exception is not AccessViolationException &&
        exception is not ThreadAbortException;

}
