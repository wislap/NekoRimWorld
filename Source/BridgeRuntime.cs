using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
namespace Neko.RimWorld;
/// <summary>
/// Process-scoped TCP runtime. It owns no Verse objects: the worker handles only
/// bytes and protocol DTOs, while NekoGameComponent consumes requests on the
/// game thread.
/// </summary>
public sealed class BridgeRuntime
{
    public const string Host = "127.0.0.1";
    public const int DefaultPort = 48919;
    public const int SessionLeaseMilliseconds = 5_000;
    private const int ConnectTimeoutMilliseconds = 3_000;
    private const int WelcomeTimeoutMilliseconds = 10_000;
    private const int InboundFrameDeadlineMilliseconds = 2_000;
    private const int PeerHeartbeatTimeoutMilliseconds = 10_000;
    private const int HeartbeatMilliseconds = 2_000;
    private const int MaxReconnectMilliseconds = 10_000;
    private const int PollMilliseconds = 20;
    private static readonly Lazy<BridgeRuntime> Singleton =
        new(() => new BridgeRuntime(), LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly object operationGate = new();
    private readonly object lifecycleGate = new();
    private readonly object configGate = new();
    private readonly object connectionGate = new();
    private readonly object sessionGate = new();
    private readonly object logGate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly BoundedQueue inbound = new(256);
    private readonly BoundedQueue control = new(256);
    private readonly BoundedQueue telemetry = new(128);
    private readonly Random random = new();
    private Thread? worker;
    private CancellationTokenSource? workerCancellation;
    private long workerGenerationCounter;
    private long activeWorkerGeneration;
    private TcpClient? activeClient;
    private long activeClientGeneration;
    private bool enabled;
    private int port = DefaultPort;
    private string token = string.Empty;
    private string modVersion = "3.2.0";
    private string gameVersion = "unknown";
    private string[] capabilities = Array.Empty<string>();
    private long configGeneration;
    private string connectionEpoch = string.Empty;
    private SessionDescriptor? session;
    private long sessionGeneration;
    private long lastSessionTouch;
    private bool sessionStale;
    private int state = (int)BridgeConnectionState.Stopped;
    private long lastStateLog;
    private long lastWarningLog;
    private Action<BridgeLogLevel, string> logger = DefaultLogger;
    private BridgeRuntime()
    {
    }
    public static BridgeRuntime Instance => Singleton.Value;
    public BridgeConnectionState State => (BridgeConnectionState)Volatile.Read(ref state);
    public void SetLogger(Action<BridgeLogLevel, string>? value)
    {
        lock (logGate)
        {
            logger = value ?? DefaultLogger;
        }
    }
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
            lock (configGate)
            {
                if (this.enabled == enabled && this.port == port &&
                    string.Equals(this.token, normalizedToken, StringComparison.Ordinal) &&
                    string.Equals(this.modVersion, normalizedModVersion, StringComparison.Ordinal) &&
                    string.Equals(this.gameVersion, normalizedGameVersion, StringComparison.Ordinal) &&
                    this.capabilities.SequenceEqual(capabilityArray, StringComparer.Ordinal))
                {
                    if (enabled)
                    {
                        StartCore();
                    }
                    return;
                }
                this.enabled = enabled;
                this.port = port;
                this.token = normalizedToken;
                this.modVersion = normalizedModVersion;
                this.gameVersion = normalizedGameVersion;
                this.capabilities = capabilityArray;
                configGeneration++;
            }
            if (!StopCore())
            {
                return;
            }
            if (enabled)
            {
                StartCore();
            }
            else
            {
                SetState(BridgeConnectionState.Disabled, "Bridge disabled.");
            }
        }
    }
    public void Start()
    {
        lock (operationGate)
        {
            StartCore();
        }
    }
    public void Stop()
    {
        lock (operationGate)
        {
            StopCore();
        }
    }
    private void StartCore()
    {
        lock (configGate)
        {
            if (!enabled)
            {
                SetState(BridgeConnectionState.Disabled, "Bridge is disabled.");
                return;
            }
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
    private bool StopCore()
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
        CloseActiveClient();
        wake.Set();
        if (current is not null && current != Thread.CurrentThread && current.IsAlive)
        {
            current.Join(5_000);
        }
        if (current is { IsAlive: true })
        {
            Log(BridgeLogLevel.Error, "Bridge worker did not stop; refusing to create a second worker.");
            return false;
        }
        lock (sessionGate)
        {
            // Stop and attach must serialize queue invalidation. Otherwise a
            // response for a newly attached session can be cleared by StopCore.
            inbound.Clear();
            control.Clear();
            telemetry.Clear();
        }
        ClearConnection();
        SetState(BridgeConnectionState.Stopped, "Bridge stopped.");
        return true;
    }
    /// <summary>Replaces the active loaded-game identity.</summary>
    public void AttachSession(SessionDescriptor descriptor)
    {
        if (descriptor is null || string.IsNullOrWhiteSpace(descriptor.ColonyId) ||
            string.IsNullOrWhiteSpace(descriptor.LoadEpoch))
        {
            throw new ArgumentException("A session requires colony_id and load_epoch.", nameof(descriptor));
        }
        lock (sessionGate)
        {
            // Clear while the old identity is still published. Producers use the
            // same lock for validate+enqueue, so a new-session item cannot be lost.
            inbound.Clear();
            control.Clear();
            telemetry.Clear();
            session = Copy(descriptor);
            lastSessionTouch = Stopwatch.GetTimestamp();
            sessionStale = false;
            sessionGeneration++;
        }
        wake.Set();
    }
    /// <summary>Renews the five-second game-thread lease.</summary>
    public bool TouchSession(string loadEpoch, long? gameTick = null, long? snapshotSequence = null)
    {
        bool revived = false;
        lock (sessionGate)
        {
            if (session is null || !string.Equals(session.LoadEpoch, loadEpoch, StringComparison.Ordinal))
            {
                return false;
            }
            lastSessionTouch = Stopwatch.GetTimestamp();
            if (gameTick.HasValue)
            {
                session.GameTick = gameTick.Value;
            }
            if (snapshotSequence.HasValue)
            {
                session.SnapshotSequence = snapshotSequence.Value;
            }
            if (sessionStale)
            {
                sessionStale = false;
                sessionGeneration++;
                revived = true;
            }
        }
        if (revived)
        {
            wake.Set();
        }
        return true;
    }
    public bool TryDequeueInbound(out ProtocolEnvelope envelope)
    {
        while (inbound.TryDequeue(out envelope))
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
        return Enqueue(envelope, control, "Control queue is full; response dropped.");
    }
    public bool EnqueueTelemetry(ProtocolEnvelope envelope)
    {
        return Enqueue(envelope, telemetry, "Telemetry queue is full; event dropped.");
    }
    private void WorkerMain(long generation, CancellationToken cancellation)
    {
        int retryDelay = 250;
        try
        {
            while (IsCurrentWorker(generation, cancellation))
            {
                Config config = ReadConfig();
                if (!config.Enabled)
                {
                    wake.WaitOne(250);
                    continue;
                }
                long connectionSessionGeneration;
                lock (sessionGate)
                {
                    connectionSessionGeneration = sessionGeneration;
                }
                try
                {
                    SetWorkerState(generation, BridgeConnectionState.Connecting,
                                   $"Connecting to {Host}:{config.Port}.");
                    RunConnection(config, generation, cancellation);
                    retryDelay = 250;
                }
                catch (Exception exception)
                {
                    if (!IsCurrentWorker(generation, cancellation))
                    {
                        break;
                    }
                    if (State == BridgeConnectionState.Ready)
                    {
                        retryDelay = 250;
                    }
                    Warn($"Bridge connection unavailable ({exception.GetType().Name}: {exception.Message}).");
                    SetWorkerState(generation, BridgeConnectionState.Backoff, "Bridge disconnected; retrying.");
                }
                finally
                {
                    CloseActiveClient(generation);
                    ClearConnection(generation);
                    lock (sessionGate)
                    {
                        // Do not clear a response queued after a new session was
                        // attached while this older connection was unwinding.
                        if (sessionGeneration == connectionSessionGeneration)
                        {
                            inbound.Clear();
                            control.Clear();
                            telemetry.Clear();
                        }
                    }
                }
                if (!IsCurrentWorker(generation, cancellation))
                {
                    break;
                }
                if (ReadConfig().Generation != config.Generation)
                {
                    retryDelay = 250;
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
                Log(BridgeLogLevel.Error, $"Bridge worker stopped unexpectedly: {exception}");
            }
        }
        finally
        {
            CloseActiveClient(generation);
            ClearConnection(generation);
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
                SetState(BridgeConnectionState.Stopped, "Bridge worker stopped.");
            }
        }
    }
    private void RunConnection(Config config, long generation, CancellationToken cancellation)
    {
        using var client =
            new TcpClient(AddressFamily.InterNetwork) { NoDelay = true, SendTimeout = 2_000, ReceiveTimeout = 1_000 };
        if (!SetActiveClient(generation, client))
        {
            return;
        }
        Connect(client, config.Port);
        if (!IsCurrentWorker(generation, cancellation) || ReadConfig().Generation != config.Generation)
        {
            return;
        }
        string epoch = Guid.NewGuid().ToString("N");
        lock (connectionGate)
        {
            if (activeClientGeneration != generation)
            {
                return;
            }
            connectionEpoch = epoch;
        }
        using NetworkStream stream = client.GetStream();
        long sendSequence = 0;
        long receiveSequence = -1;
        long nextHeartbeat = After(HeartbeatMilliseconds);
        long seenSessionGeneration = -1;
        SetWorkerState(generation, BridgeConnectionState.Handshaking, "TCP connected; authenticating peer.");
        AuthenticatePeer(client, stream, config, epoch, ref sendSequence, ref receiveSequence);
        long lastAuthenticatedInbound = Stopwatch.GetTimestamp();
        SetWorkerState(generation, BridgeConnectionState.Ready, "Bridge mutual authentication complete.");
        while (IsCurrentWorker(generation, cancellation) && ReadConfig().Generation == config.Generation)
        {
            PumpIncoming(client, stream, epoch, ref receiveSequence, ref lastAuthenticatedInbound);
            long now = Stopwatch.GetTimestamp();
            if (ElapsedMilliseconds(lastAuthenticatedInbound, now) >= PeerHeartbeatTimeoutMilliseconds)
            {
                throw new TimeoutException("Authenticated peer heartbeat timed out.");
            }
            ReadSession(now, out SessionDescriptor? currentSession, out long currentSessionGeneration, out bool stale);
            if (currentSessionGeneration != seenSessionGeneration)
            {
                if (currentSession is not null)
                {
                    ProtocolEnvelope lifecycleMessage =
                        stale
                            ? ProtocolEnvelope.CreateSessionStale(epoch, 0, currentSession, "main_thread_pump_timeout")
                            : ProtocolEnvelope.CreateSessionOpen(epoch, 0, currentSession);
                    Write(stream, lifecycleMessage, epoch, ref sendSequence);
                }
                seenSessionGeneration = currentSessionGeneration;
            }
            if (now >= nextHeartbeat)
            {
                Write(stream, ProtocolEnvelope.CreateHeartbeat(epoch, 0, stale ? null : currentSession), epoch,
                      ref sendSequence);
                nextHeartbeat = After(HeartbeatMilliseconds);
            }
            Flush(stream, control, 32, epoch, ref sendSequence);
            Flush(stream, telemetry, 8, epoch, ref sendSequence);
            wake.WaitOne(PollMilliseconds);
        }
    }
    private void AuthenticatePeer(TcpClient client, NetworkStream stream, Config config, string epoch,
                                  ref long sendSequence, ref long receiveSequence)
    {
        long deadline = After(WelcomeTimeoutMilliseconds);
        string clientNonce = ProtocolAuthentication.GenerateNonce();
        Write(stream,
              ProtocolEnvelope.CreateHello(epoch, 0, config.ModVersion, config.GameVersion, clientNonce,
                                           config.Capabilities),
              epoch, ref sendSequence);
        ProtocolEnvelope challenge = ReadHandshakeFrame(client, stream, deadline);
        ValidateIncoming(challenge, epoch, ref receiveSequence);
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
        Write(stream, ProtocolEnvelope.CreateAuthenticate(epoch, 0, clientNonce, serverNonce, clientProof), epoch,
              ref sendSequence);
        ProtocolEnvelope welcome = ReadHandshakeFrame(client, stream, deadline);
        ValidateIncoming(welcome, epoch, ref receiveSequence);
        if (!string.Equals(welcome.Type, ProtocolMessageTypes.Welcome, StringComparison.Ordinal) ||
            !string.Equals(welcome.Payload.Status, ProtocolStatuses.Ok, StringComparison.Ordinal))
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidEnvelope, "Expected successful welcome.");
        }
    }
    private static ProtocolEnvelope ReadHandshakeFrame(TcpClient client, NetworkStream stream, long deadline)
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
    private void PumpIncoming(TcpClient client, NetworkStream stream, string epoch, ref long lastSequence,
                              ref long lastAuthenticatedInbound)
    {
        Socket socket = client.Client;
        for (int received = 0; received < 32 && socket.Poll(0, SelectMode.SelectRead); received++)
        {
            if (socket.Available == 0)
            {
                throw new EndOfStreamException("Remote bridge closed.");
            }
            long peerDeadline = lastAuthenticatedInbound + ToStopwatchTicks(PeerHeartbeatTimeoutMilliseconds);
            long frameDeadline = Math.Min(peerDeadline, After(InboundFrameDeadlineMilliseconds));
            ProtocolEnvelope message =
                ProtocolCodec.ReadFrame(stream, ProtocolCodec.MaxInboundFrameBytes, frameDeadline);
            ValidateIncoming(message, epoch, ref lastSequence);
            switch (message.Type)
            {
                case ProtocolMessageTypes.Heartbeat:
                    break;
                case ProtocolMessageTypes.QueryRequest:
                    if (!TryEnqueueCurrent(message, epoch, inbound, out bool queueFull) && queueFull)
                    {
                        Warn("Inbound queue is full; request dropped.");
                    }
                    break;
                case ProtocolMessageTypes.ProtocolError:
                    Warn("Authenticated peer reported a protocol error.");
                    break;
                default:
                    throw new ProtocolException(
                        ProtocolErrorCodes.InvalidEnvelope,
                        $"Message type {message.Type} is not valid in the server-to-mod direction.");
            }
            lastAuthenticatedInbound = Stopwatch.GetTimestamp();
        }
    }
    private static void ValidateIncoming(ProtocolEnvelope message, string epoch, ref long lastSequence)
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
    private void Flush(NetworkStream stream, BoundedQueue queue, int limit, string epoch, ref long sequence)
    {
        for (int sent = 0; sent < limit && queue.TryDequeue(out ProtocolEnvelope message);)
        {
            if (!IsCurrent(message, epoch))
            {
                continue;
            }
            try
            {
                Write(stream, message, epoch, ref sequence);
            }
            catch (ProtocolException exception)
            {
                Warn($"Dropped invalid outbound {message.Type} ({exception.ErrorCode}).");
                if (!string.Equals(message.Type, ProtocolMessageTypes.ProtocolError, StringComparison.Ordinal))
                {
                    try
                    {
                        ProtocolEnvelope error = ProtocolEnvelope.CreateProtocolError(
                            epoch, 0, exception.ErrorCode, "The outbound message was rejected.", message);
                        Write(stream, error, epoch, ref sequence);
                    }
                    catch (ProtocolException fallbackException)
                    {
                        Warn($"Could not encode fallback protocol_error ({fallbackException.ErrorCode}).");
                    }
                }
            }
            sent++;
        }
    }
    private static void Write(Stream stream, ProtocolEnvelope message, string epoch, ref long sequence)
    {
        // The worker is the sole wire writer, so seq reflects actual send order.
        message.Version = ProtocolCodec.Version;
        message.ConnectionEpoch = epoch;
        message.Sequence = sequence++;
        ProtocolCodec.WriteFrame(stream, message);
    }
    private bool Enqueue(ProtocolEnvelope message, BoundedQueue queue, string fullWarning)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }
        string epoch;
        lock (connectionGate)
        {
            epoch = connectionEpoch;
        }
        if (State != BridgeConnectionState.Ready || string.IsNullOrEmpty(epoch))
        {
            return false;
        }
        if (!TryEnqueueCurrent(message, epoch, queue, out bool queueFull))
        {
            if (queueFull)
            {
                Warn(fullWarning);
            }
            return false;
        }
        wake.Set();
        return true;
    }
    private bool TryEnqueueCurrent(ProtocolEnvelope message, string epoch, BoundedQueue queue, out bool queueFull)
    {
        queueFull = false;
        if (!string.Equals(message.ConnectionEpoch, epoch, StringComparison.Ordinal))
        {
            return false;
        }
        lock (sessionGate)
        {
            if (!IsSessionCurrent(message))
            {
                return false;
            }
            bool enqueued = queue.TryEnqueue(message);
            queueFull = !enqueued;
            return enqueued;
        }
    }
    private bool IsCurrent(ProtocolEnvelope message)
    {
        string epoch;
        lock (connectionGate)
        {
            epoch = connectionEpoch;
        }
        return !string.IsNullOrEmpty(epoch) && IsCurrent(message, epoch);
    }
    private bool IsCurrent(ProtocolEnvelope message, string epoch)
    {
        if (!string.Equals(message.ConnectionEpoch, epoch, StringComparison.Ordinal))
        {
            return false;
        }
        if (string.IsNullOrEmpty(message.LoadEpoch))
        {
            return true;
        }
        lock (sessionGate)
        {
            return IsSessionCurrent(message);
        }
    }
    private bool IsSessionCurrent(ProtocolEnvelope message)
    {
        return string.IsNullOrEmpty(message.LoadEpoch) ||
               (session is not null && !sessionStale &&
                string.Equals(message.ColonyId, session.ColonyId, StringComparison.Ordinal) &&
                string.Equals(message.LoadEpoch, session.LoadEpoch, StringComparison.Ordinal));
    }
    private void ReadSession(long now, out SessionDescriptor? descriptor, out long generation, out bool stale)
    {
        lock (sessionGate)
        {
            if (session is not null && !sessionStale &&
                ElapsedMilliseconds(lastSessionTouch, now) >= SessionLeaseMilliseconds)
            {
                sessionStale = true;
                inbound.Clear();
                control.Clear();
                telemetry.Clear();
                sessionGeneration++;
            }
            descriptor = session is null ? null : Copy(session);
            generation = sessionGeneration;
            stale = sessionStale;
        }
    }
    private Config ReadConfig()
    {
        lock (configGate)
        {
            return new Config(enabled, port, token, modVersion, gameVersion, capabilities, configGeneration);
        }
    }
    private bool SetActiveClient(long generation, TcpClient client)
    {
        lock (connectionGate)
        {
            if (Interlocked.Read(ref activeWorkerGeneration) != generation)
            {
                return false;
            }
            activeClient = client;
            activeClientGeneration = generation;
            return true;
        }
    }
    private void CloseActiveClient(long? generation = null)
    {
        TcpClient? client;
        lock (connectionGate)
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
    private void ClearConnection(long? generation = null)
    {
        lock (connectionGate)
        {
            if (generation.HasValue && activeClientGeneration != 0 && activeClientGeneration != generation.Value)
            {
                return;
            }
            connectionEpoch = string.Empty;
        }
    }
    private bool IsCurrentWorker(long generation, CancellationToken cancellation)
    {
        return !cancellation.IsCancellationRequested && Interlocked.Read(ref activeWorkerGeneration) == generation;
    }
    private void SetWorkerState(long generation, BridgeConnectionState value, string message)
    {
        if (Interlocked.Read(ref activeWorkerGeneration) == generation)
        {
            SetState(value, message);
        }
    }
    private void SetState(BridgeConnectionState value, string message)
    {
        if (Interlocked.Exchange(ref state, (int)value) == (int)value)
        {
            return;
        }
        long now = Stopwatch.GetTimestamp();
        lock (logGate)
        {
            if (lastStateLog != 0 && ElapsedMilliseconds(lastStateLog, now) < 2_000)
            {
                return;
            }
            lastStateLog = now;
        }
        Log(BridgeLogLevel.Info, message);
    }
    private void Warn(string message)
    {
        long now = Stopwatch.GetTimestamp();
        lock (logGate)
        {
            if (lastWarningLog != 0 && ElapsedMilliseconds(lastWarningLog, now) < 10_000)
            {
                return;
            }
            lastWarningLog = now;
        }
        Log(BridgeLogLevel.Warning, message);
    }
    private void Log(BridgeLogLevel level, string message)
    {
        Action<BridgeLogLevel, string> sink;
        lock (logGate)
        {
            sink = logger;
        }
        try
        {
            sink(level, message);
        }
        catch
        {
            // Logging must never affect the bridge or game.
        }
    }
    private static void DefaultLogger(BridgeLogLevel level, string message)
    {
        Trace.WriteLine($"[{level}] [N.E.K.O RimWorld] {message}");
    }
    private static void Connect(TcpClient client, int targetPort)
    {
        IAsyncResult operation = client.BeginConnect(Host, targetPort, null, null);
        using WaitHandle waitHandle = operation.AsyncWaitHandle;
        if (!waitHandle.WaitOne(ConnectTimeoutMilliseconds))
        {
            client.Close();
            throw new TimeoutException($"Timed out connecting to {Host}:{targetPort}.");
        }
        client.EndConnect(operation);
    }
    private static SessionDescriptor Copy(SessionDescriptor value)
    {
        return new SessionDescriptor(value.ColonyId, value.LoadEpoch, value.GameTick, value.SnapshotSequence);
    }
    private static long After(int milliseconds)
    {
        return Stopwatch.GetTimestamp() + ToStopwatchTicks(milliseconds);
    }
    private static long ToStopwatchTicks(int milliseconds)
    {
        return (long)(Stopwatch.Frequency * (milliseconds / 1000.0));
    }
    private static double ElapsedMilliseconds(long start, long end)
    {
        return (end - start) * 1000.0 / Stopwatch.Frequency;
    }
    private readonly struct Config
    {
        public Config(bool isEnabled, int configuredPort, string pairingToken, string currentModVersion,
                      string currentGameVersion, string[] advertisedCapabilities, long generation)
        {
            Enabled = isEnabled;
            Port = configuredPort;
            Token = pairingToken;
            ModVersion = currentModVersion;
            GameVersion = currentGameVersion;
            Capabilities = advertisedCapabilities;
            Generation = generation;
        }
        public bool Enabled { get; }
        public int Port { get; }
        public string Token { get; }
        public string ModVersion { get; }
        public string GameVersion { get; }
        public string[] Capabilities { get; }
        public long Generation { get; }
    }
    private sealed class BoundedQueue
    {
        private readonly object gate = new();
        private readonly Queue<ProtocolEnvelope> items;
        public BoundedQueue(int capacity) => (Capacity, items) = (capacity, new Queue<ProtocolEnvelope>(capacity));
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
}
public enum BridgeConnectionState
{
    Stopped,
    Disabled,
    Connecting,
    Handshaking,
    Ready,
    Backoff
}
public enum BridgeLogLevel
{
    Info,
    Warning,
    Error
}
