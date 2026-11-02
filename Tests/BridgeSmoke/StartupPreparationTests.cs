using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld
{
internal static class StartupPreparationTests
{
    internal static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "neko-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            var path = Path.Combine(directory, "startup.json");
            File.WriteAllText(path, "malformed private study input");
            if (!StartupPreparation.Load(path, false).RequiredOperations.SequenceEqual(StartupPreparation.BehaviorOperations))
                throw new Exception("Private declaration affected ordinary gameplay");
            var ready = Path.Combine(directory, "ready.json");
            File.WriteAllText(ready, "original generation");
            string digest;
            using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(ready))).Replace("-", "").ToLowerInvariant();
            var declaration = new JObject { ["format"]="neko.startup.v1", ["required_behavior_operations"]=new JArray(),
                ["reused_knowledge_ready_sha256"]=digest };
            File.WriteAllText(path, declaration.ToString());
            var startup = StartupPreparation.Load(path, true);
            if (startup.RequiredOperations.Length != 0 || !startup.ReuseKnowledge(directory)) throw new Exception("Declared reuse failed");
            File.WriteAllText(ready, "changed generation");
            try { startup.ReuseKnowledge(directory); throw new Exception("Changed reuse was accepted"); }
            catch (InvalidDataException) { }
            declaration["required_behavior_operations"] = new JArray("unknown_behavior");
            File.WriteAllText(path, declaration.ToString());
            try { StartupPreparation.Load(path, true); throw new Exception("Unknown required behavior accepted"); }
            catch (InvalidDataException) { }
        } finally { Directory.Delete(directory, true); }
        Console.WriteLine("Startup preparation tests passed: isolated requirements and immutable publication identity.");
    }
}
}

namespace Verse
{
// Native path binding only; tests use Load with their own isolated directory.
internal static class GenFilePaths { internal static string SaveDataFolderPath => Path.GetTempPath(); }
}
