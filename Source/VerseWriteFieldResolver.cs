using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

// Native fields are only reachable through the internal transaction factory.
internal sealed class VerseWriteFieldResolver : IWriteFieldResolver
{
    private readonly IGameData data;
    private static bool Official(Def def)
    {
        var package = def.modContentPack?.PackageId?.ToLowerInvariant();
        return package == "ludeon.rimworld" || package == "ludeon.rimworld.royalty" ||
            package == "ludeon.rimworld.ideology" || package == "ludeon.rimworld.biotech" ||
            package == "ludeon.rimworld.anomaly" || package == "ludeon.rimworld.odyssey";
    }

    internal VerseWriteFieldResolver(IGameData data) { this.data = data; }
    internal static AtomicWriteOperation Begin(IGameData data, JObject request, Func<string> currentEpoch, bool dryRun) =>
        new(new VerseWriteFieldResolver(data), request, currentEpoch, dryRun, () => UnityData.IsInMainThread && Current.ProgramState == ProgramState.Playing && !LongEventHandler.AnyEventNowOrWaiting);

    // Each entry is independently resolved and validated; the transaction engine
    // has no operation-specific branches.
    private static readonly IReadOnlyDictionary<string, Func<VerseWriteFieldResolver, JObject, IReadOnlyList<ResolvedWriteField>>> Catalog =
        new Dictionary<string, Func<VerseWriteFieldResolver, JObject, IReadOnlyList<ResolvedWriteField>>>(StringComparer.Ordinal) {
            ["set_work_priority"] = (r, o) => One(r.ResolveWorkPriority(o), o),
            ["set_medical_care"] = (r, o) => One(r.ResolveMedical(o), o),
            ["set_allowed_area"] = (r, o) => One(r.ResolveArea(o), o),
            ["set_research"] = (r, o) => One(r.ResolveResearch(o), o),
            ["set_forbidden"] = (r, o) => One(r.ResolveForbidden(o), o),
            ["set_bill"] = (r, o) => r.ResolveBillFields(o),
            ["set_storage_priority"] = (r, o) => One(r.ResolveStorage(o, "set_storage_priority"), o),
            ["set_storage_allow"] = (r, o) => One(r.ResolveStorage(o, "set_storage_allow"), o),
        };
    public IReadOnlyList<ResolvedWriteField> Resolve(JObject operation)
    {
        var kind = (string?)operation["op"];
        if (kind == null || !Catalog.TryGetValue(kind, out var resolve)) throw new ArgumentException("Unsupported write operation.");
        return resolve(this, operation);
    }

    private static IReadOnlyList<ResolvedWriteField> One(IPreparedWriteField field, JObject operation) =>
        new[] { new ResolvedWriteField(field, operation["expect"]!, operation["value"]!) };

    private IReadOnlyList<ResolvedWriteField> ResolveBillFields(JObject operation)
    {
        if (operation["value"] is not JObject values || operation["expect"] is not JObject expected ||
            !values.HasValues || !values.Properties().Select(p => p.Name).OrderBy(x => x)
                .SequenceEqual(expected.Properties().Select(p => p.Name).OrderBy(x => x)))
            throw new ArgumentException("Bill fields must match expected fields.");
        var result = new List<ResolvedWriteField>();
        foreach (var property in values.Properties()) {
            var single = (JObject)operation.DeepClone();
            single["value"] = new JObject { [property.Name] = property.Value.DeepClone() };
            single["expect"] = new JObject { [property.Name] = expected[property.Name]!.DeepClone() };
            result.Add(new ResolvedWriteField(ResolveBill(single), expected[property.Name]!, property.Value));
        }
        return result;
    }

    private IPreparedWriteField ResolveWorkPriority(JObject operation)
    {
        var pawnId = (string?)operation["pawn"];
        var workName = (string?)operation["work"];
        if (string.IsNullOrWhiteSpace(pawnId) || string.IsNullOrWhiteSpace(workName) ||
            operation.Properties().Any(p => p.Name != "op" && p.Name != "pawn" && p.Name != "work" && p.Name != "expect" && p.Name != "value" && p.Name != "evidence"))
            throw new ArgumentException("Invalid work priority target.");
        var pawn = data.Colonists().SingleOrDefault(x => x.Id == pawnId)?.Handle as Pawn ?? throw new ArgumentException("Colonist not found.");
        var work = DefDatabase<WorkTypeDef>.GetNamedSilentFail(workName) ?? throw new ArgumentException("Work type not found.");
        if (!Official(work)) throw new ArgumentException("Only official work types can be changed.");
        return new WorkPriorityField(pawn, work);
    }

