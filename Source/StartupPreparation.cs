using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

// One private startup declaration. Production keeps its ordinary prewarm policy.
internal sealed class StartupPreparation
{
    internal static readonly string[] BehaviorOperations = { "equip", "wear", "ingest", "hostile_active", "active_threat" };
    private static StartupPreparation? current;
    internal static StartupPreparation Current => current ??= Load(
        Path.Combine(Verse.GenFilePaths.SaveDataFolderPath, "ColonyStudy", "startup.json"),
        Environment.GetCommandLineArgs().Contains("-neko-player-consultation-study"));
    internal readonly bool IsStudy;
    internal readonly string[] RequiredOperations;
    internal readonly string? KnowledgeReadySha256;
    private StartupPreparation(bool study, string[] required, string? ready)
    { IsStudy = study; RequiredOperations = required; KnowledgeReadySha256 = ready; }

    internal static StartupPreparation Load(string path, bool study)
    {
        if (!study || !File.Exists(path)) return new StartupPreparation(study, BehaviorOperations, null);
        var declaration = JObject.Parse(File.ReadAllText(path));
        if ((string?)declaration["format"] != "neko.startup.v1" ||
            declaration["required_behavior_operations"] is not JArray operations ||
            operations.Any(item => item.Type != JTokenType.String))
            throw new InvalidDataException("Invalid startup preparation declaration.");
        var required = operations.Values<string>().Select(value => value!).ToArray();
        if (required.Distinct().Count() != required.Length || required.Except(BehaviorOperations).Any())
            throw new InvalidDataException("Unknown or duplicate required behavior operation.");
        var ready = (string?)declaration["reused_knowledge_ready_sha256"];
        if (ready != null && (ready.Length != 64 || ready.Any(ch => !"0123456789abcdef".Contains(ch))))
            throw new InvalidDataException("Invalid reused knowledge identity.");
        return new StartupPreparation(true, required, ready);
    }

    internal bool ReuseKnowledge(string directory)
    {
        if (KnowledgeReadySha256 == null) return false;
        using var input = File.OpenRead(Path.Combine(directory, "ready.json"));
        using var hash = SHA256.Create();
        var actual = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        if (actual != KnowledgeReadySha256) throw new InvalidDataException("Declared knowledge publication changed.");
        return true;
    }
}
