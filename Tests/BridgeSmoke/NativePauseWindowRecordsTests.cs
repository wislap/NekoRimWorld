using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class NativePauseWindowRecordsTests
{
    private sealed class Data : IGameData {
        internal JObject Snapshot = null!;
        public IEnumerable<GameEntity> Maps() => Array.Empty<GameEntity>();
        public IEnumerable<GameEntity> Colonists() => Array.Empty<GameEntity>();
        public object? GameField(string field) => field == "pauseWindows" ? Snapshot : null;
        public object? MapField(GameEntity map, string field) => null;
        public object? ColonistField(GameEntity pawn, string field) => null;
        public object? NodeField(GameNode node, string field, IReadOnlyDictionary<string, object?> arguments) => null;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void Run() {
        var records = new NativePauseWindowRecords(); var stack = new object(); var otherStack = new object(); var game = new object(); var otherGame = new object();
        var first = new object(); var second = new object();
        records.Opened(stack, game, first, 12, 99, Enumerable.Range(0, 20).Select(i => new string('x', 300) + i));
        var rows = new[] { new NativePauseWindowRecords.Row { Instance = first, Id = 12, StackIndex = 1, Type = "Verse.Dialog" },
            new NativePauseWindowRecords.Row { Instance = second, Id = 13, StackIndex = 2, Type = "Verse.Dialog" } };
        var observed = records.Capture(stack, game, 101, rows);
        Require((string?)observed["status"] == "observed" && (int?)observed["count"] == 2, "same-type windows lost");
        Require((string?)observed["windows"]![0]!["opening_source"]!["status"] == "observed" &&
            observed["windows"]![0]!["opening_source"]!["frames"]!.Count() == 8 &&
            observed["windows"]![0]!["opening_source"]!["frames"]![0]!.ToString().Length == 240, "frame limits failed");
        Require((string?)observed["windows"]![1]!["opening_source"]!["status"] == "unknown", "unseen origin invented");
        rows[1].Id = 12;
        Require((string?)records.Capture(stack, game, 101, rows)["windows"]![1]!["opening_source"]!["status"] == "unknown", "different object reused origin by ID");
        Require((string?)records.Capture(otherStack, game, 101, rows)["windows"]![0]!["opening_source"]!["status"] == "unknown", "cross-stack origin reused");
        Require((string?)records.Capture(stack, otherGame, 101, rows)["windows"]![0]!["opening_source"]!["status"] == "unknown", "cross-game origin reused");
        rows[0].Id = 14;
        Require((string?)records.Capture(stack, game, 101, rows)["windows"]![0]!["opening_source"]!["status"] == "unknown", "reused ID accepted");
        var many = Enumerable.Range(0, 17).Select(i => new NativePauseWindowRecords.Row { Instance = new object(), Id = i, StackIndex = i, Type = "T" });
        var limited = records.Capture(stack, game, 102, many);
        Require((string?)limited["status"] == "partial" && (bool?)limited["truncated"] == true && (int?)limited["count"] == 17 && limited["windows"]!.Count() == 16, "window limit failed");
        var missing = records.Capture(null, null, null, null);
        Require((string?)missing["status"] == "unavailable" && missing["count"]!.Type == Newtonsoft.Json.Linq.JTokenType.Null, "missing stack became zero");
        var source = new Data { Snapshot = observed };
        var engine = new GameQueryEngine(1000);
        var session = new SessionDescriptor(new string('2', 32), new string('3', 32), 101, 7);
        var result = engine.Execute("query", JObject.Parse("{from:'game',select:['pause_windows.status','pause_windows.sampled_tick','pause_windows.windows.window_id','pause_windows.windows.opening_source.frames']}"), source, session);
        Require(result.Status == "ok" && (string?)result.Data?["results"]?[0]?["rows"]?[0]?["pause_windows"]?["status"] == "observed",
            "fresh game query did not expose the nested evidence: " + result.Message + result.Data);
        var projected = result.Data!["results"]![0]!["rows"]![0]!["pause_windows"]!;
        Require(projected.ToString().Contains("window_id") && projected.ToString().Contains("frames"), "nested window identities or frames missing from public query");
    }
}
