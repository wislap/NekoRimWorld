using System;
using GraphQL;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal sealed class CooperativeQuery : FrameQueryOperation
{
    private readonly Func<bool> current;
    private readonly Func<long> tick;
    private readonly SessionDescriptor session;
    private readonly long startedTick;
    private ReadContext? context;
    internal CooperativeQuery(PublicQuery query,JObject input,IGameData source,SessionDescriptor session,double budget,string schemaId,Func<bool> current,Func<long> tick)
    {
        this.current=current;this.tick=tick;this.session=session;startedTick=session.GameTick;
        InitializeWork(query.ExecuteSteps((JObject)input.DeepClone(),source,session,budget,schemaId,value=>Outcome=value,value=>context=value).GetEnumerator());
    }
    protected override void RestartReadSlice() => context?.RestartSlice();
    protected override void ValidateFrame()
    {
        if(!current())throw ReadContext.Error("world_changed","Loaded world changed during query");
        EnsureLifetime(8,"query_lifetime_exceeded","Query did not finish within its cooperative lifetime");
    }
    protected override void Complete() => Finish();
    protected override void HandleError(Exception error)
    {
        Outcome=QueryOutcome.Failure((error as ExecutionError)?.Code??"query_failed",error.Message);
        Finish();
    }
    private void Finish()
    {
        Outcome??=QueryOutcome.Failure("query_failed","Query produced no result");
        session.GameTick=tick();
        if(Outcome.Data?["sample"] is JObject sample) {
            sample["started_tick"]=startedTick;sample["finished_tick"]=session.GameTick;sample["atomic"]=false;
            sample["meaning"]="Cooperative game-thread reads over this interval; retained object handles are released on completion. Enumeration and values are not an atomic world snapshot.";
        }
    }
    protected override void ReleaseReferences() => context=null;
}
