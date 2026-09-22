using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Neko.RimWorld
{
    internal static class KnowledgeCompletenessTests
    {
        internal static void Run()
        {
            var entry = new KnowledgeEntry { Key = "Fixture/Complete", Type = "Fixture", DefName = "Complete" };
            var reader = new KnowledgeFields(_ => null, type => type == typeof(RuntimeAsset));
            var root = new Definition();
            root.self = root;
            reader.Extract(root, entry);
            string Value(string path) => entry.Facts.Single(f => f.Name == path).Value;
            Check(!entry.Truncated, "ordinary definitions are still incomplete");
            Check(Value("thingClass") == typeof(Definition).FullName, "Type value lost represented name");
            Check(Value("degreeDatas[0].label") == "善良" && Value("degreeDatas[0].description") == "完整说明", "nested language fields lost");
            Check(Value("degreeDatas[0].statFactors[0].value") == "0.75", "nested stat modifier lost");
            Check(Value("items[9999]") == "9999", "long list lost tail beyond old 8192 guard");
            Check(Value("cachedDefinitionValue") == "23", "name-based field filter lost configuration");
            Check(Value("loadable") == "17" && !entry.Facts.Any(f => f.Name == "runtimeCache"), "Unsaved.allowLoading not honored");
            Check(Value("privateValue") == "19" && Value("readonlyValue") == "29", "inherited/private/readonly fields lost");
            Check(Value("self.$ref") == "$", "cycle target path lost");
            Check(Value("table[0].key") == "threshold" && Value("table[0].value") == "0.5", "dictionary lost entries");
            Check(Value("set.count") == "2", "set lost count");
            Check(Value("matrix[1,1]") == "4", "multidimensional array lost values");
            Check(Value("curve.points[1].y") == "3.5", "custom curve fields lost");
            Check(Value("curve.$type") == typeof(Curve).FullName, "polymorphic type lost");
            Check(Value("customDictionary.config") == "31", "custom dictionary data fields lost");
            Check(Curve.Calls == 0 && HostileDictionary.Calls == 0, "custom enumerator/getter executed");
            Check(entry.Diagnostics.All(d => d.Kind == "expected_omission"), "unexpected incomplete diagnostic");

            var deep = new Chain(); var cursor = deep;
            for (var i = 0; i < KnowledgeFields.MaxDepth + 5; i++) { cursor.next = new Chain(); cursor = cursor.next; }
            var capped = new KnowledgeEntry(); reader.Extract(deep, capped);
            Check(capped.Truncated && capped.Diagnostics.Any(d => d.Reason == "depth limit"), "true safety limit hidden");
        }
        private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException("Knowledge completeness: " + message); }
        private sealed class RuntimeAsset { }
        private class Base
        {
#pragma warning disable CS0414
            private int privateValue = 19;
#pragma warning restore CS0414
        }
        private sealed class Definition : Base
        {
            public Type thingClass = typeof(Definition);
            public List<Degree> degreeDatas = new List<Degree> { new Degree() };
            public List<int> items = Enumerable.Range(0, 10000).ToList();
            public int cachedDefinitionValue = 23;
            [Verse.Unsaved(true)] public int loadable = 17;
            [Verse.Unsaved] public int runtimeCache = 1;
            public readonly int readonlyValue = 29;
            public Definition? self;
            public Dictionary<string, float> table = new Dictionary<string, float> { ["threshold"] = 0.5f };
            public HashSet<string> set = new HashSet<string> { "a", "b" };
            public int[,] matrix = new int[,] { { 1, 2 }, { 3, 4 } };
            public Curve curve = new Curve();
            public HostileDictionary customDictionary = new HostileDictionary();
            public RuntimeAsset asset = new RuntimeAsset();
        }
        private sealed class Degree
        {
            public string label = "善良";
            public string description = "完整说明";
            public List<Modifier> statFactors = new List<Modifier> { new Modifier() };
        }
        private sealed class Modifier { public float value = 0.75f; }
        private sealed class Point { public float x; public float y; }
        private sealed class Curve : IEnumerable
        {
            public static int Calls;
            public List<Point> points = new List<Point> { new Point { x = 1, y = 2 }, new Point { x = 2, y = 3.5f } };
            public IEnumerator GetEnumerator() { Calls++; throw new Exception("do not execute"); }
        }
        private sealed class HostileDictionary : IDictionary
        {
            public static int Calls;
            public int config = 31;
            public int Count { get { Calls++; throw new Exception(); } }
            public bool IsFixedSize => false;
            public bool IsReadOnly => false;
            public bool IsSynchronized => false;
            public object SyncRoot => this;
            public ICollection Keys => throw new Exception();
            public ICollection Values => throw new Exception();
            public object? this[object key] { get => throw new Exception(); set => throw new Exception(); }
            public void Add(object key, object? value) => throw new Exception();
            public void Clear() => throw new Exception();
            public bool Contains(object key) => throw new Exception();
            public void CopyTo(Array array, int index) => throw new Exception();
            public void Remove(object key) => throw new Exception();
            public IDictionaryEnumerator GetEnumerator() { Calls++; throw new Exception(); }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
        private sealed class Chain { public Chain? next; }
    }
}
namespace Verse
{
    [AttributeUsage(AttributeTargets.Field)]
    internal sealed class UnsavedAttribute : Attribute
    {
        public bool allowLoading;
        public UnsavedAttribute(bool allowLoading = false) { this.allowLoading = allowLoading; }
    }
}
