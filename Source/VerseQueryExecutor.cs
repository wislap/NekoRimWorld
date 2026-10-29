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
        var outcome = Engine.Value.Execute(operation, arguments, new VerseGameData(), session);
        if (operation == "query_catalog" && outcome.Data != null) {
            outcome.Data["native_behavior"] = ActionInspectionContract.Catalog();
            outcome.Data["situation_snapshot"] = SituationSnapshotContract.Catalog();
            outcome.Data["colony_state"] = ColonyReadContract.Catalog();
            outcome.Data["write_ledger_snapshot"] = new JObject { ["capability"] = "colony_write.ledger_snapshot.v1", ["operations"] = new JArray("write_ledger_snapshot", "write_field_identities", "write_undo_fields") };
            outcome.Data["colony_write"] = VerseWriteFieldResolver.DescribeCatalog();
            outcome.Data["game_implementation"] = GameImplementation.Describe();
        }
        return outcome;
    }
}
