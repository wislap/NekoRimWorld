using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace Neko.RimWorld;

[JsonObject(MemberSerialization.OptIn)]
internal sealed class KnowledgeSnapshot
{
    internal const int MaxEntries = 100000;
    internal const int MaxEntryBytes = 8 * 1024 * 1024;
    internal const int MaxSnapshotBytes = 512 * 1024 * 1024;
    internal const int MaxIssues = 256;

    [JsonProperty("format")] public string Format = "neko.defs.v1";
    [JsonProperty("game_version")] public string GameVersion = "";
    [JsonProperty("language")] public string Language = "";
    [JsonProperty("generated_at")] public string GeneratedAt = DateTime.UtcNow.ToString("O");
    [JsonProperty("mods")] public List<KnowledgeMod> Mods = new List<KnowledgeMod>();
    [JsonProperty("complete")] public bool Complete = true;
    [JsonProperty("provenance_complete")] public bool ProvenanceComplete = true;
    [JsonProperty("issues")] public List<string> Issues = new List<string>();
    [JsonProperty("warnings")] public List<string> Warnings = new List<string>();
    [JsonProperty("entries")] public List<KnowledgeEntry> Entries = new List<KnowledgeEntry>();
    private long entryBytes;

    public void Issue(string issue)
    {
        Complete = false;
        AddMessage(Issues, issue);
    }

    public void ProvenanceWarning(string warning)
    {
        ProvenanceComplete = false;
        AddMessage(Warnings, warning);
    }

    private static void AddMessage(List<string> target, string message)
    {
        message = Clip(message, 512);
        if (target.Contains(message)) return;
        if (target.Count < MaxIssues - 1) target.Add(message);
        else if (target.Count == MaxIssues - 1) target.Add("Further snapshot diagnostics omitted.");
    }

    public bool Add(KnowledgeEntry entry)
    {
        if (Entries.Count >= MaxEntries)
        {
            Issue("Entry count limit reached; remaining definitions omitted.");
            return false;
        }
        var serialized = BoundAndSerialize(entry);
        var bytes = Encoding.UTF8.GetByteCount(serialized);
        if (entryBytes + bytes + 1 > MaxSnapshotBytes - 8 * 1024 * 1024)
        {
            Issue("Snapshot byte budget reached; remaining definitions omitted.");
            return false;
        }
        if (entry.Truncated) Complete = false;
        entryBytes += bytes + 1;
        Entries.Add(entry);
        return true;
    }

    internal static string BoundAndSerialize(KnowledgeEntry entry) => BoundCore(entry);

    internal static void Bound(KnowledgeEntry entry)
    {
        BoundCore(entry);
    }

    private static string BoundCore(KnowledgeEntry entry)
    {
        if (entry.Key == null || entry.Type == null || entry.DefName == null ||
            entry.Key.Length > 4096 || entry.Type.Length > 2048 || entry.DefName.Length > 2048)
            throw new InvalidDataException("Definition identity exceeds the safe metadata limit.");

        entry.Label = Bounded(entry.Label, 4096, entry, "$label", "metadata field limit");
        entry.Description = Bounded(entry.Description, 65536, entry, "$description", "metadata field limit");
        entry.SourceMod = Bounded(entry.SourceMod, 1024, entry, "$source_mod", "metadata field limit");
        entry.PackageId = Bounded(entry.PackageId, 1024, entry, "$package_id", "metadata field limit");
        for (var index = 0; index < entry.Facts.Count; index++)
        {
            var fact = entry.Facts[index];
            fact.Name = Bounded(fact.Name, 2048, entry, "$facts[" + index + "].name", "fact name limit");
            fact.Value = Bounded(fact.Value, 65536, entry, "$facts[" + index + "].value", "fact value limit");
        }

        var serialized = JsonConvert.SerializeObject(entry);
        if (StrictUtf8Bytes(serialized) <= MaxEntryBytes) return serialized;
        entry.Truncated = true;
        entry.AddDiagnostic("$entry", "incomplete", "entry byte limit");
        TrimFacts(entry);
        serialized = JsonConvert.SerializeObject(entry);
        if (StrictUtf8Bytes(serialized) <= MaxEntryBytes) return serialized;
        TrimRelated(entry);
        serialized = JsonConvert.SerializeObject(entry);
        if (StrictUtf8Bytes(serialized) <= MaxEntryBytes) return serialized;
        if (entry.Description.Length > 0)
        {
            entry.Description = "";
            entry.AddDiagnostic("$description", "incomplete", "entry byte limit");
        }
        serialized = JsonConvert.SerializeObject(entry);
        if (StrictUtf8Bytes(serialized) > MaxEntryBytes)
            throw new InvalidDataException("Definition metadata exceeds entry byte limit.");
        return serialized;
    }

    private static string Bounded(string? text, int limit, KnowledgeEntry entry, string path, string reason)
    {
        text = text ?? "";
        if (text.Length > limit)
        {
            entry.Truncated = true;
            entry.AddDiagnostic(path, "incomplete", reason);
        }
        return Clip(text, limit);
    }

    private static int StrictUtf8Bytes(string serialized) => new UTF8Encoding(false, true).GetByteCount(serialized);

    private static int SerializedEntryBytes(KnowledgeEntry entry) =>
        StrictUtf8Bytes(JsonConvert.SerializeObject(entry));

    private static void TrimFacts(KnowledgeEntry entry)
    {
        var source = entry.Facts.ToArray();
        var low = 0;
        var high = source.Length;
        while (low < high)
        {
            var candidate = low + (high - low + 1) / 2;
            SetFacts(entry, source, candidate);
            if (SerializedEntryBytes(entry) <= MaxEntryBytes) low = candidate;
            else high = candidate - 1;
        }
        SetFacts(entry, source, low);
    }

