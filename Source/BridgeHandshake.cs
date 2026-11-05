using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
namespace Neko.RimWorld;
/// <summary>Mutual HMAC authentication: hello, challenge, authenticate, welcome.</summary>
internal static class BridgeHandshake
{
    private const int WelcomeTimeoutMilliseconds = 10_000;
    public static void Authenticate(TcpClient client, NetworkStream stream, BridgeConfig config, string epoch,
                                    ref long sendSequence, ref long receiveSequence)
    {
        long deadline = BridgeClock.After(WelcomeTimeoutMilliseconds);
        string clientNonce = ProtocolAuthentication.GenerateNonce();
        BridgeWire.Write(stream,
              ProtocolEnvelope.CreateHello(epoch, 0, config.ModVersion, config.GameVersion, clientNonce,
                                           config.Capabilities),
              epoch, ref sendSequence);
        ProtocolEnvelope challenge = ReadFrame(client, stream, deadline);
        BridgeWire.ValidateIncoming(challenge, epoch, ref receiveSequence);
        if (!string.Equals(challenge.Type, ProtocolMessageTypes.Challenge, StringComparison.Ordinal) ||
            !string.Equals(challenge.Payload.ClientNonce, clientNonce, StringComparison.Ordinal))
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidEnvelope, "Expected matching challenge.");
        }
        string serverNonce = challenge.Payload.ServerNonce!;
        string expectedServerProof =
            ProtocolAuthentication.ComputeServerProof(config.Token, epoch, clientNonce, serverNonce);
        if (!ProtocolAuthentication.FixedTimeEquals(expectedServerProof, challenge.Payload.Proof))
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidPayload, "Server authentication failed.");
        }
        string clientProof = ProtocolAuthentication.ComputeClientProof(config.Token, epoch, clientNonce, serverNonce);
        BridgeWire.Write(stream, ProtocolEnvelope.CreateAuthenticate(epoch, 0, clientNonce, serverNonce, clientProof), epoch,
              ref sendSequence);
        ProtocolEnvelope welcome = ReadFrame(client, stream, deadline);
        BridgeWire.ValidateIncoming(welcome, epoch, ref receiveSequence);
        if (!string.Equals(welcome.Type, ProtocolMessageTypes.Welcome, StringComparison.Ordinal) ||
            !string.Equals(welcome.Payload.Status, ProtocolStatuses.Ok, StringComparison.Ordinal))
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidEnvelope, "Expected successful welcome.");
        }
    }
    private static ProtocolEnvelope ReadFrame(TcpClient client, NetworkStream stream, long deadline)
    {
        while (!client.Client.Poll(50_000, SelectMode.SelectRead))
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException("Authentication handshake timed out.");
            }
        }
        if (client.Client.Available == 0)
        {
            throw new EndOfStreamException("Remote bridge closed during authentication.");
        }
        return ProtocolCodec.ReadFrame(stream, ProtocolCodec.MaxInboundFrameBytes, deadline);
    }
}
