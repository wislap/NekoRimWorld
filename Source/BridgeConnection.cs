using System;
using System.Net.Sockets;
namespace Neko.RimWorld;
/// <summary>
/// The worker's live TCP client and its connection epoch. <see cref="Gate"/> also
/// guards state that resets with the epoch (the event channel).
/// </summary>
internal sealed class BridgeConnection
{
    private TcpClient? activeClient;
    private long activeClientGeneration;
    private string epoch = string.Empty;
    public object Gate { get; } = new();
    public string Epoch
    {
        get
        {
            lock (Gate)
            {
                return epoch;
            }
        }
    }
    /// <summary>Publishes the client only while <paramref name="isActiveWorker"/> holds under the gate.</summary>
    public bool SetActiveClient(long generation, TcpClient client, Func<long, bool> isActiveWorker)
    {
        lock (Gate)
        {
            if (!isActiveWorker(generation))
            {
                return false;
            }
            activeClient = client;
            activeClientGeneration = generation;
            return true;
        }
    }
    /// <summary>Publishes a new epoch for the generation's client, if that client is still active.</summary>
    public bool OpenEpoch(long generation, string connectionEpoch)
    {
        lock (Gate)
        {
            if (activeClientGeneration != generation)
            {
                return false;
            }
            epoch = connectionEpoch;
            return true;
        }
    }
    public void CloseActiveClient(long? generation = null)
    {
        TcpClient? client;
        lock (Gate)
        {
            if (generation.HasValue && activeClientGeneration != generation.Value)
            {
                return;
            }
            client = activeClient;
            activeClient = null;
            activeClientGeneration = 0;
        }
        try
        {
            client?.Close();
        }
        catch
        {
            // Closing is best-effort and is also how Stop interrupts I/O.
        }
    }
    public void Clear(long? generation = null)
    {
        lock (Gate)
        {
            if (generation.HasValue && activeClientGeneration != 0 && activeClientGeneration != generation.Value)
            {
                return;
            }
            epoch = string.Empty;
        }
    }
}
