using System;
using System.Collections.Generic;
using System.Linq;
using GraphQL;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>Reads selection syntax without resolving values, moving options or merging explicit projections.</summary>
internal sealed class SelectionInput
{
    internal sealed class Field
    {
        public string Name = "";
        public string Path = "";
        public JToken? Value;
        public List<Field>? Children;
    }

    private int work;
    private static readonly HashSet<string> Options = new(new[] {
        "select", "args", "where", "aggregate", "group_by", "order_by", "limit", "offset", "on"
    }, StringComparer.Ordinal);

    internal static bool IsOption(string name) => Options.Contains(name);
    internal static string Pointer(string path, string name) => path + "/" + name.Replace("~", "~0").Replace("/", "~1");
    internal static ExecutionError Error(string message, string path)
    {
        var error = PublicFields.Error(message);
        error.Data["input_path"] = path;
        return error;
    }

    internal static ExecutionError Locate(ExecutionError error, string path, string owner)
    {
        if (error.Data.Contains("input_located")) return error;
        path = error.Data["input_path"] as string ?? path;
        // Keep diagnostics bounded; a parent pointer is still an actual input location.
        while (path.Length > 1024) path = path.Substring(0, path.LastIndexOf('/'));
        var message = error.Message.Length > 512 ? error.Message.Substring(0, 509) + "..." : error.Message;
        var located = new ExecutionError("At " + (path.Length == 0 ? "/" : path) + " (" + owner + "): " + message) {
            Code = error.Code ?? "invalid_query"
        };
        located.Data["input_path"] = path;
        located.Data["input_located"] = true;
        return located;
    }

    private void Check(string path)
    {
        // A valid 512-field selection can repeat up to 13 path segments per leaf.
        // The existing compiler still owns the actual depth/field limits.
        if (++work > 8192) throw Error("Selection exceeds its syntax work limit.", path);
    }

    internal IReadOnlyList<Field> Read(JToken token, string path, bool generated = false)
    {
        var fields = new List<Field>();
        void AddMap(JObject map, string source)
        {
            foreach (var p in map.Properties()) {
                var at = generated ? path : Pointer(source, p.Name);
                Check(at);
                if (fields.Any(f => f.Name == p.Name)) throw Error("Duplicate/overlapping field " + p.Name + "; select it once explicitly.", at);
                fields.Add(new Field { Name = p.Name, Value = p.Value, Path = at });
            }
        }
        if (token is JObject map) { AddMap(map, path); return fields; }
        if (token is not JArray array) throw Error("Expected a field map or a list of field paths and field maps.", path);
        for (var index = 0; index < array.Count; index++) {
            var item = array[index];
            var at = generated ? path : Pointer(path, index.ToString());
            if (item is JObject members) { AddMap(members, at); continue; }
            if (item.Type != JTokenType.String) throw Error("Selection-list members must be string paths or field maps.", at);
            var parts = ((string)item!).Split('.');
            if (parts.Length > 13) throw Error("Selection path exceeds depth12.", at);
            var level = fields;
            for (var part = 0; part < parts.Length; part++) {
                Check(at);
                var existing = level.FirstOrDefault(f => f.Name == parts[part]);
                if (part == parts.Length - 1) {
                    if (existing != null) throw Error("Duplicate/overlapping selected path " + item + ".", at);
                    level.Add(new Field { Name = parts[part], Value = new JValue(true), Path = at });
                } else {
                    if (existing != null && existing.Children == null) throw Error("Overlapping selected path " + item + "; no implicit merge with an explicit field selection.", at);
                    if (existing == null) {
                        existing = new Field { Name = parts[part], Path = at, Children = new List<Field>() };
                        level.Add(existing);
                    }
                    level = existing.Children!;
                }
            }
        }
        return fields;
    }
}
