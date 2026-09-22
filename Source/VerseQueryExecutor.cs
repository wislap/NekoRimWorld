using System;
using Newtonsoft.Json.Linq;
namespace Neko.RimWorld;
internal static class QueryExecutor
{
    private static readonly Lazy<GameQueryEngine> Engine = new(() => new GameQueryEngine());
    public static QueryOutcome Execute(string operation, JObject arguments, SessionDescriptor session) =>
        operation == "spatial_query"
            ? SpatialQuery.Execute(arguments, session)
            : Engine.Value.Execute(operation, arguments, new VerseGameData(), session);
}
