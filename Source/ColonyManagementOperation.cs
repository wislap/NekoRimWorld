using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal sealed class ColonyManagementOperation : FrameQueryOperation
{
    private readonly Game game = Current.Game;
    private readonly SessionDescriptor session;
    private readonly int startedTick = Find.TickManager.TicksGame;
    private JObject? result;
    internal ColonyManagementOperation(string op, JObject args, SessionDescriptor session) { this.session = session; InitializeWork(Read(op, (JObject)args.DeepClone()).GetEnumerator()); }
    private IEnumerable<int> Read(string op, JObject args) {
        ColonyManagementContract.Validate(op, args); GameImplementation.RequireKnownBinary(); yield return 0;
        if (op == "inspect_construction") { foreach (var step in ConstructionReadings.Spatial(args, value => result = value)) yield return step; }
        else if (op == "inspect_production") {
            var map = GameWorldReferences.ResolveMap((string?)args["map"]); var ids = ColonyReadContract.Texts(args, "ids");
            var all = map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>().Where(t => ids.Length == 0 || ids.Contains(t.GetUniqueLoadID(), StringComparer.Ordinal)).OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
            if (ids.Any(id => !all.Any(t => t.GetUniqueLoadID() == id))) throw new ArgumentException("A requested worktable is unavailable on this map.");
            var rows = new List<JObject>(); foreach (var t in all.Skip((int?)args["offset"] ?? 0).Take((int?)args["limit"] ?? 20)) { rows.Add(ProductionReadings.Read(t)); yield return 0; }
            result = new JObject { ["format"] = "production_observation.v1", ["objects"] = new JArray(rows), ["total"] = all.Length, ["complete"] = rows.Count == all.Length, ["map_id"] = map.GetUniqueLoadID() };
        }
        else result = op switch { "inspect_food_policies" => PolicyReadings.Read(args), "inspect_areas" => AreaZoneReadings.Read(args), "construction_catalog" => ConstructionReadings.Catalog(args, session), "preview_construction" => ConstructionReadings.Preview(args), "inspect_construction_intents" => Current.Game.GetComponent<NekoGameComponent>().Save.ConstructionIntents.Read(args), _ => throw new ArgumentException("Unknown management operation.") };
        yield return 0;
    }
    protected override void RestartReadSlice() { }
    protected override void ValidateFrame() { if (Current.Game != game) throw ReadContext.Error("world_changed", "Loaded game changed during management read."); EnsureLifetime(8, "observation_deadline", "Management read exceeded its lifetime."); }
    protected override void Complete() {
        result!["read_only"] = true; result["read_ms"] = ElapsedMilliseconds;
        result["sample"] = new JObject { ["colony_id"] = session.ColonyId, ["load_epoch"] = session.LoadEpoch, ["snapshot_sequence"] = session.SnapshotSequence, ["started_tick"] = startedTick, ["finished_tick"] = Find.TickManager.TicksGame, ["atomic"] = false };
        if (Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > ProtocolCodec.MaxFrameBytes - 8192) { Outcome = QueryOutcome.Failure("observation_result_too_large", "Narrow the requested management view."); return; }
        try { ProtocolCodec.ValidateQueryDataBudget(result); }
        catch (ProtocolException) { Outcome = QueryOutcome.Failure("observation_result_too_large", "The complete management view exceeds the existing JSON resource budget; narrow its bounds or selection."); return; }
        Outcome = (string?)result["status"] == "partial" ? QueryOutcome.Partial(result, "management_partial", "Inspect coverage and unknown cells.") : new QueryOutcome { Data = result };
    }
    protected override void HandleError(Exception e) => Outcome = QueryOutcome.Failure((e as GraphQL.ExecutionError)?.Code ?? "management_unavailable", e.Message);
    protected override void ReleaseReferences() { result = null; }
}
