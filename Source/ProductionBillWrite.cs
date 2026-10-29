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

internal sealed partial class VerseWriteFieldResolver
{
    private IPreparedWriteField ResolveProductionBill(JObject operation)
    {
        if (operation.Properties().Count() != 5 || operation.Properties().Any(p => p.Name != "op" && p.Name != "worktable" && p.Name != "expect" && p.Name != "value" && p.Name != "evidence")) throw new ArgumentException("Invalid production bill operation.");
        var id = (string?)operation["worktable"];
        var table = Find.Maps.SelectMany(m => m.listerBuildings.allBuildingsColonist).OfType<Building_WorkTable>().SingleOrDefault(t => t.GetUniqueLoadID() == id);
        if (table == null || (table.GetType() != typeof(Building_WorkTable) && table.GetType() != typeof(Building_WorkTable_HeatPush))) throw new ArgumentException("Only ordinary and heat-emitting player worktables support reversible bill creation.");
        return new ProductionBillAddition(table);
    }

    // This fingerprint covers every instance field on the two supported native
    // bill classes and ThingFilter, including private settings. Referenced game
    // objects/defs are identity values; their internals do not belong to a bill.
    private sealed class BillState
    {
        private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private sealed class ReferenceComparer : IEqualityComparer<object> {
            public new bool Equals(object? a, object? b) => ReferenceEquals(a, b);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
        private readonly Dictionary<object, int> identities = new(new ReferenceComparer());
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
            if (!identities.TryGetValue(value, out var identity)) { identity = identities.Count + 1; identities.Add(value, identity); }
            return new JObject { ["type"] = kind.FullName, ["identity"] = identity };
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
            return GameQueryEngine.Hash(state.ToString(Formatting.None));
        }
    }

    private sealed class ProductionBillAddition : IPreparedStructuralWrite, IWriteAggregateScope
    {
        private readonly Building_WorkTable table;
        private readonly BillState state = new();
        private Bill_Production? bill;
        private JObject? applied;
        private JObject? undoExpected;
        internal ProductionBillAddition(Building_WorkTable table) { this.table = table; }
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
        private JObject Result() {
            var result = Order();
            if (bill != null) result["created"] = new JObject { ["bill_id"] = bill.GetUniqueLoadID(), ["recipe"] = bill.recipe.defName };
            return result;
        }
        public JToken Read() => bill == null ? Order() : Result();
        public void Validate(JToken value) {
            if (table.Destroyed || table.BillStack.Count >= BillStack.MaxCount || value is not JObject obj || obj.Properties().Count() != 1 || obj["recipe"]?.Type != JTokenType.String) throw new ArgumentException("Invalid production bill request or full worktable.");
            var recipe = DefDatabase<RecipeDef>.GetNamedSilentFail((string)obj["recipe"]!) ?? throw new ArgumentException("Recipe not found.");
            if (!Official(recipe) || !ResearchReadings.UsesRecipe(table.def, recipe) || ResearchReadings.RecipeAvailableReadOnly(recipe) != true) throw new ArgumentException("Recipe is not an available official recipe on this worktable.");
            // Mirror BillUtility.MakeNewBill's subtype selection without allocating an ID.
            if (recipe.UsesUnfinishedThing || recipe.mechResurrection || recipe.gestationCycles > 0 || recipe.formingTicks > 0) throw new ArgumentException("This recipe requires a specialized bill and is not supported.");
        }
        public void Write(JToken value) {
            var recipe = DefDatabase<RecipeDef>.GetNamed((string)value["recipe"]!);
            var prior = table.BillStack.Bills.ToArray();
            var created = recipe.MakeNewBill();
            if (created.GetType() != typeof(Bill_Production)) throw new InvalidOperationException("Native bill factory returned an unsupported type.");
            bill = (Bill_Production)created;
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
}