    private IPreparedWriteField ResolveResearch(JObject operation)
    {
        if ((string?)operation["research"] != "current" || operation.Properties().Any(p => p.Name != "op" && p.Name != "research" && p.Name != "expect" && p.Name != "value" && p.Name != "evidence")) throw new ArgumentException("Invalid research target.");
        return new ResearchField();
    }

    private sealed class ResearchField : IPreparedWriteField
    {
        public string ObjectId => "research:current";
        public bool IsDestroyed => false;
        public object? NativeTarget => null;
        public string Field => "current_project";
        public bool MayChangeByGame => true;
        public JToken Read() => Find.ResearchManager.GetProject() is ResearchProjectDef project ? new JValue(project.defName) : JValue.CreateNull();
        public void Validate(JToken value) {
            if (Find.ResearchManager == null) throw new ArgumentException("Research manager unavailable.");
            var current = Find.ResearchManager.GetProject();
            if (current != null && (current.baseCost <= 0 || current.knowledgeCategory != null)) throw new ArgumentException("Current project cannot be restored without affecting other research.");
            if (value.Type == JTokenType.Null) return;
            if (value.Type != JTokenType.String) throw new ArgumentException("Research must be a project name or null.");
            var project = DefDatabase<ResearchProjectDef>.GetNamedSilentFail((string)value!) ?? throw new ArgumentException("Research project not found.");
            if (!Official(project) || project.baseCost <= 0 || project.knowledgeCategory != null || ResearchReadings.CanStartReadOnly(project) != true) throw new ArgumentException("Research project cannot be started.");
        }
        public void Write(JToken value) {
            var manager = Find.ResearchManager;
            if (value.Type == JTokenType.Null) { var current = manager.GetProject(); if (current != null) manager.StopProject(current); }
            else manager.SetCurrentProject(DefDatabase<ResearchProjectDef>.GetNamed((string)value!));
        }
        public void Restore(JToken value) => Write(value);
    }

    private IPreparedWriteField ResolveArea(JObject operation)
    {
        var id = (string?)operation["pawn"];
        if (string.IsNullOrWhiteSpace(id) || operation.Properties().Any(p => p.Name != "op" && p.Name != "pawn" && p.Name != "expect" && p.Name != "value" && p.Name != "evidence")) throw new ArgumentException("Invalid area target.");
        var pawn = data.Colonists().SingleOrDefault(x => x.Id == id)?.Handle as Pawn ?? throw new ArgumentException("Colonist not found.");
        return new AreaField(pawn);
    }

    private sealed class AreaField : IPreparedWriteField
    {
        private readonly Pawn pawn;
        internal AreaField(Pawn pawn) { this.pawn = pawn; }
        public string ObjectId => pawn.GetUniqueLoadID();
        public bool IsDestroyed => pawn.Destroyed;
        public object? NativeTarget => pawn;
        public string Field => "allowed_area";
        public bool MayChangeByGame => true;
        public JToken Read() {
            var area = pawn.playerSettings.AreaRestrictionInPawnCurrentMap;
            return new JObject { ["map_id"] = pawn.MapHeld.GetUniqueLoadID(), ["area_id"] = area == null ? JValue.CreateNull() : new JValue(area.ID) };
        }
        public void Validate(JToken value) {
            if (pawn.Destroyed || pawn.MapHeld == null || pawn.playerSettings == null || !pawn.playerSettings.RespectsAllowedArea || pawn.jobs?.curJob != null) throw new ArgumentException("Area change requires a spawned eligible pawn without a current job.");
            if (pawn.playerSettings.AreaRestrictionInPawnCurrentMap != null && pawn.playerSettings.AreaRestrictionInPawnCurrentMap is not Area_Allowed) throw new ArgumentException("Existing area cannot be restored safely.");
            if (value is not JObject obj || obj.Properties().Count() != 2 || (string?)obj["map_id"] != pawn.MapHeld.GetUniqueLoadID() || obj["area_id"] == null) throw new ArgumentException("Area key must identify the pawn's current map.");
            if (obj["area_id"]!.Type != JTokenType.Null && (obj["area_id"]!.Type != JTokenType.Integer || !pawn.MapHeld.areaManager.AllAreas.OfType<Area_Allowed>().Any(a => a.ID == (int)obj["area_id"]!))) throw new ArgumentException("Allowed area not found on the pawn's current map.");
        }
        public void Write(JToken value) {
            var id = value["area_id"]!;
            pawn.playerSettings.AreaRestrictionInPawnCurrentMap = id.Type == JTokenType.Null ? null : pawn.MapHeld.areaManager.AllAreas.OfType<Area_Allowed>().Single(a => a.ID == (int)id);
        }
        public void Restore(JToken value) => Write(value);
    }

