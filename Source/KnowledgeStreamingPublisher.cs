using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal sealed class KnowledgeEntryQueue : IDisposable
{
    internal const int Capacity = 4;
    internal const long ByteCapacity = 8L * 1024 * 1024;

    private readonly BlockingCollection<string> items = new BlockingCollection<string>(Capacity);
    private readonly object gate = new object();
    private long queuedBytes;

    internal bool TryAdd(string json)
    {
        if (json == null) throw new ArgumentNullException(nameof(json));
        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > ByteCapacity) throw new InvalidDataException("Entry exceeds streaming queue byte limit.");
        lock (gate)
        {
            if (items.IsAddingCompleted || queuedBytes + bytes > ByteCapacity) return false;
            if (!items.TryAdd(json)) return false;
            queuedBytes += bytes;
            return true;
        }
    }

    internal IEnumerable<string> GetConsumingEnumerable(CancellationToken token) =>
        Consume(items.GetConsumingEnumerable(token));

    private IEnumerable<string> Consume(IEnumerable<string> source)
    {
        foreach (var item in source)
        {
            lock (gate) queuedBytes -= Encoding.UTF8.GetByteCount(item);
            yield return item;
        }
    }

    internal void CompleteAdding()
    {
        lock (gate)
        {
            if (!items.IsAddingCompleted) items.CompleteAdding();
        }
    }

    internal void Cancel()
    {
        CompleteAdding();
    }

    public void Dispose() => items.Dispose();
}

// The producer owns Verse DTOs; after TryEnqueueSerialized returns the writer only sees strings.
internal sealed class KnowledgeStreamingPublisher : IDisposable
{
    private readonly KnowledgeEntryQueue entries = new KnowledgeEntryQueue();
    private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
    private readonly Task writer;
    private readonly string directory;
    private readonly object stateLock = new object();
    private string? metadataJson;
    private Exception? failure;
    private int completed;

    internal KnowledgeStreamingPublisher(string directory)
    {
        this.directory = directory;
        writer = Task.Run(WriteFile);
    }

    internal Task Completion => writer;
    internal Exception? Failure => Volatile.Read(ref failure);
    internal bool IsFailed => Failure != null || cancellation.IsCancellationRequested;

    internal bool TryEnqueueSerialized(string json)
    {
        if (json == null) throw new ArgumentNullException(nameof(json));
        if (IsFailed || Volatile.Read(ref completed) != 0) return false;
        return entries.TryAdd(json);
    }

