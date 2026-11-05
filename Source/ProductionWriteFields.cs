using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

// Production bills on ordinary player worktables: creation, copy, order,
// removal and complete settings.
internal sealed class ProductionWriteFields : IWriteFieldSet
{
    public IEnumerable<WriteOperationBinding> Operations => new[] {
        WriteOperationBinding.Single("add_production_bill", ResolveProductionBill),
        new WriteOperationBinding("set_production_bill", ResolveProductionSettings),
        WriteOperationBinding.Single("copy_production_bill", ResolveProductionCopy),
        WriteOperationBinding.Single("reorder_production_bills", ResolveProductionOrder),
        WriteOperationBinding.Single("remove_production_bill", ResolveProductionRemoval),
    };
    public IReadableWriteField? ResolveLedger(string id, string field, object? target, JToken value) =>
        field == "production_bills" ? ResolveLedgerProductionBills(id, target, value) : ResolveLedgerProductionField(id, field, target);

    private static IPreparedWriteField ResolveProductionBill(JObject operation)
    {
        if (operation.Properties().Count() != 5 || operation.Properties().Any(p => p.Name != "op" && p.Name != "worktable" && p.Name != "expect" && p.Name != "value" && p.Name != "evidence")) throw new ArgumentException("Invalid production bill operation.");
        var id = (string?)operation["worktable"];
        var table = Find.Maps.SelectMany(m => m.listerBuildings.allBuildingsColonist).OfType<Building_WorkTable>().SingleOrDefault(t => t.GetUniqueLoadID() == id);
        if (table == null || (table.GetType() != typeof(Building_WorkTable) && table.GetType() != typeof(Building_WorkTable_HeatPush))) throw new ArgumentException("Only ordinary and heat-emitting player worktables support reversible bill creation.");
        return new ProductionBillAddition(table);
    }

