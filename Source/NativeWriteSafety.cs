using System;

namespace Neko.RimWorld;

// Guards for native setters whose side effects cannot be restored from one field.
internal static class NativeWriteSafety
{
    internal static void RequireWorkPriority(int original, int requested, bool hasCurrentJob, int queuedJobs)
    {
        if ((original == 0 || requested == 0) && (hasCurrentJob || queuedJobs != 0))
            throw new ArgumentException("Changing or restoring priority zero requires an empty job tracker.");
    }

    internal static void RequireStorageItem(bool official, bool hasVirtualDefinitions, bool parentAllows)
    {
        if (!official) throw new ArgumentException("Only official item definitions can be changed.");
        if (hasVirtualDefinitions) throw new ArgumentException("Storage filter definitions with virtual descendants cannot be restored as one field.");
        if (!parentAllows) throw new ArgumentException("The storage parent filter does not allow this item definition.");
    }
}
