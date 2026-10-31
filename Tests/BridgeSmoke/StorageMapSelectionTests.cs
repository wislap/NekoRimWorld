using System;

namespace Neko.RimWorld;

internal static class StorageMapSelectionTests
{
    internal static void Run()
    {
        var current = "Map_0";
        string Current() => current;
        if (StorageMapSelection.ResolveReadMap("current", Current) != "Map_0") throw new Exception("Current map was not resolved.");
        current = "Map_1";
        if (StorageMapSelection.ResolveReadMap("current", Current) != "Map_1") throw new Exception("Current map selection was cached.");
        if (StorageMapSelection.ResolveReadMap("Map_0", Current) != "Map_0") throw new Exception("Explicit map changed.");
        if (StorageMapSelection.ResolveReadMap(null, Current) != null) throw new Exception("Unspecified map changed.");
        try
        {
            StorageMapSelection.ResolveReadMap("current", () => throw new InvalidOperationException("no current map"));
            throw new Exception("Missing current map was accepted.");
        }
        catch (InvalidOperationException) { }
    }
}
