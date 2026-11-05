using System.Linq;

namespace Neko.RimWorld;

// The live native write field directory: every domain field set, in the
// precedence the ledger uses when a recorded field identity is recovered.
internal sealed class NativeWriteFields
{
    internal NativeWriteFields(IGameData data)
    {
        var sets = new IWriteFieldSet[] {
            new ProductionWriteFields(), new PolicyWriteFields(), new BedWriteFields(data), new ConstructionWriteFields(),
            new AreaCellFlagsWriteFields(), new AreaZoneWriteFields(), new PreparationWriteFields(), new CoreNativeWriteFields(data)
        };
        Operations = new WriteFieldDispatcher(sets, WriteCatalog.Operations.Select(spec => (string)spec["op"]!));
        Ledger = new LedgerFieldRouter(sets);
    }
    internal WriteFieldDispatcher Operations { get; }
    internal LedgerFieldRouter Ledger { get; }
}
