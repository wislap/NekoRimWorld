using System.Collections.Generic;
namespace Neko.RimWorld;
/// <summary>A bounded, thread-safe FIFO of protocol envelopes.</summary>
internal sealed class BridgeMessageQueue
{
    private readonly object gate = new();
    private readonly Queue<ProtocolEnvelope> items;
    public BridgeMessageQueue(int capacity) => (Capacity, items) = (capacity, new Queue<ProtocolEnvelope>(capacity));
    private int Capacity { get; }
    public bool TryEnqueue(ProtocolEnvelope message)
    {
        lock (gate)
        {
            if (items.Count >= Capacity)
            {
                return false;
            }
            items.Enqueue(message);
            return true;
        }
    }
    public bool TryDequeueAnnounced(SessionDescriptor? announced, out ProtocolEnvelope message) {
        lock (gate) {
            if (items.Count == 0) { message = null!; return false; }
            var next = items.Peek();
            if (!string.IsNullOrEmpty(next.LoadEpoch) && (announced == null || next.ColonyId != announced.ColonyId || next.LoadEpoch != announced.LoadEpoch)) { message = null!; return false; }
            message = items.Dequeue(); return true;
        }
    }
    public bool TryDequeue(out ProtocolEnvelope message)
    {
        lock (gate)
        {
            if (items.Count == 0)
            {
                message = null!;
                return false;
            }
            message = items.Dequeue();
            return true;
        }
    }
    public void Clear()
    {
        lock (gate)
        {
            items.Clear();
        }
    }
}
/// <summary>The bridge's three queues: game-bound requests, responses, and telemetry.</summary>
internal sealed class BridgeQueues
{
    public BridgeMessageQueue Inbound { get; } = new(256);
    public BridgeMessageQueue Control { get; } = new(256);
    public BridgeMessageQueue Telemetry { get; } = new(128);
    public void Clear()
    {
        Inbound.Clear();
        Control.Clear();
        Telemetry.Clear();
    }
}
