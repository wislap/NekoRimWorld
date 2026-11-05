using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
namespace Neko.RimWorld;
/// <summary>
/// Process-scoped bridge runtime. It owns no Verse objects: the worker handles only
/// bytes and protocol DTOs, while NekoGameComponent consumes requests on the
/// game thread.
/// </summary>
public sealed class BridgeRuntime
{
    public const string ModVersion = "3.4.0";
    public const string Host = "127.0.0.1";
    public const int DefaultPort = 48919;
    public const int SessionLeaseMilliseconds = 5_000;
    private static readonly Lazy<BridgeRuntime> Singleton =
        new(() => new BridgeRuntime(), LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly object operationGate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly BridgeLog log = new();
    private readonly BridgeSettings settings = new();
    private readonly BridgeQueues queues = new();
    private readonly BridgeStatus status;
    private readonly SessionLease lease;
    private readonly BridgeConnection connection;
    private readonly EventChannel events;
    private readonly BridgeTransport transport;
    private BridgeRuntime()
    {
        status = new BridgeStatus(log);
        lease = new SessionLease(queues);
        connection = new BridgeConnection();
        events = new EventChannel(status, connection, lease, EnqueueTelemetry);
        transport = new BridgeTransport(wake, settings, status, log, queues, lease, connection, events);
    }
    public static BridgeRuntime Instance => Singleton.Value;
    public BridgeConnectionState State => status.State;
    public string ReadyConnectionEpoch {
        get {
            lock (connection.Gate) {
                if (State != BridgeConnectionState.Ready || !lease.IsLive) return string.Empty;
                return connection.Epoch;
            }
        }
    }
    public bool EnableEvents(ProtocolEnvelope request, out bool firstSubscription) => events.Enable(request, out firstSubscription);
    public bool EmitEvent(SessionDescriptor session, JObject data) => events.Emit(session, data);
    public void SetLogger(Action<BridgeLogLevel, string>? value) => log.SetLogger(value);
    /// <summary>Applies settings and starts or stops the worker.</summary>
    public void Configure(bool enabled, int port, string token, string modVersion, string gameVersion,
                          IEnumerable<string> capabilities)
    {
        if (port is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        string normalizedToken = (token ?? string.Empty).Trim();
        if (enabled && normalizedToken.Length < 32)
        {
            throw new ArgumentException("An enabled bridge requires a token of at least 32 characters.", nameof(token));
        }
        string[] capabilityArray = (capabilities ?? Array.Empty<string>())
                                       .Where(value => !string.IsNullOrWhiteSpace(value))
                                       .Select(value => value.Trim())
                                       .Distinct(StringComparer.Ordinal)
                                       .OrderBy(value => value, StringComparer.Ordinal)
                                       .ToArray();
        string normalizedModVersion = string.IsNullOrWhiteSpace(modVersion) ? "unknown" : modVersion;
        string normalizedGameVersion = string.IsNullOrWhiteSpace(gameVersion) ? "unknown" : gameVersion;
        lock (operationGate)
        {
            if (!settings.Apply(enabled, port, normalizedToken, normalizedModVersion, normalizedGameVersion, capabilityArray))
            {
                if (enabled)
                {
                    transport.Start();
                }
                return;
            }
            if (!transport.Stop())
            {
                return;
            }
            if (enabled)
            {
                transport.Start();
            }
            else
            {
                status.Set(BridgeConnectionState.Disabled, "Bridge disabled.");
            }
        }
    }
    public void Start()
    {
        lock (operationGate)
        {
            transport.Start();
        }
    }
    public void Stop()
    {
        lock (operationGate)
        {
            transport.Stop();
        }
    }
    /// <summary>Replaces the active loaded-game identity.</summary>
    public void AttachSession(SessionDescriptor descriptor, bool writerReady = false)
    {
        if (descriptor is null || string.IsNullOrWhiteSpace(descriptor.ColonyId) ||
            string.IsNullOrWhiteSpace(descriptor.LoadEpoch))
        {
            throw new ArgumentException("A session requires colony_id and load_epoch.", nameof(descriptor));
        }
        lock (connection.Gate)
        {
            events.Reset();
            lease.Attach(descriptor, writerReady);
        }
        wake.Set();
    }
    /// <summary>Renews the five-second game-thread lease.</summary>
    public bool TouchSession(string loadEpoch, long? gameTick = null, long? snapshotSequence = null)
    {
        if (!lease.Touch(loadEpoch, gameTick, snapshotSequence, out bool revived))
        {
            return false;
        }
        if (revived)
        {
            wake.Set();
        }
        return true;
    }
    public bool TryDequeueInbound(out ProtocolEnvelope envelope)
    {
        while (queues.Inbound.TryDequeue(out envelope))
        {
            if (IsCurrent(envelope))
            {
                return true;
            }
        }
        envelope = null!;
        return false;
    }
    public bool EnqueueControl(ProtocolEnvelope envelope)
    {
        return Enqueue(envelope, queues.Control, "Control queue is full; response dropped.");
    }
    public bool EnqueueTelemetry(ProtocolEnvelope envelope)
    {
        return Enqueue(envelope, queues.Telemetry, "Telemetry queue is full; event dropped.");
    }
    public bool IsActiveRequest(ProtocolEnvelope message) => State == BridgeConnectionState.Ready && IsCurrent(message);
    private bool Enqueue(ProtocolEnvelope message, BridgeMessageQueue queue, string fullWarning)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }
        string epoch = connection.Epoch;
        if (State != BridgeConnectionState.Ready || string.IsNullOrEmpty(epoch))
        {
            return false;
        }
        if (!lease.TryEnqueue(message, epoch, queue, out bool queueFull))
        {
            if (queueFull)
            {
                log.Warn(fullWarning);
            }
            return false;
        }
        wake.Set();
        return true;
    }
    private bool IsCurrent(ProtocolEnvelope message)
    {
        string epoch = connection.Epoch;
        return !string.IsNullOrEmpty(epoch) && lease.IsCurrent(message, epoch);
    }
}
