using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class SpatialCellGroupsTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static JObject Cell(int x, int z) => new() { ["x"] = x, ["z"] = z, ["known"] = true, ["status"] = "observed", ["terrain"] = "Soil",
        ["terrain_layers"] = new JObject { ["top"] = "Soil", ["foundation"] = JValue.CreateNull(), ["temporary"] = JValue.CreateNull() },
        ["roof"] = JValue.CreateNull(), ["walkable"] = true, ["roof_within_support_range"] = false, ["zone_id"] = JValue.CreateNull(), ["area_ids"] = new JArray() };
    private static JObject Data(JToken cells, bool grouped) => new() { ["format"] = "construction_observation.v1", ["status"] = "ok", ["map_id"] = "Map_1", ["bounds"] = new JObject { ["min_x"] = 181, ["min_z"] = 97, ["width"] = 64, ["height"] = 64 },
        [grouped ? "cell_groups" : "cells"] = cells, ["objects"] = new JArray(), ["coverage"] = new JObject { ["cells_complete"] = true, ["objects_complete"] = true, ["visibility_complete"] = true, ["unknown_cells"] = 0, ["atomic"] = false } };
    internal static void Run() {
        var groups = new SpatialCellGroups(); var dense48 = new JArray();
        for (int z = 97; z < 161; z++) for (int x = 181; x < 245; x++) { var cell = Cell(x, z); groups.Add(cell); if (x < 229 && z < 145) dense48.Add(cell); }
        var flat = Data(dense48, false); Check(Encoding.UTF8.GetByteCount(flat.ToString(Formatting.None)) < ProtocolCodec.MaxFrameBytes, "Node-limit regression accidentally exercised only the byte limit.");
        bool rejected = false; try { ProtocolCodec.ValidateQueryDataBudget(flat); } catch (ProtocolException) { rejected = true; }
        Check(rejected, "The former 48x48 flat cell output no longer exercises its JSON node limit.");
        var grouped = Data(groups.Groups, true); ProtocolCodec.ValidateQueryDataBudget(grouped);
        Check(groups.Groups.Count == 1 && ((JArray)groups.Groups[0]!["_xz"]!).Count == 4096 && grouped["cells"] == null, "64x64 common cell facts lost coordinates or emitted duplicate dense representation.");
        var session = new SessionDescriptor("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 42, 3);
        var request = ProtocolEnvelope.Create(ProtocolMessageTypes.QueryRequest, "cccccccccccccccccccccccccccccccc", 0, new ProtocolPayload { Operation = "inspect_construction", Arguments = new JObject { ["map"] = "Map_1" } }, session, Guid.NewGuid().ToString("N"));
        var response = ProtocolEnvelope.CreateQueryResult(request, request.ConnectionEpoch, 0, session, ProtocolStatuses.Ok, grouped);
        var encoded = ProtocolCodec.Serialize(response); var decoded = ProtocolCodec.Deserialize(encoded);
        Check(JToken.DeepEquals(decoded.Payload.Data, grouped), "The real C# query-result codec did not preserve the complete 64x64 grouped wire result.");
        var roundtrip = new Dictionary<string, JObject>(StringComparer.Ordinal);
        foreach (JObject group in (JArray)decoded.Payload.Data!["cell_groups"]!) foreach (JArray coordinate in (JArray)group["_xz"]!) {
            var cell = (JObject)group.DeepClone(); cell.Remove("_xz"); cell["x"] = coordinate[0]!.DeepClone(); cell["z"] = coordinate[1]!.DeepClone();
            roundtrip.Add((int)cell["x"]! + ":" + (int)cell["z"]!, cell);
        }
        Check(roundtrip.Count == 4096 && JToken.DeepEquals(roundtrip["181:97"], Cell(181, 97)) && JToken.DeepEquals(roundtrip["244:160"], Cell(244, 160)), "World coordinates or literal properties changed during group expansion.");
        var mixed = new SpatialCellGroups(); var original = Cell(-2, 7); original["extension"] = new JObject { ["literal"] = new JArray("kept", 3, JValue.CreateNull()) }; var before = original.DeepClone();
        mixed.Add(original); var equal = (JObject)original.DeepClone(); equal["x"] = -1; mixed.Add(equal);
        var different = (JObject)original.DeepClone(); different["x"] = 0; different["extension"]!["literal"]![1] = 4; mixed.Add(different);
        mixed.Add(JObject.Parse("{x:1,z:7,known:false,status:'fogged'}"));
        Check(JToken.DeepEquals(original, before) && mixed.Groups.Count == 3 && ((JArray)mixed.Groups[0]!["_xz"]!).Count == 2 && mixed.Groups[2]!["roof"] == null && (bool?)mixed.Groups[2]!["known"] == false, "Grouping mutated originals, merged distinct extension facts or invented unknown cell properties.");
        var distinct = new SpatialCellGroups(); for (int z = 97; z < 161; z++) for (int x = 181; x < 245; x++) { var cell = Cell(x, z); cell["extension"] = x + ":" + z; distinct.Add(cell); }
        rejected = false; try { ProtocolCodec.ValidateQueryDataBudget(Data(distinct.Groups, true)); } catch (ProtocolException) { rejected = true; }
        Check(rejected && distinct.Groups.Count == 4096, "Distinct dense facts were dropped or the transport node limit was expanded.");
        Console.WriteLine("Spatial cell-group tests passed: real 48x48 node regression, full 64x64 wire roundtrip, world coordinates, unknown/extensions and distinct-density rejection.");
    }
}
