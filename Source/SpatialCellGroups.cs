using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

// Literal cell properties remain self-contained. Coordinates are absolute world
// pairs, and a group is shared only when every other observed property is equal.
internal sealed class SpatialCellGroups
{
    private readonly Dictionary<JToken, JObject> byProperties = new(JToken.EqualityComparer);
    internal JArray Groups { get; } = new();
    internal void Add(JObject cell) {
        if (cell["x"]?.Type != JTokenType.Integer || cell["z"]?.Type != JTokenType.Integer || cell.Property("_xz") != null) throw new ArgumentException("Spatial cells require integer world coordinates and reserve _xz for grouping.");
        var properties = (JObject)cell.DeepClone(); properties.Remove("x"); properties.Remove("z");
        if (!byProperties.TryGetValue(properties, out var group)) {
            group = (JObject)properties.DeepClone(); group["_xz"] = new JArray();
            byProperties.Add(properties, group); Groups.Add(group);
        }
        ((JArray)group["_xz"]!).Add(new JArray(cell["x"]!.DeepClone(), cell["z"]!.DeepClone()));
    }
}
