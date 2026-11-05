using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class LedgerSelectionTests
{
    private sealed class Cell : IPreparedWriteField {
        internal readonly string Name; internal JObject Value;
        internal Cell(string name) { Name = name; Value = Flag(true, null); }
        public string ObjectId => "Area_1"; public string Field => Name; public bool IsDestroyed => false; public bool MayChangeByGame => true; public object? NativeTarget => null;
        public JToken Read() => Value.DeepClone(); public void Validate(JToken value) { } public void Write(JToken value) => Value = (JObject)value.DeepClone(); public void Restore(JToken value) => Write(value);
    }
    private static JObject Flag(bool enabled, string? manual) => new() { ["native_instance"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ["enabled"] = enabled, ["manual_id"] = manual == null ? JValue.CreateNull() : new JValue(manual) };
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run() {
        var ledger = new WriteLedger(); var cells = new Dictionary<string, Cell>(StringComparer.Ordinal); var writes = new List<AppliedWriteField>();
        for (int z = 0; z < 62; z++) for (int x = 0; x < 62; x++) { var cell = new Cell("area_flag:" + x + ":" + z); cells.Add(cell.Name, cell); writes.Add(new AppliedWriteField(0, cell, Flag(false, null), cell.Value)); }
        ledger.Record(writes, "roof-plan", "planner", 1); var targets = new WriteTargetIndex(_ => false); int reads = 0;
        IPreparedWriteField? Resolve(string id, string field, object? target, JToken value) { reads++; return cells[field]; }
        var full = ledger.ReadSnapshot(true, targets, _ => false, Resolve);
        Check(Encoding.UTF8.GetByteCount(full.ToString(Formatting.None)) > ProtocolCodec.MaxFrameBytes, "Capacity regression did not exercise an oversized real per-cell ledger.");
        var selection = WriteLedger.Selection(new JObject { ["fields"] = new JArray(cells.Values.Take(64).Select(cell => new JObject { ["object"] = cell.ObjectId, ["field"] = cell.Field })) })!;
        reads = 0; var scoped = ledger.ReadSnapshot(true, targets, _ => false, Resolve, selection);
        Check(reads == 64 && (int)scoped["entry_count"]! == 64 && (bool)scoped["complete"]! && (string?)scoped["scope"]?["kind"] == "fields" && JToken.DeepEquals(scoped["scope"]!["fields"], selection), "Scoped guard resolved unselected native fields or lost exact coverage.");
        Check(Encoding.UTF8.GetByteCount(scoped.ToString(Formatting.None)) < ProtocolCodec.MaxFrameBytes / 2, "Selected native guard still exceeds its frame budget.");
        var missing = WriteLedger.Selection(JObject.Parse("{fields:[{object:'Area_1',field:'area_flag:99:99'}]}"))!;
        reads = 0; var absent = ledger.ReadSnapshot(true, targets, _ => false, Resolve, missing);
        Check(reads == 0 && (int)absent["entry_count"]! == 0 && ((JArray)absent["missing_fields"]!).Count == 1 && (bool)absent["complete"]!, "An unowned selected field was resolved or reported as a whole-ledger absence.");
        void Invalid(JObject args) { try { WriteLedger.Selection(args); } catch (ArgumentException) { return; } throw new Exception("Invalid ledger selector was accepted."); }
        Invalid(JObject.Parse("{fields:[]}")); Invalid(new JObject { ["fields"] = new JArray(selection.Concat(selection.Take(1))) }); Invalid(JObject.Parse("{fields:[{object:'Area_1',field:'a'},{object:'Area_1',field:'a'}]}"));
        var first = cells["area_flag:0:0"]; const string manualId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var source = new JObject { ["manual_id"] = manualId, ["manual_order"] = 1, ["origin_load_epoch"] = "cccccccccccccccccccccccccccccccc", ["source"] = "user_designator", ["map_id"] = "Map_1", ["object"] = first.ObjectId, ["field"] = first.Field,
            ["cell"] = new JObject { ["x"] = 0, ["z"] = 0 }, ["area_kind"] = "build_roof", ["designator"] = "RimWorld.Designator_AreaIgnoreRoof.DesignateSingleCell", ["old_value"] = Flag(true, null), ["new_value"] = Flag(false, manualId), ["tick"] = 2 };
        var proof = ledger.RecordManualChange(first.ObjectId, first.Field, source)!;
        Check(JToken.DeepEquals(ledger.Find(first.ObjectId, first.Field)!["value"], Flag(true, null)) && (string?)proof["ownership"]?["changeset_id"] == "roof-plan", "Manual proof overwrote the original AI value or invented its owner.");
        var reloaded = new WriteLedger(); reloaded.Load(ledger.Serialize());
        Check(reloaded.ManualId(first.ObjectId, first.Field) == manualId && JToken.DeepEquals(reloaded.Find(first.ObjectId, first.Field)!["manual_source"], proof), "Manual source identity did not survive the save-scoped ledger.");
        reloaded.Record(new[] { new AppliedWriteField(0, first, Flag(false, manualId), Flag(true, manualId)) }, "returned-plan", "planner", 3);
        Check(reloaded.ManualId(first.ObjectId, first.Field) == manualId && (string?)reloaded.Find(first.ObjectId, first.Field)!["changeset_id"] == "returned-plan", "Approved planner work reset the manual generation and could resurrect the same source.");
        var unowned = ledger.RecordManualChange("Area_1", "area_flag:99:99", source); Check(unowned == null, "Unowned manual change manufactured an AI ledger value.");
        Console.WriteLine("Ledger selected-scope and manual-source persistence tests passed: 62x62 capacity, bounded native reads, exact missing fields and stable return generation.");
    }
}
