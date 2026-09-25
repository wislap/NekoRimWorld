using System;
using System.Collections.Generic;

namespace Neko.RimWorld;

// Internal read selection, not another public tool/aspect or ThingDef whitelist.
internal static class SpatialStatePolicy
{
    private static readonly HashSet<string> Facets = new(StringComparer.Ordinal)
    {
        "condition", "door", "trap", "explosive", "turret", "power", "switch",
        "breakdown", "ammunition", "manning"
    };

    internal static bool Includes(string facet) => Facets.Contains(facet);
    internal const string Meaning = "Spatial object state only: identity, position, condition and supported passage/hazard facets. " +
        "Full Pawn health/skills/work/gear, bed suitability/reservations, storage and production state were not requested. " +
        "Use object-state inspection for those facts; omitted facets are unknown, not empty or false.";
}
