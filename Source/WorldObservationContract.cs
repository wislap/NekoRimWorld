using System;
using System.Linq;
using Newtonsoft.Json.Linq;
namespace Neko.RimWorld;

internal interface IFrameQueryOperation : IDisposable
{
    bool Advance();
    QueryOutcome? Outcome { get; }
}

internal static class WorldObservationContract
{
    internal const string Version = "world_observation.v2.2";
    internal static readonly string[] StateSections = { "identity", "health", "skills", "gear", "work", "state" };
    internal static readonly string[] Operations = { "survey_space", "inspect_route", "inspect_state" };
    internal static readonly string[] Capabilities = { "pawn", "building", "item", "plant", "medicine", "sleeping_place", "openable", "powered", "refuelable", "storage", "production", "hazard" };
    internal static JObject Text() => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 256 };
    internal static JObject Ref() => JObject.Parse(@"{'type':'object','additionalProperties':false,'properties':{'id':{'type':'string','minLength':1,'maxLength':256},'name':{'type':'string','minLength':1,'maxLength':256},'map':{'type':'string','default':'current'},'kind':{'type':'string','enum':['pawn','thing','cell']},'x':{'type':'integer','minimum':-2147483648,'maximum':2147483647},'z':{'type':'integer','minimum':-2147483648,'maximum':2147483647}},'allOf':[{'if':{'properties':{'kind':{'const':'cell'}},'required':['kind']},'then':{'required':['x','z']}},{'if':{'properties':{'kind':{'enum':['pawn','thing']}},'required':['kind']},'then':{'not':{'anyOf':[{'required':['x']},{'required':['z']}]}}}],'oneOf':[{'required':['id'],'not':{'anyOf':[{'required':['name']},{'required':['x']},{'required':['z']}]}},{'required':['name'],'not':{'anyOf':[{'required':['id']},{'required':['x']},{'required':['z']}]}},{'required':['x','z'],'not':{'anyOf':[{'required':['id']},{'required':['name']}]}}]}");
    private static JObject Int(int min, int max, int value) => new() { ["type"] = "integer", ["minimum"] = min, ["maximum"] = max, ["default"] = value };
    internal static JObject Region() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["properties"] = new JObject
        {
            ["map"] = Text(),
            ["near"] = Ref(),
            ["radius"] = Int(0, 128, 20),
            ["room_of"] = Ref(),
            ["rect"] = JObject.Parse(@"{'type':'object','additionalProperties':false,'required':['min_x','min_z','max_x','max_z'],'properties':{'min_x':{'type':'integer'},'min_z':{'type':'integer'},'max_x':{'type':'integer'},'max_z':{'type':'integer'}}}"),
            ["area"] = JObject.Parse(@"{'type':'object','additionalProperties':false,'properties':{'id':{'type':'string'},'name':{'type':'string'},'kind':{'type':'string','enum':['area','zone']}},'oneOf':[{'required':['id'],'not':{'required':['name']}},{'required':['name'],'not':{'required':['id']}}]}")
        },
        ["allOf"] = RegionRules(),
        ["description"] = "Exactly one footprint: near (+radius), rect, room_of, area/zone, or map alone. All boundaries inclusive; nearby is a Euclidean circle, not a square or path distance. Multi-cell Things match any occupied cell."
    };
    private static JArray RegionRules()
    {
        var modes = new[] { "near", "rect", "room_of", "area" };
        var choices = new JArray();
        foreach (var mode in modes) choices.Add(new JObject
        {
            ["required"] = new JArray(mode),
            ["not"] = new JObject { ["anyOf"] = new JArray(modes.Where(x => x != mode).Select(x => new JObject { ["required"] = new JArray(x) })) }
        });
        choices.Add(new JObject { ["required"] = new JArray("map"), ["not"] = new JObject { ["anyOf"] = new JArray(modes.Select(x => new JObject { ["required"] = new JArray(x) })) } });
        return new JArray(new JObject { ["oneOf"] = choices }, new JObject { ["if"] = new JObject { ["required"] = new JArray("radius") }, ["then"] = new JObject { ["required"] = new JArray("near") } });
    }
    internal static JObject Catalog()
    {
        var common = new JObject { ["detail"] = new JObject { ["type"] = "string", ["enum"] = new JArray("summary", "details"), ["default"] = "summary" } };
        var survey = (JObject)common.DeepClone(); survey["region"] = Region(); survey["actor"] = Ref(); survey["limit"] = Int(1, 50, 20); survey["cursor"] = Text();
        survey["select"] = new JObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JObject
            {
                ["definitions"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 32, ["items"] = Text() },
                ["capabilities"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 12, ["items"] = new JObject { ["type"] = "string", ["enum"] = new JArray(Capabilities) } }
            },
            ["description"] = "Exact definition names and/or native capabilities. OR within each list, AND between lists. Selection filters objects, never suppresses environmental hazards."
        };
        var route = (JObject)common.DeepClone(); route["actor"] = Ref(); route["user"] = Ref(); route["to"] = Ref(); route["start"] = Ref(); route["margin"] = Int(0, 12, 2); route["limit"] = Int(1, 50, 20); route["cursor"] = Text();
        var state = (JObject)common.DeepClone(); state["targets"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 8, ["items"] = Ref() }; state["actor"] = Ref(); state["user"] = Ref();
        state["exact_lookup"] = new JObject { ["type"] = "boolean", ["default"] = false, ["description"] = "Resolve object targets by exact case-sensitive Pawn.LabelShort / Thing.Label. Search loaded maps when map is omitted; actor/user and cells retain current-map defaults." };
        state["detail_limit"] = Int(1, 100, 20);
        state["include_fogged"] = new JObject { ["type"] = "boolean", ["default"] = false, ["description"] = "Explicit visibility policy for object-state reads; default excludes hidden targets." };
        state["sections"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 6, ["items"] = new JObject { ["type"] = "string", ["enum"] = new JArray(StateSections) }, ["description"] = "Selected current-state sections; omitted means all native facets. Selection does not claim omitted facets absent." };
        state["stats"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 16, ["items"] = Text(), ["description"] = "Exact StatDef names, evaluated by the game. No formula reconstruction." };
        JObject Tool(string name, string description, JObject props, params string[] required) => new() { ["name"] = name, ["operation"] = name, ["description"] = description, ["parameters"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = props, ["required"] = new JArray(required) } };
        return new JObject
        {
            ["version"] = Version,
            ["visibility"] = "Visible spawned objects/cells and real contents of visible holders. Hidden objects are not evidence of absence.",
            ["tools"] = new JArray {
            Tool("survey_space","Discover a spatial footprint, scoped object counts and current environment together. Resolve names directly. Includes state summaries of displayed objects. Inspect returned coverage; not a safety guarantee.",survey,"region"),
            Tool("inspect_route","Compute one native Pawn path and inspect its cells and surrounding environment in one call. No preliminary inspect_space/route ID. start is hypothetical when supplied. Path costs are not ETA; current hazards are not a future combat simulation.",route,"actor","to"),
            Tool("inspect_state","Read all supported state facets of 1..8 objects or cells. user is the intended user/recipient; actor is the interacting/travelling Pawn (defaults to user). Returns occupancy, reservation sources, native contextual checks and coverage separately, never universal can_use.",state,"targets") }
        };
    }
    internal static void Validate(string op, JObject args)
    {
        var schema = (JObject)((JArray)Catalog()["tools"]!).Single(t => (string?)t["name"] == op)["parameters"]!;
        Keys(args, ((JObject)schema["properties"]!).Properties().Select(p => p.Name).ToArray());
        foreach (var key in ((JArray)schema["required"]!).Values<string>()) if (args[key!] == null) Fail("Missing " + key);
        foreach (var key in new[] { "actor", "user", "to", "start" }) if (args[key] != null) ValidateRef(args[key], key);
        if (args["detail"] != null && (string?)args["detail"] is not ("summary" or "details")) Fail("detail must be summary or details");
        Number(args, "detail_limit", 1, 100); Number(args, "limit", 1, 50); Number(args, "margin", 0, 12);
        if (args["cursor"] != null) String(args["cursor"], "cursor");
        if (args["targets"] != null) { if (args["targets"] is not JArray a || a.Count < 1 || a.Count > 8) Fail("targets requires 1..8 refs"); foreach (var t in (JArray)args["targets"]!) ValidateRef(t, "target"); }
        if (args["exact_lookup"] != null && args["exact_lookup"]!.Type != JTokenType.Boolean) Fail("exact_lookup must be boolean");
        if (args["include_fogged"] != null && args["include_fogged"]!.Type != JTokenType.Boolean) Fail("include_fogged must be boolean");
        if (args["sections"] != null) { Strings(args["sections"], StateSections.Length, "sections"); if (((JArray)args["sections"]!).Any(s => !StateSections.Contains(s.ToString()))) Fail("Unknown state section"); }
        if (args["stats"] != null) Strings(args["stats"], 16, "stats");
        if (args["select"] != null) { if (args["select"] is not JObject s) { Fail("select must be object"); return; } Keys(s, "definitions", "capabilities"); foreach (var k in new[] { "definitions", "capabilities" }) if (s[k] != null) Strings(s[k], k == "definitions" ? 32 : 12, k); if (s["capabilities"] is JArray cs && cs.Any(c => !Capabilities.Contains(c.ToString()))) Fail("Unknown capability"); }
        if (args["region"] != null)
        {
            if (args["region"] is not JObject r) { Fail("region must be object"); return; }
            Keys(r, "map", "near", "radius", "rect", "room_of", "area");
            var modes = new[] { "near", "rect", "room_of", "area" }.Count(k => r[k] != null); if (modes > 1 || modes == 0 && r["map"] == null) Fail("Choose one region footprint");
            if (r["map"] != null) String(r["map"], "region.map"); if (r["radius"] != null && r["near"] == null) Fail("radius requires near"); Number(r, "radius", 0, 128);
            foreach (var k in new[] { "near", "room_of" }) if (r[k] != null) ValidateRef(r[k], k);
            if (r["rect"] != null) { if (r["rect"] is not JObject rect) { Fail("rect must be object"); return; } Keys(rect, "min_x", "min_z", "max_x", "max_z"); foreach (var k in new[] { "min_x", "min_z", "max_x", "max_z" }) Integer(rect[k], k); if ((int)rect["min_x"]! > (int)rect["max_x"]! || (int)rect["min_z"]! > (int)rect["max_z"]!) Fail("Reversed rectangle"); }
            if (r["area"] != null) { if (r["area"] is not JObject area) { Fail("area must be object"); return; } Keys(area, "id", "name", "kind"); if ((area["id"] != null) == (area["name"] != null)) Fail("area requires id or name"); String(area["id"] ?? area["name"], "area selector"); if (area["kind"] != null && (string?)area["kind"] is not ("area" or "zone")) Fail("area.kind must be area or zone"); }
        }
    }
    internal static void ValidateRef(JToken? token, string label) { if (token is not JObject r) { Fail(label + " must be object"); return; } Keys(r, "id", "name", "map", "kind", "x", "z"); foreach (var k in new[] { "id", "name", "map", "kind" }) if (r[k] != null) String(r[k], label + "." + k); if (r["kind"] != null && (string?)r["kind"] is not ("pawn" or "thing" or "cell")) Fail("Unknown reference kind"); bool cell = r["x"] != null || r["z"] != null; if (cell) { Integer(r["x"], "x"); Integer(r["z"], "z"); if (r["id"] != null || r["name"] != null || (string?)r["kind"] is "pawn" or "thing") Fail("Cell requires x/z only"); } else if ((r["id"] != null) == (r["name"] != null) || (string?)r["kind"] == "cell") Fail("Reference requires id or name"); }
    internal static void Keys(JObject o, params string[] keys) { foreach (var p in o.Properties()) if (!keys.Contains(p.Name)) Fail("Unsupported field: " + p.Name); }
    private static void Strings(JToken? t, int max, string label) { if (t is not JArray a || a.Count < 1 || a.Count > max) { Fail(label + " array out of bounds"); return; } foreach (var v in a) String(v, label); }
    private static void String(JToken? t, string label) { if (t?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)t) || t!.ToString().Length > 256) Fail(label + " requires bounded nonempty text"); }
    private static void Integer(JToken? t, string label) { if (t?.Type != JTokenType.Integer || !int.TryParse(t.ToString(), out _)) Fail(label + " requires 32-bit integer"); }
    private static void Number(JObject o, string k, int min, int max) { if (o[k] == null) return; Integer(o[k], k); if ((int)o[k]! < min || (int)o[k]! > max) Fail(k + " out of bounds"); }
    private static void Fail(string message) => throw ReadContext.Error("invalid_arguments", message);
}
