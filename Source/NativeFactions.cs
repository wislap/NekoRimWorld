using RimWorld;

namespace Neko.RimWorld;

// Native player-faction membership, one spelling for every reader and writer.
internal static class NativeFactions
{
    internal static bool IsPlayer(Faction? faction) => faction != null && faction == Faction.OfPlayer;
}
