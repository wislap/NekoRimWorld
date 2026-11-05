using System.Diagnostics;
namespace Neko.RimWorld;
/// <summary>Monotonic stopwatch arithmetic shared by the bridge transport and session lease.</summary>
internal static class BridgeClock
{
    public static long After(int milliseconds)
    {
        return Stopwatch.GetTimestamp() + ToStopwatchTicks(milliseconds);
    }
    public static long ToStopwatchTicks(int milliseconds)
    {
        return (long)(Stopwatch.Frequency * (milliseconds / 1000.0));
    }
    public static double ElapsedMilliseconds(long start, long end)
    {
        return (end - start) * 1000.0 / Stopwatch.Frequency;
    }
}
