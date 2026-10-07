using System;
using Newtonsoft.Json.Linq;
namespace Neko.RimWorld;
internal static class QueryExecutor
{
    private static readonly Lazy<GameQueryEngine> Engine = new(() => new GameQueryEngine());
    internal static IFrameQueryOperation BeginPublicQuery(JObject arguments,SessionDescriptor session)
    {
        var game=Verse.Current.Game;
        return Engine.Value.BeginPublicQuery(arguments,new VerseGameData(),session,()=>Verse.Current.Game==game,()=>Verse.Find.TickManager.TicksGame);
    }
    public static QueryOutcome Execute(string operation, JObject arguments, SessionDescriptor session)
    {
        if (Array.IndexOf(ActionInspectionContract.Operations, operation) >= 0) {
            using var read = new NativeBehaviorOperation(operation, arguments, session);
            while (!read.Advance()) { }
            return read.Outcome!;
        }
        var outcome = Array.IndexOf(WorldObservationContract.Operations, operation) >= 0 ? WorldObservation.Execute(operation, arguments, session) : operation == "spatial_query"
            ? SpatialQuery.Execute(arguments, session) : Engine.Value.Execute(operation, arguments, new VerseGameData(), session);
        if (operation == "query_catalog" && outcome.Data != null) {
            outcome.Data["native_behavior"] = ActionInspectionContract.Catalog();
            outcome.Data["game_implementation"] = GameImplementation.Describe();
        }
        return outcome;
    }
}