    private IPreparedWriteField ResolveMedical(JObject operation)
    {
        var id = (string?)operation["pawn"];
        if (string.IsNullOrWhiteSpace(id) || operation.Properties().Any(p => p.Name != "op" && p.Name != "pawn" && p.Name != "expect" && p.Name != "value" && p.Name != "evidence")) throw new ArgumentException("Invalid medical target.");
        var pawn = data.Colonists().SingleOrDefault(x => x.Id == id)?.Handle as Pawn ?? throw new ArgumentException("Colonist not found.");
        return new MedicalField(pawn);
    }

    private IPreparedWriteField ResolveForbidden(JObject operation)
    {
        var id = (string?)operation["thing"];
        if (string.IsNullOrWhiteSpace(id) || operation.Properties().Any(p => p.Name != "op" && p.Name != "thing" && p.Name != "expect" && p.Name != "value" && p.Name != "evidence")) throw new ArgumentException("Invalid forbidden target.");
        var thing = Find.Maps.SelectMany(m => m.listerThings.AllThings).SingleOrDefault(t => t.GetUniqueLoadID() == id) ?? throw new ArgumentException("Thing not found.");
        var comp = thing.TryGetComp<CompForbiddable>() ?? throw new ArgumentException("Thing is not forbiddable.");
        return new ForbiddenField(thing, comp);
    }

    private IPreparedWriteField ResolveStorage(JObject operation, string kind)
    {
        var id = (string?)operation["storage"];
        if (string.IsNullOrWhiteSpace(id) || operation.Properties().Any(p => p.Name != "op" && p.Name != "storage" && p.Name != "item" && p.Name != "expect" && p.Name != "value" && p.Name != "evidence")) throw new ArgumentException("Invalid storage target.");
        var thing = Find.Maps.SelectMany(m => m.listerThings.AllThings).SingleOrDefault(t => t.GetUniqueLoadID() == id);
        var zone = Find.Maps.SelectMany(m => m.zoneManager.AllZones).OfType<Zone_Stockpile>().SingleOrDefault(z => z.GetUniqueLoadID() == id);
        ISlotGroupParent? storage = thing is ISlotGroupParent building && thing.Faction == Faction.OfPlayer ? building : zone;
        if (storage == null) throw new ArgumentException("Target is not player storage.");
        var settings = storage.GetStoreSettings() ?? throw new ArgumentException("Storage settings unavailable.");
        Func<bool> valid = () => thing != null ? !thing.Destroyed : zone != null && zone.Map != null && zone.Map.zoneManager.AllZones.Contains(zone);
        if (kind == "set_storage_priority") {
            if (operation["item"] != null) throw new ArgumentException("Priority does not take item.");
            return new StoragePriorityField(id!, valid, () => thing != null ? thing.Destroyed : zone?.zoneManager != null && !zone.zoneManager.AllZones.Contains(zone), (object?)thing ?? zone!, settings);
        }
        var name = (string?)operation["item"];
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Item definition required.");
        var item = DefDatabase<ThingDef>.GetNamedSilentFail(name) ?? throw new ArgumentException("Item definition not found.");
        return new StorageAllowField(id!, valid, () => thing != null ? thing.Destroyed : zone?.zoneManager != null && !zone.zoneManager.AllZones.Contains(zone), (object?)thing ?? zone!, settings, item);
    }

    private sealed class StoragePriorityField : IPreparedWriteField
    {
        private readonly string id;
        private readonly Func<bool> valid;
        private readonly Func<bool> destroyed;
        private readonly object nativeTarget;
        private readonly StorageSettings settings;
        internal StoragePriorityField(string id, Func<bool> valid, Func<bool> destroyed, object nativeTarget, StorageSettings settings) { this.id = id; this.valid = valid; this.destroyed = destroyed; this.nativeTarget = nativeTarget; this.settings = settings; }
        public string ObjectId => id;
        public bool IsDestroyed => destroyed();
        public object? NativeTarget => nativeTarget;
        public string Field => "storage_priority";
        public bool MayChangeByGame => false;
        public JToken Read() => new JValue(settings.Priority.ToString());
        public void Validate(JToken value) { if (!valid() || value.Type != JTokenType.String || !Enum.TryParse<StoragePriority>((string?)value, false, out var priority) || !Enum.IsDefined(typeof(StoragePriority), priority) || priority == StoragePriority.Unstored) throw new ArgumentException("Invalid storage priority."); }
        public void Write(JToken value) => settings.Priority = (StoragePriority)Enum.Parse(typeof(StoragePriority), (string)value!);
        public void Restore(JToken value) => Write(value);
    }

