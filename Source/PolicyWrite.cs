using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal sealed partial class VerseWriteFieldResolver
{
    private static Registration FoodRegistration(string op, string properties, string required, string path, string? policyArg = null)
    {
        var schema = JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'op':{'const':'" + op + "'}," + properties + "},'required':['op'," + required + "]}");
        var arguments = new JObject { ["object"] = new JObject { ["kind"] = "food_policy" }, ["aspects"] = new JArray("content", "users") };
        if (policyArg != null) arguments["object"]!["id"] = new JObject { ["arg"] = policyArg };
        var extract = new JObject
        {
            ["kind"] = "field",
            ["object_id"] = policyArg == null ? JValue.CreateNull() : new JObject { ["arg"] = policyArg },
            ["path"] = new JArray(path),
            ["allow_null"] = false,
            ["objects_path"] = policyArg == null ? new JArray() : new JArray("objects"),
            ["identity_path"] = policyArg == null ? new JArray() : new JArray("id"),
            ["status_path"] = new JArray("status"),
            ["status_value"] = policyArg == null ? "ok" : "observed"
        };
        var contentSchema = JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'native_instance':{'type':'string','minLength':1},'label':{'type':'string'},'filter':{'type':'object'},'users':{'type':'array','items':{'type':'string','minLength':1}},'users_complete':{'const':true}},'required':['native_instance','label','filter','users','users_complete']}");
        JObject expected;
        if (op == "set_food_policy_content") expected = contentSchema;
        else
        {
            expected = JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'policy_ids':{'type':'array','items':{'type':'string','minLength':1}}},'required':['policy_ids']}");
            if (policyArg != null)
            {
                var name = op == "copy_food_policy" ? "source" : "policy";
                expected["properties"]![name + "_id"] = JObject.Parse("{'type':'string','minLength':1}");
                expected["properties"]![name] = contentSchema;
                ((JArray)expected["required"]!).Add(name + "_id"); ((JArray)expected["required"]!).Add(name);
            }
        }
        return new Registration(new JObject
        {
            ["op"] = op,
            ["schema"] = schema,
            ["expect_schema"] = expected,
            ["verification_read"] = new JObject { ["tool"] = "inspect_food_policies", ["native_operation"] = "inspect_food_policies", ["arguments"] = new JObject() },
            ["observation"] = new JObject { ["read"] = new JObject { ["tool"] = "look", ["arguments"] = arguments, ["native_operation"] = "inspect_food_policies" }, ["extract"] = extract }
        },
            (r, o) => One(r.ResolveFoodOperation(o), o));
    }
    private static readonly Registration[] PolicyCatalog = {
        FoodRegistration("create_food_policy", "'policy':{'const':'new'},'value':{'type':'object','additionalProperties':false,'properties':{'label':{'type':'string','minLength':1,'maxLength':128}},'required':['label']}", "'policy','value'", "structure"),
        FoodRegistration("copy_food_policy", "'policy':{'type':'string','minLength':1},'value':{'type':'object','additionalProperties':false,'properties':{'label':{'type':'string','minLength':1,'maxLength':128}},'required':['label']}", "'policy','value'", "copy_expect", "policy"),
        FoodRegistration("set_food_policy_content", "'policy':{'type':'string','minLength':1},'value':{'type':'object','additionalProperties':false,'properties':{'label':{'type':'string','minLength':1,'maxLength':128},'filter':{'type':'object'},'impact':{'type':'object'}},'required':['label','filter','impact']}", "'policy','value'", "write_content", "policy"),
        FoodRegistration("delete_food_policy", "'policy':{'type':'string','minLength':1},'value':{'type':'object','additionalProperties':false,'properties':{'impact':{'type':'object'}},'required':['impact']}", "'policy','value'", "delete_expect", "policy"),
        new Registration(JObject.Parse(@"{'op':'set_food_policy','schema':{'type':'object','additionalProperties':false,'properties':{'op':{'const':'set_food_policy'},'pawn':{'type':'string','minLength':1},'value':{'type':'object','additionalProperties':false,'properties':{'policy_id':{'type':'string','minLength':1},'native_instance':{'type':'string','minLength':1}},'required':['policy_id','native_instance']}},'required':['op','pawn','value']},'observation':{'read':{'tool':'look','arguments':{'object':{'kind':'pawn','id':{'arg':'pawn'}},'aspects':['work']},'native_operation':'inspect_state'},'extract':{'kind':'field','object_id':{'arg':'pawn'},'path':['facets','work','food_policy_key'],'allow_null':false,'objects_path':['objects'],'identity_path':['target','id'],'status_path':['facets','work','status'],'status_value':'observed'}}}"), (r,o) => One(r.ResolveFoodOperation(o),o))
    };

    private IPreparedWriteField ResolveFoodOperation(JObject operation)
    {
        var op = (string?)operation["op"];
        if (operation.Properties().Any(p => p.Name != "op" && p.Name != "policy" && p.Name != "pawn" && p.Name != "expect" && p.Name != "value" && p.Name != "evidence")) throw new ArgumentException("Invalid food policy operation.");
        if (op == "set_food_policy") return new FoodAssignment(PolicyReadings.Pawn((string?)operation["pawn"]));
        if (op == "create_food_policy") return new FoodPolicyStructure(null, true);
        var policy = PolicyReadings.Resolve((string?)operation["policy"]);
        if (op == "copy_food_policy") return new FoodPolicyStructure(policy, true);
        if (op == "delete_food_policy") return new FoodPolicyStructure(policy, false);
        if (op == "set_food_policy_content") return new FoodContent(policy);
        throw new ArgumentException("Unsupported food policy operation.");
    }

    private static string FoodLabel(JToken value)
    {
        var label = (string?)value["label"];
        if (value["label"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(label) || label!.Length > 128) throw new ArgumentException("Food policy label must contain 1–128 characters.");
        return label;
    }
    private static void RequireFoodImpact(FoodPolicy policy, JToken? impact)
    {
        if (impact is not JObject obj || obj.Properties().Any(p => p.Name != "mode" && p.Name != "users")) throw new ArgumentException("Explicit shared food-policy impact is required.");
        var mode = (string?)obj["mode"];
        if (mode == "dynamic_shared")
        {
            if (obj["users"] != null) throw new ArgumentException("Dynamic shared impact has no fixed users.");
            return;
        }
        if (mode != "fixed_users" || obj["users"] is not JArray users || users.Any(u => u.Type != JTokenType.String) || users.Count != users.Distinct(JToken.EqualityComparer).Count()) throw new ArgumentException("Fixed food-policy impact requires unique pawn IDs.");
        if (!JToken.DeepEquals(new JArray(users.Values<string>().OrderBy(x => x, StringComparer.Ordinal)), PolicyReadings.Users(policy))) throw new InvalidOperationException("Food-policy users changed outside the authorized impact scope.");
    }
    private static void ValidateFoodFilter(JToken value, ThingFilter current)
    {
        if (value is not JObject obj || obj.Properties().Any(p => p.Name != "allowed_defs" && p.Name != "special_filters" && p.Name != "hitpoints" && p.Name != "quality" && p.Name != "mental_break_chance") || obj.Count != 5) throw new ArgumentException("Complete native food filter required.");
        if (obj["allowed_defs"] is not JArray defs || defs.Any(x => x.Type != JTokenType.String) || defs.Distinct(JToken.EqualityComparer).Count() != defs.Count) throw new ArgumentException("Unique allowed food definitions required.");
        var retained = new HashSet<ThingDef>(current.AllowedThingDefs);
        foreach (var name in defs.Values<string>())
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
            // Existing allowances can include latent UI entries and Mod defs.
            // Only newly allowed definitions must pass the native UI parent gate.
            if (def != null && retained.Contains(def)) continue;
            if (def == null || !Official(def) || !(def.GetStatValueAbstract(StatDefOf.Nutrition, null) > 0)) throw new ArgumentException("New food allowances require a loaded official definition with positive native Nutrition: " + name);
        }
        if (obj["special_filters"] is not JObject specials || specials.Count != DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.Count || specials.Properties().Any(p => p.Value.Type != JTokenType.Boolean || DefDatabase<SpecialThingFilterDef>.GetNamedSilentFail(p.Name) == null)) throw new ArgumentException("Complete native special-filter allowances required.");
        ValidateFoodRange(obj["hitpoints"]!, "hitpoints"); ValidateFoodRange(obj["mental_break_chance"]!, "mental_break_chance");
        if (obj["quality"] is not JArray quality || quality.Count != 2 || quality.Any(q => q.Type != JTokenType.String || !Enum.TryParse((string?)q, false, out QualityCategory c) || !Enum.IsDefined(typeof(QualityCategory), c)) || Enum.Parse(typeof(QualityCategory), (string)quality[0]!).GetHashCode() > Enum.Parse(typeof(QualityCategory), (string)quality[1]!).GetHashCode()) throw new ArgumentException("Invalid native quality range.");
    }
    private static void ValidateFoodRange(JToken value, string name)
    {
        if (value is not JArray a || a.Count != 2 || a.Any(x => x.Type != JTokenType.Float && x.Type != JTokenType.Integer)) throw new ArgumentException("Invalid " + name + " range.");
        var min = (float)a[0]; var max = (float)a[1];
        if (float.IsNaN(min) || float.IsNaN(max) || min < 0 || max > 1 || min > max) throw new ArgumentException("Invalid " + name + " range.");
    }
    private static void ApplyFoodFilter(ThingFilter filter, JToken value)
    {
        if (JToken.DeepEquals(PolicyReadings.Filter(filter), value)) return;
        filter.SetDisallowAll();
        foreach (var name in ((JArray)value["allowed_defs"]!).Values<string>()) filter.SetAllow(DefDatabase<ThingDef>.GetNamed(name), true);
        foreach (var property in ((JObject)value["special_filters"]!).Properties()) filter.SetAllow(DefDatabase<SpecialThingFilterDef>.GetNamed(property.Name), (bool)property.Value);
        filter.AllowedHitPointsPercents = new FloatRange((float)value["hitpoints"]![0]!, (float)value["hitpoints"]![1]!);
        filter.AllowedMentalBreakChance = new FloatRange((float)value["mental_break_chance"]![0]!, (float)value["mental_break_chance"]![1]!);
        filter.AllowedQualityLevels = new QualityRange((QualityCategory)Enum.Parse(typeof(QualityCategory), (string)value["quality"]![0]!), (QualityCategory)Enum.Parse(typeof(QualityCategory), (string)value["quality"]![1]!));
    }

    private sealed class FoodAssignment : IPreparedWriteField
    {
        private readonly Pawn pawn;
        internal FoodAssignment(Pawn pawn) { this.pawn = pawn; }
        public string ObjectId => pawn.GetUniqueLoadID(); public string Field => "food_policy";
        public bool IsDestroyed => pawn.Destroyed; public bool MayChangeByGame => true; public object NativeTarget => pawn;
        public JToken Read() => PolicyReadings.AssignedPolicyKey(pawn);
        public void Validate(JToken value)
        {
            if (pawn.Destroyed || pawn.Dead || !pawn.IsFreeColonist || !pawn.foodRestriction.Configurable || PolicyReadings.AssignedPolicy(pawn) == null) throw new ArgumentException("A living configurable free colonist is required.");
            if (value is not JObject obj || obj.Count != 2 || obj["policy_id"]?.Type != JTokenType.String || obj["native_instance"]?.Type != JTokenType.String || !JToken.DeepEquals(value, PolicyReadings.Key(PolicyReadings.Resolve((string?)obj["policy_id"])))) throw new ArgumentException("Current food policy ID and native instance key required.");
        }
        public void Write(JToken value) { Validate(value); pawn.foodRestriction.CurrentFoodPolicy = PolicyReadings.Resolve((string?)value["policy_id"]); }
        public void Restore(JToken value) => Write(value);
    }
    private sealed class FoodContent : IPreparedStructuralWrite, IWriteAggregateScope
    {
        private readonly FoodPolicy policy;
        internal FoodContent(FoodPolicy policy) { this.policy = policy; }
        public string ObjectId => PolicyReadings.Id(policy); public string Field => "food_policy_content";
        public bool IsDestroyed => !PolicyReadings.Database.AllFoodRestrictions.Contains(policy); public bool MayChangeByGame => true; public object NativeTarget => policy;
        public string AggregateScope => "food_policies"; public bool ChangesStructure => false;
        public JToken Read() => PolicyReadings.Content(policy);
        public void Validate(JToken value)
        {
            if (value is not JObject obj || obj.Count != 3 || obj.Properties().Any(p => p.Name != "label" && p.Name != "filter" && p.Name != "impact")) throw new ArgumentException("Label, complete filter and impact are required.");
            FoodLabel(value); ValidateFoodFilter(value["filter"]!, policy.filter); RequireFoodImpact(policy, value["impact"]);
        }
        public void Write(JToken value) { RequireFoodImpact(policy, value["impact"]); policy.label = FoodLabel(value); ApplyFoodFilter(policy.filter, value["filter"]!); }
        public void Restore(JToken original) { policy.label = (string)original["label"]!; ApplyFoodFilter(policy.filter, original["filter"]!); }
        public JToken AppliedValue() => Read(); public JToken UndoExpected() => Read();
        public IPreparedWriteField CreateUndo() => new FoodContentUndo(policy);
    }
    private sealed class FoodContentUndo : IPreparedWriteField
    {
        private readonly FoodPolicy policy;
        internal FoodContentUndo(FoodPolicy policy) { this.policy = policy; }
        public string ObjectId => PolicyReadings.Id(policy); public string Field => "food_policy_content";
        public bool IsDestroyed => !PolicyReadings.Database.AllFoodRestrictions.Contains(policy); public bool MayChangeByGame => true; public object NativeTarget => policy;
        public JToken Read() => PolicyReadings.Content(policy);
        public void Validate(JToken value) { if (!JToken.DeepEquals(value["users"], PolicyReadings.Users(policy))) throw new InvalidOperationException("Food-policy users changed before undo."); }
        public void Write(JToken value) { policy.label = (string)value["label"]!; ApplyFoodFilter(policy.filter, value["filter"]!); }
        public void Restore(JToken value) => Write(value);
    }

    private sealed class FoodPolicyStructure : IPreparedStructuralWrite, IWriteAggregateScope
    {
        private readonly FoodPolicy? source; private readonly bool addition;
        private FoodPolicy? created; private int removedIndex = -1; private JObject? applied; private bool restored;
        internal FoodPolicyStructure(FoodPolicy? source, bool addition) { this.source = source; this.addition = addition; }
        public string ObjectId => "food_policies"; public string Field => "food_policy_structure";
        public bool IsDestroyed => false; public bool MayChangeByGame => true; public object? NativeTarget => null;
        public string AggregateScope => "food_policies"; public bool ChangesStructure => true;
        public JToken Read()
        {
            var state = PolicyReadings.Structure();
            if (source != null) { state[addition ? "source_id" : "policy_id"] = PolicyReadings.Id(source); state[addition ? "source" : "policy"] = PolicyReadings.Database.AllFoodRestrictions.Contains(source) ? PolicyReadings.Content(source) : JValue.CreateNull(); }
            if (created != null && !restored) state["created"] = PolicyReadings.Database.AllFoodRestrictions.Contains(created) ? new JObject { ["policy_id"] = PolicyReadings.Id(created), ["content"] = PolicyReadings.Content(created) } : JValue.CreateNull();
            return state;
        }
        public void Validate(JToken value)
        {
            if (value is not JObject obj) throw new ArgumentException("Food-policy structure value required.");
            if (addition) { if (obj.Count != 1) throw new ArgumentException("Only label is accepted when creating or copying a food policy."); FoodLabel(value); }
            else { if (obj.Count != 1 || source == null) throw new ArgumentException("Explicit delete impact required."); RequireFoodImpact(source, value["impact"]); RequireDeletable(source); }
        }
        private static void RequireDeletable(FoodPolicy policy)
        {
            if (!PolicyReadings.Database.AllFoodRestrictions.Contains(policy) || ReferenceEquals(PolicyReadings.Database.AllFoodRestrictions.FirstOrDefault(), policy) || PolicyReadings.Users(policy).Count != 0) throw new InvalidOperationException("Only an existing unreferenced non-default food policy may be deleted.");
        }
        public void Write(JToken value)
        {
            if (addition) { created = PolicyReadings.Database.MakeNewFoodRestriction(); if (source != null) created.CopyFrom(source); created.label = FoodLabel(value); }
            else { RequireFoodImpact(source!, value["impact"]); RequireDeletable(source!); removedIndex = PolicyReadings.Database.AllFoodRestrictions.IndexOf(source!); var accepted = PolicyReadings.Database.TryDelete(source!); if (!accepted.Accepted) throw new InvalidOperationException(accepted.Reason); }
            applied = (JObject)Read();
        }
        public void Restore(JToken original)
        {
            var list = PolicyReadings.Database.AllFoodRestrictions;
            if (addition && created != null && list.Contains(created)) { RequireDeletable(created); var accepted = PolicyReadings.Database.TryDelete(created); if (!accepted.Accepted) throw new InvalidOperationException(accepted.Reason); }
            if (!addition && source != null && !list.Contains(source)) list.Insert(removedIndex, source);
            restored = true;
        }
        public JToken AppliedValue() => applied ?? throw new InvalidOperationException("Food-policy structure not applied.");
        public JToken UndoExpected() => Read();
        public IPreparedWriteField CreateUndo() => new FoodStructureUndo(this);
        private sealed class FoodStructureUndo : IPreparedStructuralWrite
        {
            private readonly FoodPolicyStructure owner;
            internal FoodStructureUndo(FoodPolicyStructure owner) { this.owner = owner; }
            public string ObjectId => owner.ObjectId; public string Field => owner.Field; public bool IsDestroyed => false;
            public bool MayChangeByGame => true; public object? NativeTarget => null;
            public JToken Read() => owner.Read();
            public void Validate(JToken value) { if (owner.addition && owner.created != null) RequireDeletable(owner.created); }
            public void Write(JToken value) => owner.Restore(value);
            public void Restore(JToken value)
            { // Reverse an interrupted undo using the original retained objects.
                var list = PolicyReadings.Database.AllFoodRestrictions;
                if (owner.addition && owner.created != null && !list.Contains(owner.created)) list.Add(owner.created);
                if (!owner.addition && owner.source != null && list.Contains(owner.source)) list.Remove(owner.source);
                owner.restored = false;
            }
            public JToken AppliedValue() => Read(); public JToken UndoExpected() => Read(); public IPreparedWriteField CreateUndo() => throw new NotSupportedException();
        }
    }
    private sealed class FoodStructureLedger : IPreparedWriteField
    {
        private readonly JObject recorded;
        internal FoodStructureLedger(JObject recorded) { this.recorded = (JObject)recorded.DeepClone(); }
        public string ObjectId => "food_policies"; public string Field => "food_policy_structure";
        public bool IsDestroyed => false; public bool MayChangeByGame => true; public object? NativeTarget => null;
        public JToken Read()
        {
            var state = PolicyReadings.Structure();
            foreach (var pair in new[] { ("source_id", "source"), ("policy_id", "policy") })
            {
                if (recorded[pair.Item1]?.Type != JTokenType.String) continue;
                var id = (string)recorded[pair.Item1]!; state[pair.Item1] = id;
                var policy = PolicyReadings.Database.AllFoodRestrictions.SingleOrDefault(p => PolicyReadings.Id(p) == id);
                state[pair.Item2] = policy == null ? JValue.CreateNull() : PolicyReadings.Content(policy);
            }
            if (recorded["created"] is JObject created)
            {
                var id = (string?)created["policy_id"];
                var policy = PolicyReadings.Database.AllFoodRestrictions.SingleOrDefault(p => PolicyReadings.Id(p) == id);
                state["created"] = policy == null ? JValue.CreateNull() : new JObject { ["policy_id"] = id, ["content"] = PolicyReadings.Content(policy) };
            }
            return state;
        }
        public void Validate(JToken value) => throw new NotSupportedException();
        public void Write(JToken value) => throw new NotSupportedException();
        public void Restore(JToken value) => throw new NotSupportedException();
    }
    internal IPreparedWriteField? ResolvePolicyLedger(string id, string field, object? target, JToken value)
    {
        if (field == "food_policy" && target is Pawn pawn && pawn.GetUniqueLoadID() == id && pawn.foodRestriction != null) return new FoodAssignment(pawn);
        if (field == "food_policy_content" && target is FoodPolicy policy && PolicyReadings.Id(policy) == id) return new FoodContentUndo(policy);
        if (field == "food_policy_structure" && id == "food_policies" && value is JObject recorded) return new FoodStructureLedger(recorded);
        return null;
    }
}


