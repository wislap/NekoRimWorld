using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class JsonValueCasTests
{
    private sealed class Site : IPreparedWriteField, IWriteFieldResolver {
        internal JObject Current; internal int Writes;
        internal Site(JObject current) { Current = current; }
        public string ObjectId => "Map_0"; public string Field => "construction:test"; public bool IsDestroyed => false; public bool MayChangeByGame => true; public object? NativeTarget => null;
        public JToken Read() => Current.DeepClone(); public void Validate(JToken value) { } public void Write(JToken value) { Writes++; } public void Restore(JToken original) { }
        public IReadOnlyList<ResolvedWriteField> Resolve(JObject op) => new[] { new ResolvedWriteField(this, op["expect"]!, op["value"]!) };
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static JObject Sample(JToken roof) => new() { ["element_id"] = "wall", ["map_id"] = "Map_0", ["element"] = JObject.Parse("{plan_id:'plan',element_id:'wall',definition:'Wall',stuff:'WoodLog',x:101,z:101,rotation:0,kind:'building'}"), ["stage"] = "absent",
        ["site"] = new JArray(new JObject { ["x"] = 101, ["z"] = 101, ["terrain"] = "Soil", ["terrain_layers"] = new JObject { ["top"] = "Soil", ["foundation"] = JValue.CreateNull(), ["temporary"] = JValue.CreateNull() }, ["roof"] = roof, ["objects"] = new JArray() }) };
    private static QueryOutcome Preview(Site site, JObject expected) {
        var request = new JObject { ["load_epoch"] = "epoch", ["ops"] = new JArray(new JObject { ["expect"] = expected, ["value"] = expected["element"]!.DeepClone() }) };
        using var operation = new AtomicWriteOperation(site, request, () => "epoch", true); operation.Advance(); return operation.Outcome!;
    }
    internal static void Run() {
        string? nativeRoof = null;
        JToken oldStringNull = nativeRoof;
        var old = Sample(oldStringNull); var transported = JObject.Parse(old.ToString(Formatting.None));
        Check(oldStringNull.Type == JTokenType.String && transported["site"]![0]!["roof"]!.Type == JTokenType.Null && !JToken.DeepEquals(old, transported), "The real Newtonsoft native-string-null wire mismatch was not reproduced.");
        var legacy = new Site(old); Check((string?)Preview(legacy, transported).Data?["status"] == "conflict" && legacy.Writes == 0, "The former native site did not reproduce identical-wire CAS conflict.");
        var current = new Site(Sample(JsonValues.Text(nativeRoof)));
        Check(JToken.DeepEquals(current.Current, transported) && (string?)Preview(current, transported).Data?["status"] == "validated" && current.Writes == 0, "Explicit native JSON Null did not restore strict wire-roundtrip CAS.");
        var extra = (JObject)transported.DeepClone(); extra["site"]![0]!["unknown_extension"] = true; Check((string?)Preview(current, extra).Data?["status"] == "conflict", "The null fix weakened comparison of extra properties.");
        var absent = (JObject)transported.DeepClone(); ((JObject)absent["site"]![0]!).Remove("roof"); Check((string?)Preview(current, absent).Data?["status"] == "conflict", "Unknown/missing roof was incorrectly treated as observed null.");
        var changed = (JObject)transported.DeepClone(); changed["element"]!["x"] = 102; Check((string?)Preview(current, changed).Data?["status"] == "conflict", "The null fix ignored bound element coordinates.");
        Check(JsonValues.Text(null).Type == JTokenType.Null && JsonValues.Text("WoodLog").Type == JTokenType.String && (string?)JsonValues.Text("WoodLog") == "WoodLog", "Nullable string construction coerced a non-null native value.");
        Console.WriteLine("Native JSON CAS tests passed: exact string-null mismatch, wire-roundtrip site preview, strict extension/missing/coordinate conflicts.");
    }
}
