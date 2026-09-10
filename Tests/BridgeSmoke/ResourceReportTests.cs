using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class ResourceReportTests
{
    private static readonly SessionDescriptor Session = new(new string('2', 32), new string('3', 32), 100, 7);
    private static void Require(bool value, string message) { if (!value) throw new Exception("Resource report: " + message); }
    private static QueryOutcome Read(Data source, string args = "{}", double budget = 1000) => new GameQueryEngine(budget).Execute("resource_report", JObject.Parse(args), source, Session);

    public static void Run()
    {
        var source = new Data(); var result = Read(source, "{'def_names':['Steel']}");
        Require(result.Status == "ok", "complete report failed");
        var row = result.Data!["rows"]![0]!;
        Require((long)row["scopes"]!["ground"]!["units"]! == 200 && (long)row["scopes"]!["stored"]!["units"]! == 75 && (long)row["scopes"]!["cached"]!["units"]! == 0, "scope counts differ");
        Require((long)row["selected_physical_union"]!["units"]! == 200, "storage was double-counted");
        Require((long)row["overlaps"]![0]!["units"]! == 75, "overlap receipt lost");
        Require((string?)result.Data!["contract"] == "resource_evidence.v2" && row["quantities"] == null && result.Data["view"] == null,
            "new shape silently reused the old contract or duplicated legacy fields");
        Require((string?)row["scopes"]!["ground"]!["fog_filter"] == "unfogged_cells" &&
            (string?)row["scopes"]!["stored"]!["fog_filter"] == "none", "visibility was widened across scopes");
        Require((int?)result.Data["row_page"]!["total_rows"] == 1, "definition rows lost their pagination metadata");

        result = Read(new Data { MissingCached = true }, "{'def_names':['Steel'],'scopes':['cached']}");
        var cached = result.Data!["rows"]![0]!["scopes"]!["cached"]!;
        Require(cached["units"]!.Type == JTokenType.Null && (bool?)cached["entry_present"] == false, "missing cached key became zero");
        result = Read(new Data { FailCachedBeforeSteel = true }, "{'def_names':['Steel'],'scopes':['cached']}");
        cached = result.Data!["rows"]![0]!["scopes"]!["cached"]!;
        Require(cached["units"]!.Type == JTokenType.Null && cached["entry_present"]!.Type == JTokenType.Null, "unread cached key became absent");

        result = Read(new Data { FailGround = true }, "{'def_names':['Steel'],'scopes':['ground','cached']}");
        row = result.Data!["rows"]![0]!;
        Require(result.Status == "partial" && row["scopes"]!["ground"]!["units"]!.Type == JTokenType.Null && (long)row["scopes"]!["ground"]!["known_units"]! == 75, "partial source became an exact count");
        Require(row["selected_physical_union"]!["units"]!.Type == JTokenType.Null, "partial union became complete");

        result = Read(new Data { FailCachedAfterSteel = true }, "{'def_names':['Steel'],'scopes':['cached']}");
        Require(result.Status == "partial" && (long)result.Data!["rows"]![0]!["scopes"]!["cached"]!["units"]! == 0, "a directly observed cache entry was discarded because later keys failed");
        Require((bool)result.Data!["rows"]![0]!["scopes"]!["cached"]!["entry_present"]!, "known cached entry confused with absence");

        source = new Data(); result = Read(source, "{'scopes':['invalid']}");
        Require(result.Status == "failed" && source.ResourceReads == 0, "invalid selection read game resources");
        source = new Data(); result = Read(source, "{'def_names':['UnknownA','Steel','UnknownB']}");
        Require(result.Status == "failed" && result.ErrorCode == "unknown_definition" && source.ResourceReads == 0,
            "invalid definition batch performed quantity reads");
        Require(result.Issues.Length == 2 && (int?)result.Issues[0].Path.Last == 0 && (int?)result.Issues[1].Path.Last == 2 &&
            result.Issues[0].Message!.Contains("UnknownA") && result.Issues[1].Message!.Contains("UnknownB"),
            "unknown definition batch did not identify every invalid argument");
        result = Read(new Data(), "{'map_id':'Map_1','def_names':['Steel'],'scopes':['ground']}");
        Require((long)result.Data!["rows"]![0]!["scopes"]!["ground"]!["units"]! == 300, "explicit map was ignored");

        result = Read(new Data { Many = true }, "{'scopes':['ground']}");
        Require(result.Status == "partial" && (long)result.Data!["rows"]![0]!["scopes"]!["ground"]!["known_units"]! == 8192, "scan guard lost its lower bound");
        Require(result.Data!["rows"]![0]!["scopes"]!["ground"]!["units"]!.Type == JTokenType.Null, "scan limit invented a total");
        result = Read(new Data(), "{'map_id':'Map_0'}", 0);
        Require(result.Status == "failed" && result.ErrorCode == "deadline_exceeded", "expired read budget was ignored");
        Console.WriteLine("Resource evidence tests passed: scopes, overlap, partials, exact cached entries, map isolation, validation and read/scan limits.");
    }

    private sealed class Data : IGameData, IResourceData
    {
        public bool FailGround, FailCachedAfterSteel, FailCachedBeforeSteel, MissingCached, Many;
        public int ResourceReads;
        public IEnumerable<GameEntity> Maps() => new[] { new GameEntity("Map_0", null, this), new GameEntity("Map_1", null, this) };
        public IEnumerable<GameEntity> Colonists() => Array.Empty<GameEntity>();
        public object? GameField(string field) => field == "currentMapId" ? "Map_0" : (object)true;
        public object? MapField(GameEntity entity, string field) => entity.Id;
        public object? ColonistField(GameEntity pawn, string field) => throw new NotSupportedException();
        public object? NodeField(GameNode node, string field, IReadOnlyDictionary<string, object?> args) => throw new NotSupportedException();
        public bool ResourceDefinitionExists(string name, ReadContext context) { context.Check(); return name == "Steel"; }
        private static ResourceItem Item(string? id, int amount) => new() { Id = id, Definition = "Steel", Label = "Steel", Units = amount, Supply = true };
        public IEnumerable<ResourceItem> ResourceItems(string mapId, string scope, bool visibleOnly, string[]? definitions, bool classify, ReadContext context)
        {
            ResourceReads++;
            if (scope == "ground") {
                if (Many) { for (var i = 0; i < 9000; i++) yield return Item("s" + i, 1); yield break; }
                yield return Item("steel1", mapId == "Map_0" ? 75 : 300);
                if (FailGround) throw ReadContext.Error("field_unavailable", "Injected incomplete source");
                if (mapId == "Map_0") yield return Item("steel2", 125);
            }
            if (scope == "stored") yield return Item("steel1", 75);
            if (scope == "cached") {
                if (FailCachedBeforeSteel) throw ReadContext.Error("field_unavailable", "Injected unread cached source");
                if (MissingCached) yield break;
                yield return Item(null, 0);
                if (FailCachedAfterSteel) throw ReadContext.Error("field_unavailable", "Injected later cached key failure");
            }
        }
    }
}