    private static void SetFacts(KnowledgeEntry entry, KnowledgeFact[] source, int count)
    {
        entry.Facts.Clear();
        for (var index = 0; index < count; index++) entry.Facts.Add(source[index]);
    }

    private static void TrimRelated(KnowledgeEntry entry)
    {
        var source = entry.Related.ToArray();
        var low = 0;
        var high = source.Length;
        while (low < high)
        {
            var candidate = low + (high - low + 1) / 2;
            SetRelated(entry, source, candidate);
            if (SerializedEntryBytes(entry) <= MaxEntryBytes) low = candidate;
            else high = candidate - 1;
        }
        SetRelated(entry, source, low);
    }

    private static void SetRelated(KnowledgeEntry entry, KnowledgeRelated[] source, int count)
    {
        entry.Related.Clear();
        for (var index = 0; index < count; index++) entry.Related.Add(source[index]);
    }

    internal static string Clip(string? text, int limit)
    {
        if (text == null) return "";
        if (text.Length <= limit) return text;
        if (limit > 0 && char.IsHighSurrogate(text[limit - 1])) limit--;
        return text.Substring(0, limit);
    }
}

[JsonObject(MemberSerialization.OptIn)]
internal sealed class KnowledgeMod
{
    [JsonProperty("package_id")] public string PackageId = "";
    [JsonProperty("name")] public string Name = "";
}

[JsonObject(MemberSerialization.OptIn)]
internal sealed class KnowledgeEntry
{
    [JsonProperty("key")] public string Key = "";
    [JsonProperty("type")] public string Type = "";
    [JsonProperty("def_name")] public string DefName = "";
    [JsonProperty("label")] public string Label = "";
    [JsonProperty("description")] public string Description = "";
    [JsonProperty("source_mod")] public string SourceMod = "";
    [JsonProperty("package_id")] public string PackageId = "";
    [JsonProperty("facts")] public List<KnowledgeFact> Facts = new List<KnowledgeFact>();
    [JsonProperty("related")] public List<KnowledgeRelated> Related = new List<KnowledgeRelated>();
    [JsonProperty("diagnostics")] public List<KnowledgeDiagnostic> Diagnostics = new List<KnowledgeDiagnostic>();
    [JsonProperty("truncated")] public bool Truncated;

    internal void AddDiagnostic(string path, string kind, string reason)
    {
        if (Diagnostics.Count >= KnowledgeFields.MaxDiagnosticsPerEntry) return;
        Diagnostics.Add(new KnowledgeDiagnostic
        {
            Path = KnowledgeSnapshot.Clip(path, 2048),
            Kind = KnowledgeSnapshot.Clip(kind, 64),
            Reason = KnowledgeSnapshot.Clip(reason, 512),
        });
    }
}

[JsonObject(MemberSerialization.OptIn)]
internal sealed class KnowledgeFact
{
    [JsonProperty("name")] public string Name = "";
    [JsonProperty("value")] public string Value = "";
    [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)] public string? Type;
}

[JsonObject(MemberSerialization.OptIn)]
internal sealed class KnowledgeRelated
{
    [JsonProperty("key")] public string Key = "";
    [JsonProperty("label")] public string Label = "";
}

[JsonObject(MemberSerialization.OptIn)]
internal sealed class KnowledgeDiagnostic
{
    [JsonProperty("path")] public string Path = "";
    [JsonProperty("kind")] public string Kind = "";
    [JsonProperty("reason")] public string Reason = "";
}

internal static class KnowledgeFiles
{
    private static readonly object WriterLock = new object();

    internal static string DirectoryPath() => Environment.GetEnvironmentVariable("NEKO_RIMWORLD_KNOWLEDGE_DIR") is string configured &&
        !string.IsNullOrWhiteSpace(configured) ? Path.GetFullPath(configured) :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "neko-rimworld", "knowledge");

    internal static void ReplaceTemporary(string temporary, string current)
    {
        lock (WriterLock)
        {
            if (File.Exists(current)) File.Replace(temporary, current, null);
            else File.Move(temporary, current);
        }
    }

    internal static void ValidateUtf8(KnowledgeSnapshot snapshot)
    {
        ValidateString(snapshot.Format);
        ValidateString(snapshot.GameVersion);
        ValidateString(snapshot.Language);
        ValidateString(snapshot.GeneratedAt);
        foreach (var mod in snapshot.Mods) { ValidateString(mod.PackageId); ValidateString(mod.Name); }
        foreach (var issue in snapshot.Issues) ValidateString(issue);
        foreach (var warning in snapshot.Warnings) ValidateString(warning);
        foreach (var entry in snapshot.Entries)
        {
            ValidateString(entry.Key); ValidateString(entry.Type); ValidateString(entry.DefName);
            ValidateString(entry.Label); ValidateString(entry.Description);
            ValidateString(entry.SourceMod); ValidateString(entry.PackageId);
            foreach (var fact in entry.Facts) { ValidateString(fact.Name); ValidateString(fact.Value); ValidateString(fact.Type); }
            foreach (var related in entry.Related) { ValidateString(related.Key); ValidateString(related.Label); }
            foreach (var diagnostic in entry.Diagnostics)
            {
                ValidateString(diagnostic.Path); ValidateString(diagnostic.Kind); ValidateString(diagnostic.Reason);
            }
        }
    }

    private static void ValidateString(string? value)
    {
        if (value == null) return;
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                index++;
                continue;
            }
            throw new EncoderFallbackException("Invalid UTF-16 string cannot be published as UTF-8.");
        }
    }
}
