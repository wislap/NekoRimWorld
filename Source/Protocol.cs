using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Text;
namespace Neko.RimWorld;
public static class ProtocolMessageTypes
{
    public const string Hello = "hello";
    public const string Challenge = "challenge";
    public const string Authenticate = "authenticate";
    public const string Welcome = "welcome";
    public const string SessionOpen = "session_open";
    public const string SessionStale = "session_stale";
    public const string Heartbeat = "heartbeat";
    public const string QueryRequest = "query_request";
    public const string QueryResult = "query_result";
    public const string ProtocolError = "protocol_error";
}
public static class ProtocolStatuses
{
    public const string Ok = "ok";
    public const string NotFound = "not_found";
    public const string StaleSession = "stale_session";
    public const string Unsupported = "unsupported";
    public const string Busy = "busy";
    public const string Partial = "partial";
    public const string Failed = "failed";
}
public static class ProtocolErrorCodes
{
    public const string InvalidEnvelope = "invalid_envelope";
    public const string InvalidField = "invalid_field";
    public const string InvalidPayload = "invalid_payload";
    public const string UnsupportedVersion = "unsupported_version";
    public const string EmptyFrame = "empty_frame";
    public const string FrameTooLarge = "frame_too_large";
    public const string UnexpectedEndOfStream = "unexpected_end_of_stream";
    public const string ReadDeadlineExceeded = "read_deadline_exceeded";
    public const string InvalidJson = "invalid_json";
}
public sealed class ProtocolException : Exception
{
    public ProtocolException(string errorCode, string message) : base(message) => ErrorCode = errorCode;
    public ProtocolException(string errorCode, string message, Exception innerException)
        : base(message, innerException) => ErrorCode = errorCode;
    public string ErrorCode { get; }
}

public static class ProtocolAuthentication
{
    public const string Domain = "neko-rimworld-bridge/v2";
    public const int HexLength = 64;
    public static string GenerateNonce()
    {
        var bytes = new byte[32];
        try
        {
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return ToHex(bytes);
        }
        finally { Array.Clear(bytes, 0, bytes.Length); }
    }
    public static string ComputeServerProof(
        string token, string connectionEpoch, string clientNonce, string serverNonce) =>
        ComputeProof(token, "server", connectionEpoch, clientNonce, serverNonce);
    public static string ComputeClientProof(
        string token, string connectionEpoch, string clientNonce, string serverNonce) =>
        ComputeProof(token, "client", connectionEpoch, clientNonce, serverNonce);
    public static bool FixedTimeEquals(string? expected, string? actual)
    {
        if (expected is null || actual is null ||
            expected.Length != HexLength || actual.Length != HexLength)
            return false;
        var difference = 0;
        for (var index = 0; index < HexLength; index++)
            difference |= expected[index] ^ actual[index];
        return difference == 0;
    }
    internal static bool IsCanonicalHex(string? value) =>
        value is not null && value.Length == HexLength && IsLowerHex(value);
    private static string ComputeProof(
        string token,
        string role,
        string connectionEpoch,
        string clientNonce,
        string serverNonce)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 4096)
            throw new ArgumentException("Token must contain 1-4096 characters.", nameof(token));
        if (!IsCanonicalHex(clientNonce)) throw new ArgumentException("Invalid client nonce.", nameof(clientNonce));
        if (!IsCanonicalHex(serverNonce)) throw new ArgumentException("Invalid server nonce.", nameof(serverNonce));
        if (!Guid.TryParseExact(connectionEpoch, "N", out _) || connectionEpoch != connectionEpoch.ToLowerInvariant())
            throw new ArgumentException("connectionEpoch must be a lowercase N-format UUID.", nameof(connectionEpoch));
        var key = Encoding.UTF8.GetBytes(token);
        var transcript = Encoding.UTF8.GetBytes(
            $"{Domain}/{role}\n{connectionEpoch}\n{clientNonce}\n{serverNonce}");
        try
        {
            using var hmac = new HMACSHA256(key);
            var hash = hmac.ComputeHash(transcript);
            try { return ToHex(hash); }
            finally { Array.Clear(hash, 0, hash.Length); }
        }
        finally
        {
            Array.Clear(key, 0, key.Length);
            Array.Clear(transcript, 0, transcript.Length);
        }
    }
    private static bool IsLowerHex(string value)
    {
        foreach (var character in value)
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                return false;
        return true;
    }
    private static string ToHex(byte[] value)
    {
        const string digits = "0123456789abcdef";
        var result = new char[value.Length * 2];
        for (var index = 0; index < value.Length; index++)
        {
            result[index * 2] = digits[value[index] >> 4];
            result[index * 2 + 1] = digits[value[index] & 0x0f];
        }
        return new string(result);
    }
}
/// <summary>Pure session data; this type must never contain Verse objects.</summary>

