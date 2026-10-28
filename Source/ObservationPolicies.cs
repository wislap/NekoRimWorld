using System.Collections.Generic;
using Newtonsoft.Json.Linq;
namespace Neko.RimWorld;
internal static class ObservationPolicies
{
    internal static float Progress<T>(IDictionary<T, float> values, T key) => values.TryGetValue(key, out var value) ? value : 0f;
    internal static JObject AreaKey(string map, int id) => new JObject { ["map_id"] = map, ["area_id"] = id };
    internal static bool LetterPauses(int setting, int threshold) => setting >= threshold;
}
