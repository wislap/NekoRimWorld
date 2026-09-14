using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GraphQL.Types;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>Predicate scalar domain attached to string fields; wire types stay compatible. No gameplay label aliases.</summary>
internal sealed class NativeEnumScalar : StringGraphType
{
    internal static NativeEnumScalar? For(FieldType field) => field.Metadata.TryGetValue("native_enum", out var domain) ? (NativeEnumScalar)domain! : null;
    internal string NativeType { get; }
    internal bool Flags { get; }
    private readonly SortedDictionary<string, ulong> values;

    internal NativeEnumScalar(string nativeType)
    {
        NativeType = nativeType;
        Name = "Native_" + nativeType.Replace('.', '_');
#if NEKO_SCHEMA_FIXTURE
        var domain = Fixture["domains"]?[nativeType] as JObject
            ?? throw new InvalidOperationException("Missing reference enum domain " + nativeType);
        Flags = (bool)domain["flags"]!;
        values = new SortedDictionary<string, ulong>(((JObject)domain["values"]!).Properties().ToDictionary(p => p.Name, p => Bits((string)p.Value!)), StringComparer.Ordinal);
#else
        var type = Type.GetType(nativeType + ", Assembly-CSharp", throwOnError: true)!;
        if (!type.IsEnum) throw new InvalidOperationException(nativeType + " is not an enum");
        Flags = type.IsDefined(typeof(FlagsAttribute), false);
        values = new SortedDictionary<string, ulong>(Enum.GetNames(type).ToDictionary(n => n,
            n => Bits(Enum.Format(type, Enum.Parse(type, n), "D"))), StringComparer.Ordinal);
#endif
        Description = "Native " + nativeType + (Flags ? " flags. Comma-separated declared names form a bitwise union." : " enum. Use a declared name, not a gameplay/content-category label.")
            + " String wire representation is preserved; eq/ne/in compare native values. Unknown literals are rejected before reads.";
    }
#if NEKO_SCHEMA_FIXTURE
    private static readonly JObject Fixture = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "native-enums.json")));
#endif
    private static ulong Bits(string value) => value.StartsWith("-", StringComparison.Ordinal)
        ? unchecked((ulong)long.Parse(value, CultureInfo.InvariantCulture)) : ulong.Parse(value, CultureInfo.InvariantCulture);

    internal ulong Value(string value, bool sensitive = true)
    {
        if (value.Length > 4096) throw new ArgumentException("Enum literal exceeds 4096 characters.");
        ulong result = 0;
        var names = Flags ? value.Split(',') : new[] { value };
        foreach (var raw in names) {
            var name = Flags ? raw.Trim() : raw;
            var found = values.TryGetValue(name, out var bits);
            if (!sensitive) {
                var matches = 0;
                foreach (var pair in values) if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) { matches++; bits = pair.Value; }
                found = matches == 1;
            }
            if (!found) throw new ArgumentException("Invalid " + NativeType + " name '" + name + "'. Declared names: " + string.Join(", ", values.Keys));
            result |= bits;
        }
        return result;
    }
    internal bool Valid(JToken value, bool sensitive = true)
    {
        if (value.Type != JTokenType.String) return false;
        try { Value((string)value!, sensitive); return true; }
        catch (ArgumentException) { return false; }
    }
    internal ulong ObservedValue(object value)
    {
        try { return Value(Convert.ToString(value, CultureInfo.InvariantCulture)!); }
        catch (ArgumentException) { throw ReadContext.Error("enum_value_unavailable", "Observed " + NativeType + " value is outside the declared native domain; membership is unknown."); }
    }
    internal JObject Describe() => new() {
        ["native_type"] = NativeType, ["kind"] = Flags ? "flags" : "enum",
        ["values"] = new JArray(values.Keys),
        ["operators"] = Flags ? new JArray("eq", "ne", "in", "has_all", "has_any", "is_null") : new JArray("eq", "ne", "in", "is_null"),
        ["flags_separator"] = Flags ? "," : null
    };
}
