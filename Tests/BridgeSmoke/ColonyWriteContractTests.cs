using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
namespace Neko.RimWorld;
internal static class ColonyWriteContractTests
{
    internal static void Run()
    {
        var progress = new Dictionary<string, float>();
        if (ObservationPolicies.Progress(progress, "absent") != 0 || progress.Count != 0) throw new Exception("missing research read mutated dictionary");
        progress["known"] = 12; if (ObservationPolicies.Progress(progress, "known") != 12 || progress.Count != 1) throw new Exception("known research progress lost");
        var key = ObservationPolicies.AreaKey("Map_1", 7);
        if ((string?)key["map_id"] != "Map_1" || (int?)key["area_id"] != 7) throw new Exception("area key lost map or native ID");
        if (!ObservationPolicies.LetterPauses(2, 2) || !ObservationPolicies.LetterPauses(3, 1) || ObservationPolicies.LetterPauses(1, 2)) throw new Exception("native pause threshold comparison changed");
        var vectors = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "colony-write-v1", "vectors.json")));
        foreach (var item in (JArray)vectors["valid"]!) ProtocolCodec.Deserialize(Encoding.UTF8.GetBytes(item.ToString()));
        foreach (var item in (JArray)vectors["invalid"]!) {
            try { ProtocolCodec.Deserialize(Encoding.UTF8.GetBytes(item.ToString())); throw new Exception("invalid fixture accepted"); }
            catch (ProtocolException) { }
        }
        var changes = JObject.Parse("{'changeset_id':'cs-1','author':'planner','entry_id':'t-1','load_epoch':'22222222222222222222222222222222','idempotency_key':'key','ops':[{'op':'set_work_priority','pawn':'Thing_1','work':'Doctor','expect':3,'value':1,'evidence':'e-1'}]}");
        ColonyWriteContract.ValidateRequest("commit", changes);
        ColonyWriteContract.ValidateRequest("dry_run", changes);
        ColonyWriteContract.ValidateRequest("undo", JObject.Parse("{'token':'undo-1'}"));
        ColonyWriteContract.ValidateRequest("set_paused", JObject.Parse("{'paused':true,'role':'planner'}"));
        void Reject(string mode, JObject args) { try { ColonyWriteContract.ValidateRequest(mode, args); throw new Exception("accepted invalid write"); } catch (ProtocolException e) when (e.ErrorCode == ProtocolErrorCodes.InvalidPayload) { } }
        Reject("commit", JObject.Parse("{'changeset_id':'x'}"));
        Reject("set_paused", JObject.Parse("{'paused':'true','role':'planner'}"));
        Reject("undo", JObject.Parse("{'token':'x','extra':1}"));
        Reject("unknown", new JObject());
        var session = new SessionDescriptor("11111111111111111111111111111111", "22222222222222222222222222222222", 1, 1);
        var req = ProtocolEnvelope.Create(ProtocolMessageTypes.WriteRequest, "33333333333333333333333333333333", 1, new ProtocolPayload { Mode = "commit", Arguments = changes }, session, "44444444444444444444444444444444");
        if (ProtocolCodec.Deserialize(ProtocolCodec.Serialize(req)).Payload.Mode != "commit") throw new Exception("write request round trip failed");
        var current = ColonyWriteContract.UnavailableResult(req, session, false);
        if (current.Payload.Status != "unsupported" || current.RequestId != req.RequestId) throw new Exception("write dispatch failed");
        var stale = ColonyWriteContract.UnavailableResult(req, new SessionDescriptor(session.ColonyId, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1, 1), false);
        if (stale.Payload.Status != "stale_session" || stale.Payload.ErrorCode != "stale_epoch") throw new Exception("stale write dispatched");
        if (ColonyWriteContract.UnavailableResult(req, session, true).Payload.ErrorCode != "stale_epoch") throw new Exception("unavailable session accepted");
        var badOp = (JObject)changes.DeepClone(); ((JObject)((JArray)badOp["ops"]!)[0]!).Remove("pawn"); Reject("commit", badOp);
        badOp = (JObject)changes.DeepClone(); ((JObject)((JArray)badOp["ops"]!)[0]!)["expect"] = JObject.Parse("{'x':1}"); Reject("commit", badOp);
        var result = ProtocolEnvelope.Create(ProtocolMessageTypes.WriteResult, req.ConnectionEpoch, 2, new ProtocolPayload { Status = ProtocolStatuses.Unsupported, ErrorCode = "unsupported", Message = "Writing is not available.", Issues = Array.Empty<QueryIssue>() }, session, req.RequestId);
        if (ProtocolCodec.Deserialize(ProtocolCodec.Serialize(result)).RequestId != req.RequestId) throw new Exception("write correlation failed");
    }
}