    private sealed class StorageAllowField : IPreparedWriteField
    {
        private readonly string id;
        private readonly Func<bool> valid;
        private readonly Func<bool> destroyed;
        private readonly object nativeTarget;
        private readonly StorageSettings settings;
        private readonly ThingDef item;
        internal StorageAllowField(string id, Func<bool> valid, Func<bool> destroyed, object nativeTarget, StorageSettings settings, ThingDef item) { this.id = id; this.valid = valid; this.destroyed = destroyed; this.nativeTarget = nativeTarget; this.settings = settings; this.item = item; }
        public string ObjectId => id;
        public bool IsDestroyed => destroyed();
        public object? NativeTarget => nativeTarget;
        public string Field => "storage_allow:" + item.defName;
        public bool MayChangeByGame => false;
        public JToken Read() => new JValue(settings.filter.Allows(item));
        public void Validate(JToken value) {
            if (!valid() || value.Type != JTokenType.Boolean) throw new ArgumentException("Invalid storage filter value.");
            var parent = settings.owner?.GetParentStoreSettings();
            NativeWriteSafety.RequireStorageItem(Official(item), item.virtualDefs != null && item.virtualDefs.Count != 0,
                parent == null || parent.AllowedToAccept(item));
        }
        public void Write(JToken value) => settings.filter.SetAllow(item, (bool)value);
        public void Restore(JToken value) => Write(value);
    }

    private IPreparedWriteField ResolveBill(JObject operation)
    {
        var id = (string?)operation["bill"];
        if (string.IsNullOrWhiteSpace(id) || operation.Properties().Any(p => p.Name != "op" && p.Name != "bill" && p.Name != "expect" && p.Name != "value" && p.Name != "evidence")) throw new ArgumentException("Invalid bill target.");
        if (operation["value"] is not JObject value || !value.HasValues || value.Properties().Any(p => p.Name != "suspended" && p.Name != "repeat_mode" && p.Name != "repeat_count" && p.Name != "target_count")) throw new ArgumentException("Invalid bill fields.");
        if (operation["expect"] is not JObject expect || !expect.Properties().Select(p => p.Name).OrderBy(x => x).SequenceEqual(value.Properties().Select(p => p.Name).OrderBy(x => x))) throw new ArgumentException("Bill fields must match expected fields.");
        var pair = Find.Maps.SelectMany(m => m.listerBuildings.allBuildingsColonist).OfType<Building_WorkTable>()
            .SelectMany(table => table.BillStack.Bills.OfType<Bill_Production>().Select(bill => (table, bill)))
            .SingleOrDefault(x => x.bill.GetUniqueLoadID() == id);
        if (pair.bill == null) throw new ArgumentException("Production bill on a player worktable not found.");
        if (!Official(pair.bill.recipe)) throw new ArgumentException("Only official recipes can be changed.");
        return new BillField(pair.table, pair.bill, value.Properties().Select(p => p.Name).ToArray());
    }

    private sealed class BillField : IPreparedWriteField
    {
        private readonly Building_WorkTable table;
        private readonly Bill_Production bill;
        private readonly string[] fields;
        internal BillField(Building_WorkTable table, Bill_Production bill, string[] fields) { this.table = table; this.bill = bill; this.fields = fields; }
        public string ObjectId => bill.GetUniqueLoadID();
        public bool IsDestroyed => bill.deleted || table.Destroyed;
        public object? NativeTarget => bill;
        public string Field => "bill:" + fields[0];
        public bool MayChangeByGame => fields[0] == "repeat_count" || fields[0] == "suspended";
        public JToken Read() => fields[0] switch { "suspended" => new JValue(bill.suspended), "repeat_mode" => new JValue(bill.repeatMode.defName), "repeat_count" => new JValue(bill.repeatCount), "target_count" => new JValue(bill.targetCount), _ => throw new ArgumentException("Unknown bill field.") };
        public void Validate(JToken value) {
            if (table.Destroyed || !table.BillStack.Bills.Contains(bill)) throw new ArgumentException("Bill unavailable.");
            var field = fields[0];
            if (field == "suspended" && value.Type != JTokenType.Boolean) throw new ArgumentException("Invalid suspended value.");
            if ((field == "repeat_count" || field == "target_count") && (value.Type != JTokenType.Integer || (long)value < 0 || (long)value > int.MaxValue)) throw new ArgumentException("Invalid bill count.");
            if (field == "repeat_mode" && (value.Type != JTokenType.String || ((string?)value != "Forever" && (string?)value != "RepeatCount" && (string?)value != "TargetCount") || DefDatabase<BillRepeatModeDef>.GetNamedSilentFail((string)value!) == null)) throw new ArgumentException("Invalid bill repeat mode.");
        }
        public void Write(JToken value) {
            switch (fields[0]) { case "suspended": bill.suspended = (bool)value; break; case "repeat_mode": bill.repeatMode = DefDatabase<BillRepeatModeDef>.GetNamed((string)value!); break; case "repeat_count": bill.repeatCount = (int)value; break; case "target_count": bill.targetCount = (int)value; break; }
        }
        public void Restore(JToken value) => Write(value);
    }

