using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

// Frozen on the game thread after bounding; no Verse object crosses the queue.
internal sealed class KnowledgePublicationEntry
{
    internal readonly byte[] Bytes;
    internal readonly string Key;
    internal readonly string[] Names;
    internal readonly string Description;
    internal KnowledgePublicationEntry(byte[] bytes, string key, string[] names, string description)
    { Bytes = bytes; Key = key; Names = names; Description = description; }

    internal static KnowledgePublicationEntry Capture(KnowledgeEntry entry)
    {
        var serialized = KnowledgeSnapshot.BoundAndSerialize(entry);
        var names = new List<string>();
        Action<string> add = value => { if (!string.IsNullOrEmpty(value) && !names.Contains(value)) names.Add(value); };
        add(entry.DefName); add(entry.Label);
        var descriptions = new List<string> { entry.Description };
        foreach (var fact in entry.Facts) {
            var leaf = fact.Name.Substring(fact.Name.LastIndexOf('.') + 1);
            if (leaf.Equals("label", StringComparison.OrdinalIgnoreCase)) add(fact.Value);
            else if (leaf.Equals("description", StringComparison.OrdinalIgnoreCase)) descriptions.Add(fact.Value);
        }
        return new KnowledgePublicationEntry(new UTF8Encoding(false, true).GetBytes(serialized), entry.Key,
            names.ToArray(), string.Join(" ", descriptions));
    }
}

internal sealed class KnowledgeEntryQueue : IDisposable
{
    internal const int Capacity = 4;
    internal const long ByteCapacity = 8L * 1024 * 1024;

    private readonly BlockingCollection<KnowledgePublicationEntry> items = new BlockingCollection<KnowledgePublicationEntry>(Capacity);
    private readonly object gate = new object();
    private long queuedBytes;

    internal bool TryAdd(KnowledgePublicationEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        var bytes = entry.Bytes.Length;
        if (bytes > ByteCapacity) throw new InvalidDataException("Entry exceeds streaming queue byte limit.");
        lock (gate)
        {
            if (items.IsAddingCompleted || queuedBytes + bytes > ByteCapacity) return false;
            if (!items.TryAdd(entry)) return false;
            queuedBytes += bytes;
            return true;
        }
    }

    internal IEnumerable<KnowledgePublicationEntry> GetConsumingEnumerable(CancellationToken token) =>
        Consume(items.GetConsumingEnumerable(token));

