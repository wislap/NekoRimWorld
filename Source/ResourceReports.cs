using System;
using System.Collections.Generic;
using System.Linq;
using GraphQL;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>Detached primitive evidence; no live handles escape the query.</summary>
internal sealed class ResourceItem
{
    public string? Id;
    public string Definition = "";
    public string Label = "";
    public long Units;
    public bool Supply;
}

internal interface IResourceData
{
    bool ResourceDefinitionExists(string name, ReadContext context);
    IEnumerable<ResourceItem> ResourceItems(string mapId, string scope, bool visibleOnly, string[]? definitions, bool classify, ReadContext context);
}

/// <summary>One bounded read, deterministic scoped aggregation, explicit unknowns.</summary>
internal static class ResourceReports
{
    public const string Version = "resource_evidence.v2";
    public static readonly string[] Scopes = { "ground", "stored", "inventory", "worn", "equipment", "carried", "cached" };
    public static JObject Parameters() => JObject.Parse(@"{
      'type':'object','additionalProperties':false,'properties':{
        'map_id':{'type':'string','minLength':1,'maxLength':128,'description':'Exact loaded map ID; omitted means the current map.'},
        'def_names':{'type':'array','minItems':1,'maxItems':32,'items':{'type':'string','minLength':1,'maxLength':128},'description':'Exact ThingDef names returned by observations or thing_defs discovery, not category names. Explicit definitions bypass only focus; map, scopes and ground visibility still apply. A zero row describes that definition in those scopes, not its entire category.'},
        'focus':{'type':'string','enum':['supplies','all_haulable'],'default':'supplies','description':'supplies = CountAsResource OR weapons OR Apparel category. all_haulable includes chunks and corpses; not current pawn hauling eligibility.'},
        'scopes':{'type':'array','minItems':1,'maxItems':7,'items':{'type':'string','enum':['ground','stored','inventory','worn','equipment','carried','cached']}},
        'visible_only':{'type':'boolean','default':true,'description':'Filters ground by map-cell fog. Stored retains all native storage-cell items; pawn scopes cover spawned free colonists; cached is an unfiltered native counter. Other scopes are not made visible-only by this flag.'},
        'row_offset':{'type':'integer','minimum':0,'maximum':8192,'default':0},
        'row_limit':{'type':'integer','minimum':1,'maximum':64,'default':32},
        'detail_limit':{'type':'integer','minimum':0,'maximum':20,'default':3,'description':'Bounded item-ID examples per definition; samples are not the counting source. Use exact queries for more details.'}
      }}");

    public static JObject Contract() => new() {
        ["version"] = Version,
        ["purpose"] = "Resource overview and exact per-definition quantities without model-side pagination or arithmetic.",
        ["scope_rules"] = new JObject {
            ["ground"] = "Spawned, non-destroyed HaulableEver map items; includes storage-cell items; excludes held contents.",
            ["stored"] = "Items in native SlotGroup storage cells on this map, irrespective of fog. Counts and scanned records describe items, not the number or existence of storage groups, shelves or buildings. Empty items do not establish absent facilities. May overlap visible ground; excludes arbitrary container contents.",
            ["inventory"] = "Spawned free colonists' inventory.innerContainer contents on this map.",
            ["worn"] = "Spawned free colonists' WornApparel, distinct from inventory/equipment/carried.",
            ["equipment"] = "Spawned free colonists' equipment.AllEquipmentListForReading.",
            ["carried"] = "Spawned free colonists' active carryTracker container.",
            ["cached"] = "Current entry in the native ResourceCounter dictionary, a separate counting scope from physical ground/held items. Different scope values do not establish a stale, broken or unsynchronized counter. This read neither refreshes the counter nor determines the cause of differences. Missing entry is null/not tracked, not zero. Not filtered by fog."
        },
        ["reading_rules"] = "Each row.scopes entry keeps its units and status together: complete physical scopes have exact units, otherwise units is null and known_units is an observed lower bound. fog_filter applies to that scope only. A cached units value is exact for a directly observed entry even if other keys were unavailable; entry_present distinguishes absent from unknown. selected_physical_union excludes cached and deduplicates IDs across the requested physical scopes, not the entire colony. Overlaps prove shared IDs. coverage counts item records; empty complete differs from unavailable. row_page describes only the selected definition rows, not ground categories or colony-wide completeness. Later requests are separate samples. game_tick records sample time, not load history or why a counter differs. Zero-only cached entries are hidden unless definitions were explicitly requested.",
        ["outside_scope"] = new JArray("arbitrary nested containers", "caravans and prisoners", "other maps", "minified/corpse inner contents", "unmined ore or embedded building materials", "causes of differences between counting scopes", "existence or absence of storage facilities")
    };

