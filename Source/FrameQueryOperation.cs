using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Neko.RimWorld;

internal interface IFrameQueryOperation : IDisposable
{
    bool Advance();
    QueryOutcome? Outcome { get; }
}

/// <summary>Game-thread iterator advancement, elapsed time and one-time release.</summary>
internal abstract class FrameQueryOperation : IFrameQueryOperation
{
    private readonly Stopwatch lifetime = Stopwatch.StartNew();
    private IEnumerator<int>? work;
    private bool disposed;
    public QueryOutcome? Outcome { get; protected set; }
    protected double ElapsedMilliseconds => lifetime.Elapsed.TotalMilliseconds;

    protected void InitializeWork(IEnumerator<int> steps) => work = steps;
    protected void EnsureLifetime(double seconds, string code, string message)
    {
        if (lifetime.Elapsed.TotalSeconds > seconds) throw ReadContext.Error(code, message);
    }
    protected abstract void RestartReadSlice();
    protected abstract void ValidateFrame();
    protected abstract void Complete();
    protected abstract void HandleError(Exception error);
    protected virtual bool YieldImmediately(int step) => false;
    protected virtual void ReleaseReferences() { }

    public bool Advance()
    {
        if (Outcome != null || disposed) return true;
        RestartReadSlice();
        var slice = Stopwatch.StartNew();
        try {
            ValidateFrame();
            do {
                if (!work!.MoveNext()) {
                    Complete();
                    Dispose();
                    return true;
                }
                if (YieldImmediately(work.Current)) return false;
            } while (slice.Elapsed.TotalMilliseconds < 2);
        } catch (Exception error) when (ReadContext.Recoverable(error)) {
            try { HandleError(error); }
            finally { Dispose(); }
        }
        return Outcome != null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { work?.Dispose(); }
        finally { work = null; ReleaseReferences(); }
    }
}