public sealed class SessionDescriptor
{
    public SessionDescriptor(string colonyId, string loadEpoch, long gameTick, long snapshotSequence)
    {
        ColonyId = colonyId ?? throw new ArgumentNullException(nameof(colonyId));
        LoadEpoch = loadEpoch ?? throw new ArgumentNullException(nameof(loadEpoch));
        GameTick = gameTick;
        SnapshotSequence = snapshotSequence;
    }
    public string ColonyId { get; set; }
    public string LoadEpoch { get; set; }
    public long GameTick { get; set; }
    public long SnapshotSequence { get; set; }
}
[DataContract]
public sealed class ProtocolPayload
{
    [DataMember(Name = "mod_version", Order = 0, EmitDefaultValue = false)] public string? ModVersion { get; set; }
    [DataMember(Name = "game_version", Order = 1, EmitDefaultValue = false)] public string? GameVersion { get; set; }
    [DataMember(Name = "capabilities", Order = 2, EmitDefaultValue = false)] public string[]? Capabilities { get; set; }
    [DataMember(Name = "client_nonce", Order = 3, EmitDefaultValue = false)] public string? ClientNonce { get; set; }
    [DataMember(Name = "server_nonce", Order = 4, EmitDefaultValue = false)] public string? ServerNonce { get; set; }
    [DataMember(Name = "proof", Order = 5, EmitDefaultValue = false)] public string? Proof { get; set; }
    [DataMember(Name = "operation", Order = 6, EmitDefaultValue = false)] public string? Operation { get; set; }
    [DataMember(Name = "arguments", Order = 12, EmitDefaultValue = false)] public JObject? Arguments { get; set; }
    [DataMember(Name = "issues", Order = 13, EmitDefaultValue = false)] public QueryIssue[]? Issues { get; set; }
    [DataMember(Name = "execution", Order = 14, EmitDefaultValue = false)] public JObject? Execution { get; set; }
    [DataMember(Name = "status", Order = 7, EmitDefaultValue = false)] public string? Status { get; set; }
    [DataMember(Name = "data", Order = 8, EmitDefaultValue = false)] public JObject? Data { get; set; }
    [DataMember(Name = "error_code", Order = 9, EmitDefaultValue = false)] public string? ErrorCode { get; set; }
    [DataMember(Name = "message", Order = 10, EmitDefaultValue = false)] public string? Message { get; set; }
    [DataMember(Name = "reason", Order = 11, EmitDefaultValue = false)] public string? Reason { get; set; }
}
[DataContract]
public sealed class QueryIssue
{
    [DataMember(Name = "code", IsRequired = true)] public string Code { get; set; } = "";
    [DataMember(Name = "path", IsRequired = true)] public JArray Path { get; set; } = new JArray();
    [DataMember(Name = "message", EmitDefaultValue = false)] public string? Message { get; set; }
    [DataMember(Name = "omitted_count", EmitDefaultValue = false)] public int? OmittedCount { get; set; }
}
[DataContract]
public sealed class ProtocolEnvelope
{
    public ProtocolEnvelope()
    {
        Version = ProtocolCodec.Version;
        Type = string.Empty;
        ConnectionEpoch = string.Empty;
        ColonyId = string.Empty;
        LoadEpoch = string.Empty;
        RequestId = string.Empty;
        GameTick = -1;
        SnapshotSequence = -1;
        Payload = new ProtocolPayload();
    }
    [DataMember(Name = "v", Order = 0, IsRequired = true)] public int Version { get; set; }
    [DataMember(Name = "type", Order = 1, IsRequired = true)] public string Type { get; set; }
    [DataMember(Name = "connection_epoch", Order = 2, IsRequired = true)] public string ConnectionEpoch { get; set; }
    [DataMember(Name = "colony_id", Order = 3, IsRequired = true)] public string ColonyId { get; set; }
    [DataMember(Name = "load_epoch", Order = 4, IsRequired = true)] public string LoadEpoch { get; set; }
    [DataMember(Name = "seq", Order = 5, IsRequired = true)] public long Sequence { get; set; }
    [DataMember(Name = "request_id", Order = 6, IsRequired = true)] public string RequestId { get; set; }
    [DataMember(Name = "game_tick", Order = 7, IsRequired = true)] public long GameTick { get; set; }
    [DataMember(Name = "snapshot_sequence", Order = 8, IsRequired = true)] public long SnapshotSequence { get; set; }
    [DataMember(Name = "payload", Order = 9, IsRequired = true)] public ProtocolPayload Payload { get; set; }
    public static ProtocolEnvelope Create(
        string type,
        string connectionEpoch,
        long sequence,
        ProtocolPayload? payload = null,
        SessionDescriptor? session = null,
        string? requestId = null)
    {
        var result = new ProtocolEnvelope
        {
            Type = type,
            ConnectionEpoch = connectionEpoch,
            Sequence = sequence,
            RequestId = requestId ?? string.Empty,
            Payload = payload ?? new ProtocolPayload(),
        };
        if (session is not null)
        {
            result.ColonyId = session.ColonyId;
            result.LoadEpoch = session.LoadEpoch;
            result.GameTick = session.GameTick;
            result.SnapshotSequence = session.SnapshotSequence;
        }
        ProtocolCodec.Validate(result);
        return result;
    }
    public static ProtocolEnvelope CreateHello(
        string connectionEpoch,
        long sequence,
        string modVersion,
        string gameVersion,
        string clientNonce,
        IEnumerable<string> capabilities)
    {
        if (capabilities is null) throw new ArgumentNullException(nameof(capabilities));
        return Create(ProtocolMessageTypes.Hello, connectionEpoch, sequence, new ProtocolPayload
        {
            ModVersion = modVersion,
            GameVersion = gameVersion,
            Capabilities = new List<string>(capabilities).ToArray(),
            ClientNonce = clientNonce,
        });
    }
    public static ProtocolEnvelope CreateAuthenticate(
        string connectionEpoch,
        long sequence,
        string clientNonce,
        string serverNonce,
        string proof) =>
        Create(ProtocolMessageTypes.Authenticate, connectionEpoch, sequence, new ProtocolPayload
        {
            ClientNonce = clientNonce,
            ServerNonce = serverNonce,
            Proof = proof,
        });
    public static ProtocolEnvelope CreateSessionOpen(
        string connectionEpoch, long sequence, SessionDescriptor session) =>
        Create(ProtocolMessageTypes.SessionOpen, connectionEpoch, sequence, session: session);
    public static ProtocolEnvelope CreateHeartbeat(
        string connectionEpoch, long sequence, SessionDescriptor? session = null) =>
        Create(ProtocolMessageTypes.Heartbeat, connectionEpoch, sequence, session: session);
    public static ProtocolEnvelope CreateSessionStale(
        string connectionEpoch, long sequence, SessionDescriptor session, string reason) =>
        Create(
            ProtocolMessageTypes.SessionStale,
            connectionEpoch,
            sequence,
            new ProtocolPayload { Reason = reason },
            session);
    public static ProtocolEnvelope CreateQueryResult(
        ProtocolEnvelope request,
        string connectionEpoch,
        long sequence,
        SessionDescriptor session,
        string status,
        JObject? data = null,
        QueryIssue[]? issues = null,
        string? errorCode = null,
        string? message = null,
        JObject? execution = null)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        return Create(
            ProtocolMessageTypes.QueryResult,
            connectionEpoch,
            sequence,
            new ProtocolPayload { Status = status, Data = data, Issues = issues ?? Array.Empty<QueryIssue>(), Execution = execution, ErrorCode = errorCode, Message = message },
            session,
            request.RequestId);
    }
    public static ProtocolEnvelope CreateProtocolError(
        string connectionEpoch,
        long sequence,
        string errorCode,
        string message,
        ProtocolEnvelope? request = null,
        SessionDescriptor? session = null)
    {
        if (session is null && request is not null && !string.IsNullOrEmpty(request.ColonyId))
        {
            session = new SessionDescriptor(
                request.ColonyId,
                request.LoadEpoch,
                request.GameTick,
                request.SnapshotSequence);
        }
        return Create(
            ProtocolMessageTypes.ProtocolError,
            connectionEpoch,
            sequence,
            new ProtocolPayload { ErrorCode = errorCode, Message = message },
            session,
            request?.RequestId);
    }
}

