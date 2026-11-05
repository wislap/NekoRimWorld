using System.Collections.Generic;
using Verse;

namespace Neko.RimWorld;

// Real containment relations above a thing.
internal static class NativeHolders
{
    // Holders along Thing.ParentHolder, nearest first, visiting each holder once
    // and at most limit holders. complete is true when the chain reached its root.
    internal static List<IThingHolder> Ancestors(Thing thing, int limit, out bool complete)
    {
        var chain = new List<IThingHolder>(); var seen = new HashSet<IThingHolder>(); var holder = thing.ParentHolder;
        while (holder != null && chain.Count < limit && seen.Add(holder)) { chain.Add(holder); holder = holder.ParentHolder; }
        complete = holder == null;
        return chain;
    }
}
