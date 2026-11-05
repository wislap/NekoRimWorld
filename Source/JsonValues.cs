using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class JsonValues
{
    // Use a JSON Null immediately, rather than a String token with a null CLR value.
    internal static JToken Text(string? value) => value == null ? JValue.CreateNull() : new JValue(value);

    // The reader produces a Double from the Single's JSON spelling. Use that
    // value in native guards as well, without widening the public precision.
    internal static JValue Number(float value) => new(double.Parse(JsonConvert.ToString(value), CultureInfo.InvariantCulture));
}
