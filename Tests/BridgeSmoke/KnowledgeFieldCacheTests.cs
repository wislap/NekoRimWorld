using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Neko.RimWorld;

internal static class KnowledgeFieldCacheTests
{
    internal static void Run()
    {
        var root = new Fixture { defName = "root-name", label = "root-label", description = "root-desc", modContentPack = "root-mod" };
        root.child = new Fixture { defName = "child-name", label = "child-label", description = "child-desc", modContentPack = "child-mod" };
        root.cycle = root;
        root.child.cycle = root;
        Func<object, KnowledgeRelated?> reference = value => value is Reference item
            ? new KnowledgeRelated { Key = item.Key, Label = item.Label } : null;
        Func<Type, bool> forbidden = type => type == typeof(Forbidden);
        var legacy = new LegacyKnowledgeFields(reference, forbidden);
        var optimized = new KnowledgeFields(reference, forbidden);
        for (var i = 0; i < 4; i++)
        {
            var oldEntry = Entry();
            var newEntry = Entry();
            legacy.Extract(root, oldEntry);
            optimized.Extract(root, newEntry);
            var before = JsonConvert.SerializeObject(oldEntry, Formatting.None);
            var after = JsonConvert.SerializeObject(newEntry, Formatting.None);
            Check(before == after, "cached extraction changed entry bytes");
            using (var sha = SHA256.Create())
                Check(Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(before))) ==
                      Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(after))), "entry hash differs");
            Check(newEntry.Facts.Exists(f => f.Name == "child.defName" && f.Value == "child-name"), "nested root-excluded field missing");
            Check(!newEntry.Facts.Exists(f => f.Name == "defName"), "root metadata leaked into facts");
            Check(newEntry.Facts.Exists(f => f.Name == "shared" && f.Value == "derived"), "derived shadowing changed");
            Check(newEntry.Facts.Exists(f => f.Name == "allowed" && f.Value == "7"), "loadable unsaved field missing");
            Check(newEntry.Facts.Exists(f => f.Name == "namedAllowed" && f.Value == "11"), "named loadable unsaved field missing");
            Check(!newEntry.Facts.Exists(f => f.Name == "ignored"), "unsaved field unexpectedly included");
            Check(newEntry.Related.Count == 2 && newEntry.Related[0].Key == "Ref/first" && newEntry.Related[1].Key == "Ref/second", "related order or dedup changed");
            if (i == 2) optimized.ClearMetadata();
        }
    }

    private static KnowledgeEntry Entry() => new KnowledgeEntry {
        Key = "Fixture/root", Type = "Fixture", DefName = "root",
        Related = new List<KnowledgeRelated> { new KnowledgeRelated { Key = "Ref/first", Label = "existing" } }
    };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private class Base { public string shared = "base"; }
    private sealed class Fixture : Base
    {
        public new string shared = "derived";
        public string defName = "";
        public string label = "";
        public string description = "";
        public string modContentPack = "";
        [Verse.Unsaved(true)] public int allowed = 7;
        [Verse.Unsaved(allowLoading = true)] public int namedAllowed = 11;
        [Verse.Unsaved] public int ignored = 9;
        public Fixture? child;
        public Fixture? cycle;
        public List<object?> list = new List<object?> { null, 3, "text", new Reference("Ref/first", "first"), new Reference("Ref/second", "second"), new Reference("Ref/second", "duplicate") };
        public Dictionary<string, int> map = new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 };
        public Forbidden resource = new Forbidden();
    }
    private sealed class Reference
    {
        internal readonly string Key;
        internal readonly string Label;
        internal Reference(string key, string label) { Key = key; Label = label; }
    }
    private sealed class Forbidden { public int shouldNotRead = 1; }
}
