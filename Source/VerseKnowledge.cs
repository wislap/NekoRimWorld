using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Verse;

namespace Neko.RimWorld;

// Called exclusively by GameComponent lifecycle/update on the game thread.
internal static class VerseKnowledge
{
    private static bool started;
    private static bool requested;
    private static KnowledgeSnapshot? snapshot;
    private static KnowledgeStreamingPublisher? publisher;
    private static string? pendingEntry;
    private static int pendingEntryBytes;
    private static long entryBytes;
    private static IEnumerator<Type>? databases;
    private static IEnumerator<Def>? definitions;
    private static readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal);
    private static readonly HashSet<Type> Types = new HashSet<Type>();
    private static string? directory;
    private static int visited;
    private static readonly KnowledgeFields Fields = new KnowledgeFields(Reference, Forbidden);

    // Lifecycle callbacks may run during a long event; only arm work here.
    internal static void StartOnce() { requested = true; }

    private static void Initialize()
    {
        started = true;
        snapshot = new KnowledgeSnapshot();
        try
        {
            directory = KnowledgeFiles.DirectoryPath();
            publisher = new KnowledgeStreamingPublisher(directory);
            snapshot.GameVersion = global::RimWorld.VersionControl.CurrentVersionStringWithRev;
            snapshot.Language = LanguageDatabase.activeLanguage?.folderName ?? "";
            if (snapshot.GameVersion.Length > 512 || snapshot.Language.Length > 256)
                throw new InvalidOperationException("Game metadata exceeds string limits.");
            foreach (var mod in LoadedModManager.RunningModsListForReading)
            {
                if (snapshot.Mods.Count >= 1024) { snapshot.Issue("Mod metadata count limit reached."); break; }
                if (mod.PackageId.Length > 256 || mod.Name.Length > 256) snapshot.Issue("Mod metadata string limit reached.");
                snapshot.Mods.Add(new KnowledgeMod { PackageId = KnowledgeSnapshot.Clip(mod.PackageId, 256), Name = KnowledgeSnapshot.Clip(mod.Name, 256) });
            }
            databases = GenDefDatabase.AllDefTypesWithDatabases().GetEnumerator();
            Log.Message("[N.E.K.O RimWorld] Building offline Def knowledge on the game thread.");
        }
        catch (Exception exception) when (KnowledgeFields.Recoverable(exception))
        {
            // No trustworthy enumeration: retain the old file, never publish a misleading empty library.
            Log.Warning("[N.E.K.O RimWorld] Def knowledge initialization failed; old snapshot retained: " + exception.GetType().Name);
            publisher?.Fail(exception);
            Cleanup();
        }
    }

    internal static void Update()
    {
        if (Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting) return;
        if (requested && !started) Initialize();
        if (publisher != null && publisher.Completion.IsCompleted)
        {
            if (publisher.IsFailed)
                Log.Warning("[N.E.K.O RimWorld] Def knowledge publish failed; old snapshot retained: " + publisher.Failure?.GetType().Name);
            else
                Log.Message("[N.E.K.O RimWorld] Offline Def knowledge published to ready.json.");
            var failed = publisher.IsFailed;
            publisher.Dispose();
            publisher = null;
            if (failed) { Cleanup(); return; }
        }
        if (publisher != null && publisher.IsFailed)
        {
            // Cancellation is asynchronous; never wait on the game thread. The next frame
            // disposes the publisher after its worker has observed cancellation.
            Cleanup();
            return;
        }
        if (snapshot == null || databases == null || publisher == null) return;
        var clock = Stopwatch.StartNew();
        // Cooperative 2 ms / 8 Defs per frame; one field traversal cannot be preempted.
        for (var processed = 0; processed < 8 && clock.Elapsed.TotalMilliseconds < 2; processed++)
        {
            try
            {
                if (publisher.IsFailed) { Cleanup(); return; }
                if (pendingEntry != null)
                {
                    if (!publisher.TryEnqueueSerialized(pendingEntry)) return;
                    entryBytes += pendingEntryBytes + 1;
                    pendingEntry = null;
                    pendingEntryBytes = 0;
                    continue;
                }
                if (definitions == null || !definitions.MoveNext())
                {
                    definitions?.Dispose(); definitions = null;
                    if (!databases.MoveNext()) { Finish(); return; }
                    var type = databases.Current;
                    if (!Types.Add(type)) continue;
                    if (Types.Count > 8192) { snapshot.Issue("Definition database type limit reached."); Finish(); return; }
                    definitions = GenDefDatabase.GetAllDefsInDatabaseForDef(type).GetEnumerator();
                    continue;
                }
                // Includes duplicate visits, bounding pathological database overlap as well as output.
                if (++visited > 1000000) { snapshot.Issue("Definition database visit limit reached."); Finish(); return; }
                var def = definitions.Current;
                if (def == null) { snapshot.Issue("Null definition in database."); continue; }
                var fullName = def.GetType().FullName;
                if (string.IsNullOrEmpty(fullName) || string.IsNullOrEmpty(def.defName))
                { snapshot.Issue("Definition missing full type name or defName; omitted."); continue; }
                var key = fullName + "/" + def.defName;
                if (!Keys.Add(key)) continue;
                if (Keys.Count > KnowledgeSnapshot.MaxEntries) { snapshot.Issue("Entry count limit reached."); Finish(); return; }
                var entry = new KnowledgeEntry
                {
                    Key = key, Type = fullName!, DefName = def.defName,
                    Label = def.label ?? "", Description = def.description ?? "",
                    SourceMod = def.modContentPack?.Name ?? "", PackageId = def.modContentPack?.PackageId ?? ""
                };
                if (def.modContentPack == null) snapshot.ProvenanceWarning("One or more definitions lack source mod metadata.");
                try { Fields.Extract(def, entry); }
                catch (Exception exception) when (KnowledgeFields.Recoverable(exception))
                {
                    entry.Truncated = true;
                    snapshot.Issue("Field extraction failed for " + key + ": " + exception.GetType().Name);
                }
                var serialized = KnowledgeSnapshot.BoundAndSerialize(entry);
                var bytes = Encoding.UTF8.GetByteCount(serialized);
                if (entry.Truncated) snapshot.Complete = false;
                if (entryBytes + bytes + 1 > KnowledgeSnapshot.MaxSnapshotBytes - 8 * 1024 * 1024)
                {
                    snapshot.Issue("Snapshot byte budget reached; remaining definitions omitted.");
                    Finish(); return;
                }
                if (!publisher.TryEnqueueSerialized(serialized))
                {
                    pendingEntry = serialized;
                    pendingEntryBytes = bytes;
                    return;
                }
                entryBytes += bytes + 1;
            }
            catch (Exception exception) when (KnowledgeFields.Recoverable(exception))
            {
                snapshot.Issue("Definition read failed: " + exception.GetType().Name);
                // Abort enumeration after a broken iterator rather than retry it forever.
                Finish(); return;
            }
        }
    }

    private static void Finish()
    {
        if (publisher == null || snapshot == null) return;
        publisher.Complete(snapshot);
        Cleanup();
    }

    private static void Cleanup()
    {
        definitions?.Dispose(); databases?.Dispose();
        definitions = null; databases = null; snapshot = null;
        pendingEntry = null;
        pendingEntryBytes = 0;
        entryBytes = 0;
        Keys.Clear(); Types.Clear();
    }

    private static KnowledgeRelated? Reference(object value)
    {
        if (!(value is Def def)) return null;
        if (string.IsNullOrEmpty(def.defName) || string.IsNullOrEmpty(def.GetType().FullName))
            throw new InvalidOperationException("Referenced definition has no identity.");
        return new KnowledgeRelated { Key = def.GetType().FullName + "/" + def.defName, Label = def.label ?? "" };
    }

    private static bool Forbidden(Type type) => typeof(Thing).IsAssignableFrom(type) ||
        typeof(Map).IsAssignableFrom(type) || typeof(global::RimWorld.Planet.WorldObject).IsAssignableFrom(type) ||
        typeof(Game).IsAssignableFrom(type) || typeof(GameComponent).IsAssignableFrom(type) ||
        typeof(UnityEngine.Object).IsAssignableFrom(type);
}
