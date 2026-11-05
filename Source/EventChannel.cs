using System;
using Newtonsoft.Json.Linq;
namespace Neko.RimWorld;
/// <summary>
/// The game_event stream of one connection: subscription and a monotonically
/// increasing cursor. Its state is guarded by the connection gate and resets
/// with each connection epoch and each attached session.
/// </summary>
internal sealed class EventChannel
{
    private readonly BridgeStatus status;
    private readonly BridgeConnection connection;
    private readonly SessionLease lease;
    private readonly Func<ProtocolEnvelope, bool> enqueueTelemetry;
    private long cursor;
    private bool enabled;
    public EventChannel(BridgeStatus status, BridgeConnection connection, SessionLease lease,
                        Func<ProtocolEnvelope, bool> enqueueTelemetry)
    {
        this.status = status;
        this.connection = connection;
        this.lease = lease;
        this.enqueueTelemetry = enqueueTelemetry;
    }
    public void Reset()
    {
        lock (connection.Gate)
        {
            cursor = 0;
            enabled = false;
        }
    }
    public bool Enable(ProtocolEnvelope request, out bool firstSubscription)
    {
        firstSubscription = false;
        lock (connection.Gate)
        {
            if (status.State != BridgeConnectionState.Ready || request.ConnectionEpoch != connection.Epoch) return false;
            if (!lease.IsCurrent(request)) return false;
            firstSubscription = !enabled; enabled = true; return true;
        }
    }
    public bool Emit(SessionDescriptor session, JObject data)
    {
        lock (connection.Gate)
        {
            string epoch = connection.Epoch;
            if (status.State != BridgeConnectionState.Ready || string.IsNullOrEmpty(epoch) || !enabled) return false;
            data["cursor"] = ++cursor;
            var message = ProtocolEnvelope.Create(ProtocolMessageTypes.GameEvent, epoch, 0, new ProtocolPayload { Data = data }, session);
            return enqueueTelemetry(message);
        }
    }
}
