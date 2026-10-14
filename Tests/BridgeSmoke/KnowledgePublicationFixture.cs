using System;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class KnowledgePublicationFixture
{
    internal static void Publish(KnowledgeSnapshot snapshot, string directory)
    {
        var metadata = JsonConvert.DeserializeObject<KnowledgeSnapshot>(JsonConvert.SerializeObject(snapshot))!;
        metadata.Entries.Clear();
        var publisher = new KnowledgeStreamingPublisher(directory);
        try
        {
            foreach (var entry in snapshot.Entries)
            {
                var record = KnowledgeSnapshot.BoundAndSerialize(entry);
                while (!publisher.TryEnqueueSerialized(record))
                {
                    if (publisher.IsFailed) throw publisher.Failure ?? new InvalidOperationException("Publication cancelled.");
                    Thread.Yield();
                }
            }
            publisher.Complete(metadata);
            publisher.Completion.GetAwaiter().GetResult();
            if (publisher.Failure != null) throw publisher.Failure;
        }
        catch (Exception exception)
        {
            publisher.Fail(exception);
            publisher.Completion.GetAwaiter().GetResult();
            throw;
        }
        finally { publisher.Dispose(); }
    }

    internal static string PayloadPath(string directory)
    {
        var ready = JObject.Parse(File.ReadAllText(Path.Combine(directory, "ready.json")));
        return Path.Combine(directory, ready["data_file"]!.ToString());
    }

    internal static void Export(string directory)
    {
        var snapshot = new KnowledgeSnapshot { GameVersion = "1.6.fixture", Language = "ChineseSimplified" };
        snapshot.Mods.Add(new KnowledgeMod { PackageId = "fixture.mod", Name = "跨语言知识夹具" });
        var garden = new KnowledgeEntry {
            Key = "Verse.ThingDef/Fixture_Lantern_Garden", Type = "Verse.ThingDef",
            DefName = "Fixture_Lantern_Garden", Label = "庭院灯", Description = "为庭院提供照明。",
            SourceMod = "跨语言知识夹具", PackageId = "fixture.mod",
        };
        garden.Facts.Add(new KnowledgeFact { Name = "comps[0].label", Value = "花园灯", Type = "System.String" });
        garden.Facts.Add(new KnowledgeFact { Name = "comps[0].description", Value = "柔和照明覆盖庭院入口。", Type = "System.String" });
        garden.Related.Add(new KnowledgeRelated { Key = "Verse.ThingDef/Fixture_MoonBlade", Label = "月纹短刃" });
        snapshot.Add(garden);
        var blade = new KnowledgeEntry {
            Key = "Verse.ThingDef/Fixture_MoonBlade", Type = "Verse.ThingDef",
            DefName = "Fixture_MoonBlade", Label = "月纹短刃", Description = "银色月纹的近战短刃。",
            SourceMod = "跨语言知识夹具", PackageId = "fixture.mod",
        };
        blade.Facts.Add(new KnowledgeFact { Name = "stackLimit", Value = "1", Type = "System.Int32" });
        snapshot.Add(blade);
        Publish(snapshot, directory);
        if (File.Exists(Path.Combine(directory, "current.json")))
            throw new InvalidOperationException("Publication produced a retired compatibility file.");
        Console.WriteLine("Knowledge fixture published to " + Path.GetFullPath(directory));
    }
}
