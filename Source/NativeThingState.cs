using RimWorld;
using Verse;

namespace Neko.RimWorld;

// Native per-thing flags read the same way by observations and write guards.
internal static class NativeThingState
{
    // Null when the thing has no forbiddable component.
    internal static bool? Forbidden(Thing thing) => thing.TryGetComp<CompForbiddable>()?.Forbidden;
}
