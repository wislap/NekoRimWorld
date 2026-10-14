using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
namespace Neko.RimWorld;

// Pure reduction of native reversed path nodes. No path algorithm or game state.
internal static class SpatialRouteSummary
{
    internal const int MaxNodes = 100000;
    internal static JObject Create(IReadOnlyList<(int X, int Z)> reversed, float cost, string mapId)
    {
        if (reversed.Count == 0 || reversed.Count > MaxNodes || float.IsNaN(cost) || float.IsInfinity(cost) || cost < 0)
            throw ReadContext.Error("route_invalid", "Native path has invalid nodes or cost.");
        double distance = 0;
        for (var i = 1; i < reversed.Count; i++) {
            var dx = Math.Abs((long)reversed[i].X - reversed[i-1].X);
            var dz = Math.Abs((long)reversed[i].Z - reversed[i-1].Z);
            if (dx > 1 || dz > 1 || dx + dz == 0)
                throw ReadContext.Error("route_unsupported_geometry", "Native path contains nonadjacent or repeated nodes; ordinary grid length cannot be reported.");
            distance += dx + dz == 2 ? Math.Sqrt(2) : 1;
        }
        JObject Cell((int X,int Z) p) => new JObject { ["map"] = mapId, ["x"] = p.X, ["z"] = p.Z };
        return new JObject {
            ["status"] = "found", ["steps"] = reversed.Count - 1,
            ["path_length"] = new JObject { ["value"] = distance, ["unit"] = "map_cell_widths", ["meaning"] = "Sum of segment lengths along the returned path, not straight-line distance between endpoints." },
            ["native_cost"] = new JObject { ["value"] = cost, ["unit"] = "native_path_cost" },
            ["start"] = Cell(reversed[reversed.Count - 1]), ["end"] = Cell(reversed[0]),
            ["meaning"] = "One path returned by the native pathfinder, not guaranteed shortest. Steps count grid transitions. Path length sums every segment along the returned path (cardinal 1, diagonal sqrt(2) cell widths); it is not straight-line endpoint distance. Cost is the native weighted search cost, not elapsed ticks, seconds or an arrival-time estimate. No movement command was issued."
        };
    }
}