    private sealed class Ledger
    {
        public string Name = "";
        public string Status = "complete";
        public string? Error;
        public int Scanned, Excluded;
        public Dictionary<string, ResourceItem> Items = new(StringComparer.Ordinal);
    }

    public static QueryOutcome Execute(JObject args, IGameData source, SessionDescriptor session, double budgetMs, string schemaId)
    {
        foreach (var key in args.Properties())
            if (Parameters()["properties"]![key.Name] == null) throw ReadContext.Error("invalid_arguments", "Unknown resource report argument: " + key.Name);
        string? Text(string name) {
            if (args[name] == null) return null;
            if (args[name]!.Type != JTokenType.String || args[name]!.ToString().Length is < 1 or > 128) throw ReadContext.Error("invalid_arguments", "Invalid " + name);
            return args[name]!.ToString();
        }
        string[]? List(string name, int limit) {
            if (args[name] == null) return null;
            if (args[name] is not JArray array || array.Count < 1 || array.Count > limit || array.Any(x => x.Type != JTokenType.String || x.ToString().Length is < 1 or > 128)) throw ReadContext.Error("invalid_arguments", "Invalid " + name);
            return array.Values<string>().Select(x => x!).Distinct(StringComparer.Ordinal).ToArray();
        }
        int Integer(string name, int fallback, int min, int max) {
            if (args[name] == null) return fallback;
            if (args[name]!.Type != JTokenType.Integer || (long)args[name]! < min || (long)args[name]! > max) throw ReadContext.Error("invalid_arguments", "Invalid " + name);
            return (int)args[name]!;
        }
        if (args["visible_only"] != null && args["visible_only"]!.Type != JTokenType.Boolean) throw ReadContext.Error("invalid_arguments", "visible_only must be boolean");
        var focus = Text("focus") ?? "supplies";
        if (focus is not ("supplies" or "all_haulable")) throw ReadContext.Error("invalid_arguments", "Invalid focus");
        var names = List("def_names", 32);
        var selected = List("scopes", 7) ?? Scopes;
        if (selected.Any(s => !Scopes.Contains(s))) throw ReadContext.Error("invalid_arguments", "Unknown resource scope");
        var offset = Integer("row_offset", 0, 0, 8192); var limit = Integer("row_limit", 32, 1, 64); var details = Integer("detail_limit", 3, 0, 20);
        var visible = (bool?)args["visible_only"] ?? true;
        var context = new ReadContext(source, session, budgetMs);
        var mapId = Text("map_id") ?? context.Read("Game", null, "currentMapId") as string;
        if (mapId == null || !context.Maps().Any(m => m.Id == mapId)) throw ReadContext.Error("object_unavailable", "Select a loaded map; no implicit all-map scan.");
        if (source is not IResourceData resources) return QueryOutcome.Failure("resource_report_unavailable", "This data source does not support resource evidence.", ProtocolStatuses.Unsupported);
        if (names != null)
        {
            var unknown = new List<QueryIssue>();
            // Report all invalid names in the existing issue channel so one
            // recovery can address the batch. No quantity scope has been read.
            var supplied = (JArray)args["def_names"]!;
            var exists = names.ToDictionary(name => name, name => resources.ResourceDefinitionExists(name, context));
            for (var i = 0; i < supplied.Count; i++)
                if (!exists[supplied[i].ToString()]) unknown.Add(new QueryIssue {
                    Code = "unknown_definition", Path = new JArray("arguments", "def_names", i),
                    Message = "Unknown exact ThingDef: " + supplied[i],
                });
            if (unknown.Count > 0) return new QueryOutcome {
                Status = ProtocolStatuses.Failed, ErrorCode = "unknown_definition", Issues = unknown.ToArray(),
                Message = "The listed def_names are unknown. Use existing thing_defs discovery for exact names. No quantities were read; omitted or unresolved definitions are not zero.",
            };
        }
        var ledgers = new List<Ledger>(); var issues = new List<QueryIssue>();
        foreach (var scope in selected)
        {
            var ledger = new Ledger { Name = scope }; ledgers.Add(ledger);
            try {
                foreach (var item in resources.ResourceItems(mapId, scope, visible, names, names == null && focus == "supplies", context))
                {
                    context.Check();
                    if (++ledger.Scanned > 8192) throw ReadContext.Error("resource_scan_limit", "Scope exceeds 8192 items; select exact definitions or narrower scopes.");
                    if (string.IsNullOrEmpty(item.Definition) || item.Definition.Length > 128 || item.Units < 0 || item.Units > int.MaxValue) throw ReadContext.Error("invalid_resource_item", "Invalid definition or stack quantity.");
                    if (names != null ? !names.Contains(item.Definition) : focus == "supplies" && !item.Supply) { ledger.Excluded++; continue; }
                    var id = scope == "cached" ? item.Definition : item.Id;
                    if (string.IsNullOrEmpty(id)) throw ReadContext.Error("invalid_resource_item", "A physical item requires stable identity.");
                    if (ledger.Items.TryGetValue(id!, out var previous) && (previous.Definition != item.Definition || previous.Units != item.Units)) throw ReadContext.Error("resource_identity_conflict", "Conflicting values for the same item ID.");
                    ledger.Items[id!] = item;
                }
            }
            catch (ExecutionError error) {
                ledger.Status = ledger.Items.Count == 0 ? "unavailable" : "partial"; ledger.Error = error.Code ?? "field_unavailable";
                issues.Add(new QueryIssue { Code = ledger.Error, Path = new JArray("coverage", scope), Message = "Resource scope incomplete; exact quantities are unavailable. " + error.Message });
            }
            catch (Exception error) when (ReadContext.Recoverable(error)) {
                ledger.Status = "unavailable"; ledger.Error = "field_unavailable";
                issues.Add(new QueryIssue { Code = ledger.Error, Path = new JArray("coverage", scope), Message = "Resource scope could not be read; unavailable is not empty." });
            }
        }
        var identities = new Dictionary<string, ResourceItem>(StringComparer.Ordinal);
        foreach (var ledger in ledgers) {
            if (ledger.Error is "resource_identity_conflict" or "invalid_resource_item")
                return QueryOutcome.Failure(ledger.Error, "Resource evidence is inconsistent; no totals are published. Request a fresh sample.");
            if (ledger.Name == "cached") continue;
            foreach (var pair in ledger.Items) {
                if (identities.TryGetValue(pair.Key, out var previous) && (previous.Definition != pair.Value.Definition || previous.Units != pair.Value.Units))
                    return QueryOutcome.Failure("resource_identity_conflict", "Physical item identity conflicts across scopes; no totals are published.");
                identities[pair.Key] = pair.Value;
            }
        }
        var definitions = names ?? ledgers.SelectMany(l => l.Items.Values.Where(i => l.Name != "cached" || i.Units != 0).Select(i => i.Definition)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var rows = new JArray(); var physical = ledgers.Where(l => l.Name != "cached").ToArray();
        foreach (var name in definitions.Skip(offset).Take(limit))
        {
            var scoped = new JObject();
            var all = new Dictionary<string, ResourceItem>(StringComparer.Ordinal); var membership = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var label = name; var conflict = false;
            foreach (var ledger in ledgers)
            {
                var items = ledger.Items.Where(p => p.Value.Definition == name).ToArray();
                foreach (var p in items) if (!string.IsNullOrEmpty(p.Value.Label)) label = p.Value.Label.Length <= 256 ? p.Value.Label : name;
                var exact = ledger.Name == "cached" ? items.Length > 0 : ledger.Status == "complete";
                var units = items.Sum(p => p.Value.Units);
                var reading = new JObject {
                    ["units"] = exact ? new JValue(units) : JValue.CreateNull(),
                    ["fog_filter"] = ledger.Name == "ground" && visible ? "unfogged_cells" : "none"
                };
                scoped[ledger.Name] = reading;
                if (ledger.Name == "cached") {
                    reading["entry_present"] = items.Length > 0 ? new JValue(true) : ledger.Status == "complete" ? new JValue(false) : JValue.CreateNull();
                    continue;
                }
                reading["status"] = ledger.Status;
                reading["known_units"] = units;
                reading["item_count"] = exact ? new JValue(items.Length) : JValue.CreateNull();
                foreach (var p in items) {
                    if (all.TryGetValue(p.Key, out var old) && (old.Units != p.Value.Units || old.Definition != p.Value.Definition)) conflict = true;
                    all[p.Key] = p.Value;
                    if (!membership.TryGetValue(p.Key, out var scopes)) membership[p.Key] = scopes = new List<string>();
                    scopes.Add(ledger.Name);
                }
            }
            var complete = physical.Length > 0 && physical.All(l => l.Status == "complete") && !conflict;
            var overlaps = new JArray();
            for (var i = 0; i < physical.Length; i++) for (var j = i + 1; j < physical.Length; j++) {
                var ids = membership.Where(p => p.Value.Contains(physical[i].Name) && p.Value.Contains(physical[j].Name)).Select(p => p.Key).ToArray();
                if (ids.Length > 0) overlaps.Add(new JObject { ["left"] = physical[i].Name, ["right"] = physical[j].Name, ["units"] = conflict ? JValue.CreateNull() : new JValue(ids.Sum(id => all[id].Units)), ["item_count"] = ids.Length });
            }
            if (conflict) issues.Add(new QueryIssue { Code = "resource_identity_conflict", Path = new JArray("rows", name), Message = "Conflicting physical item identity across scopes; union is unavailable." });
            rows.Add(new JObject { ["def_name"] = name, ["label"] = label, ["scopes"] = scoped,
                ["selected_physical_union"] = new JObject { ["units"] = complete ? new JValue(all.Values.Sum(i => i.Units)) : JValue.CreateNull(), ["known_units"] = conflict || physical.Length == 0 ? JValue.CreateNull() : new JValue(all.Values.Sum(i => i.Units)), ["item_count"] = complete ? new JValue(all.Count) : JValue.CreateNull(), ["scopes"] = new JArray(physical.Select(l => l.Name)), ["complete"] = complete },
                ["overlaps"] = overlaps, ["item_examples"] = new JArray(all.OrderBy(p => p.Key, StringComparer.Ordinal).Take(details).Select(p => new JObject { ["id"] = p.Key, ["units"] = p.Value.Units, ["scopes"] = new JArray(membership[p.Key]) })),
                ["examples_complete"] = !conflict && all.Count <= details && physical.All(l => l.Status == "complete") });
        }
        var coverage = new JObject();
        foreach (var l in ledgers) coverage[l.Name] = new JObject { ["status"] = l.Status, ["scanned"] = l.Scanned, ["excluded_by_selector"] = l.Excluded, ["selected_records"] = l.Items.Count, ["error_code"] = l.Error };
        var data = new JObject { ["contract"] = Version, ["map_id"] = mapId,
            ["selection"] = new JObject { ["focus"] = focus, ["def_names"] = names == null ? JValue.CreateNull() : new JArray(names), ["visible_only"] = visible, ["scopes"] = new JArray(selected) },
            ["sample"] = new JObject { ["colony_id"] = session.ColonyId, ["load_epoch"] = session.LoadEpoch, ["game_tick"] = session.GameTick, ["snapshot_sequence"] = session.SnapshotSequence },
            ["coverage"] = coverage, ["rows"] = rows,
            ["row_page"] = new JObject { ["total_rows"] = definitions.Length, ["offset"] = offset, ["returned_rows"] = rows.Count, ["complete"] = offset == 0 && rows.Count == definitions.Length, ["has_more"] = offset + rows.Count < definitions.Length, ["next_offset"] = offset + rows.Count < definitions.Length ? new JValue(offset + rows.Count) : JValue.CreateNull() },
            ["not_covered"] = Contract()["outside_scope"]!.DeepClone() };
        if (data.ToString(Formatting.None).Length > 96000) return QueryOutcome.Failure("query_result_too_large", "Reduce row_limit/detail_limit; no truncated evidence is returned.");
        return new QueryOutcome { Status = issues.Count == 0 ? ProtocolStatuses.Ok : ProtocolStatuses.Partial, Data = data, Issues = issues.ToArray(),
            Execution = new JObject { ["operation"] = "resource_report", ["arguments"] = args.DeepClone(), ["schema_id"] = schemaId, ["contract"] = Version } };
    }
}