    private IEnumerable<KnowledgePublicationEntry> Consume(IEnumerable<KnowledgePublicationEntry> source)
    {
        foreach (var item in source)
        {
            lock (gate) queuedBytes -= item.Bytes.Length;
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

// The producer owns mutable DTOs; the writer only sees frozen bytes and index fields.
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
    // Counters only: the game thread takes snapshots; the writer never calls Verse.
    private int progressStage;
    private long writtenEntries, writtenBytes, entryProcessingTicks, hashTicks, writeStarted, writeFinished, readyStarted, readyFinished;

    private static double Milliseconds(long start, long end) => start == 0 ? 0 :
        (double)((end == 0 ? Stopwatch.GetTimestamp() : end) - start) * 1000 / Stopwatch.Frequency;

    internal JObject Progress()
    {
        var stage = Volatile.Read(ref progressStage);
        var names = new[] { "starting", "writing", "hashing", "ready", "complete", "failed" };
        return new JObject {
            ["stage"] = names[stage], ["written_entries"] = Interlocked.Read(ref writtenEntries),
            ["written_bytes"] = Interlocked.Read(ref writtenBytes),
            ["entry_processing_elapsed_ms"] = (double)Interlocked.Read(ref entryProcessingTicks) * 1000 / Stopwatch.Frequency,
            ["write_elapsed_ms"] = Milliseconds(Interlocked.Read(ref writeStarted), Interlocked.Read(ref writeFinished)),
            ["hash_mode"] = "streaming_sha256",
            ["hash_elapsed_ms"] = (double)Interlocked.Read(ref hashTicks) * 1000 / Stopwatch.Frequency,
            ["ready_elapsed_ms"] = Milliseconds(Interlocked.Read(ref readyStarted), Interlocked.Read(ref readyFinished))
        };
    }

    internal KnowledgeStreamingPublisher(string directory)
    {
        this.directory = directory;
        writer = Task.Run(WriteFile);
    }

    internal Task Completion => writer;
    internal Exception? Failure => Volatile.Read(ref failure);
    internal bool IsFailed => Failure != null || cancellation.IsCancellationRequested;

    internal bool TryEnqueue(KnowledgePublicationEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (IsFailed || Volatile.Read(ref completed) != 0) return false;
        return entries.TryAdd(entry);
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
        var readyTemporary = "";
        var dataPath = "";
        var published = false;
        try
        {
            Interlocked.Exchange(ref writeStarted, Stopwatch.GetTimestamp());
            Volatile.Write(ref progressStage, 1);
            Directory.CreateDirectory(directory);
            var id = Guid.NewGuid().ToString("N");
            var filename = "snapshot-" + id + ".json";
            dataPath = Path.Combine(directory, filename);
            temporary = dataPath + ".tmp";
            var utf8 = new UTF8Encoding(false, true);
            var index = new JArray();
            string finalMetadata;
            string dataHash;
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
            using (var limited = new StreamingLimitStream(file, KnowledgeSnapshot.MaxSnapshotBytes))
            {
                var opening = utf8.GetBytes("{\"entries\":[");
                limited.Write(opening, 0, opening.Length);
                Interlocked.Exchange(ref writtenBytes, limited.Position);
                var first = true;
                foreach (var entry in entries.GetConsumingEnumerable(cancellation.Token))
                {
                    var entryStarted = Stopwatch.GetTimestamp();
                    try {
                    if (!first) limited.WriteByte((byte)',');
                    var bytes = entry.Bytes;
                    var offset = limited.Position;
                    index.Add(new JObject { ["key"] = entry.Key, ["names"] = new JArray(entry.Names),
                        ["description"] = entry.Description, ["offset"] = offset,
                        ["length"] = bytes.Length, ["sha256"] = Digest(bytes) });
                    limited.Write(bytes, 0, bytes.Length);
                    Interlocked.Exchange(ref writtenBytes, limited.Position);
                    Interlocked.Increment(ref writtenEntries);
                    first = false;
                    } finally {
                        Interlocked.Add(ref entryProcessingTicks, Stopwatch.GetTimestamp() - entryStarted);
                    }
                }
                cancellation.Token.ThrowIfCancellationRequested();
                lock (stateLock) finalMetadata = metadataJson ?? throw new InvalidOperationException("Publication completed without metadata.");
                var ending = utf8.GetBytes("]," + finalMetadata.Substring(1, finalMetadata.Length - 2) + "}");
                limited.Write(ending, 0, ending.Length);
                Interlocked.Exchange(ref writtenBytes, limited.Position);
                dataHash = limited.FinishHash();
                Interlocked.Exchange(ref hashTicks, limited.HashTicks);
                limited.Flush();
                file.Flush(true);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref writeFinished, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref readyStarted, Stopwatch.GetTimestamp());
            Volatile.Write(ref progressStage, 3);
            File.Move(temporary, dataPath);
            temporary = "";
            var metadata = JObject.Parse(finalMetadata);
            metadata["sha256"] = dataHash;
            var ready = new JObject { ["format"] = "neko.defs.index.v1", ["data_file"] = filename,
                ["data_bytes"] = new FileInfo(dataPath).Length, ["data_sha256"] = dataHash,
                ["snapshot"] = metadata, ["entries"] = index };
            readyTemporary = Path.Combine(directory, "ready-" + id + ".tmp");
            var readyBytes = utf8.GetBytes(ready.ToString(Formatting.None));
            if (readyBytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("Knowledge directory exceeds reader limit.");
            using (var output = new FileStream(readyTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(readyBytes, 0, readyBytes.Length); output.Flush(true); }
            cancellation.Token.ThrowIfCancellationRequested();
            KnowledgeFiles.ReplaceTemporary(readyTemporary, Path.Combine(directory, "ready.json"));
            readyTemporary = "";
            published = true;
            Interlocked.Exchange(ref readyFinished, Stopwatch.GetTimestamp());
            Volatile.Write(ref progressStage, 4);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref progressStage, 5);
            if (!(exception is OperationCanceledException && cancellation.IsCancellationRequested)) Fail(exception);
        }
        finally
        {
            foreach (var file in new[] { temporary, readyTemporary, published ? "" : dataPath })
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
        private readonly System.Security.Cryptography.SHA256 hash = System.Security.Cryptography.SHA256.Create();
        internal long HashTicks { get; private set; }
        internal string FinishHash() {
            var start = Stopwatch.GetTimestamp();
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            HashTicks += Stopwatch.GetTimestamp() - start;
            return Hex(hash.Hash!);
        }
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
            var start = Stopwatch.GetTimestamp();
            hash.TransformBlock(buffer, offset, count, buffer, offset);
            HashTicks += Stopwatch.GetTimestamp() - start;
            written += count;
        }
        protected override void Dispose(bool disposing) { if (disposing) { hash.Dispose(); inner.Flush(); } base.Dispose(disposing); }
    }
}
