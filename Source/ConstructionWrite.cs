using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal sealed partial class VerseWriteFieldResolver
{
    private static Registration[] ConstructionCatalog => new[] { ConstructionRegistration(), CancellationRegistration(), RestartRegistration() };
    private static Registration ConstructionRegistration() {
        var schema = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = new JObject { ["op"] = new JObject { ["const"] = "place_blueprint" }, ["map"] = new JObject { ["type"] = "string", ["minLength"] = 1 }, ["value"] = ColonyManagementContract.ElementSchema() }, ["required"] = new JArray("op", "map", "value") };
        return new Registration(new JObject { ["op"] = "place_blueprint", ["schema"] = schema, ["expect_schema"] = JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'element_id':{'type':'string'},'map_id':{'type':'string'},'element':{'type':'object'},'stage':{'const':'absent'},'site':{'type':'array','items':{'type':'object'}}},'required':['element_id','map_id','element','stage','site']}"), ["description"] = "Place one normally qualified visible building or floor blueprint. No object clearing, replacement, instant construction or roof creation. Undo is limited to the exact unchanged unstarted blueprint.",
            ["verification_read"] = IntentRead(true),
            ["observation"] = new JObject { ["read"] = new JObject { ["tool"] = "look", ["arguments"] = new JObject { ["object"] = new JObject { ["kind"] = "construction_site", ["map"] = new JObject { ["arg"] = "map" } }, ["aspects"] = new JArray("state"), ["elements"] = new JArray(new JObject { ["value_path"] = new JArray() }) }, ["native_operation"] = "preview_construction" },
                ["extract"] = new JObject { ["kind"] = "field", ["object_id"] = new JObject { ["value_path"] = new JArray("element_id") }, ["path"] = new JArray("state"), ["allow_null"] = false, ["objects_path"] = new JArray("elements"), ["identity_path"] = new JArray("element_id"), ["status_path"] = new JArray("status"), ["status_value"] = "eligible", ["match"] = new JArray(new JObject { ["path"] = new JArray("element"), ["value"] = new JObject { ["value_path"] = new JArray() } }, new JObject { ["path"] = new JArray("map_id"), ["value"] = new JObject { ["arg"] = "map" } }) } } },
            (r, o) => One(r.ResolveConstructionPlacement(o), o));
    }
    private static JObject IntentRead(bool valueTarget) => new() { ["tool"] = "inspect_construction_intents", ["native_operation"] = "inspect_construction_intents", ["arguments"] = new JObject { ["map"] = new JObject { ["arg"] = "map" }, ["plan_id"] = valueTarget ? new JObject { ["value_path"] = new JArray("plan_id") } : new JObject { ["arg"] = "plan_id" }, ["ids"] = new JArray(valueTarget ? new JObject { ["value_path"] = new JArray("element_id") } : new JObject { ["arg"] = "element_id" }) } };
    private static Registration CancellationRegistration() => new(new JObject { ["op"] = "cancel_blueprint", ["schema"] = JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'op':{'const':'cancel_blueprint'},'map':{'type':'string','minLength':1},'plan_id':{'type':'string','minLength':1},'element_id':{'type':'string','minLength':1},'value':{'type':'boolean','const':false}},'required':['op','map','plan_id','element_id','value']}"), ["expect_schema"] = new JObject { ["type"] = "object" }, ["verification_read"] = IntentRead(false), ["observation"] = new JObject { ["read"] = IntentRead(false), ["extract"] = new JObject { ["kind"] = "field", ["object_id"] = new JObject { ["arg"] = "element_id" }, ["path"] = new JArray("cancel_guard"), ["allow_null"] = false, ["objects_path"] = new JArray("objects"), ["identity_path"] = new JArray("element_id"), ["status_path"] = new JArray("stage"), ["status_value"] = "blueprint", ["match"] = new JArray(new JObject { ["path"] = new JArray("plan_id"), ["value"] = new JObject { ["arg"] = "plan_id" } }, new JObject { ["path"] = new JArray("map_id"), ["value"] = new JObject { ["arg"] = "map" } }) } } }, (r, op) => One(r.ResolveBlueprintCancellation(op), op));
    private static Registration RestartRegistration() {
        var placement = ConstructionRegistration(); var spec = (JObject)placement.Spec.DeepClone(); spec["op"] = "restart_blueprint"; spec["schema"]!["properties"]!["op"]!["const"] = "restart_blueprint";
        spec["expect_schema"] = JObject.Parse("{'type':'object','additionalProperties':false,'properties':{'intent':{'type':'object'},'site':{'type':'object'}},'required':['intent','site']}");
        spec["observation"]!["read"] = IntentRead(true); var extract = (JObject)spec["observation"]!["extract"]!; extract["path"] = new JArray("restart_guard"); extract["objects_path"] = new JArray("objects"); extract["status_path"] = new JArray("status"); extract["status_value"] = "observed";
        return new Registration(spec, (r, op) => One(r.ResolveConstructionRestart(op), op));
    }
    private IPreparedWriteField ResolveBlueprintCancellation(JObject op) {
        var map = GameWorldReferences.ResolveMap((string?)op["map"]); var element = new JObject { ["plan_id"] = op["plan_id"]!.DeepClone(), ["element_id"] = op["element_id"]!.DeepClone() }; var entry = Current.Game.GetComponent<NekoGameComponent>().ConstructionIntents.Find(map, ConstructionIntents.Key(element)) ?? throw new ArgumentException("Construction intent unavailable."); return new BlueprintCancellation(entry);
    }
    private IPreparedWriteField ResolveConstructionRestart(JObject op) { if (op["value"] is not JObject e) throw new ArgumentException("Construction element required."); ColonyManagementContract.ValidateElement(e); var map = GameWorldReferences.ResolveMap((string?)op["map"]); var registry = Current.Game.GetComponent<NekoGameComponent>().ConstructionIntents; var original = registry.Find(map, ConstructionIntents.Key(e)) ?? throw new ArgumentException("No original construction intent to restart."); return new BlueprintRestart(map, e, registry, original); }
    private sealed class BlueprintCancellation : IPreparedStructuralWrite, IWriteAggregateScope {
        private readonly ConstructionIntent entry; private readonly Blueprint_Build? original; private bool removed;
        private readonly ConstructionIntents registry = Current.Game.GetComponent<NekoGameComponent>().ConstructionIntents;
        internal BlueprintCancellation(ConstructionIntent entry) { this.entry = entry; original = entry.Target as Blueprint_Build; }
        public string ObjectId => entry.Map!.GetUniqueLoadID(); public string Field => "construction:" + ConstructionIntents.Key(JObject.Parse(entry.ElementJson)); public bool IsDestroyed => entry.Map == null || entry.Map.Disposed;
        public bool MayChangeByGame => true; public object NativeTarget => entry.Map!; public string AggregateScope => ObjectId + ":" + Field; public bool ChangesStructure => true;
        public JToken Read() => registry.Find(entry.Map!, ConstructionIntents.Key(JObject.Parse(entry.ElementJson)))?.Evidence()["cancel_guard"]!.DeepClone() ?? JValue.CreateNull();
        public void Validate(JToken value) { if (value.Type != JTokenType.Boolean || (bool)value || original == null || !ReferenceEquals(entry.Target, original) || original.Destroyed || !original.Spawned || entry.TransitionKnown || entry.Stage != "blueprint") throw new ArgumentException("Only the original unchanged unstarted planner blueprint may be cancelled."); }
        public void Write(JToken value) { Validate(value); removed = true; entry.Stage = "model_cancelled"; original!.DeSpawn(DestroyMode.Vanish); }
        public void Restore(JToken originalState) { if (!removed) return; var e = JObject.Parse(entry.ElementJson); if (!original!.Spawned && !original.Destroyed) { ConstructionReadings.Qualify(entry.Map!, e); GenSpawn.Spawn(original!, ConstructionReadings.Position(e), entry.Map!, original.Rotation); } entry.Stage = "blueprint"; removed = false; }
        public JToken AppliedValue() => Read(); public JToken UndoExpected() => Read(); public IPreparedWriteField CreateUndo() => new Undo(this);
        private sealed class Undo : IPreparedWriteField {
            private readonly BlueprintCancellation cancel; internal Undo(BlueprintCancellation cancel) { this.cancel = cancel; }
            public string ObjectId => cancel.ObjectId; public string Field => cancel.Field; public bool IsDestroyed => cancel.IsDestroyed; public bool MayChangeByGame => true; public object NativeTarget => cancel.NativeTarget;
            public JToken Read() => cancel.Read(); public void Validate(JToken value) { if (!cancel.removed || cancel.original == null || cancel.original.Destroyed || cancel.entry.Stage != "model_cancelled" || !ReferenceEquals(cancel.registry.Find(cancel.entry.Map!, ConstructionIntents.Key(JObject.Parse(cancel.entry.ElementJson))), cancel.entry)) throw new ArgumentException("Cancelled original generation is unavailable."); ConstructionReadings.Qualify(cancel.entry.Map!, JObject.Parse(cancel.entry.ElementJson)); }
            public void Write(JToken value) => cancel.Restore(value); public void Restore(JToken value) { if (!cancel.removed) cancel.Write(new JValue(false)); }
        }
    }
    private interface IConstructionClaim { Map ClaimMap { get; } JObject ClaimElement { get; } }
    private static bool ConstructionConflicts(IConstructionClaim claim, IPreparedWriteField other) {
        if (other is not IConstructionClaim second || claim.ClaimMap != second.ClaimMap) return false;
        var a = ConstructionReadings.Definition(claim.ClaimElement); var b = ConstructionReadings.Definition(second.ClaimElement);
        var firstRect = ConstructionReadings.Footprint(a, claim.ClaimElement); var secondRect = ConstructionReadings.Footprint(b, second.ClaimElement);
        if (!firstRect.Overlaps(secondRect)) return false;
        return a is TerrainDef && b is TerrainDef || GenSpawn.SpawningWipes(a.blueprintDef, b.blueprintDef, false) || GenSpawn.SpawningWipes(b.blueprintDef, a.blueprintDef, false) || a is ThingDef ta && b is ThingDef tb && (GenSpawn.SpawningWipes(ta, tb, false) || GenSpawn.SpawningWipes(tb, ta, false));
    }
    private sealed class BlueprintRestart : IPreparedStructuralWrite, IWriteAggregateScope, IConstructionClaim, IWriteConflictScope {
        private readonly Map map; private readonly JObject element; private readonly ConstructionIntents registry; private readonly ConstructionIntent prior; private readonly BlueprintPlacement placement; private bool applied;
        internal BlueprintRestart(Map map, JObject element, ConstructionIntents registry, ConstructionIntent prior) { this.map = map; this.element = (JObject)element.DeepClone(); this.registry = registry; this.prior = prior; placement = new BlueprintPlacement(map, element, registry); }
        public string ObjectId => placement.ObjectId; public string Field => placement.Field; public bool IsDestroyed => placement.IsDestroyed; public bool MayChangeByGame => true; public object NativeTarget => map; public string AggregateScope => placement.AggregateScope; public bool ChangesStructure => true;
        public Map ClaimMap => map; public JObject ClaimElement => element; public bool ConflictsWith(IPreparedWriteField other) => ConstructionConflicts(this, other);
        public JToken Read() => applied ? placement.Read() : prior.Evidence()["restart_guard"]?.DeepClone() ?? JValue.CreateNull();
        public void Validate(JToken value) { var state = (string?)prior.Evidence()["stage"]; if (state != "destroyed" && state != "user_cancelled" && state != "model_cancelled" || prior.Target != null && prior.Target.Spawned || !JToken.DeepEquals(JObject.Parse(prior.ElementJson), element)) throw new ArgumentException("Only a known cancelled or destroyed identical element can explicitly restart."); ConstructionReadings.Qualify(map, element); }
        public void Write(JToken value) { Validate(value); registry.Remove(prior); applied = true; placement.Write(value); }
        public void Restore(JToken original) { if (!applied) return; placement.Restore(original); registry.Restore(prior); applied = false; }
        public JToken AppliedValue() => Read(); public JToken UndoExpected() => Read(); public IPreparedWriteField CreateUndo() => new Undo(this, placement.CreateUndo());
        private sealed class Undo : IPreparedWriteField {
            private readonly BlueprintRestart restart; private readonly IPreparedWriteField remove; internal Undo(BlueprintRestart restart, IPreparedWriteField remove) { this.restart = restart; this.remove = remove; }
            public string ObjectId => restart.ObjectId; public string Field => restart.Field; public bool IsDestroyed => restart.IsDestroyed; public bool MayChangeByGame => true; public object NativeTarget => restart.NativeTarget;
            public JToken Read() => restart.Read(); public void Validate(JToken value) => remove.Validate(value);
            public void Write(JToken value) { remove.Write(value); restart.registry.Restore(restart.prior); restart.applied = false; }
            public void Restore(JToken value) { restart.registry.Remove(restart.prior); remove.Restore(value); restart.applied = true; }
        }
    }
    private IPreparedWriteField ResolveConstructionPlacement(JObject op) {
        if (op.Properties().Any(p => !new[] { "op", "map", "expect", "value", "evidence" }.Contains(p.Name)) || op["value"] is not JObject e) throw new ArgumentException("Invalid blueprint placement.");
        ColonyManagementContract.ValidateElement(e); var map = GameWorldReferences.ResolveMap((string?)op["map"]);
        return new BlueprintPlacement(map, (JObject)e.DeepClone(), Current.Game.GetComponent<NekoGameComponent>().ConstructionIntents);
    }
    internal IPreparedWriteField? ResolveConstructionLedger(string id, string field, object? target, JToken value) {
        if (target is not Map map || map.GetUniqueLoadID() != id || !field.StartsWith("construction:", StringComparison.Ordinal)) return null;
        var entry = Current.Game.GetComponent<NekoGameComponent>().ConstructionIntents.Find(map, field.Substring(13));
        return entry == null ? null : new BlueprintPlacement(map, JObject.Parse(entry.ElementJson), Current.Game.GetComponent<NekoGameComponent>().ConstructionIntents);
    }
    private sealed class BlueprintPlacement : IPreparedStructuralWrite, IWriteAggregateScope, IConstructionClaim, IWriteConflictScope
    {
        private readonly Map map; private readonly JObject element; private readonly ConstructionIntents registry;
        private Blueprint_Build? created; private ConstructionIntent? registered;
        internal BlueprintPlacement(Map map, JObject e, ConstructionIntents registry) { this.map = map; element = e; this.registry = registry; }
        public string ObjectId => map.GetUniqueLoadID(); public string Field => "construction:" + ConstructionIntents.Key(element);
        public bool IsDestroyed => map.Disposed; public bool MayChangeByGame => true; public object NativeTarget => map;
        public string AggregateScope => ObjectId + ":" + Field; public bool ChangesStructure => true;
        public Map ClaimMap => map; public JObject ClaimElement => element; public bool ConflictsWith(IPreparedWriteField other) => ConstructionConflicts(this, other);
        public JToken Read() => registry.Find(map, ConstructionIntents.Key(element))?.Evidence() ?? ConstructionReadings.SiteState(map, element);
        public void Validate(JToken value) { if (!JToken.DeepEquals(value, element)) throw new ArgumentException("Construction element differs from its prepared value."); if (registry.Find(map, ConstructionIntents.Key(element)) != null) throw new ArgumentException("Construction element identity is already in use."); ConstructionReadings.Qualify(map, element); }
        public void Write(JToken value) { ConstructionReadings.Qualify(map, element); var d = ConstructionReadings.Definition(element); created = GenConstruct.PlaceBlueprintForBuild(d, ConstructionReadings.Position(element), map, new Rot4((int)element["rotation"]!), Faction.OfPlayer, ConstructionReadings.Stuff(d, element)); if (created == null || !created.Spawned) throw new InvalidOperationException("Native blueprint creation was not observed."); registered = registry.Register(map, element, created); }
        public JToken AppliedValue() => Read(); public JToken UndoExpected() => Read();
        public void Restore(JToken original) { if (created != null && !created.Destroyed) created.Destroy(DestroyMode.Cancel); if (registered != null) registry.Remove(registered); }
        public IPreparedWriteField CreateUndo() => new BlueprintRemoval(this);
        private sealed class BlueprintRemoval : IPreparedWriteField, IWriteAggregateScope {
            private readonly BlueprintPlacement placement; private bool removed; private JToken? before;
            internal BlueprintRemoval(BlueprintPlacement p) { placement = p; }
            public string ObjectId => placement.ObjectId; public string Field => placement.Field; public bool IsDestroyed => placement.IsDestroyed; public bool MayChangeByGame => true; public object NativeTarget => placement.map;
            public string AggregateScope => placement.AggregateScope; public bool ChangesStructure => true;
            public JToken Read() => placement.Read();
            public void Validate(JToken value) { var entry = placement.registry.Find(placement.map, ConstructionIntents.Key(placement.element)); if (entry == null || !ReferenceEquals(entry.Target, placement.created) || placement.created == null || placement.created.Destroyed || !placement.created.Spawned || entry.Stage != "blueprint" || entry.TransitionKnown) throw new ArgumentException("Only the original unchanged unstarted blueprint can be cancelled."); }
            public void Write(JToken value) { Validate(value); before = placement.Read(); removed = true; placement.created!.DeSpawn(DestroyMode.Vanish); placement.registry.Remove(placement.registered!); }
            public void Restore(JToken value) { if (!removed) return; if (!placement.created!.Spawned && !placement.created.Destroyed) GenSpawn.Spawn(placement.created!, ConstructionReadings.Position(placement.element), placement.map, placement.created.Rotation); placement.registry.Restore(placement.registered!); removed = false; }
        }
    }
}
