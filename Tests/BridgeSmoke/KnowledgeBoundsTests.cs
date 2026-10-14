using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class KnowledgeBoundsTests
{
    internal static void Run()
    {
        ClippedFieldsCarryDiagnostics();
        EntryBudgetUsesBoundedTrim();
        FactTypeIsOptionalAndRoundTrips();
        InvalidUtf16PreservesPreviousPublication();
    }

    private static void ClippedFieldsCarryDiagnostics()
    {
        var entry = Entry();
        entry.Label = new string('l', 4097);
        entry.Description = new string('d', 65537);
        entry.SourceMod = new string('m', 1025);
        entry.PackageId = new string('p', 1025);
        entry.Facts.Add(new KnowledgeFact { Name = new string('n', 2049), Value = new string('v', 65537) });

        KnowledgeSnapshot.Bound(entry);

        Require(entry.Truncated, "clipped fields did not mark the entry incomplete");
        Require(entry.Diagnostics.Any(d => d.Path == "$label" && d.Kind == "incomplete"), "label clipping diagnostic missing");
        Require(entry.Diagnostics.Any(d => d.Path == "$description" && d.Kind == "incomplete"), "description clipping diagnostic missing");
        Require(entry.Diagnostics.Any(d => d.Path == "$facts[0].name" && d.Kind == "incomplete"), "fact name clipping diagnostic missing");
        Require(entry.Diagnostics.Any(d => d.Path == "$facts[0].value" && d.Kind == "incomplete"), "fact value clipping diagnostic missing");
        Require(entry.Facts[0].Name.Length <= 2048 && entry.Facts[0].Value.Length <= 65536, "fact bound exceeded");
    }

    private static void EntryBudgetUsesBoundedTrim()
    {
        var entry = Entry();
        entry.Description = new string('界', 65536);
        for (var index = 0; index < 20000; index++)
            entry.Facts.Add(new KnowledgeFact { Name = "fact" + index, Value = new string('x', 500) });

        KnowledgeSnapshot.Bound(entry);
        var bytes = Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(entry));
        Require(bytes <= KnowledgeSnapshot.MaxEntryBytes, "entry byte budget exceeded");
        Require(entry.Truncated && entry.Diagnostics.Any(d => d.Path == "$entry"), "entry byte clipping diagnostic missing");
        Require(entry.Facts.Count > 0 && entry.Facts.Count < 20000, "bounded trim removed the wrong fact range");
    }

    private static void FactTypeIsOptionalAndRoundTrips()
    {
        var legacy = JObject.Parse(JsonConvert.SerializeObject(new KnowledgeFact { Name = "x", Value = "null" }));
        Require(legacy["type"] == null, "legacy fact unexpectedly gained a type field");

        var typed = JObject.Parse(JsonConvert.SerializeObject(new KnowledgeFact
        {
            Name = "x",
            Value = "null",
            Type = typeof(string).FullName,
        }));
        Require((string?)typed["type"] == typeof(string).FullName, "fact CLR type was not serialized");
    }

    private static void InvalidUtf16PreservesPreviousPublication()
    {
        var directory = Path.Combine(Path.GetTempPath(), "neko-knowledge-bounds-" + Guid.NewGuid().ToString("N"));
        try
        {
            var snapshot = new KnowledgeSnapshot();
            snapshot.Add(Entry());
            KnowledgePublicationFixture.Publish(snapshot, directory);
            var path = KnowledgePublicationFixture.PayloadPath(directory);
            var previous = File.ReadAllBytes(path);
            var readyPath = Path.Combine(directory, "ready.json");
            var previousPointer = File.ReadAllBytes(readyPath);

            snapshot.Language = "\ud800";
            ExpectFailure(() => KnowledgePublicationFixture.Publish(snapshot, directory));
            Require(previous.SequenceEqual(File.ReadAllBytes(path)), "invalid UTF-16 damaged previous publication");
            Require(previousPointer.SequenceEqual(File.ReadAllBytes(readyPath)), "invalid UTF-16 replaced the ready pointer");
            Require(Directory.GetFiles(directory, "*.tmp").Length == 0, "failed publication leaked a temporary file");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static KnowledgeEntry Entry() => new KnowledgeEntry
    {
        Key = "Fixture.Def/Bounds",
        Type = "Fixture.Def",
        DefName = "Bounds",
        Label = "Bounds",
        Description = "Fixture",
    };

    private static void ExpectFailure(Action action)
    {
        try
        {
            action();
        }
        catch (EncoderFallbackException)
        {
            return;
        }
        throw new InvalidOperationException("Knowledge bounds: invalid UTF-16 was accepted");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Knowledge bounds: " + message);
    }
}
