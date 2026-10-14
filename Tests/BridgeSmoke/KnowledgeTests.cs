using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class KnowledgeTests
{
    internal static void Run()
    {
        FieldsAreUsefulAndSafe();
        BoundsAreExplicit();
        AtomicFilesAndMetadata();
    }

    private static KnowledgeEntry Entry() => new KnowledgeEntry
    { Key = "Fixture.Def/Example", Type = "Fixture.Def", DefName = "Example", Label = "测试", Description = "Definition" };

    private static void FieldsAreUsefulAndSafe()
    {
        var fixture = new Fixture();
        fixture.self = fixture;
        var entry = Entry();
        var reader = new KnowledgeFields(value => value is Ref reference
            ? new KnowledgeRelated { Key = reference.type + "/" + reference.name, Label = reference.name } : null,
            type => type == typeof(LiveObject));
        reader.Extract(fixture, entry);
        Require(entry.Facts.Any(f => f.Name == "privateXmlField" && f.Value == "17"), "private data field absent");
        Require(entry.Facts.Any(f => f.Name == "numbers[1]" && f.Value == "2"), "list data absent");
        Require(entry.Facts.Any(f => f.Name == "optional" && f.Value == "null"), "null field lost");
        Require(entry.Related.Count == 2 && entry.Related[0].Key != entry.Related[1].Key, "typed reference identities collided");
        Require(entry.Facts.Any(f => f.Name == "cachedValue" && f.Value == "3"), "field name heuristic lost a value");
        Require(!entry.Facts.Any(f => f.Name == "transient" || f.Name == "Getter"), "runtime/getter data read");
        Require(!entry.Truncated && entry.Diagnostics.Any(d => d.Kind == "expected_omission" && d.Reason.Contains("LiveObject")) &&
            entry.Facts.Any(f => f.Name.EndsWith(".$ref", StringComparison.Ordinal)), "omission reasons or cycle refs lost");
        Require(Fixture.GetterCalls == 0 && EvilSequence.Calls == 0, "arbitrary code executed");
        Require(entry.Facts.Count <= KnowledgeFields.MaxFacts, "fact bound exceeded");
        var next = Entry();
        reader.Extract(new Simple { value = 42 }, next);
        Require(next.Facts.Count == 1 && !next.Truncated, "reader leaked previous entry state");
    }

    private static void BoundsAreExplicit()
    {
        var entry = Entry();
        var reader = new KnowledgeFields(_ => null, _ => false);
        reader.Extract(new Limits(), entry);
        Require(!entry.Truncated, "ordinary collection/depth values were incorrectly truncated");
        Require(KnowledgeSnapshot.Clip("a\ud83d\ude00b", 2) == "a", "surrogate pair split by clipping");
        Require(entry.Facts.All(f => f.Value.Length <= 65536 && f.Name.Length <= 2048), "fact string limit exceeded");
        Require(entry.Facts.Any(f => f.Name == "items[999]"), "complete list data was lost");
        Require(!entry.Diagnostics.Exists(d => d.Kind == "incomplete"), "unexpected incomplete diagnostic");
        entry.Description = new string('界', 8000000);
        for (var i = 0; i < 96; i++) entry.Facts.Add(new KnowledgeFact { Name = "long", Value = new string('界', 768) });
        KnowledgeSnapshot.Bound(entry);
        Require(entry.Truncated, "entry size clipping was not reported");
        var snapshot = new KnowledgeSnapshot();
        Require(snapshot.Add(entry) && !snapshot.Complete && snapshot.Entries[0].Truncated, "truncated entry called complete");
        Require(Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(entry)) <= KnowledgeSnapshot.MaxEntryBytes, "UTF8 entry budget exceeded");
        for (var i = 0; i < 1000; i++) snapshot.Issue("issue " + i + new string('x', 500));
        Require(snapshot.Issues.Count == KnowledgeSnapshot.MaxIssues && snapshot.Issues.All(i => i.Length <= 512), "issue budget exceeded");
        var identity = Entry(); identity.Key = new string('a', 4097);
        ExpectFailure(() => KnowledgeSnapshot.Bound(identity));
        Require(identity.Key.Length == 4097, "identity silently aliased");
        var count = new KnowledgeSnapshot();
        for (var i = 0; i < KnowledgeSnapshot.MaxEntries; i++) count.Entries.Add(Entry());
        Require(!count.Add(Entry()) && !count.Complete, "entry count cap absent");

        // Reuse immutable DTO values so exercising the actual total byte counter is inexpensive in memory.
        var total = new KnowledgeSnapshot();
        var large = Entry();
        for (var i = 0; i < 20; i++) large.Facts.Add(new KnowledgeFact { Name = "value", Value = new string('x', 700) });
        var accepted = 0;
        while (total.Add(large)) accepted++;
        Require(accepted > 1000 && accepted < KnowledgeSnapshot.MaxEntries && !total.Complete &&
            total.Issues.Any(i => i.Contains("byte budget")), "total snapshot byte cap absent");
    }

    private static void AtomicFilesAndMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), "neko-knowledge-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var snapshot = new KnowledgeSnapshot { GameVersion = "1.6.fixture", Language = "ChineseSimplified" };
            snapshot.Mods.Add(new KnowledgeMod { PackageId = "fixture.mod", Name = "Fixture" });
            snapshot.Add(Entry());
            KnowledgePublicationFixture.Publish(snapshot, directory);
            var path = KnowledgePublicationFixture.PayloadPath(directory);
            var firstPath = path;
            var first = File.ReadAllBytes(path);
            Require(first[0] == (byte)'{', "UTF8 BOM unexpectedly present");
            var parsed = JObject.Parse(Encoding.UTF8.GetString(first));
            Require((string?)parsed["format"] == "neko.defs.v1" && (string?)parsed["language"] == "ChineseSimplified" &&
                (bool?)parsed["complete"] == true && parsed["mods"]![0]!["package_id"]!.ToString() == "fixture.mod", "snapshot metadata mismatch");
            Require(snapshot.GeneratedAt.EndsWith("Z", StringComparison.Ordinal) && DateTime.TryParse(snapshot.GeneratedAt, out _), "UTC timestamp invalid");
            Require(parsed["entries"]![0]!["key"]!.ToString() == "Fixture.Def/Example", "typed key changed");
            snapshot.Language = "English";
            KnowledgePublicationFixture.Publish(snapshot, directory);
            path = KnowledgePublicationFixture.PayloadPath(directory);
            Require(path != firstPath && first.SequenceEqual(File.ReadAllBytes(firstPath)), "replacement rewrote the immutable payload");
            Require(File.ReadAllText(path).Contains("English"), "atomic replacement did not publish");
            var preserved = File.ReadAllText(path);
            var readyPath = Path.Combine(directory, "ready.json");
            var preservedPointer = File.ReadAllBytes(readyPath);
            // A failure before publication must not remove/modify the last complete file.
            snapshot.Language = "\ud800";
            ExpectFailure(() => KnowledgePublicationFixture.Publish(snapshot, directory));
            Require(File.ReadAllText(path) == preserved, "failed publication damaged old file");
            Require(preservedPointer.SequenceEqual(File.ReadAllBytes(readyPath)), "failed publication changed the ready pointer");
            snapshot.Language = "English";
            Task.WaitAll(Task.Run(() => KnowledgePublicationFixture.Publish(snapshot, directory)), Task.Run(() => KnowledgePublicationFixture.Publish(snapshot, directory)));
            Require(JObject.Parse(File.ReadAllText(KnowledgePublicationFixture.PayloadPath(directory)))["entries"]!.Count() == 1, "concurrent writers corrupted JSON");
            Require(Directory.GetFiles(directory, "*.tmp").Length == 0, "temporary file leaked");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException("Knowledge: " + message); }
    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is InvalidDataException || exception is EncoderFallbackException) { return; }
        throw new InvalidOperationException("Knowledge: expected bounded/publication failure");
    }
