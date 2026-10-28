using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;
internal static class ColonyWriteCatalogTests
{
    internal static void Run()
    {
        var catalog = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "colony-write-v1", "catalog.json")));
        if (!JToken.DeepEquals(catalog, VerseWriteFieldResolver.DescribeCatalog())) throw new Exception("Production write catalog differs from fixture.");
        var id = (string?)catalog["catalog_id"]; catalog.Remove("catalog_id");
        if (id != GameQueryEngine.Hash(catalog.ToString(Newtonsoft.Json.Formatting.None))) throw new Exception("Write catalog hash differs.");
        var ops = (JArray)catalog["operations"]!;
        if (ops.Count != 8 || ops.Select(o => (string?)o["op"]).Distinct().Count() != 8) throw new Exception("Write catalog operation list is invalid.");
        foreach (var op in ops) {
            var properties = (JObject)op["schema"]!["properties"]!;
            if (properties["expect"] != null || properties["evidence"] != null || properties["value"] == null || properties["op"] == null) throw new Exception("Model schema contains wrong fields.");
            if (op["observation"]!["read"]!["native_operation"] == null || op["observation"]!["extract"]!["kind"] == null) throw new Exception("Missing observation descriptor.");
        }
        var readOnly = new GameQueryEngine().Execute("query_catalog", new JObject(), null!, new SessionDescriptor("a", "b", 0, 0)).Data!;
        if (readOnly["colony_write"] != null) throw new Exception("Query-only catalog must not advertise native writes.");
    }
}
