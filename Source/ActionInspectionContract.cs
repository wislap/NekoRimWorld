using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class ActionInspectionContract
{
    internal const string Version = "native_behavior.v1";
    internal static readonly string[] Operations = { "inspect_actions", "inspect_threats" };
    internal static readonly string[] Actions = { "equip", "wear", "ingest" };

    internal static JObject Catalog()
    {
        var text = WorldObservationContract.Text();
        JObject ObjectRef() => new JObject {
            ["type"]="object", ["additionalProperties"]=false, ["properties"]=new JObject {
                ["kind"]=new JObject{["type"]="string",["enum"]=new JArray("pawn","thing")},
                ["id"]=text.DeepClone(),["name"]=text.DeepClone(),["map"]=text.DeepClone()},
            ["oneOf"]=new JArray(new JObject{["required"]=new JArray("id"),["not"]=new JObject{["required"]=new JArray("name")}},
                                new JObject{["required"]=new JArray("name"),["not"]=new JObject{["required"]=new JArray("id")}}) };
        var references = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 8, ["items"] = ObjectRef() };
        var actions = new JObject {
            ["actor"] = ObjectRef(), ["targets"] = references,
            ["radius"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 40, ["default"] = 20 },
            ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 24, ["default"] = 12 },
            ["actions"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 3, ["uniqueItems"] = true, ["items"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Actions) } }
        };
        var threats = new JObject { ["map"] = text, ["subjects"] = references.DeepClone(), ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 50, ["default"] = 20 } };
        JObject Tool(string name, string description, JObject properties, params string[] required) => new() {
            ["name"] = name, ["operation"] = name, ["description"] = description,
            ["parameters"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties, ["required"] = new JArray(required) }
        };
        return new JObject {
            ["version"] = Version,
            ["tools"] = new JArray(
                Tool("inspect_actions", "Check the audited native player-order eligibility branches for equip/wear/ingest for one actor. Supply up to eight targets, or discover visible spawned candidates within radius. Uses native predicates in matched provider branch order without constructing GUI options or executing callbacks. Available is not job completion, autonomous work, desirability, confirmation or safety. Returns checks, blocking reasons, actor needs, forbidden flag and implementation references.", actions, "actor"),
                Tool("inspect_threats", "Read visible hostile attack targets and the game's active-threat classification, including dormancy and current jobs. Optional subjects add native attack checks at their current cells. Hostility, active threat, range/line-of-fire eligibility and an actual attack are distinct. This does not predict future attacks or declare overall safety.", threats))
        };
    }

    internal static void Validate(string operation, JObject arguments)
    {
        var tool = ((JArray)Catalog()["tools"]!).Single(t => (string?)t["operation"] == operation);
        var schema = (JObject)tool["parameters"]!;
        var keys = ((JObject)schema["properties"]!).Properties().Select(p => p.Name).ToArray();
        if (arguments.Properties().Any(p => !keys.Contains(p.Name))) Fail("Unknown behavior inspection argument");
        foreach (var key in ((JArray)schema["required"]!).Values<string>()) if (arguments[key!] == null) Fail("Missing " + key);
        if (operation == "inspect_actions") {
            WorldObservationContract.ValidateRef(arguments["actor"], "actor");
            if ((string?)arguments["actor"]?["kind"] == "cell" || arguments["actor"]?["x"] != null) Fail("actor must identify a Pawn");
            if (arguments["targets"] != null && arguments["radius"] != null) Fail("Use targets or radius, not both");
            if (arguments["actions"] != null && (arguments["actions"] is not JArray a || a.Count < 1 || a.Count > 3 || a.Any(v => v.Type != JTokenType.String || !Actions.Contains(v.ToString())) || a.Select(v => v.ToString()).Distinct().Count() != a.Count)) Fail("actions requires distinct equip/wear/ingest names");
            Number(arguments, "radius", 0, 40); Number(arguments, "limit", 1, 24);
        } else {
            if (arguments["map"] != null && (arguments["map"]!.Type != JTokenType.String || arguments["map"]!.ToString().Length is < 1 or > 256)) Fail("Invalid map reference");
            Number(arguments, "limit", 1, 50);
        }
        foreach (var key in new[] { "targets", "subjects" }) if (arguments[key] != null) {
            if (arguments[key] is not JArray rows || rows.Count < 1 || rows.Count > 8) Fail(key + " requires 1..8 references");
            foreach (var reference in (JArray)arguments[key]!) {
                WorldObservationContract.ValidateRef(reference, key);
                if ((string?)reference["kind"] == "cell" || reference["x"] != null) Fail(key + " must identify Things/Pawns, not cells");
            }
        }
    }

    private static void Number(JObject args, string key, int min, int max)
    {
        if (args[key] != null && (args[key]!.Type != JTokenType.Integer || (long)args[key]! < min || (long)args[key]! > max)) Fail(key + " out of range");
    }
    private static void Fail(string message) => throw ReadContext.Error("invalid_arguments", message);
}
