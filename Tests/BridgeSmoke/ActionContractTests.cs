using System;
using System.Linq;
using GraphQL;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class ActionContractTests
{
    internal static void Run()
    {
        void Good(string operation, string input) => ActionInspectionContract.Validate(operation, JObject.Parse(input));
        void Bad(string operation, string input) {
            try { Good(operation, input); } catch (ExecutionError e) when (e.Code == "invalid_arguments") { return; }
            throw new Exception("Invalid behavior inspection accepted: " + input);
        }
        Good("inspect_actions", "{'actor':{'name':'Lumi'},'radius':20,'actions':['equip','wear','ingest']}");
        Good("inspect_actions", "{'actor':{'id':'pawn'},'targets':[{'id':'gun'}]}");
        Good("inspect_actions", "{'actor':{'kind':'thing','id':'pawn'},'targets':[{'id':'gun'}]}");
        var catalog = (JArray)ActionInspectionContract.Catalog()["tools"]!;
        var actorRef = (JObject)catalog[0]!["parameters"]!["properties"]!["actor"]!;
        var kinds = (JArray)actorRef["properties"]!["kind"]!["enum"]!;
        var declaredKinds = kinds.Values<string>().ToArray();
        if (!declaredKinds.Contains("pawn") || !declaredKinds.Contains("thing") || declaredKinds.Contains("cell") || actorRef["properties"]!["x"] != null)
            throw new Exception("Behavior references must retain supported Pawn-as-Thing references and omit unsupported cells");
        Good("inspect_threats", "{'map':'current','subjects':[{'id':'pawn'}]}");
        Bad("inspect_actions", "{'actor':{'x':0,'z':0}}");
        Bad("inspect_actions", "{'actor':{'id':'pawn'},'actions':['equip','equip']}");
        Bad("inspect_actions", "{'actor':{'id':'pawn'},'targets':[{'id':'gun'}],'radius':20}");
        Bad("inspect_actions", "{'actor':{'id':'pawn'},'actions':['execute']}");
        Bad("inspect_actions", "{'actor':{'id':'pawn'},'limit':0}");
        Bad("inspect_threats", "{'subjects':[{'x':0,'z':0}]}");
        Bad("inspect_threats", "{'include_fogged':true}");
        Bad("inspect_threats", "{'command':'draft'}");
        var session = new SessionDescriptor(new string('a',32),new string('b',32),1,1);
        var request = new ProtocolEnvelope { Version=ProtocolCodec.Version, Type=ProtocolMessageTypes.QueryRequest,
            ConnectionEpoch=new string('c',32), Sequence=0, RequestId=Guid.NewGuid().ToString("N"),
            ColonyId=session.ColonyId, LoadEpoch=session.LoadEpoch, GameTick=session.GameTick,
            SnapshotSequence=session.SnapshotSequence,
            Payload=new ProtocolPayload { Operation="inspect_threats", Arguments=new JObject() } };
        var partial = QueryOutcome.Partial(new JObject { ["status"]="partial" }, "behavior_partial", "Incomplete read");
        var delivered = QueryResultDelivery.Build(request,session,partial);
        if (delivered.Payload.Status!=ProtocolStatuses.Partial || delivered.Payload.Issues.Length!=1)
            throw new Exception("Behavior partial result did not survive the actual transport contract");
        var malformed = new QueryOutcome { Status=ProtocolStatuses.Partial,Data=new JObject() };
        var failure = QueryResultDelivery.Build(request,session,malformed);
        if (failure.Payload.Status!=ProtocolStatuses.Failed || failure.RequestId!=request.RequestId)
            throw new Exception("Malformed partial result must produce a correlated failure");
        var next = QueryResultDelivery.Build(request,session,new QueryOutcome { Data=new JObject { ["count"]=8 } });
        if (next.Payload.Status!=ProtocolStatuses.Ok) throw new Exception("Result validation blocked a later valid read");
        Console.WriteLine("Native behavior contract tests passed.");
    }
}
