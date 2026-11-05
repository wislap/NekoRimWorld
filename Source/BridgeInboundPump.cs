using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
namespace Neko.RimWorld;
/// <summary>
/// Reads authenticated frames from the server and routes them by type: requests
/// are admitted to the game-bound queue or answered with a rejection on the wire.
/// </summary>
internal sealed class BridgeInboundPump
{
    private const int InboundFrameDeadlineMilliseconds = 2_000;
    private const int MaxFramesPerPump = 32;
    private const string SetPausedMode = "set_paused";
    private readonly SessionLease lease;
    private readonly BridgeMessageQueue inbound;
    private readonly BridgeLog log;
    public BridgeInboundPump(SessionLease lease, BridgeMessageQueue inbound, BridgeLog log)
    {
        this.lease = lease;
        this.inbound = inbound;
        this.log = log;
    }
    public void Pump(TcpClient client, NetworkStream stream, string epoch, ref long lastSequence,
                     ref long sendSequence, ref long lastAuthenticatedInbound)
    {
        Socket socket = client.Client;
        for (int received = 0; received < MaxFramesPerPump && socket.Poll(0, SelectMode.SelectRead); received++)
        {
            if (socket.Available == 0)
            {
                throw new EndOfStreamException("Remote bridge closed.");
            }
            long peerDeadline = lastAuthenticatedInbound + BridgeClock.ToStopwatchTicks(BridgeTransport.PeerHeartbeatTimeoutMilliseconds);
            long frameDeadline = Math.Min(peerDeadline, BridgeClock.After(InboundFrameDeadlineMilliseconds));
            ProtocolEnvelope message =
                ProtocolCodec.ReadFrame(stream, ProtocolCodec.MaxInboundFrameBytes, frameDeadline);
            BridgeWire.ValidateIncoming(message, epoch, ref lastSequence);
            switch (message.Type)
            {
                case ProtocolMessageTypes.Heartbeat:
                    break;
                case ProtocolMessageTypes.WriteRequest:
                    AdmitWrite(stream, message, epoch, ref sendSequence);
                    break;
                case ProtocolMessageTypes.PolicyRequest:
                    AdmitPolicy(stream, message, epoch, ref sendSequence);
                    break;
                case ProtocolMessageTypes.QueryRequest:
                    AdmitQuery(message, epoch);
                    break;
                case ProtocolMessageTypes.ProtocolError:
                    log.Warn("Authenticated peer reported a protocol error.");
                    break;
                default:
                    throw new ProtocolException(
                        ProtocolErrorCodes.InvalidEnvelope,
                        $"Message type {message.Type} is not valid in the server-to-mod direction.");
            }
            lastAuthenticatedInbound = Stopwatch.GetTimestamp();
        }
    }
    private void AdmitWrite(NetworkStream stream, ProtocolEnvelope message, string epoch, ref long sendSequence)
    {
        lease.Read(Stopwatch.GetTimestamp(), out var writeSession, out _, out bool writeStale);
        var responseSession = ResponseSession(writeSession, message);
        // Pausing is not a native write, so it is admitted before the writer is ready.
        bool requiresWriter = message.Payload.Mode != SetPausedMode;
        if (writeSession == null || writeStale || message.ColonyId != responseSession.ColonyId || message.LoadEpoch != responseSession.LoadEpoch || (requiresWriter && !lease.WriterReady)) {
            BridgeWire.Write(stream, ColonyWriteContract.UnavailableResult(message, responseSession, writeSession == null || writeStale), epoch, ref sendSequence);
            return;
        }
        if (!lease.TryEnqueue(message, epoch, inbound, out bool queueFull, requiresWriter)) {
            BridgeWire.Write(stream, RequestRejection.QueueRefused(message, responseSession, queueFull), epoch, ref sendSequence);
        }
    }
    private void AdmitPolicy(NetworkStream stream, ProtocolEnvelope message, string epoch, ref long sendSequence)
    {
        lease.Read(Stopwatch.GetTimestamp(), out var policySession, out _, out bool policyStale);
        bool queueFull = false;
        if (policySession == null || policyStale || !lease.TryEnqueue(message, epoch, inbound, out queueFull)) {
            BridgeWire.Write(stream, RequestRejection.QueueRefused(message, ResponseSession(policySession, message), queueFull), epoch, ref sendSequence);
        }
    }
    private void AdmitQuery(ProtocolEnvelope message, string epoch)
    {
        if (!lease.TryEnqueue(message, epoch, inbound, out bool queueFull) && queueFull)
        {
            log.Warn("Inbound queue is full; request dropped.");
        }
    }
    private static SessionDescriptor ResponseSession(SessionDescriptor? current, ProtocolEnvelope request) =>
        current ?? new SessionDescriptor(request.ColonyId, request.LoadEpoch, request.GameTick, request.SnapshotSequence);
}
