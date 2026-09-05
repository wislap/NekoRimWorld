using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Neko.RimWorld;

internal static class Program
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    public static int Main()
    {
        try
        {
            ProtocolRoundTrip();
            AuthenticationMatchesCrossLanguageVector();
            StrictSchemaRejectsUnexpectedInput();
            FramingRejectsOversizedInput();
            FramingHonorsAbsoluteDeadline();
            MutualAuthenticationLoopback();
            Console.WriteLine("Bridge smoke tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            BridgeRuntime.Instance.Stop();
        }
    }

    private static void AuthenticationMatchesCrossLanguageVector()
    {
        var epoch = Id('1');
        var clientNonce = new string('a', ProtocolAuthentication.HexLength);
        var serverNonce = new string('b', ProtocolAuthentication.HexLength);
        var serverProof = ProtocolAuthentication.ComputeServerProof(Token, epoch, clientNonce, serverNonce);
        var clientProof = ProtocolAuthentication.ComputeClientProof(Token, epoch, clientNonce, serverNonce);
        Require(
            serverProof ==
            "69826430fc5cebe86d61f08a38749ab5c0d57e3f1459ac21559a98dd7015068e",
            $"server proof differs from the Python hmac test vector: {serverProof}");
        Require(
            clientProof ==
            "7d8c68b95d01f6d08b10b2a06266d867f64729069f058be50979e56a9cec5204",
            $"client proof differs from the Python hmac test vector: {clientProof}");
    }

    private static void StrictSchemaRejectsUnexpectedInput()
    {
        var epoch = Id('1');
        var session = new SessionDescriptor(Id('2'), Id('3'), 10, 0);
        var valid = ProtocolEnvelope.Create(
            ProtocolMessageTypes.QueryRequest,
            epoch,
            1,
            new ProtocolPayload { Query = "colony_digest" },
            session,
            Id('4'));
        var json = Encoding.UTF8.GetString(ProtocolCodec.Serialize(valid));
        var withUnknownField = json.Replace(
            "\"query\":\"colony_digest\"",
            "\"query\":\"colony_digest\",\"unexpected\":true");
        ExpectProtocolError(
            () => ProtocolCodec.Deserialize(Encoding.UTF8.GetBytes(withUnknownField)),
            ProtocolErrorCodes.InvalidPayload,
            "unknown payload field was accepted");

        valid.RequestId = Guid.Parse(valid.RequestId).ToString("D");
        ExpectProtocolError(
            () => ProtocolCodec.Validate(valid),
            ProtocolErrorCodes.InvalidField,
            "non-canonical UUID was accepted");

        var failedWelcome = ProtocolEnvelope.Create(
            ProtocolMessageTypes.Welcome,
            epoch,
            0,
            new ProtocolPayload { Status = ProtocolStatuses.Ok });
        failedWelcome.Payload.Status = ProtocolStatuses.Failed;
        ExpectProtocolError(
            () => ProtocolCodec.Validate(failedWelcome),
            ProtocolErrorCodes.InvalidPayload,
            "failed welcome was accepted");
    }

    private static void ProtocolRoundTrip()
    {
        var epoch = Id('1');
        var session = new SessionDescriptor(Id('2'), Id('3'), 1234, 7);
        var request = ProtocolEnvelope.Create(
            ProtocolMessageTypes.QueryRequest,
            epoch,
            4,
            new ProtocolPayload { Query = "colony_digest" },
            session,
            Id('4'));
        var data = QueryDataDto.FromColonyDigest(new ColonyDigestDto
        {
            GameTick = 1234,
            Paused = true,
            SnapshotSequence = 7,
            HasVisibleThreat = false,
            CurrentMapId = "Map_1",
            MapCount = 1,
            Maps = new[]
            {
                new MapDigestDto
                {
                    Id = "Map_1",
                    Index = 0,
                    Label = "Home",
                    IsCurrent = true,
                    IsPlayerHome = true,
                    HasVisibleThreat = false,
                    FreeColonistCount = 1,
                    FreeColonistsSpawned = new[]
                    {
                        new PawnDigestDto
                        {
                            Id = "Thing_Human1",
                            Label = "Ada",
                            HealthPercent = 0.875,
                            Mood = 0.5,
                        },
                    },
                },
            },
        });
        var response = ProtocolEnvelope.CreateQueryResult(
            request,
            epoch,
            5,
            session,
            ProtocolStatuses.Ok,
            data);

        var jsonBytes = ProtocolCodec.Serialize(response);
        var json = Encoding.UTF8.GetString(jsonBytes);
        Require(json.Contains("\"data\":{"), "query data must be a JSON object");
        Require(json.Contains("\"maps\":["), "maps must be a JSON array");
        Require(!json.Contains("__type") && !json.Contains("KeyValuePair"), "wire JSON leaked .NET types");

        var decoded = ProtocolCodec.Deserialize(jsonBytes);
        var pawn = decoded.Payload.Data!.ColonyDigest!.Maps[0].FreeColonistsSpawned[0];
        Require(pawn.Id == "Thing_Human1" && pawn.HealthPercent == 0.875, "typed round-trip lost pawn data");

        var hello = ProtocolEnvelope.CreateHello(
            epoch,
            0,
            "0.1.0-dev",
            "1.6.4871 rev591",
            new string('a', ProtocolAuthentication.HexLength),
            new[] { "query.colony_digest.v1" });
        Require(!Encoding.UTF8.GetString(ProtocolCodec.Serialize(hello)).Contains(Token), "hello leaked pairing token");

        using var fragmented = new FragmentedReadStream(ProtocolCodec.EncodeFrame(response));
        var framed = ProtocolCodec.ReadFrame(
            fragmented,
            ProtocolCodec.MaxFrameBytes,
            long.MaxValue);
        Require(framed.RequestId == request.RequestId, "fragmented frame lost request correlation");
    }

    private static void FramingRejectsOversizedInput()
    {
        var declared = ProtocolCodec.MaxInboundFrameBytes + 1;
        var header = new[]
        {
            (byte)(declared >> 24),
            (byte)(declared >> 16),
            (byte)(declared >> 8),
            (byte)declared,
        };
        using var stream = new MemoryStream(header, false);
        try
        {
            ProtocolCodec.ReadFrame(stream, ProtocolCodec.MaxInboundFrameBytes, long.MaxValue);
            throw new InvalidOperationException("oversized input frame was accepted");
        }
        catch (ProtocolException exception)
        {
            Require(exception.ErrorCode == ProtocolErrorCodes.FrameTooLarge, "wrong oversized-frame error");
        }
    }

    private static void FramingHonorsAbsoluteDeadline()
    {
        var hello = ProtocolEnvelope.CreateHello(
            Id('1'),
            0,
            "0.1.0-dev",
            "1.6.4871 rev591",
            new string('a', ProtocolAuthentication.HexLength),
            new[] { "query.colony_digest.v1" });
        using var slow = new SlowFragmentedReadStream(ProtocolCodec.EncodeFrame(hello), 20);
        ExpectProtocolError(
            () => ProtocolCodec.ReadFrame(slow, ProtocolCodec.MaxInboundFrameBytes, Deadline(50)),
            ProtocolErrorCodes.ReadDeadlineExceeded,
            "slow frame bypassed its absolute deadline");
    }

    private static void MutualAuthenticationLoopback()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Exception? serverFailure = null;
        var serverDone = new ManualResetEventSlim(false);
        var session = new SessionDescriptor(Id('5'), Id('6'), 99, 0);

        var server = new Thread(() =>
        {
            try
            {
                using var client = listener.AcceptTcpClient();
                using var stream = client.GetStream();
                var hello = Read(stream);
                Require(hello.Type == ProtocolMessageTypes.Hello, "first frame was not hello");
                var clientNonce = hello.Payload.ClientNonce!;
                var serverNonce = new string('b', ProtocolAuthentication.HexLength);
                var serverProof = ProtocolAuthentication.ComputeServerProof(
                    Token,
                    hello.ConnectionEpoch,
                    clientNonce,
                    serverNonce);
                ProtocolCodec.WriteFrame(
                    stream,
                    ProtocolEnvelope.Create(
                        ProtocolMessageTypes.Challenge,
                        hello.ConnectionEpoch,
                        0,
                        new ProtocolPayload
                        {
                            ClientNonce = clientNonce,
                            ServerNonce = serverNonce,
                            Proof = serverProof,
                        }));

                var authenticate = Read(stream);
                var expectedClientProof = ProtocolAuthentication.ComputeClientProof(
                    Token,
                    hello.ConnectionEpoch,
                    clientNonce,
                    serverNonce);
                Require(
                    authenticate.Type == ProtocolMessageTypes.Authenticate &&
                    ProtocolAuthentication.FixedTimeEquals(expectedClientProof, authenticate.Payload.Proof),
                    "client authentication proof was invalid");

                ProtocolCodec.WriteFrame(
                    stream,
                    ProtocolEnvelope.Create(
                        ProtocolMessageTypes.Welcome,
                        hello.ConnectionEpoch,
                        1,
                        new ProtocolPayload { Status = ProtocolStatuses.Ok }));

                ProtocolEnvelope opened;
                do { opened = Read(stream); }
                while (opened.Type != ProtocolMessageTypes.SessionOpen);
                Require(opened.ColonyId == session.ColonyId, "session_open carried the wrong colony");

                var request = ProtocolEnvelope.Create(
                    ProtocolMessageTypes.QueryRequest,
                    hello.ConnectionEpoch,
                    2,
                    new ProtocolPayload { Query = "colony_digest" },
                    session,
                    Id('7'));
                ProtocolCodec.WriteFrame(stream, request);

                ProtocolEnvelope result;
                do { result = Read(stream); }
                while (result.Type != ProtocolMessageTypes.QueryResult);
                Require(result.RequestId == request.RequestId, "query result correlation failed");
                Require(result.Payload.Status == ProtocolStatuses.Ok, "query result was not ok");
            }
            catch (Exception exception)
            {
                serverFailure = exception;
            }
            finally
            {
                serverDone.Set();
            }
        })
        {
            IsBackground = true,
            Name = "NekoRimWorld.BridgeSmoke.Server",
        };
        server.Start();

        var runtime = BridgeRuntime.Instance;
        runtime.Configure(
            true,
            port,
            Token,
            "0.1.0-dev",
            "1.6.4871 rev591",
            new[] { "query.colony_digest.v1" });
        runtime.AttachSession(session);

        var deadline = DateTime.UtcNow.AddSeconds(8);
        ProtocolEnvelope? query = null;
        while (DateTime.UtcNow < deadline)
        {
            runtime.TouchSession(session.LoadEpoch, session.GameTick, session.SnapshotSequence);
            if (runtime.TryDequeueInbound(out var candidate))
            {
                query = candidate;
                break;
            }
            Thread.Sleep(10);
        }
        Require(query is not null, "runtime did not deliver the loopback query");

        var responseData = QueryDataDto.FromColonyDigest(new ColonyDigestDto
        {
            GameTick = session.GameTick,
            Paused = false,
            SnapshotSequence = session.SnapshotSequence,
            MapCount = 0,
            Maps = Array.Empty<MapDigestDto>(),
        });
        Require(
            runtime.EnqueueControl(ProtocolEnvelope.CreateQueryResult(
                query!,
                query!.ConnectionEpoch,
                0,
                session,
                ProtocolStatuses.Ok,
                responseData)),
            "runtime rejected the current query result");

        Require(serverDone.Wait(TimeSpan.FromSeconds(8)), "loopback server did not finish");
        listener.Stop();
        runtime.Stop();
        if (serverFailure is not null) throw new InvalidOperationException("loopback server failed", serverFailure);
    }

    private static ProtocolEnvelope Read(Stream stream) =>
        ProtocolCodec.ReadFrame(stream, ProtocolCodec.MaxFrameBytes, Deadline(5_000));

    private static long Deadline(int milliseconds) =>
        Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (milliseconds / 1000.0));

    private static string Id(char value) => new string(value, 32);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ExpectProtocolError(Action action, string errorCode, string failureMessage)
    {
        try
        {
            action();
            throw new InvalidOperationException(failureMessage);
        }
        catch (ProtocolException exception)
        {
            Require(exception.ErrorCode == errorCode, $"{failureMessage}: got {exception.ErrorCode}");
        }
    }

    private sealed class FragmentedReadStream : MemoryStream
    {
        public FragmentedReadStream(byte[] data) : base(data, false) { }

        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(1, count));
    }

    private sealed class SlowFragmentedReadStream : MemoryStream
    {
        private readonly int delayMilliseconds;

        public SlowFragmentedReadStream(byte[] data, int delayMilliseconds) : base(data, false) =>
            this.delayMilliseconds = delayMilliseconds;

        public override int Read(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(delayMilliseconds);
            return base.Read(buffer, offset, Math.Min(1, count));
        }
    }
}