#pragma warning disable CS0414
    private sealed class Fixture
    {
        private int privateXmlField = 17;
        public int cachedValue = 3;
        [NonSerialized] public int transient = 4;
        public string? optional = null;
        public List<int> numbers = new List<int> { 1, 2 };
        public Ref first = new Ref { type = "A.Shared", name = "Same" };
        public Ref second = new Ref { type = "B.Shared", name = "Same" };
        public Fixture? self;
        public LiveObject live = new LiveObject();
        public EvilSequence sequence = new EvilSequence();
        public static int GetterCalls;
        public int Getter { get { GetterCalls++; throw new Exception("never call"); } }
    }
#pragma warning restore CS0414
    private sealed class Ref { public string type = ""; public string name = ""; }
    private sealed class Simple { public int value; }
    private sealed class LiveObject { public int health = 90; }
    private sealed class EvilSequence : IEnumerable
    {
        public static int Calls;
        public IEnumerator GetEnumerator() { Calls++; throw new Exception("never enumerate"); }
    }
    private sealed class Limits
    {
        public string text = new string('x', 2000);
        public List<int> items = Enumerable.Range(0, 1000).ToList();
        public Node deep = new Node { next = new Node { next = new Node { next = new Node() } } };
    }
    private sealed class Node { public Node? next; }
}
