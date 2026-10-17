using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

// Additive business reads. Existing public-query and world-observation contracts
// remain independent; these parameters are never native field paths.
internal static class ColonyReadContract
{
    internal const string Version = "colony_state.v1";
    internal static readonly string[] Operations = { "inspect_planet", "inspect_factions", "inspect_quests", "inspect_trade", "inspect_resources", "inspect_research", "evaluate_journey", "evaluate_trade" };
    internal static JObject Catalog() => new() {
        ["version"] = Version,
        ["operations"] = new JArray(Operations.Select(op => new JObject { ["operation"] = op, ["parameters"] = Parameters(op) }))
    };
    private static JObject Strings() => new() { ["type"] = "array", ["items"] = new JObject { ["type"] = "string", ["minLength"] = 1 }, ["minItems"] = 1 };
    private static JObject Reference(bool map = false)
    {
        var properties = new JObject { ["id"] = new JObject { ["type"] = "string", ["minLength"] = 1 }, ["name"] = new JObject { ["type"] = "string", ["minLength"] = 1 } };
        if (map) properties["map"] = new JObject { ["type"] = "string" };
        return new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties,
            ["anyOf"] = new JArray(new JObject { ["required"] = new JArray("id") }, new JObject { ["required"] = new JArray("name") }) };
    }
    private static JObject Basket() => new() { ["type"] = "array", ["items"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
        ["properties"] = new JObject { ["item"] = new JObject { ["type"] = "string", ["minLength"] = 1 }, ["quantity"] = new JObject { ["type"] = "integer", ["minimum"] = 1 } }, ["required"] = new JArray("item", "quantity") } };
    internal static JObject Parameters(string op)
    {
        var properties = new JObject();
        var required = new JArray();
        if (op == "evaluate_journey") {
            properties["cargo_add"] = Basket(); properties["cargo_remove"] = Basket();
            properties["caravan"] = Reference(); properties["destinations"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["items"] = Reference() }; properties["include"] = Strings(); required.Add("caravan"); required.Add("destinations");
        } else if (op == "evaluate_trade") {
            properties["trader"] = Reference(true); properties["negotiator"] = Reference(true); properties["buy"] = Basket(); properties["sell"] = Basket(); required.Add("trader"); required.Add("negotiator");
        } else {
            properties["ids"] = Strings(); properties["names"] = Strings(); properties["aspects"] = Strings();
            properties["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100, ["default"] = 20 };
            properties["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0 };
            if (op == "inspect_planet") {
                properties["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray("world", "world_objects", "caravans") };
                properties["faction"] = new JObject { ["type"] = "string" };
                properties["category"] = new JObject { ["type"] = "string" };
                properties["near"] = new JObject { ["type"] = "object" };
            }
            if (op == "inspect_research") properties["items"] = Strings();
            if (op == "inspect_resources") { properties["map"] = new JObject { ["type"] = "string" }; properties["items"] = Strings(); required.Add("items"); }
            if (op == "inspect_trade") { properties["map"] = new JObject { ["type"] = "string" }; properties["items"] = Strings(); }
        }
        return new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties, ["required"] = required };
    }
    internal static void Validate(string operation, JObject args)
    {
        if (!Operations.Contains(operation)) throw ReadContext.Error("invalid_arguments", "Unknown colony-state operation.");
        var schema = Parameters(operation);
        var properties = (JObject)schema["properties"]!;
        foreach (var property in args.Properties()) {
            if (properties[property.Name] is not JObject descriptor) throw ReadContext.Error("invalid_arguments", "Unknown " + operation + " parameter: " + property.Name);
            string? type = (string?)descriptor["type"];
            if (type == "array" && property.Value is not JArray || type == "object" && property.Value is not JObject || type == "integer" && property.Value.Type != JTokenType.Integer || type == "string" && (property.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)property.Value)))
                throw ReadContext.Error("invalid_arguments", "Invalid " + property.Name + " value.");
        }
        foreach (var key in ((JArray)schema["required"]!).Values<string>()) if (args[key!] == null) throw ReadContext.Error("invalid_arguments", "Missing " + key + ".");
        foreach (string field in new[] { "ids", "names", "aspects", "items", "include" }) if (args[field] != null) Texts(args, field);
        if (args["limit"] != null && ((long)args["limit"]! < 1 || (long)args["limit"]! > 100) || args["offset"] != null && ((long)args["offset"]! < 0 || (long)args["offset"]! > int.MaxValue)) throw ReadContext.Error("invalid_arguments", "limit must be 1..100; offset must be nonnegative.");
    }
    internal static string[] Texts(JObject args, string key)
    {
        if (args[key] == null) return Array.Empty<string>();
        if (args[key] is not JArray list || list.Count == 0 || list.Any(x => x.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)x))) throw ReadContext.Error("invalid_arguments", key + " must contain nonempty strings.");
        return list.Values<string>().Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static bool Targeted(JObject args) => Texts(args, "ids").Length != 0 || Texts(args, "names").Length != 0;
    internal static HashSet<string> Aspects(JObject args, params string[] defaults) => new(args["aspects"] == null ? defaults : Texts(args, "aspects"), StringComparer.Ordinal);
    internal static void RequireAspects(HashSet<string> aspects, params string[] allowed)
    {
        if (aspects.Any(x => !allowed.Contains(x))) throw ReadContext.Error("invalid_arguments", "Supported aspects: " + string.Join(", ", allowed));
    }
    internal static T[] Select<T>(IEnumerable<T> population, JObject args, Func<T, string> id, Func<T, string> label, Func<T, bool>? defaultPredicate = null)
    {
        var all = population.ToArray();
        var ids = Texts(args, "ids"); var names = Texts(args, "names");
        if (ids.Length == 0 && names.Length == 0) return (defaultPredicate == null ? all : all.Where(defaultPredicate)).ToArray();
        var selected = new List<T>();
        foreach (var reference in ids.Select(x => (value: x, isId: true)).Concat(names.Select(x => (value: x, isId: false)))) {
            var matches = all.Where(x => string.Equals(reference.isId ? id(x) : label(x), reference.value, reference.isId ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
                || !reference.isId && string.Equals(id(x), reference.value, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) throw ReadContext.Error("object_unavailable", "No matching public object: " + reference.value);
            if (matches.Length > 1) throw ReadContext.Error("ambiguous_reference", "Several objects match " + reference.value + "; use an exact ID. Candidates: " + string.Join(", ", matches.Select(id)));
            if (!selected.Contains(matches[0])) selected.Add(matches[0]);
        }
        return selected.ToArray();
    }
    // A complete result for exact definitions does not close their business category.
    internal static JObject ExactDefinitionSelection(IEnumerable<string> requested, IEnumerable<string> resolvedDefinitions) => new() {
        ["mode"] = "exact_definitions_or_labels",
        ["requested"] = new JArray(requested.Distinct(StringComparer.OrdinalIgnoreCase)),
        ["resolved_definitions"] = new JArray(resolvedDefinitions.Distinct(StringComparer.OrdinalIgnoreCase)),
        ["coverage"] = "selected_definitions_only"
    };
    internal static JObject Page(JObject args, IEnumerable<JObject> rows, string scope, SessionDescriptor session)
    {
        var all = rows.ToArray(); int offset = (int?)args["offset"] ?? 0, limit = (int?)args["limit"] ?? 20;
        var page = all.Skip(offset).Take(limit).ToArray();
        return new JObject { ["status"] = "ok", ["scope"] = scope, ["total"] = all.Length, ["rows"] = new JArray(page),
            ["page"] = new JObject { ["offset"] = offset, ["limit"] = limit, ["returned"] = page.Length, ["has_more"] = offset + page.Length < all.Length,
                ["next_offset"] = offset + page.Length < all.Length ? (JToken)(offset + page.Length) : null },
            ["sample"] = new JObject { ["colony_id"] = session.ColonyId, ["load_epoch"] = session.LoadEpoch, ["snapshot_sequence"] = session.SnapshotSequence, ["game_tick"] = session.GameTick, ["atomic"] = false } };
    }
    internal static JObject Page(JObject args, IEnumerable<JObject> rows, JObject scope, SessionDescriptor session)
    {
        var result = Page(args, rows, "", session); result["scope"] = scope; return result;
    }
}
