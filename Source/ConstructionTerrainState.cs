using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

// Frame.CompleteConstruction writes these three independent native terrain
// layers. A bridge remains a foundation even when a floor covers it later.
internal static class ConstructionTerrainState
{
    internal static string Layer(TerrainDef definition) => definition.isFoundation ? "foundation" : definition.temporary ? "temporary" : "top";
    internal static TerrainDef? At(Map map, IntVec3 cell, TerrainDef definition) => definition.isFoundation ? map.terrainGrid.FoundationAt(cell)
        : definition.temporary ? map.terrainGrid.TempTerrainAt(cell) : map.terrainGrid.TopTerrainAt(cell);
    private static JToken Definition(TerrainDef? definition) => definition == null ? JValue.CreateNull() : new JValue(definition.defName);
    internal static JObject Layers(Map map, IntVec3 cell) => new() {
        ["top"] = Definition(map.terrainGrid.TopTerrainAt(cell)), ["foundation"] = Definition(map.terrainGrid.FoundationAt(cell)),
        ["temporary"] = Definition(map.terrainGrid.TempTerrainAt(cell))
    };
    internal static JObject Observe(Map map, IntVec3 cell, TerrainDef expected)
    {
        var actual = At(map, cell, expected);
        return new JObject { ["layer"] = Layer(expected), ["expected_definition"] = expected.defName,
            ["actual_definition"] = Definition(actual), ["matches"] = ReferenceEquals(actual, expected) };
    }
}

internal static class ConstructionTerrainCompletion
{
    internal static void Record(TerrainGrid grid, IntVec3 cell, TerrainDef terrain, string layer)
    {
        var from = ConstructionCompletionScope.Completing; var map = ConstructionCompletionScope.CompletingMap;
        if (from == null || map == null || map.Disposed || !ReferenceEquals(map.terrainGrid, grid) || from.Position != cell || !cell.InBounds(map)
            || !ReferenceEquals(from.def.entityDefToBuild, terrain) || ConstructionTerrainState.Layer(terrain) != layer || !ReferenceEquals(ConstructionTerrainState.At(map, cell, terrain), terrain)) return;
        Current.Game?.GetComponent<NekoGameComponent>()?.Save.ConstructionIntents.TerrainCompleted(from, map, cell, terrain);
    }
}
