using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

// This fingerprint covers every instance field on the supported native
// bill classes and ThingFilter, including private settings. Referenced game
// objects/defs are identity values; their internals do not belong to a bill.
internal sealed class ProductionBillState
{
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private JToken Value(object? value) {
        if (value == null) return JValue.CreateNull();
        if (value is string text) return new JValue(text);
        if (value is Def def) return new JObject { ["def"] = def.defName, ["type"] = def.GetType().FullName };
        if (value is ILoadReferenceable reference) return new JObject { ["id"] = reference.GetUniqueLoadID(), ["type"] = value.GetType().FullName };
        if (value is Type type) return new JValue(type.FullName);
        if (value is ThingFilter filter) return Fields(filter, typeof(ThingFilter));
        var kind = value.GetType();
        if (kind.IsEnum) return new JValue(value.ToString());
        if (kind.IsPrimitive || value is decimal) return JToken.FromObject(value);
        if (value is IEnumerable sequence) {
            var values = new List<JToken>(); foreach (var item in sequence) values.Add(Value(item));
            if (kind.IsGenericType && kind.GetGenericTypeDefinition() == typeof(HashSet<>)) values = values.OrderBy(v => v.ToString(Formatting.None), StringComparer.Ordinal).ToList();
            return new JArray(values);
        }
        if (kind.IsValueType) return Fields(value, kind);
        return new JObject { ["type"] = kind.FullName, ["identity"] = RuntimeHelpers.GetHashCode(value) };
    }
    private JObject Fields(object value, Type type) {
        var result = new JObject();
        foreach (var field in type.GetFields(Declared).OrderBy(f => f.Name, StringComparer.Ordinal)) {
            if (field.IsStatic || (type == typeof(Bill) && field.Name == "billStack")) continue;
            result[field.Name] = Value(field.GetValue(value));
        }
        return result;
    }
    internal string Fingerprint(Bill_Production bill) {
        var state = new JObject { ["bill"] = Fields(bill, typeof(Bill)), ["production"] = Fields(bill, typeof(Bill_Production)) };
        if (bill is Bill_ProductionWithUft) state["unfinished_binding"] = Fields(bill, typeof(Bill_ProductionWithUft));
        return GameQueryEngine.Hash(state.ToString(Formatting.None));
    }
}

// Shared with ledger sampling: createdId is only a lookup key. All returned
// values are taken from the current complete native stack.
internal static class ProductionBillStack
{
    internal static JObject Read(Building_WorkTable table, string? createdId) {
        var bills = table.BillStack.Bills;
        var result = new JObject { ["bill_ids"] = new JArray(bills.Select(b => b.GetUniqueLoadID())) };
        if (createdId != null) {
            var matches = bills.Where(b => b.GetUniqueLoadID() == createdId).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Ambiguous bill identity.");
            if (matches.Length == 0) result["created"] = JValue.CreateNull();
            else if (matches[0] is Bill_Production production && production.recipe != null)
                result["created"] = new JObject { ["bill_id"] = production.GetUniqueLoadID(), ["recipe"] = production.recipe.defName,
                    ["runtime_type"] = production.GetType().FullName, ["settings"] = ProductionReadings.Settings(production), ["presentation"] = ProductionReadings.Presentation(production),
                    ["unfinished_id"] = ProductionReadings.Text((production as Bill_ProductionWithUft)?.BoundUft?.GetUniqueLoadID()) };
            else throw new InvalidOperationException("Bill identity cannot be read.");
        }
        return result;
    }
}

internal sealed class ProductionBillLedgerField : IReadableWriteField {
    private readonly Building_WorkTable table;
    private readonly string? createdId;
    private readonly string? removedId;
    internal ProductionBillLedgerField(Building_WorkTable table, string? createdId, string? removedId) { this.table = table; this.createdId = createdId; this.removedId = removedId; }
    public string ObjectId => table.GetUniqueLoadID();
    public string Field => "production_bills";
    public bool IsDestroyed => table.Destroyed;
    public bool MayChangeByGame => false;
    public JToken Read() { var result = ProductionBillStack.Read(table, createdId); if (removedId != null) result["removed"] = removedId; return result; }
}

