using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
namespace Neko.RimWorld;
/// <summary>
/// The single background worker that owns the wire: connect, authenticate,
/// pump inbound frames, flush outbound queues, and reconnect with jittered backoff.
/// </summary>
internal sealed class BridgeTransport
{
    public const int PeerHeartbeatTimeoutMilliseconds = 10_000;
    private const int HeartbeatMilliseconds = 2_000;
    private const int InitialReconnectMilliseconds = 250;
    private const int MaxReconnectMilliseconds = 10_000;
    private const int PollMilliseconds = 20;
    private readonly object lifecycleGate = new();
    private readonly AutoResetEvent wake;
    private readonly Random random = new();
    private readonly BridgeSettings settings;
    private readonly BridgeStatus status;
    private readonly BridgeLog log;
    private readonly BridgeQueues queues;
    private readonly SessionLease lease;
    private readonly BridgeConnection connection;
    private readonly EventChannel events;
    private readonly BridgeInboundPump pump;
    private Thread? worker;
    private CancellationTokenSource? workerCancellation;
    private long workerGenerationCounter;
    private long activeWorkerGeneration;
    public BridgeTransport(AutoResetEvent wake, BridgeSettings settings, BridgeStatus status, BridgeLog log,
                           BridgeQueues queues, SessionLease lease, BridgeConnection connection, EventChannel events)
    {
        this.wake = wake;
        this.settings = settings;
        this.status = status;
        this.log = log;
        this.queues = queues;
        this.lease = lease;
        this.connection = connection;
        this.events = events;
        pump = new BridgeInboundPump(lease, queues.Inbound, log);
    }
    private bool IsActiveWorker(long generation) => Interlocked.Read(ref activeWorkerGeneration) == generation;
    public void Start()
    {
        if (!settings.Enabled)
        {
            status.Set(BridgeConnectionState.Disabled, "Bridge is disabled.");
            return;
        }
        lock (lifecycleGate)
        {
            if (worker is { IsAlive: true })
            {
                return;
            }
            workerCancellation?.Dispose();
            workerCancellation = new CancellationTokenSource();
            long generation = ++workerGenerationCounter;
            Interlocked.Exchange(ref activeWorkerGeneration, generation);
            CancellationToken cancellation = workerCancellation.Token;
            worker = new Thread(() => WorkerMain(generation, cancellation))
            {
                IsBackground = true,
                Name = "NekoRimWorld.Bridge"
            };
            worker.Start();
        }
    }
    /// <summary>Stops the worker; returns false when it would not stop, so no second worker is created.</summary>
    public bool Stop()
    {
        Thread? current;
        CancellationTokenSource? cancellation;
        lock (lifecycleGate)
        {
            current = worker;
            cancellation = workerCancellation;
            Interlocked.Exchange(ref activeWorkerGeneration, 0);
        }
        cancellation?.Cancel();
        connection.CloseActiveClient();
        wake.Set();
        if (current is not null && current != Thread.CurrentThread && current.IsAlive)
        {
            current.Join(5_000);
        }
        if (current is { IsAlive: true })
        {
            log.Log(BridgeLogLevel.Error, "Bridge worker did not stop; refusing to create a second worker.");
            return false;
        }
        lease.Invalidate();
        connection.Clear();
        status.Set(BridgeConnectionState.Stopped, "Bridge stopped.");
        return true;
    }
    private void WorkerMain(long generation, CancellationToken cancellation)
    {
        int retryDelay = InitialReconnectMilliseconds;
        try
        {
            while (IsCurrentWorker(generation, cancellation))
            {
                BridgeConfig config = settings.Read();
                if (!config.Enabled)
                {
                    wake.WaitOne(250);
                    continue;
                }
                long connectionSessionGeneration = lease.Generation;
                try
                {
                    SetWorkerState(generation, BridgeConnectionState.Connecting,
                                   $"Connecting to {BridgeRuntime.Host}:{config.Port}.");
                    RunConnection(config, generation, cancellation);
                    retryDelay = InitialReconnectMilliseconds;
                }
                catch (Exception exception)
                {
                    if (!IsCurrentWorker(generation, cancellation))
                    {
                        break;
                    }
                    if (status.State == BridgeConnectionState.Ready)
                    {
                        retryDelay = InitialReconnectMilliseconds;
                    }
                    log.Warn($"Bridge connection unavailable ({exception.GetType().Name}: {exception.Message}).");
                    SetWorkerState(generation, BridgeConnectionState.Backoff, "Bridge disconnected; retrying.");
                }
                finally
                {
                    connection.CloseActiveClient(generation);
                    connection.Clear(generation);
                    lease.ClearQueuesIfGeneration(connectionSessionGeneration);
                }
                if (!IsCurrentWorker(generation, cancellation))
                {
                    break;
                }
                if (settings.Read().Generation != config.Generation)
                {
                    retryDelay = InitialReconnectMilliseconds;
                    continue;
                }
                int spread = Math.Max(1, retryDelay / 5);
                wake.WaitOne(Math.Max(1, retryDelay + random.Next(-spread, spread + 1)));
                retryDelay = Math.Min(MaxReconnectMilliseconds, retryDelay * 2);
            }
        }
        catch (Exception exception)
        {
            if (IsCurrentWorker(generation, cancellation))
            {
                log.Log(BridgeLogLevel.Error, $"Bridge worker stopped unexpectedly: {exception}");
            }
        }
        finally
        {
            connection.CloseActiveClient(generation);
            connection.Clear(generation);
            CancellationTokenSource? retiredCancellation = null;
            bool wasCurrent = false;
            lock (lifecycleGate)
            {
                if (worker == Thread.CurrentThread)
                {
                    wasCurrent = Interlocked.Read(ref activeWorkerGeneration) == generation;
                    worker = null;
                    retiredCancellation = workerCancellation;
                    workerCancellation = null;
                    if (Interlocked.Read(ref activeWorkerGeneration) == generation)
                    {
                        Interlocked.Exchange(ref activeWorkerGeneration, 0);
                    }
                }
            }
            retiredCancellation?.Dispose();
            if (wasCurrent)
            {
                status.Set(BridgeConnectionState.Stopped, "Bridge worker stopped.");
            }
        }
    }
    private void RunConnection(BridgeConfig config, long generation, CancellationToken cancellation)
    {
        using var client =
            new TcpClient(AddressFamily.InterNetwork) { NoDelay = true, SendTimeout = 2_000, ReceiveTimeout = 1_000 };
        if (!connection.SetActiveClient(generation, client, IsActiveWorker))
        {
            return;
        }
        BridgeWire.Connect(client, config.Port);
        if (!IsCurrentWorker(generation, cancellation) || settings.Read().Generation != config.Generation)
        {
            return;
        }
        string epoch = Guid.NewGuid().ToString("N");
        lock (connection.Gate)
        {
            if (!connection.OpenEpoch(generation, epoch))
            {
                return;
            }
            events.Reset();
        }
        using NetworkStream stream = client.GetStream();
        long sendSequence = 0;
        long receiveSequence = -1;
        long nextHeartbeat = BridgeClock.After(HeartbeatMilliseconds);
        long seenSessionGeneration = -1;
        SetWorkerState(generation, BridgeConnectionState.Handshaking, "TCP connected; authenticating peer.");
        BridgeHandshake.Authenticate(client, stream, config, epoch, ref sendSequence, ref receiveSequence);
        long lastAuthenticatedInbound = Stopwatch.GetTimestamp();
        SetWorkerState(generation, BridgeConnectionState.Ready, "Bridge mutual authentication complete.");
        while (IsCurrentWorker(generation, cancellation) && settings.Read().Generation == config.Generation)
        {
            pump.Pump(client, stream, epoch, ref receiveSequence, ref sendSequence, ref lastAuthenticatedInbound);
            long now = Stopwatch.GetTimestamp();
            if (BridgeClock.ElapsedMilliseconds(lastAuthenticatedInbound, now) >= PeerHeartbeatTimeoutMilliseconds)
            {
                throw new TimeoutException("Authenticated peer heartbeat timed out.");
            }
            lease.Read(now, out SessionDescriptor? currentSession, out long currentSessionGeneration, out bool stale);
            if (currentSessionGeneration != seenSessionGeneration)
            {
                if (currentSession is not null)
                {
                    ProtocolEnvelope lifecycleMessage =
                        stale
                            ? ProtocolEnvelope.CreateSessionStale(epoch, 0, currentSession, "main_thread_pump_timeout")
                            : ProtocolEnvelope.CreateSessionOpen(epoch, 0, currentSession);
                    BridgeWire.Write(stream, lifecycleMessage, epoch, ref sendSequence);
                }
                seenSessionGeneration = currentSessionGeneration;
            }
            if (now >= nextHeartbeat)
            {
                BridgeWire.Write(stream, ProtocolEnvelope.CreateHeartbeat(epoch, 0, stale ? null : currentSession), epoch,
                      ref sendSequence);
                nextHeartbeat = BridgeClock.After(HeartbeatMilliseconds);
            }
            Flush(stream, queues.Control, 32, epoch, currentSession, ref sendSequence);
            Flush(stream, queues.Telemetry, 8, epoch, currentSession, ref sendSequence);
            wake.WaitOne(PollMilliseconds);
        }
    }
    private void Flush(NetworkStream stream, BridgeMessageQueue queue, int limit, string epoch, SessionDescriptor? announcedSession, ref long sequence)
    {
        // Leave messages for a newly attached session queued until its
        // session_open has been sent, even if attachment races this flush.
        for (int sent = 0; sent < limit && queue.TryDequeueAnnounced(announcedSession, out ProtocolEnvelope message);)
        {
            if (!lease.IsCurrent(message, epoch))
            {
                continue;
            }
            try
            {
                BridgeWire.Write(stream, message, epoch, ref sequence);
            }
            catch (ProtocolException exception)
            {
                log.Warn($"Dropped invalid outbound {message.Type} ({exception.ErrorCode}).");
                if (!string.Equals(message.Type, ProtocolMessageTypes.ProtocolError, StringComparison.Ordinal))
                {
                    try
                    {
                        ProtocolEnvelope error = ProtocolEnvelope.CreateProtocolError(
                            epoch, 0, exception.ErrorCode, "The outbound message was rejected.", message);
                        BridgeWire.Write(stream, error, epoch, ref sequence);
                    }
                    catch (ProtocolException fallbackException)
                    {
                        log.Warn($"Could not encode fallback protocol_error ({fallbackException.ErrorCode}).");
                    }
                }
            }
            sent++;
        }
    }
    private bool IsCurrentWorker(long generation, CancellationToken cancellation)
    {
        return !cancellation.IsCancellationRequested && IsActiveWorker(generation);
    }
    private void SetWorkerState(long generation, BridgeConnectionState value, string message)
    {
        if (IsActiveWorker(generation))
        {
            status.Set(value, message);
        }
    }
}