public static class ProtocolCodec
{
    public const int Version = 2;
    public const int MaxFrameBytes = 1024 * 1024;
    public const int MaxInboundFrameBytes = 64 * 1024;
    private const int HeaderBytes = 4;
    private const int MaxJsonDepth = 32;
    private const int MaxItems = 65_536;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    private const int ModVersionField = 1 << 0, GameVersionField = 1 << 1;
    private const int CapabilitiesField = 1 << 2, ClientNonceField = 1 << 3;
    private const int ServerNonceField = 1 << 4, ProofField = 1 << 5, QueryField = 1 << 6;
    private const int StatusField = 1 << 7, DataField = 1 << 8, ErrorCodeField = 1 << 9;
    private const int MessageField = 1 << 10, ReasonField = 1 << 11, ArgumentsField = 1 << 12, IssuesField = 1 << 13, ExecutionField = 1 << 14;
    public static byte[] Serialize(ProtocolEnvelope envelope)
    {
        Validate(envelope);
        try
        {
            var token = JObject.FromObject(envelope, Serializer());
            ValidateTree(token);
            var bytes = Utf8.GetBytes(token.ToString(Formatting.None));
            if (bytes.Length > MaxFrameBytes) throw TooLarge(bytes.Length);
            return bytes;
        }
        catch (ProtocolException) { throw; }
        catch (Exception exception) when (IsCodecException(exception))
        {
            throw Error(ProtocolErrorCodes.InvalidJson, "Envelope serialization failed.", exception);
        }
    }
    public static ProtocolEnvelope Deserialize(byte[] jsonBytes)
    {
        if (jsonBytes is null) throw new ArgumentNullException(nameof(jsonBytes));
        if (jsonBytes.Length == 0) throw Error(ProtocolErrorCodes.EmptyFrame, "Empty JSON payload.");
        if (jsonBytes.Length > MaxFrameBytes) throw TooLarge(jsonBytes.Length);
        try
        {
            var json = Utf8.GetString(jsonBytes);
            ValidateJsonShape(json);
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, MaxDepth = MaxJsonDepth };
            var tree = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error, CommentHandling = CommentHandling.Load });
            ValidateTree(tree);
            ValidateWireTypes(tree);
            var envelope = tree.ToObject<ProtocolEnvelope>(Serializer())
                ?? throw Error(ProtocolErrorCodes.InvalidEnvelope, "JSON root is not an envelope.");
            Validate(envelope);
            return envelope;
        }
        catch (ProtocolException) { throw; }
        catch (Exception exception) when (IsCodecException(exception))
        {
            throw Error(ProtocolErrorCodes.InvalidJson, "Invalid protocol JSON.", exception);
        }
    }
    public static byte[] EncodeFrame(ProtocolEnvelope envelope)
    {
        var payload = Serialize(envelope);
        var frame = new byte[payload.Length + HeaderBytes];
        var length = (uint)payload.Length;
        frame[0] = (byte)(length >> 24);
        frame[1] = (byte)(length >> 16);
        frame[2] = (byte)(length >> 8);
        frame[3] = (byte)length;
        Buffer.BlockCopy(payload, 0, frame, HeaderBytes, payload.Length);
        return frame;
    }
    public static void WriteFrame(Stream stream, ProtocolEnvelope envelope)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanWrite) throw new ArgumentException("Stream is not writable.", nameof(stream));
        var frame = EncodeFrame(envelope);
        stream.Write(frame, 0, frame.Length);
        stream.Flush();
    }
    public static ProtocolEnvelope ReadFrame(
        Stream stream,
        int maxFrameBytes,
        long deadlineTimestamp)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead) throw new ArgumentException("Stream is not readable.", nameof(stream));
        if (maxFrameBytes < 1 || maxFrameBytes > MaxFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));
        var header = new byte[HeaderBytes];
        ReadExact(stream, header, 0, HeaderBytes, deadlineTimestamp);
        var length = ((uint)header[0] << 24) | ((uint)header[1] << 16) |
                     ((uint)header[2] << 8) | header[3];
        if (length == 0) throw Error(ProtocolErrorCodes.EmptyFrame, "Zero-length frame.");
        if (length > maxFrameBytes) throw TooLarge(length, maxFrameBytes);
        CheckDeadline(deadlineTimestamp);
        var payload = new byte[(int)length];
        ReadExact(stream, payload, 0, payload.Length, deadlineTimestamp);
        var envelope = Deserialize(payload);
        CheckDeadline(deadlineTimestamp);
        return envelope;
    }
    public static void ReadExact(
        Stream stream,
        byte[] buffer,
        int offset,
        int count,
        long deadlineTimestamp)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(offset));
        while (count != 0)
        {
            CheckDeadline(deadlineTimestamp);
            var read = stream.Read(buffer, offset, count);
            CheckDeadline(deadlineTimestamp);
            if (read <= 0)
                throw Error(
                    ProtocolErrorCodes.UnexpectedEndOfStream,
                    $"Stream ended with {count} bytes remaining.",
                    new EndOfStreamException());
            offset += read;
            count -= read;
        }
    }
    public static void Validate(ProtocolEnvelope envelope)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));
        if (envelope.Version != Version)
            throw Error(ProtocolErrorCodes.UnsupportedVersion, $"Expected protocol v{Version}.");
        if (!IsKnownMessageType(envelope.Type))
            throw Field("type", "is not a protocol v2 message type");
        ValidateGuid(envelope.ConnectionEpoch, "connection_epoch", true);
        ValidateGuid(envelope.ColonyId, "colony_id", false);
        ValidateGuid(envelope.LoadEpoch, "load_epoch", false);
        ValidateGuid(envelope.RequestId, "request_id", false);
        if (envelope.Sequence < 0) throw Field("seq", "must be non-negative");
        if (envelope.GameTick < -1) throw Field("game_tick", "must be -1 or greater");
        if (envelope.SnapshotSequence < -1) throw Field("snapshot_sequence", "must be -1 or greater");
        var hasSession = envelope.ColonyId.Length != 0;
        if (hasSession != (envelope.LoadEpoch.Length != 0))
            throw Field("load_epoch", "must be present exactly when colony_id is present");
        if (NeedsSession(envelope.Type) && !hasSession)
            throw Field("colony_id", $"is required for {envelope.Type}");
        if (!AllowsSession(envelope.Type) && hasSession)
            throw Field("colony_id", $"is forbidden for {envelope.Type}");
        if (hasSession && (envelope.GameTick < 0 || envelope.SnapshotSequence < 0))
            throw Field("game_tick", "and snapshot_sequence must be non-negative in a session");
        if (!hasSession && (envelope.GameTick != -1 || envelope.SnapshotSequence != -1))
            throw Field("game_tick", "and snapshot_sequence must be -1 without a session");
        if (NeedsRequestId(envelope.Type) && envelope.RequestId.Length == 0)
            throw Field("request_id", $"is required for {envelope.Type}");
        if (!AllowsRequestId(envelope.Type) && envelope.RequestId.Length != 0)
            throw Field("request_id", $"is forbidden for {envelope.Type}");
        if (envelope.Payload is null) throw Error(ProtocolErrorCodes.InvalidPayload, "payload is required.");
        ValidatePayload(envelope);
    }
    private static void ValidatePayload(ProtocolEnvelope envelope)
    {
        var payload = envelope.Payload;
        var present = PresentFields(payload);
        var allowed = AllowedFields(envelope.Type);
        if ((present & ~allowed) != 0) throw Payload($"Payload contains a field not allowed for {envelope.Type}.");
        switch (envelope.Type)
        {
            case ProtocolMessageTypes.Hello:
                Required(payload.ModVersion, "mod_version", 128);
                Required(payload.GameVersion, "game_version", 128);
                if (payload.Capabilities is null) throw Payload("capabilities is required.");
                ValidateCapabilities(payload.Capabilities);
                CanonicalHex(payload.ClientNonce, "client_nonce");
                break;
            case ProtocolMessageTypes.Challenge:
            case ProtocolMessageTypes.Authenticate:
                CanonicalHex(payload.ClientNonce, "client_nonce");
                CanonicalHex(payload.ServerNonce, "server_nonce");
                CanonicalHex(payload.Proof, "proof");
                break;
            case ProtocolMessageTypes.Welcome:
                if (payload.Status != ProtocolStatuses.Ok) throw Payload("welcome status must be ok.");
                break;
            case ProtocolMessageTypes.SessionStale:
                Required(payload.Reason, "reason", 256);
                break;
            case ProtocolMessageTypes.QueryRequest:
                Required(payload.Operation, "operation", 128);
                if (payload.Arguments is null) throw Payload("arguments must be an object.");
                break;
            case ProtocolMessageTypes.QueryResult:
                ValidateQueryResult(payload, envelope);
                break;
            case ProtocolMessageTypes.ProtocolError:
                Required(payload.ErrorCode, "error_code", 128);
                Required(payload.Message, "message", 2048);
                break;
        }
    }
    private static void ValidateQueryResult(ProtocolPayload payload, ProtocolEnvelope envelope)
    {
        if (!IsKnownStatus(payload.Status)) throw Payload("Unknown query result status.");
        var carriesData = payload.Status == ProtocolStatuses.Ok || payload.Status == ProtocolStatuses.Partial;
        if (carriesData != (payload.Data is not null))
            throw Payload("Only ok and partial query results carry data.");
        if (payload.Status == ProtocolStatuses.Ok && (payload.ErrorCode is not null || payload.Message is not null))
            throw Payload("An ok query result cannot carry an error.");
        if (!carriesData)
        {
            Required(payload.ErrorCode, "error_code", 128);
            Required(payload.Message, "message", 2048);
            if (payload.Issues is null || payload.Issues.Length > 256) throw Payload("issues is required.");
            ValidateIssues(payload.Issues);
            return;
        }
        if (payload.ErrorCode is not null) Required(payload.ErrorCode, "error_code", 128);
        if (payload.Message is not null) Required(payload.Message, "message", 2048);
        if (payload.Issues is null || payload.Issues.Length > 256)
            throw Payload("issues must be an array of at most 256 entries.");
        if ((payload.Status == ProtocolStatuses.Partial) != (payload.Issues.Length > 0))
            throw Payload("partial requires issues; ok forbids issues.");
        ValidateIssues(payload.Issues);
    }
    private static void ValidateIssues(QueryIssue[] issues)
    {
        foreach (var issue in issues)
        {
            if (issue is null) throw Payload("Invalid issue.");
            Required(issue.Code, "issue.code", 128);
            if (issue.Path is null || issue.Path.Count > MaxJsonDepth) throw Payload("Invalid issue.path.");
            foreach (var part in issue.Path)
            {
                if (part.Type == JTokenType.String) Required((string?)part, "path", 256);
                else if (part.Type != JTokenType.Integer || (long)part < 0 || (long)part > int.MaxValue)
                    throw Payload("Invalid issue path segment.");
            }
            if (issue.Message is not null) Required(issue.Message, "issue.message", 512);
            if (issue.OmittedCount.HasValue && issue.OmittedCount <= 0) throw Payload("Invalid omitted_count.");
        }
    }
    private static JsonSerializer Serializer() => JsonSerializer.Create(new JsonSerializerSettings
    {
        MissingMemberHandling = MissingMemberHandling.Error,
        DateParseHandling = DateParseHandling.None,
        TypeNameHandling = TypeNameHandling.None,
        MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
        MaxDepth = MaxJsonDepth,
    });
    private static void ValidateWireTypes(JObject root)
    {
        void Types(JObject obj, string[] fields, JTokenType type)
        {
            foreach (var field in fields)
                if (obj.TryGetValue(field, out var value) && value.Type != type)
                    throw Payload($"{field} has an invalid JSON type.");
        }
        Types(root, new[] { "v", "seq", "game_tick", "snapshot_sequence" }, JTokenType.Integer);
        Types(root, new[] { "type", "connection_epoch", "colony_id", "load_epoch", "request_id" }, JTokenType.String);
        if (root["payload"] is not JObject payload) throw Payload("payload must be an object.");
        Types(payload, new[] { "operation", "status", "error_code", "message", "reason", "mod_version", "game_version", "client_nonce", "server_nonce", "proof" }, JTokenType.String);
        Types(payload, new[] { "arguments", "data", "execution" }, JTokenType.Object);
        Types(payload, new[] { "issues", "capabilities" }, JTokenType.Array);
        if (payload["capabilities"] is JArray capabilities)
            foreach (var item in capabilities) if (item.Type != JTokenType.String) throw Payload("capability must be text.");
        if (payload["issues"] is JArray issues)
            foreach (var item in issues)
            {
                if (item is not JObject issue) throw Payload("issue must be an object.");
                Types(issue, new[] { "code", "message" }, JTokenType.String);
                Types(issue, new[] { "omitted_count" }, JTokenType.Integer);
                Types(issue, new[] { "path" }, JTokenType.Array);
            }
    }
    private static void ValidateTree(JToken root)
    {
        var count = 0;
        void Visit(JToken value, int depth)
        {
            if (++count > MaxItems || depth > MaxJsonDepth) throw Payload("JSON resource limit exceeded.");
            if (value is JObject obj)
            {
                foreach (var property in obj.Properties()) { count++; Visit(property.Value, depth + 1); }
            }
            else if (value is JArray array) foreach (var item in array) Visit(item, depth + 1);
            else if (value.Type == JTokenType.Float)
            {
                var number = (double)value;
                if (double.IsNaN(number) || double.IsInfinity(number)) throw Payload("Non-finite JSON number.");
            }
            else if (value.Type != JTokenType.String && value.Type != JTokenType.Integer &&
                     value.Type != JTokenType.Boolean && value.Type != JTokenType.Null)
                throw Payload("Unsupported JSON value.");
        }
        Visit(root, 1);
        if (count > MaxItems) throw Payload("JSON resource limit exceeded.");
    }
    private static void ValidateJsonShape(string json)
    {
        var index = 0;
        while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
        if (index == json.Length || json[index] != '{')
            throw Error(ProtocolErrorCodes.InvalidJson, "JSON root must be an object.");
        var stack = new char[MaxJsonDepth];
        var size = 0;
        var quoted = false;
        var escaped = false;
        for (; index < json.Length; index++)
        {
            var current = json[index];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (current == '\\') escaped = true;
                else if (current == '"') quoted = false;
                continue;
            }
            if (current == '"') { quoted = true; continue; }
            if (current == '{' || current == '[')
            {
                if (size == stack.Length) throw Error(ProtocolErrorCodes.InvalidJson, "JSON nesting is too deep.");
                stack[size++] = current;
            }
            else if (current == '}' || current == ']')
            {
                if (size == 0 || (current == '}' ? stack[size - 1] != '{' : stack[size - 1] != '['))
                    throw Error(ProtocolErrorCodes.InvalidJson, "Unbalanced JSON delimiters.");
                if (--size == 0)
                {
                    index++;
                    while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
                    if (index != json.Length) throw Error(ProtocolErrorCodes.InvalidJson, "Trailing JSON content.");
                    return;
                }
            }
        }
        throw Error(ProtocolErrorCodes.InvalidJson, "Incomplete JSON envelope.");
    }
    private static bool IsKnownMessageType(string type) =>
        type == ProtocolMessageTypes.Hello || type == ProtocolMessageTypes.Challenge ||
        type == ProtocolMessageTypes.Authenticate || type == ProtocolMessageTypes.Welcome ||
        type == ProtocolMessageTypes.SessionOpen || type == ProtocolMessageTypes.SessionStale ||
        type == ProtocolMessageTypes.Heartbeat || type == ProtocolMessageTypes.QueryRequest ||
        type == ProtocolMessageTypes.QueryResult || type == ProtocolMessageTypes.ProtocolError;
    private static int PresentFields(ProtocolPayload payload)
    {
        var fields = 0;
        if (payload.ModVersion is not null) fields |= ModVersionField;
        if (payload.GameVersion is not null) fields |= GameVersionField;
        if (payload.Capabilities is not null) fields |= CapabilitiesField;
        if (payload.ClientNonce is not null) fields |= ClientNonceField;
        if (payload.ServerNonce is not null) fields |= ServerNonceField;
        if (payload.Proof is not null) fields |= ProofField;
        if (payload.Operation is not null) fields |= QueryField;
        if (payload.Arguments is not null) fields |= ArgumentsField;
        if (payload.Issues is not null) fields |= IssuesField;
        if (payload.Execution is not null) fields |= ExecutionField;
        if (payload.Status is not null) fields |= StatusField;
        if (payload.Data is not null) fields |= DataField;
        if (payload.ErrorCode is not null) fields |= ErrorCodeField;
        if (payload.Message is not null) fields |= MessageField;
        if (payload.Reason is not null) fields |= ReasonField;
        return fields;
    }
    private static int AllowedFields(string type)
    {
        if (type == ProtocolMessageTypes.Hello)
            return ModVersionField | GameVersionField | CapabilitiesField | ClientNonceField;
        if (type == ProtocolMessageTypes.Challenge || type == ProtocolMessageTypes.Authenticate)
            return ClientNonceField | ServerNonceField | ProofField;
        if (type == ProtocolMessageTypes.Welcome) return StatusField;
        if (type == ProtocolMessageTypes.SessionStale) return ReasonField;
        if (type == ProtocolMessageTypes.QueryRequest) return QueryField | ArgumentsField;
        if (type == ProtocolMessageTypes.QueryResult)
            return StatusField | DataField | IssuesField | ExecutionField | ErrorCodeField | MessageField;
        if (type == ProtocolMessageTypes.ProtocolError)
            return ErrorCodeField | MessageField;
        return 0;
    }
    private static bool IsKnownStatus(string? status) =>
        status == ProtocolStatuses.Ok || status == ProtocolStatuses.Partial ||
        status == ProtocolStatuses.NotFound || status == ProtocolStatuses.StaleSession ||
        status == ProtocolStatuses.Unsupported || status == ProtocolStatuses.Busy ||
        status == ProtocolStatuses.Failed;
    private static void ValidateCapabilities(string[] capabilities)
    {
        if (capabilities.Length == 0 || capabilities.Length > 256)
            throw Payload("capabilities must contain 1-256 entries.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            Required(capability, "capability", 128);
            if (!seen.Add(capability)) throw Payload("capabilities cannot contain duplicates.");
        }
    }
    private static void ValidateGuid(string value, string name, bool required)
    {
        if (value is null) throw Field(name, "must not be null");
        if (value.Length == 0)
        {
            if (required) throw Field(name, "is required");
            return;
        }
        if (!Guid.TryParseExact(value, "N", out var parsed) ||
            !string.Equals(value, parsed.ToString("N"), StringComparison.Ordinal))
            throw Field(name, "must be a lowercase N-format UUID");
    }
    private static bool NeedsSession(string type) =>
        type == ProtocolMessageTypes.SessionOpen || type == ProtocolMessageTypes.SessionStale ||
        type == ProtocolMessageTypes.QueryRequest || type == ProtocolMessageTypes.QueryResult;
    private static bool AllowsSession(string type) =>
        NeedsSession(type) || type == ProtocolMessageTypes.Heartbeat ||
        type == ProtocolMessageTypes.ProtocolError;
    private static bool NeedsRequestId(string type) =>
        type == ProtocolMessageTypes.QueryRequest || type == ProtocolMessageTypes.QueryResult;
    private static bool AllowsRequestId(string type) =>
        NeedsRequestId(type) || type == ProtocolMessageTypes.ProtocolError;
    private static void Required(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value!.Length > maxLength)
            throw Payload($"{field} must contain 1-{maxLength} characters.");
    }
    private static void Optional(string? value, string field, int maxLength)
    {
        if (value is not null) Required(value, field, maxLength);
    }
    private static void CanonicalHex(string? value, string field)
    {
        if (!ProtocolAuthentication.IsCanonicalHex(value))
            throw Payload($"{field} must be 64 lowercase hexadecimal characters.");
    }
    private static bool IsCodecException(Exception exception) =>
        exception is JsonException || exception is OverflowException || exception is SerializationException || exception is InvalidDataContractException ||
        exception is DecoderFallbackException || exception is ArgumentException;
    private static ProtocolException Field(string name, string reason) =>
        Error(ProtocolErrorCodes.InvalidField, $"Envelope field '{name}' {reason}.");
    private static ProtocolException Payload(string message) =>
        Error(ProtocolErrorCodes.InvalidPayload, message);
    private static ProtocolException TooLarge(long length, int limit = MaxFrameBytes) =>
        Error(ProtocolErrorCodes.FrameTooLarge, $"Frame is {length} bytes; limit is {limit}.");
    private static void CheckDeadline(long deadlineTimestamp)
    {
        if (deadlineTimestamp != long.MaxValue && Stopwatch.GetTimestamp() >= deadlineTimestamp)
            throw Error(ProtocolErrorCodes.ReadDeadlineExceeded, "Frame read deadline exceeded.");
    }
    private static ProtocolException Error(string code, string message, Exception? inner = null) =>
        inner is null ? new ProtocolException(code, message) : new ProtocolException(code, message, inner);
}