internal sealed class ProductionBillAddition : IPreparedStructuralWrite, IWriteAggregateScope
{
    private readonly Building_WorkTable table;
    private readonly Bill_Production? presentationSource;
    private readonly ProductionBillState state = new();
    private Bill_Production? bill;
    private JObject? applied;
    private JObject? undoExpected;
    internal ProductionBillAddition(Building_WorkTable table, Bill_Production? presentationSource = null) { this.table = table; this.presentationSource = presentationSource; }
    public string ObjectId => table.GetUniqueLoadID();
    public string Field => "production_bills";
    public bool MayChangeByGame => false;
    public bool IsDestroyed => table.Destroyed;
    public object NativeTarget => table;
    public string AggregateScope => ObjectId;
    public bool ChangesStructure => true;
    private JObject Order() => new() { ["bill_ids"] = new JArray(table.BillStack.Bills.Select(b => b.GetUniqueLoadID())) };
    private JObject Current() {
        var result = Order();
        if (bill != null) result["created"] = new JObject { ["bill_id"] = bill.GetUniqueLoadID(), ["recipe"] = bill.recipe.defName,
            ["suspended"] = bill.suspended, ["repeat_mode"] = bill.repeatMode?.defName, ["repeat_count"] = bill.repeatCount,
            ["target_count"] = bill.targetCount, ["state_fingerprint"] = state.Fingerprint(bill) };
        return result;
    }
    private JObject Result() => ProductionBillStack.Read(table, bill?.GetUniqueLoadID());
    public JToken Read() => bill == null ? Order() : Result();
    public void Validate(JToken value) {
        if (table.Destroyed || table.BillStack.Count >= BillStack.MaxCount || value is not JObject obj || obj.Properties().Any(p => p.Name != "recipe" && p.Name != "settings") || obj["recipe"]?.Type != JTokenType.String) throw new ArgumentException("Invalid production bill request or full worktable.");
        var recipe = DefDatabase<RecipeDef>.GetNamedSilentFail((string)obj["recipe"]!) ?? throw new ArgumentException("Recipe not found.");
        if (!OfficialContent.Contains(recipe) || !ResearchReadings.UsesRecipe(table.def, recipe) || ResearchReadings.RecipeAvailableReadOnly(recipe) != true) throw new ArgumentException("Recipe is not an available official recipe on this worktable.");
        // Mirror BillUtility.MakeNewBill's subtype selection without allocating an ID.
        if (ProductionReadings.SurgeryReadOnly(recipe) || recipe.mechResurrection || recipe.gestationCycles > 0 || recipe.formingTicks > 0) throw new ArgumentException("This recipe requires a specialized bill and is not supported.");
        if (obj.TryGetValue("settings", out var settings)) {
            if (settings is not JObject fields || !fields.HasValues || fields.Properties().Any(p => !ProductionBillSettings.Names.Contains(p.Name))) throw new ArgumentException("Invalid initial production settings.");
            foreach (var field in fields.Properties()) ProductionBillSettings.Validate(table, recipe, field.Name, field.Value);
        }
    }
    public void Write(JToken value) {
        var recipe = DefDatabase<RecipeDef>.GetNamed((string)value["recipe"]!);
        var prior = table.BillStack.Bills.ToArray();
        var created = recipe.MakeNewBill();
        if (created is not Bill_Production production || !ProductionReadings.Standard(production) || (created is Bill_ProductionWithUft) != recipe.UsesUnfinishedThing) throw new InvalidOperationException("Native bill factory returned an unsupported type.");
        bill = (Bill_Production)created;
        if (presentationSource != null) ProductionReadings.CopyPresentation(presentationSource, bill);
        if (value["settings"] is JObject settings) {
            foreach (var field in settings.Properties()) ProductionBillSettings.Write(table, bill, field.Name, field.Value);
            var observed = ProductionReadings.Settings(bill);
            foreach (var field in settings.Properties()) if (!JToken.DeepEquals(observed[field.Name], ProductionBillSettings.Normalize(field.Name, field.Value))) throw new InvalidOperationException("Native initial bill setting differs from its requested value: " + field.Name);
        }
        table.BillStack.AddBill(bill);
        if (bill.recipe != recipe || bill.deleted || bill.billStack != table.BillStack || table.BillStack.Count != prior.Length + 1 || !table.BillStack.Bills.Take(prior.Length).SequenceEqual(prior) || !ReferenceEquals(table.BillStack.Bills[prior.Length], bill) || table.BillStack.Bills.Count(b => b.GetUniqueLoadID() == bill.GetUniqueLoadID()) != 1) throw new InvalidOperationException("Native bill readback differs from the requested addition.");
        applied = Result();
        undoExpected = Current();
    }
    public JToken AppliedValue() => applied ?? throw new InvalidOperationException("Bill was not created.");
    public JToken UndoExpected() => undoExpected ?? throw new InvalidOperationException("Bill was not created.");
    public void Restore(JToken original) {
        if (bill != null && table.BillStack.Bills.Contains(bill)) table.BillStack.Delete(bill);
        bill = null;
    }
    public IPreparedWriteField CreateUndo() => new Removal(this);

    private sealed class Removal : IPreparedWriteField, IWriteAggregateScope
    {
        private readonly ProductionBillAddition add;
        private int index = -1;
        private bool removed;
        internal Removal(ProductionBillAddition add) { this.add = add; }
        public string ObjectId => add.ObjectId; public string Field => add.Field; public bool MayChangeByGame => false;
        public bool IsDestroyed => add.IsDestroyed; public object NativeTarget => add.table;
        public string AggregateScope => add.AggregateScope; public bool ChangesStructure => true;
        public JToken Read() => removed ? add.Order() : add.Current();
        public void Validate(JToken value) {
            var bill = add.bill ?? throw new ArgumentException("Bill unavailable.");
            if (add.table.Destroyed) throw new ArgumentException("Worktable unavailable.");
            if (bill is Bill_ProductionWithUft uft && uft.BoundUft != null) throw new ArgumentException("Bill has bound unfinished work; suspend it instead of undoing its creation.");
            if (Find.Maps.SelectMany(m => m.mapPawns.AllPawns).Concat(Find.WorldPawns.AllPawnsAliveOrDead).Any(p => p.jobs?.curJob?.bill == bill || p.jobs?.jobQueue.Any(j => j.job?.bill == bill) == true)) throw new ArgumentException("Bill is referenced by a current or queued job.");
        }
        public void Write(JToken value) {
            var bill = add.bill!; index = add.table.BillStack.Bills.IndexOf(bill);
            if (index < 0) throw new InvalidOperationException("Bill unavailable.");
            // Mark before Delete so a throw after its mutation is restorable.
            removed = true; add.table.BillStack.Delete(bill);
        }
        public void Restore(JToken value) {
            if (!removed) return;
            var bill = add.bill!; bill.deleted = false; bill.billStack = add.table.BillStack;
            if (!add.table.BillStack.Bills.Contains(bill)) add.table.BillStack.Bills.Insert(index, bill);
            removed = false;
        }
    }
}
