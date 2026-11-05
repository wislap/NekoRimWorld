using Verse;

namespace Neko.RimWorld;

// Native writes only target definitions shipped by Ludeon's base game and DLCs.
internal static class OfficialContent
{
    internal static bool Contains(Def def)
    {
        var package = def.modContentPack?.PackageId?.ToLowerInvariant();
        return package == "ludeon.rimworld" || package == "ludeon.rimworld.royalty" ||
            package == "ludeon.rimworld.ideology" || package == "ludeon.rimworld.biotech" ||
            package == "ludeon.rimworld.anomaly" || package == "ludeon.rimworld.odyssey";
    }
}
