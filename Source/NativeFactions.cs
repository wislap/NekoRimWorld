using RimWorld;

namespace Neko.RimWorld;

// Membership in the colony's own faction (Faction.OfPlayer). The faction def flag stays Faction.IsPlayer.
internal static class NativeFactions
{
    internal static bool IsPlayer(Faction? faction) => faction != null && faction == Faction.OfPlayer;
}
