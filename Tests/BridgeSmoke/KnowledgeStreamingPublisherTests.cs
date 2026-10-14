using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class KnowledgeStreamingPublisherTests
{
    internal static void Run()
    {
        WritesCompleteJsonAndPreservesOrder();
        QueueAppliesBackpressure();
        FailedPublicationPreservesOldFile();
        CompletionClosesBeforeReplacement();
        CancellationNeverPublishes();
        ReplacementPreservesPinnedPayloads();
    }

    private static void WritesCompleteJsonAndPreservesOrder()
    {
        var directory = TemporaryDirectory();
        try
        {
            var metadata = new KnowledgeSnapshot { GameVersion = "fixture", Language = "English" };
            var publisher = new KnowledgeStreamingPublisher(directory);
            Require(publisher.TryEnqueueSerialized(Entry("one")), "first entry was rejected");
            Require(publisher.TryEnqueueSerialized(Entry("two")), "second entry was rejected");
            metadata.Language = "mutated before completion";
            publisher.Complete(metadata);
            metadata.Language = "changed after handoff";
            Require(!publisher.TryEnqueueSerialized(Entry("late")), "closed publisher accepted a late entry");
            publisher.Completion.GetAwaiter().GetResult();
            Require(!publisher.IsFailed, "successful publication was marked failed");
            publisher.Dispose();

            var parsed = JObject.Parse(File.ReadAllText(KnowledgePublicationFixture.PayloadPath(directory)));
            Require((string?)parsed["format"] == "neko.defs.v1", "format metadata missing");
            Require((string?)parsed["language"] == "mutated before completion", "metadata was read before handoff");
            Require(parsed["entries"]!.Count() == 2, "entry count changed during streaming");
            var ready = JObject.Parse(File.ReadAllText(Path.Combine(directory, "ready.json")));
            Require(!File.Exists(Path.Combine(directory, "current.json")), "retired compatibility file was published");
            Require((string?)ready["format"] == "neko.defs.index.v1", "ready index missing");
            var body = File.ReadAllBytes(Path.Combine(directory, ready["data_file"]!.ToString()));
            Require((long)ready["data_bytes"]! == body.Length, "payload size mismatch");
            using (var hash = System.Security.Cryptography.SHA256.Create())
                Require(Hex(hash.ComputeHash(body)) == ready["data_sha256"]!.ToString(), "payload hash mismatch");
            foreach (var row in (JArray)ready["entries"]!)
            {
                var bytes = body.Skip((int)row["offset"]!).Take((int)row["length"]!).ToArray();
                var item = JObject.Parse(Encoding.UTF8.GetString(bytes));
                Require(item["key"]!.ToString() == row["key"]!.ToString(), "offset changed identity");
                using (var hash = System.Security.Cryptography.SHA256.Create())
                    Require(Hex(hash.ComputeHash(bytes)) == row["sha256"]!.ToString(), "entry hash mismatch");
            }
            Require((string?)parsed["entries"]![0]!["key"] == "one" && (string?)parsed["entries"]![1]!["key"] == "two",
                "streamed entries changed order");
        }
        finally { Delete(directory); }
    }

    private static void QueueAppliesBackpressure()
    {
        using (var queue = new KnowledgeEntryQueue())
        {
            for (var index = 0; index < KnowledgeEntryQueue.Capacity; index++)
                Require(queue.TryAdd("x"), "queue rejected an item below capacity");
            Require(!queue.TryAdd("x"), "full queue accepted an item without blocking");
            queue.CompleteAdding();
            Require(queue.GetConsumingEnumerable(CancellationToken.None).Count() == 4, "close dropped queued entries");
            Require(!queue.TryAdd("late"), "closed queue accepted an item");
        }
        using (var queue = new KnowledgeEntryQueue())
        {
            var large = new string('x', (int)KnowledgeEntryQueue.ByteCapacity);
            Require(queue.TryAdd(large), "8 MiB entry rejected");
            Require(!queue.TryAdd("x"), "byte capacity not enforced");
            using (var consumer = queue.GetConsumingEnumerable(CancellationToken.None).GetEnumerator())
            {
                Require(consumer.MoveNext() && consumer.Current == large, "queue changed entry");
                Require(queue.TryAdd("retry"), "drained bytes not released");
                queue.CompleteAdding();
                Require(consumer.MoveNext() && consumer.Current == "retry", "retry order changed");
                Require(!consumer.MoveNext(), "closed queue did not terminate");
            }
        }
    }

    private static void CancellationNeverPublishes()
    {
        var directory = TemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "ready.json");
            File.WriteAllText(path, "old");
            var publisher = new KnowledgeStreamingPublisher(directory);
            Require(publisher.TryEnqueueSerialized(Entry("cancelled")), "entry rejected before cancellation");
            Require(!publisher.Completion.IsCompleted, "writer finished without metadata handoff");
            publisher.Fail(new OperationCanceledException("test cancellation"));
            publisher.Completion.GetAwaiter().GetResult();
            Require(publisher.IsFailed && !publisher.TryEnqueueSerialized(Entry("late")), "cancelled publisher remained open");
            publisher.Dispose();
            Require(File.ReadAllText(path) == "old", "cancelled publication replaced old file");
            Require(Directory.GetFiles(directory, "snapshot-*.json").Length == 0, "cancelled publication leaked an immutable payload");
            Require(Directory.GetFiles(directory, "*.tmp").Length == 0, "cancellation leaked temporary file");
        }
        finally { Delete(directory); }
    }

    private static void FailedPublicationPreservesOldFile()
    {
        var directory = TemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "ready.json");
            File.WriteAllText(path, "{\"old\":true}", new UTF8Encoding(false));
            var publisher = new KnowledgeStreamingPublisher(directory);
            publisher.TryEnqueueSerialized(Entry("new"));
            var metadata = new KnowledgeSnapshot { Language = "\ud800" };
            publisher.Complete(metadata);
            publisher.Completion.GetAwaiter().GetResult();
            Require(publisher.IsFailed, "invalid metadata did not cancel publication");
            publisher.Dispose();
            Require(File.ReadAllText(path) == "{\"old\":true}", "failed publication replaced old file");
            Require(Directory.GetFiles(directory, "snapshot-*.json").Length == 0, "failed publication leaked an immutable payload");
            Require(Directory.GetFiles(directory, "*.tmp").Length == 0, "failed publication leaked temporary file");
        }
        finally { Delete(directory); }
    }

    private static void CompletionClosesBeforeReplacement()
    {
        var directory = TemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "ready.json"), "old");
            var publisher = new KnowledgeStreamingPublisher(directory);
            publisher.TryEnqueueSerialized(Entry("closed"));
            publisher.Complete(new KnowledgeSnapshot());
            publisher.Completion.GetAwaiter().GetResult();
            publisher.Dispose();
            var parsed = JObject.Parse(File.ReadAllText(KnowledgePublicationFixture.PayloadPath(directory)));
            Require((string?)parsed["entries"]![0]!["key"] == "closed", "closed stream was not replaced");
        }
        finally { Delete(directory); }
    }

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

    private static void ReplacementPreservesPinnedPayloads()
    {
        var directory = TemporaryDirectory();
        try
        {
            var snapshot = new KnowledgeSnapshot { Language = "ChineseSimplified" };
            snapshot.Add(new KnowledgeEntry { Key = "Fixture.Def/Pinned", Type = "Fixture.Def", DefName = "Pinned", Label = "旧观察" });
            KnowledgePublicationFixture.Publish(snapshot, directory);
            var oldPath = KnowledgePublicationFixture.PayloadPath(directory);
            var oldBody = File.ReadAllBytes(oldPath);
            using (var pinned = new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                snapshot.Language = "English";
                System.Threading.Tasks.Task.WaitAll(
                    System.Threading.Tasks.Task.Run(() => KnowledgePublicationFixture.Publish(snapshot, directory)),
                    System.Threading.Tasks.Task.Run(() => KnowledgePublicationFixture.Publish(snapshot, directory)));
                var currentPath = KnowledgePublicationFixture.PayloadPath(directory);
                Require(currentPath != oldPath && JObject.Parse(File.ReadAllText(currentPath))["language"]!.ToString() == "English",
                    "concurrent publication did not replace the ready pointer");
                var stillPinned = new byte[oldBody.Length];
                Require(pinned.Read(stillPinned, 0, stillPinned.Length) == oldBody.Length && stillPinned.SequenceEqual(oldBody),
                    "replacement damaged an open immutable payload");
            }
            Require(File.ReadAllBytes(oldPath).SequenceEqual(oldBody), "old payload was rewritten");
            Require(Directory.GetFiles(directory, "snapshot-*.json").Length == 3, "completed immutable payload was removed");
            Require(Directory.GetFiles(directory, "*.tmp").Length == 0, "concurrent publication leaked temporary files");
        }
        finally { Delete(directory); }
    }

    private static string Entry(string key) => "{\"key\":\"" + key + "\",\"type\":\"Fixture\",\"def_name\":\"" + key +
        "\",\"label\":\"\",\"description\":\"\",\"source_mod\":\"\",\"package_id\":\"\",\"facts\":[],\"related\":[],\"diagnostics\":[],\"truncated\":false}";

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "neko-streaming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Knowledge streaming: " + message);
    }
}