    private sealed class MedicalField : IPreparedWriteField
    {
        private readonly Pawn pawn;
        internal MedicalField(Pawn pawn) { this.pawn = pawn; }
        public string ObjectId => pawn.GetUniqueLoadID();
        public bool IsDestroyed => pawn.Destroyed;
        public object? NativeTarget => pawn;
        public string Field => "medical_care";
        public bool MayChangeByGame => true;
        public JToken Read() => new JValue(pawn.playerSettings.medCare.ToString());
        public void Validate(JToken value) {
            if (pawn.Destroyed || pawn.playerSettings == null) throw new ArgumentException("Medical care is unavailable.");
            if (value.Type != JTokenType.String || !Enum.TryParse<MedicalCareCategory>((string?)value, false, out var care) || !Enum.IsDefined(typeof(MedicalCareCategory), care)) throw new ArgumentException("Unknown medical care category.");
        }
        public void Write(JToken value) => pawn.playerSettings.medCare = (MedicalCareCategory)Enum.Parse(typeof(MedicalCareCategory), (string)value!);
        public void Restore(JToken value) => Write(value);
    }

    private sealed class ForbiddenField : IPreparedWriteField
    {
        private readonly Thing thing;
        private readonly CompForbiddable comp;
        internal ForbiddenField(Thing thing, CompForbiddable comp) { this.thing = thing; this.comp = comp; }
        public string ObjectId => thing.GetUniqueLoadID();
        public bool IsDestroyed => thing.Destroyed;
        public object? NativeTarget => thing;
        public string Field => "forbidden_flag";
        public bool MayChangeByGame => true;
        public JToken Read() => new JValue(comp.Forbidden);
        public void Validate(JToken value) { if (thing.Destroyed || value.Type != JTokenType.Boolean) throw new ArgumentException("Invalid forbidden flag."); }
        public void Write(JToken value) => comp.Forbidden = (bool)value;
        public void Restore(JToken value) => Write(value);
    }

    private sealed class WorkPriorityField : IPreparedWriteField
    {
        private readonly Pawn pawn;
        private readonly WorkTypeDef work;
        internal WorkPriorityField(Pawn pawn, WorkTypeDef work) { this.pawn = pawn; this.work = work; }
        public string ObjectId => pawn.GetUniqueLoadID();
        public bool IsDestroyed => pawn.Destroyed;
        public object? NativeTarget => pawn;
        public string Field => "work_priority:" + work.defName;
        public bool MayChangeByGame => true;
        public JToken Read() => new JValue(pawn.workSettings.GetPriority(work));
        public void Validate(JToken value)
        {
            if (pawn.Destroyed || pawn.workSettings == null || !pawn.workSettings.Initialized || pawn.WorkTypeIsDisabled(work)) throw new ArgumentException("Work type is unavailable or work settings are not initialized.");
            if (Current.Game?.playSettings?.useWorkPriorities != true) throw new ArgumentException("Manual work priorities are disabled.");
            if (value.Type != JTokenType.Integer || (long)value < 0 || (long)value > 4) throw new ArgumentException("Work priority must be 0-4.");
            NativeWriteSafety.RequireWorkPriority(pawn.workSettings.GetPriority(work), (int)value,
                pawn.jobs?.curJob != null, pawn.jobs?.jobQueue.Count ?? 0);
        }
        public void Write(JToken value) => pawn.workSettings.SetPriority(work, (int)value);
        public void Restore(JToken original) => pawn.workSettings.SetPriority(work, (int)original);
    }
}
