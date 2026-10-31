using System;

namespace Neko.RimWorld;

internal static class StorageMapSelection
{
    internal static string? ResolveReadMap(string? requested, Func<string> currentMapId)
        => requested == "current" ? currentMapId() : requested;
}
