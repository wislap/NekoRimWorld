using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
namespace Neko.RimWorld;
/// <summary>Socket connect and the framed, sequenced envelope I/O of one connection.</summary>
internal static class BridgeWire
{
    private const int ConnectTimeoutMilliseconds = 3_000;
    public static void Connect(TcpClient client, int targetPort)
    {
        IAsyncResult operation = client.BeginConnect(BridgeRuntime.Host, targetPort, null, null);
        using WaitHandle waitHandle = operation.AsyncWaitHandle;
        if (!waitHandle.WaitOne(ConnectTimeoutMilliseconds))
        {
            client.Close();
            throw new TimeoutException($"Timed out connecting to {BridgeRuntime.Host}:{targetPort}.");
        }
        client.EndConnect(operation);
    }
    public static void Write(Stream stream, ProtocolEnvelope message, string epoch, ref long sequence)
    {
        // The worker is the sole wire writer, so seq reflects actual send order.
        message.Version = ProtocolCodec.Version;
        message.ConnectionEpoch = epoch;
        message.Sequence = sequence++;
        ProtocolCodec.WriteFrame(stream, message);
    }
    public static void ValidateIncoming(ProtocolEnvelope message, string epoch, ref long lastSequence)
    {
        if (!string.Equals(message.ConnectionEpoch, epoch, StringComparison.Ordinal))
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidEnvelope, "Wrong connection_epoch.");
        }
        if (message.Sequence <= lastSequence)
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidEnvelope, "Message sequence is not increasing.");
        }
        lastSequence = message.Sequence;
    }
}
