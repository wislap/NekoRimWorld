using System;
namespace Neko.RimWorld;
/// <summary>
/// The one builder for the result the mod returns when it refuses to queue a
/// write or policy request: busy when the game queue is full, otherwise stale.
/// </summary>
internal static class RequestRejection
{
    public static ProtocolEnvelope QueueRefused(ProtocolEnvelope request, SessionDescriptor session, bool queueFull)
    {
        var (resultType, staleMessage) = request.Type switch
        {
            ProtocolMessageTypes.WriteRequest => (ProtocolMessageTypes.WriteResult, "The requested colony load is no longer active."),
            ProtocolMessageTypes.PolicyRequest => (ProtocolMessageTypes.PolicyResult, "The policy session is no longer active."),
            _ => throw new ArgumentException($"{request.Type} has no queued result.", nameof(request)),
        };
        var payload = new ProtocolPayload
        {
            Status = queueFull ? ProtocolStatuses.Busy : ProtocolStatuses.StaleSession,
            ErrorCode = queueFull ? "busy" : "stale_epoch",
            Message = queueFull ? "The game request queue is full." : staleMessage,
            Issues = Array.Empty<QueryIssue>(),
        };
        return ProtocolEnvelope.Create(resultType, request.ConnectionEpoch, 0, payload, session, request.RequestId);
    }
}
