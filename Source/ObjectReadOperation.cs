using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Neko.RimWorld;

internal sealed class ObjectReadOperation : IFrameQueryOperation
{
    private IEnumerator<int>? steps;
    private readonly Func<bool> current;
    private readonly Stopwatch lifetime=Stopwatch.StartNew();
    private ReadContext? context;
    private readonly Action? cancel;
    public QueryOutcome? Outcome { get; private set; }
    internal ObjectReadOperation(Func<Action<QueryOutcome>,Action<ReadContext>,IEnumerable<int>> execute,Func<bool> current,Action? cancel=null) {
        this.current=current;this.cancel=cancel;steps=execute(value=>Outcome=value,value=>context=value).GetEnumerator();
    }
    public bool Advance() {
        if(Outcome!=null)return true;
        try {
            if(!current())throw ReadContext.Error("world_changed","Loaded world changed during object read.");
            context?.RestartSlice();var slice=Stopwatch.StartNew();
            // Exceptional process protection, never normal business pagination.
            if(lifetime.Elapsed.TotalSeconds>8)throw ReadContext.Error("query_lifetime_exceeded","Object read exceeded its cooperative lifetime; no completeness is asserted.");
            do { if(steps==null || !steps.MoveNext()) { Outcome??=QueryOutcome.Failure("object_read_failed","Object read produced no outcome.");Dispose();return true; } }
            while(slice.Elapsed.TotalMilliseconds<2 && Outcome==null);
        } catch(Exception error) when(ReadContext.Recoverable(error)) {
            var diagnostic=error.Message.Length<=512?error.Message:error.Message.Substring(0,512);
            Outcome=QueryOutcome.Failure((error as GraphQL.ExecutionError)?.Code??"object_read_failed",diagnostic);Dispose();return true;
        }
        if(Outcome!=null)Dispose();return Outcome!=null;
    }
    public void Dispose() { var unfinished=Outcome==null || Outcome.ErrorCode is "query_lifetime_exceeded" or "world_changed" or "object_read_failed";steps?.Dispose();steps=null;context=null;if(unfinished)cancel?.Invoke(); }
}