    internal void Complete(KnowledgeSnapshot metadata)
    {
        if (Interlocked.Exchange(ref completed, 1) != 0) return;
        try
        {
            if (metadata.Entries.Count != 0)
                throw new InvalidOperationException("Streaming completion accepts metadata only.");
            KnowledgeFiles.ValidateUtf8(metadata);
            var serialized = SerializeMetadata(metadata);
            lock (stateLock) metadataJson = serialized;
            if (!IsFailed) entries.CompleteAdding();
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    internal void Fail(Exception exception)
    {
        if (Interlocked.CompareExchange(ref failure, exception, null) == null)
        {
            cancellation.Cancel();
            entries.Cancel();
        }
    }

    private void WriteFile()
    {
        var temporary = "";
        var legacyTemporary = "";
        var readyTemporary = "";
        var dataPath = "";
        var published = false;
        try
        {
            Directory.CreateDirectory(directory);
            var id = Guid.NewGuid().ToString("N");
            var filename = "snapshot-" + id + ".json";
            dataPath = Path.Combine(directory, filename);
            temporary = dataPath + ".tmp";
            var utf8 = new UTF8Encoding(false, true);
            var index = new JArray();
            string finalMetadata;
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
            using (var limited = new StreamingLimitStream(file, KnowledgeSnapshot.MaxSnapshotBytes))
            {
                var opening = utf8.GetBytes("{\"entries\":[");
                limited.Write(opening, 0, opening.Length);
                var first = true;
                foreach (var entry in entries.GetConsumingEnumerable(cancellation.Token))
                {
                    if (!first) limited.WriteByte((byte)',');
                    var bytes = utf8.GetBytes(entry);
                    var offset = limited.Position;
                    var record = JObject.Parse(entry);
                    var names = new JArray();
                    var seenNames = new HashSet<string>(StringComparer.Ordinal);
                    Action<string?> addName = value => { if (!string.IsNullOrEmpty(value) && seenNames.Add(value!)) names.Add(value); };
                    addName((string?)record["def_name"]);
                    addName((string?)record["label"]);
                    var descriptions = new List<string> { (string?)record["description"] ?? "" };
                    if (record["facts"] is JArray facts)
                        foreach (var fact in facts)
                        {
                            var name = (string?)fact["name"] ?? "";
                            var leaf = name.Substring(name.LastIndexOf('.') + 1);
                            if (leaf.Equals("label", StringComparison.OrdinalIgnoreCase)) addName((string?)fact["value"]);
                            else if (leaf.Equals("description", StringComparison.OrdinalIgnoreCase)) descriptions.Add((string?)fact["value"] ?? "");
                        }
                    index.Add(new JObject { ["key"] = record["key"], ["names"] = names,
                        ["description"] = string.Join(" ", descriptions), ["offset"] = offset,
                        ["length"] = bytes.Length, ["sha256"] = Digest(bytes) });
                    limited.Write(bytes, 0, bytes.Length);
                    first = false;
                }
                cancellation.Token.ThrowIfCancellationRequested();
                lock (stateLock) finalMetadata = metadataJson ?? throw new InvalidOperationException("Publication completed without metadata.");
                var ending = utf8.GetBytes("]," + finalMetadata.Substring(1, finalMetadata.Length - 2) + "}");
                limited.Write(ending, 0, ending.Length);
                limited.Flush();
                file.Flush(true);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            string dataHash;
            using (var input = File.OpenRead(temporary))
            using (var hash = System.Security.Cryptography.SHA256.Create()) dataHash = Hex(hash.ComputeHash(input));
            File.Move(temporary, dataPath);
            temporary = "";
            var metadata = JObject.Parse(finalMetadata);
            metadata["sha256"] = dataHash;
            var ready = new JObject { ["format"] = "neko.defs.index.v1", ["data_file"] = filename,
                ["data_bytes"] = new FileInfo(dataPath).Length, ["data_sha256"] = dataHash,
                ["snapshot"] = metadata, ["entries"] = index };
            // Old clients can still read current.json. New clients use only the
            // final ready pointer and the immutable payload it names.
            legacyTemporary = Path.Combine(directory, "legacy-" + id + ".tmp");
            File.Copy(dataPath, legacyTemporary);
            KnowledgeFiles.ReplaceTemporary(legacyTemporary, Path.Combine(directory, "current.json"));
            legacyTemporary = "";
            readyTemporary = Path.Combine(directory, "ready-" + id + ".tmp");
            var readyBytes = utf8.GetBytes(ready.ToString(Formatting.None));
            if (readyBytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("Knowledge directory exceeds reader limit.");
            using (var output = new FileStream(readyTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(readyBytes, 0, readyBytes.Length); output.Flush(true); }
            cancellation.Token.ThrowIfCancellationRequested();
            KnowledgeFiles.ReplaceTemporary(readyTemporary, Path.Combine(directory, "ready.json"));
            readyTemporary = "";
            published = true;
        }
        catch (Exception exception)
        {
            if (!(exception is OperationCanceledException && cancellation.IsCancellationRequested)) Fail(exception);
        }
        finally
        {
            foreach (var file in new[] { temporary, legacyTemporary, readyTemporary, published ? "" : dataPath })
                if (!string.IsNullOrEmpty(file)) { try { File.Delete(file); } catch { } }
        }
    }

    private static string Digest(byte[] bytes)
    {
        using (var hash = System.Security.Cryptography.SHA256.Create()) return Hex(hash.ComputeHash(bytes));
    }
    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

    private static string SerializeMetadata(KnowledgeSnapshot metadata)
    {
        var objectValue = JObject.Parse(JsonConvert.SerializeObject(metadata, Formatting.None));
        objectValue.Remove("entries");
        return objectValue.ToString(Formatting.None);
    }

    public void Dispose()
    {
        if (!writer.IsCompleted) throw new InvalidOperationException("Streaming publisher must be disposed after completion.");
        cancellation.Dispose();
        entries.Dispose();
    }

    private sealed class StreamingLimitStream : Stream
    {
        private readonly Stream inner;
        private readonly long limit;
        private long written;
        internal StreamingLimitStream(Stream inner, long limit) { this.inner = inner; this.limit = limit; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > limit - written) throw new InvalidDataException("Snapshot exceeds byte limit; old snapshot retained.");
            inner.Write(buffer, offset, count);
            written += count;
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Flush(); base.Dispose(disposing); }
    }
}
