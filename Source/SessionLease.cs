using System;
using System.Diagnostics;
namespace Neko.RimWorld;
/// <summary>
/// The loaded-game identity and its five-second game-thread lease. Every
/// generation change invalidates the queues under the same lock, so producers
/// that validate and enqueue under this lock cannot lose a new-session item.
/// </summary>
internal sealed class SessionLease
{
    private readonly object gate = new();
    private readonly BridgeQueues queues;
    private SessionDescriptor? session;
    private bool nativeWriterReady;
    private long generation;
    private long lastTouch;
    private bool stale;
    public SessionLease(BridgeQueues queues) => this.queues = queues;
    public long Generation
    {
        get
        {
            lock (gate)
            {
                return generation;
            }
        }
    }
    public bool WriterReady
    {
        get
        {
            lock (gate)
            {
                return nativeWriterReady;
            }
        }
    }
    /// <summary>True while a session is attached, not stale, and its lease has not expired.</summary>
    public bool IsLive
    {
        get
        {
            lock (gate)
            {
                return session != null && !stale &&
                       BridgeClock.ElapsedMilliseconds(lastTouch, Stopwatch.GetTimestamp()) < BridgeRuntime.SessionLeaseMilliseconds;
            }
        }
    }
    public void Attach(SessionDescriptor descriptor, bool writerReady)
    {
        lock (gate)
        {
            // Clear while the old identity is still published.
            queues.Clear();
            session = Copy(descriptor);
            nativeWriterReady = writerReady;
            lastTouch = Stopwatch.GetTimestamp();
            stale = false;
            generation++;
        }
    }
    /// <summary>Renews the lease; returns false for another load, and reports whether a stale lease revived.</summary>
    public bool Touch(string loadEpoch, long? gameTick, long? snapshotSequence, out bool revived)
    {
        revived = false;
        lock (gate)
        {
            if (session is null || !string.Equals(session.LoadEpoch, loadEpoch, StringComparison.Ordinal))
            {
                return false;
            }
            lastTouch = Stopwatch.GetTimestamp();
            if (gameTick.HasValue)
            {
                session.GameTick = gameTick.Value;
            }
            if (snapshotSequence.HasValue)
            {
                session.SnapshotSequence = snapshotSequence.Value;
            }
            if (stale)
            {
                stale = false;
                generation++;
                revived = true;
            }
            return true;
        }
    }
    /// <summary>Snapshots the session, first marking it stale when its lease has expired.</summary>
    public void Read(long now, out SessionDescriptor? descriptor, out long currentGeneration, out bool isStale)
    {
        lock (gate)
        {
            if (session is not null && !stale &&
                BridgeClock.ElapsedMilliseconds(lastTouch, now) >= BridgeRuntime.SessionLeaseMilliseconds)
            {
                stale = true;
                queues.Clear();
                generation++;
            }
            descriptor = session is null ? null : Copy(session);
            currentGeneration = generation;
            isStale = stale;
        }
    }
    /// <summary>Drops every queued item and writer readiness when the bridge stops.</summary>
    public void Invalidate()
    {
        lock (gate)
        {
            // Stop and attach must serialize queue invalidation. Otherwise a
            // response for a newly attached session can be cleared by Stop.
            queues.Clear();
            nativeWriterReady = false;
        }
    }
    /// <summary>Clears the queues only if no new session was attached since <paramref name="observedGeneration"/>.</summary>
    public void ClearQueuesIfGeneration(long observedGeneration)
    {
        lock (gate)
        {
            // Do not clear a response queued after a new session was
            // attached while an older connection was unwinding.
            if (generation == observedGeneration)
            {
                queues.Clear();
            }
        }
    }
    public bool IsCurrent(ProtocolEnvelope message)
    {
        if (string.IsNullOrEmpty(message.LoadEpoch))
        {
            return true;
        }
        lock (gate)
        {
            return session is not null && !stale &&
                   string.Equals(message.ColonyId, session.ColonyId, StringComparison.Ordinal) &&
                   string.Equals(message.LoadEpoch, session.LoadEpoch, StringComparison.Ordinal);
        }
    }
    public bool IsCurrent(ProtocolEnvelope message, string epoch)
    {
        return string.Equals(message.ConnectionEpoch, epoch, StringComparison.Ordinal) && IsCurrent(message);
    }
    /// <summary>Validates the message against this connection and session, then enqueues it atomically.</summary>
    public bool TryEnqueue(ProtocolEnvelope message, string epoch, BridgeMessageQueue queue, out bool queueFull,
                           bool requireWriter = false)
    {
        queueFull = false;
        if (!string.Equals(message.ConnectionEpoch, epoch, StringComparison.Ordinal))
        {
            return false;
        }
        lock (gate)
        {
            if (!IsCurrent(message) || (requireWriter && !nativeWriterReady))
            {
                return false;
            }
            bool enqueued = queue.TryEnqueue(message);
            queueFull = !enqueued;
            return enqueued;
        }
    }
    private static SessionDescriptor Copy(SessionDescriptor value)
    {
        return new SessionDescriptor(value.ColonyId, value.LoadEpoch, value.GameTick, value.SnapshotSequence);
    }
}
