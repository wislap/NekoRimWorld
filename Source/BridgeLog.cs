using System;
using System.Diagnostics;
namespace Neko.RimWorld;
/// <summary>Throttled bridge logging. A failing sink never affects the bridge or game.</summary>
internal sealed class BridgeLog
{
    private readonly object gate = new();
    private Action<BridgeLogLevel, string> logger = DefaultLogger;
    private long lastStateLog;
    private long lastWarningLog;
    public void SetLogger(Action<BridgeLogLevel, string>? value)
    {
        lock (gate)
        {
            logger = value ?? DefaultLogger;
        }
    }
    public void StateChanged(string message)
    {
        long now = Stopwatch.GetTimestamp();
        lock (gate)
        {
            if (lastStateLog != 0 && BridgeClock.ElapsedMilliseconds(lastStateLog, now) < 2_000)
            {
                return;
            }
            lastStateLog = now;
        }
        Log(BridgeLogLevel.Info, message);
    }
    public void Warn(string message)
    {
        long now = Stopwatch.GetTimestamp();
        lock (gate)
        {
            if (lastWarningLog != 0 && BridgeClock.ElapsedMilliseconds(lastWarningLog, now) < 10_000)
            {
                return;
            }
            lastWarningLog = now;
        }
        Log(BridgeLogLevel.Warning, message);
    }
    public void Log(BridgeLogLevel level, string message)
    {
        Action<BridgeLogLevel, string> sink;
        lock (gate)
        {
            sink = logger;
        }
        try
        {
            sink(level, message);
        }
        catch
        {
            // Logging must never affect the bridge or game.
        }
    }
    private static void DefaultLogger(BridgeLogLevel level, string message)
    {
        Trace.WriteLine($"[{level}] [N.E.K.O RimWorld] {message}");
    }
}
public enum BridgeLogLevel
{
    Info,
    Warning,
    Error
}