    private static Building_WorkTable ProductionTable(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Worktable identity required.");
        var table = Find.Maps.SelectMany(m => m.listerBuildings.allBuildingsColonist).OfType<Building_WorkTable>().SingleOrDefault(t => t.GetUniqueLoadID() == id);
        if (table == null || table.Destroyed || (table.GetType() != typeof(Building_WorkTable) && table.GetType() != typeof(Building_WorkTable_HeatPush))) throw new ArgumentException("Supported player worktable not found.");
        return table;
    }
    private static Bill_Production ProductionTarget(Building_WorkTable table, string? id)
    {
        var bill = table.BillStack.Bills.OfType<Bill_Production>().SingleOrDefault(b => b.GetUniqueLoadID() == id);
        if (bill == null || bill.deleted || !ProductionReadings.Standard(bill) || !OfficialContent.Contains(bill.recipe)) throw new ArgumentException("Supported official production bill not found.");
        return bill;
    }
    private static Building_WorkTable ProductionTable(JObject operation)
    {
        var table = ProductionTable((string?)operation["worktable"]);
        if (operation["map"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)operation["map"]) || (string?)operation["map"] != table.MapHeld?.GetUniqueLoadID()) throw new ArgumentException("Worktable does not belong to the requested map identity.");
        return table;
    }
    private static void ProductionKeys(JObject operation, params string[] extras)
    {
        var allowed = new HashSet<string>(new[] { "op", "worktable", "map", "bill", "expect", "value", "evidence" }.Concat(extras), StringComparer.Ordinal);
        if (operation.Properties().Any(p => !allowed.Contains(p.Name))) throw new ArgumentException("Unexpected production operation property.");
    }
    private static IReadOnlyList<ResolvedWriteField> ResolveProductionSettings(JObject operation)
    {
        ProductionKeys(operation);
        var table = ProductionTable(operation); var bill = ProductionTarget(table, (string?)operation["bill"]);
        if (operation["value"] is not JObject value || !value.HasValues || value.Properties().Any(p => !ProductionBillSettings.Names.Contains(p.Name))) throw new ArgumentException("Invalid production settings.");
        if (operation["expect"] is not JObject expected || !expected.Properties().Select(p => p.Name).OrderBy(n => n).SequenceEqual(value.Properties().Select(p => p.Name).OrderBy(n => n))) throw new ArgumentException("Production settings and expected keys must match.");
        return value.Properties().Select(p => new ResolvedWriteField(new ProductionSettingField(table, bill, p.Name), expected[p.Name]!, ProductionBillSettings.Normalize(p.Name, p.Value))).ToArray();
    }
    private static IPreparedWriteField ResolveProductionOrder(JObject operation) { ProductionKeys(operation); return new ProductionOrderField(ProductionTable(operation)); }
    private static IPreparedWriteField ResolveProductionRemoval(JObject operation) { ProductionKeys(operation); var table = ProductionTable(operation); return new ProductionRemovalField(table, ProductionTarget(table, (string?)operation["bill"])); }
    private static IPreparedWriteField ResolveProductionCopy(JObject operation) { ProductionKeys(operation); var table = ProductionTable(operation); return new ProductionCopyField(table, ProductionTarget(table, (string?)operation["bill"])); }

    private static IReadableWriteField? ResolveLedgerProductionField(string id, string field, object? target)
    {
        if (!field.StartsWith("bill:", StringComparison.Ordinal)) return null;
        var name = field.Substring(5); if (!ProductionBillSettings.Names.Contains(name)) return null;
        var pair = Find.Maps.SelectMany(m => m.listerBuildings.allBuildingsColonist).OfType<Building_WorkTable>()
            .SelectMany(t => t.BillStack.Bills.OfType<Bill_Production>().Select(b => (table: t, bill: b))).SingleOrDefault(p => p.bill.GetUniqueLoadID() == id);
        if (pair.bill == null || (target != null && !ReferenceEquals(target, pair.bill))) return null;
        return new ProductionSettingField(pair.table, pair.bill, name);
    }

    private static IReadableWriteField? ResolveLedgerProductionBills(string id, object? target, JToken value) {
        if (target is not Building_WorkTable table || table.GetUniqueLoadID() != id || table.Destroyed || table.BillStack == null || value is not JObject recorded) return null;
        string? createdId = null;
        if (recorded.TryGetValue("created", out var created)) {
            if (created is not JObject obj || obj["bill_id"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)obj["bill_id"])) return null;
            createdId = (string)obj["bill_id"]!;
        }
        var removedId = recorded["removed"]?.Type == JTokenType.String ? (string?)recorded["removed"] : null;
        return new ProductionBillLedgerField(table, createdId, removedId);
    }
    private sealed class ProductionSettingField : IPreparedWriteField, IWriteAggregateScope
    {
        private readonly Building_WorkTable table; private readonly Bill_Production bill; private readonly string name;
        internal ProductionSettingField(Building_WorkTable table, Bill_Production bill, string name) { this.table = table; this.bill = bill; this.name = name; }
        public string ObjectId => bill.GetUniqueLoadID(); public string Field => "bill:" + name;
        public string AggregateScope => table.GetUniqueLoadID(); public bool ChangesStructure => false;
        public object NativeTarget => bill; public bool IsDestroyed => table.Destroyed || bill.deleted || !table.BillStack.Bills.Contains(bill);
        public bool MayChangeByGame => name == "repeat_count" || name == "suspended";
        public JToken Read() => ProductionReadings.Settings(bill)[name]!;
        public void Validate(JToken value) { if (IsDestroyed || !ProductionReadings.Standard(bill)) throw new ArgumentException("Production bill unavailable."); ProductionBillSettings.Validate(table, bill.recipe, name, value); }
        public void Write(JToken value) => ProductionBillSettings.Write(table, bill, name, value);
        public void Restore(JToken value) => Write(value);
    }

    private sealed class ProductionOrderField : IPreparedWriteField, IWriteAggregateScope
    {
        private readonly Building_WorkTable table;
        internal ProductionOrderField(Building_WorkTable table) { this.table = table; }
        public string ObjectId => table.GetUniqueLoadID(); public string Field => "production_bills"; public object NativeTarget => table;
        public bool IsDestroyed => table.Destroyed; public bool MayChangeByGame => false; public string AggregateScope => ObjectId; public bool ChangesStructure => true;
        public JToken Read() => new JObject { ["bill_ids"] = new JArray(table.BillStack.Bills.Select(b => b.GetUniqueLoadID())) };
        public void Validate(JToken value) {
            if (IsDestroyed) throw new ArgumentException("Worktable unavailable."); var obj = ProductionBillSettings.Shape(value, "bill_ids");
            if (obj["bill_ids"] is not JArray ids || ids.Any(t => t.Type != JTokenType.String) || !ids.Select(t => t.Value<string>()!).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(table.BillStack.Bills.Select(b => b.GetUniqueLoadID()).OrderBy(n => n, StringComparer.Ordinal))) throw new ArgumentException("Bill order must be a complete permutation of current identities.");
        }
        public void Write(JToken value) {
            var ids = ((JArray)value["bill_ids"]!).Select(t => t.Value<string>()!).ToArray();
            for (var i = 0; i < ids.Length; i++) { var bill = table.BillStack.Bills.Single(b => b.GetUniqueLoadID() == ids[i]); table.BillStack.Reorder(bill, i - table.BillStack.Bills.IndexOf(bill)); }
        }
        public void Restore(JToken value) => Write(value);
    }

    private static bool ProductionReferenced(Bill bill) => Find.Maps.SelectMany(m => m.mapPawns.AllPawns).Concat(Find.WorldPawns.AllPawnsAliveOrDead)
        .Any(p => p.jobs?.curJob?.bill == bill || p.jobs?.jobQueue.Any(j => j.job?.bill == bill) == true);
    internal static JObject RemovalGuard(Building_WorkTable table, Bill_Production bill) => new() {
        ["bill_ids"] = new JArray(table.BillStack.Bills.Select(b => b.GetUniqueLoadID())), ["bill_id"] = bill.GetUniqueLoadID(), ["recipe"] = bill.recipe.defName,
        ["state_fingerprint"] = new ProductionBillState().Fingerprint(bill), ["unfinished_id"] = ProductionReadings.Text((bill as Bill_ProductionWithUft)?.BoundUft?.GetUniqueLoadID())
    };
    internal static JObject CopyGuard(Building_WorkTable table, Bill_Production bill) => new() {
        ["bill_ids"] = new JArray(table.BillStack.Bills.Select(b => b.GetUniqueLoadID())),
        ["source"] = ProductionCopySource(bill)
    };
    private static JObject ProductionCopySource(Bill_Production bill) => new() { ["bill_id"] = bill.GetUniqueLoadID(), ["recipe"] = bill.recipe.defName, ["state_fingerprint"] = new ProductionBillState().Fingerprint(bill) };

    private sealed class ProductionCopyField : IPreparedStructuralWrite, IWriteAggregateScope
    {
        private readonly Building_WorkTable table; private readonly Bill_Production source; private readonly ProductionBillAddition addition;
        private bool applied;
        internal ProductionCopyField(Building_WorkTable table, Bill_Production source) { this.table = table; this.source = source; addition = new ProductionBillAddition(table, source); }
        public string ObjectId => table.GetUniqueLoadID(); public string Field => "production_bills"; public object NativeTarget => table;
        public string AggregateScope => ObjectId; public bool ChangesStructure => true; public bool IsDestroyed => table.Destroyed || source.deleted; public bool MayChangeByGame => false;
        public JToken Read() => applied ? addition.Read() : CopyGuard(table, source);
        private JObject Request() => new() { ["recipe"] = source.recipe.defName, ["settings"] = ProductionReadings.Settings(source) };
        public void Validate(JToken value) {
            if (IsDestroyed || !table.BillStack.Bills.Contains(source) || value.Type != JTokenType.Boolean || !(bool)value) throw new ArgumentException("Bill copy requires an existing standard source and value true.");
            addition.Validate(Request());
        }
        public void Write(JToken value) { applied = true; addition.Write(Request()); }
        public void Restore(JToken value) { addition.Restore(value); applied = false; }
        public JToken AppliedValue() => addition.AppliedValue();
        public JToken UndoExpected() { var expected = (JObject)addition.UndoExpected().DeepClone(); expected["source"] = ProductionCopySource(source); return expected; }
        public IPreparedWriteField CreateUndo() => new CopyUndo(this, addition.CreateUndo());
        private sealed class CopyUndo : IPreparedWriteField, IWriteAggregateScope {
            private readonly ProductionCopyField copy; private readonly IPreparedWriteField inner;
            internal CopyUndo(ProductionCopyField copy, IPreparedWriteField inner) { this.copy = copy; this.inner = inner; }
            public string ObjectId => copy.ObjectId; public string Field => copy.Field; public object NativeTarget => copy.table;
            public string AggregateScope => ObjectId; public bool ChangesStructure => true; public bool IsDestroyed => copy.IsDestroyed; public bool MayChangeByGame => false;
            public JToken Read() { var state = (JObject)inner.Read().DeepClone(); state["source"] = ProductionCopySource(copy.source); return state; }
            public void Validate(JToken value) { if (copy.source.deleted || !copy.table.BillStack.Bills.Contains(copy.source)) throw new ArgumentException("Copy source unavailable."); inner.Validate(value); }
            public void Write(JToken value) => inner.Write(value);
            public void Restore(JToken value) => inner.Restore(value);
        }
    }

    private sealed class ProductionRemovalField : IPreparedStructuralWrite, IWriteAggregateScope
    {
        private readonly Building_WorkTable table; private readonly Bill_Production bill; private int index = -1; private bool removed;
        private JObject? result;
        internal ProductionRemovalField(Building_WorkTable table, Bill_Production bill) { this.table = table; this.bill = bill; }
        public string ObjectId => table.GetUniqueLoadID(); public string Field => "production_bills"; public object NativeTarget => table;
        public bool IsDestroyed => table.Destroyed; public bool MayChangeByGame => false; public string AggregateScope => ObjectId; public bool ChangesStructure => true;
        public JToken Read() => removed ? new JObject { ["bill_ids"] = new JArray(table.BillStack.Bills.Select(b => b.GetUniqueLoadID())), ["removed"] = bill.GetUniqueLoadID() } : RemovalGuard(table, bill);
        public void Validate(JToken value) {
            if (IsDestroyed || bill.deleted || !table.BillStack.Bills.Contains(bill) || value.Type != JTokenType.Boolean || (bool)value) throw new ArgumentException("Bill removal requires an existing bill and value false.");
            if (bill is Bill_ProductionWithUft uft && uft.BoundUft != null) throw new ArgumentException("Suspend a bill with bound unfinished work; removal does not cancel or reclaim its materials.");
            if (ProductionReferenced(bill)) throw new ArgumentException("Bill is referenced by a current or queued Job.");
        }
        public void Write(JToken value) { index = table.BillStack.Bills.IndexOf(bill); removed = true; table.BillStack.Delete(bill); result = new JObject { ["bill_ids"] = new JArray(table.BillStack.Bills.Select(b => b.GetUniqueLoadID())), ["removed"] = bill.GetUniqueLoadID() }; }
        public void Restore(JToken value) { if (!removed) return; bill.deleted = false; bill.billStack = table.BillStack; if (!table.BillStack.Bills.Contains(bill)) table.BillStack.Bills.Insert(index, bill); removed = false; }
        public JToken AppliedValue() => result ?? throw new InvalidOperationException("Bill was not removed.");
        public JToken UndoExpected() => AppliedValue();
        public IPreparedWriteField CreateUndo() => new ProductionRestoreField(this);
        private sealed class ProductionRestoreField : IPreparedWriteField, IWriteAggregateScope {
            private readonly ProductionRemovalField removal;
            internal ProductionRestoreField(ProductionRemovalField removal) { this.removal = removal; }
            public string ObjectId => removal.ObjectId; public string Field => removal.Field; public object NativeTarget => removal.table;
            public bool IsDestroyed => removal.IsDestroyed; public bool MayChangeByGame => false; public string AggregateScope => ObjectId; public bool ChangesStructure => true;
            public JToken Read() => removal.removed ? new JObject { ["bill_ids"] = new JArray(removal.table.BillStack.Bills.Select(b => b.GetUniqueLoadID())), ["removed"] = removal.bill.GetUniqueLoadID() } : removal.Read();
            public void Validate(JToken value) { if (IsDestroyed || removal.table.BillStack.Count >= BillStack.MaxCount || removal.table.BillStack.Bills.Contains(removal.bill) || !removal.bill.deleted) throw new ArgumentException("Removed bill cannot be restored."); }
            public void Write(JToken value) => removal.Restore(value);
            public void Restore(JToken value) => removal.Write(new JValue(false));
        }
    }
}
