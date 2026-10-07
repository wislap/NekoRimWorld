using System;
using System.Collections.Generic;
using System.Diagnostics;
using GraphQL;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal sealed class CooperativeQuery : IFrameQueryOperation
{
    private readonly IEnumerator<int> steps;
    private readonly Stopwatch lifetime=Stopwatch.StartNew();
    private readonly Func<bool> current;
    private readonly Func<long> tick;
    private readonly SessionDescriptor session;
    private readonly long startedTick;
    private ReadContext? context;
    private bool disposed;
    public QueryOutcome? Outcome { get; private set; }
    internal CooperativeQuery(PublicQuery query,JObject input,IGameData source,SessionDescriptor session,double budget,string schemaId,Func<bool> current,Func<long> tick)
    {
        this.current=current;this.tick=tick;this.session=session;startedTick=session.GameTick;
        steps=query.ExecuteSteps((JObject)input.DeepClone(),source,session,budget,schemaId,value=>Outcome=value,value=>context=value).GetEnumerator();
    }
    public bool Advance()
    {
        if(Outcome!=null)return true;
        context?.RestartSlice();var slice=Stopwatch.StartNew();
        try {
            if(!current())throw ReadContext.Error("world_changed","Loaded world changed during query");
            if(lifetime.Elapsed.TotalSeconds>8)throw ReadContext.Error("query_lifetime_exceeded","Query did not finish within its cooperative lifetime");
            do { if(!steps.MoveNext()) { Finish();return true; } } while(slice.Elapsed.TotalMilliseconds<2);
        } catch(Exception error) when(ReadContext.Recoverable(error)) {
            Outcome=QueryOutcome.Failure((error as ExecutionError)?.Code??"query_failed",error.Message);Finish();return true;
        }
        return false;
    }
    private void Finish()
    {
        Outcome??=QueryOutcome.Failure("query_failed","Query produced no result");
        session.GameTick=tick();
        if(Outcome.Data?["sample"] is JObject sample) {
            sample["started_tick"]=startedTick;sample["finished_tick"]=session.GameTick;sample["atomic"]=false;
            sample["meaning"]="Cooperative game-thread reads over this interval; retained object handles are released on completion. Enumeration and values are not an atomic world snapshot.";
            if (Outcome.Execution != null)
                Outcome.Execution["evidence"] = ResultEvidence.PublicQuery(Outcome.Data!);
        }
        Dispose();
    }
    public void Dispose() { if(disposed)return;disposed=true;steps.Dispose();context=null; }
}
