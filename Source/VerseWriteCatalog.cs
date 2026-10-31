using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal sealed partial class VerseWriteFieldResolver
{
    // Each entry is independently resolved and validated; the transaction engine
    // has no operation-specific branches.
    private sealed class Registration
    {
        internal readonly JObject Spec;
        internal readonly Func<VerseWriteFieldResolver, JObject, IReadOnlyList<ResolvedWriteField>> Resolve;
        internal Registration(JObject spec, Func<VerseWriteFieldResolver, JObject, IReadOnlyList<ResolvedWriteField>> resolve) { Spec = spec; Resolve = resolve; }
    }
    private static readonly Registration[] Catalog = {
        new Registration(JObject.Parse(@"{ 'op':'add_production_bill', 'schema':{'type':'object','additionalProperties':false,'properties':{'op':{'const':'add_production_bill'},'worktable':{'type':'string','minLength':1},'value':{'type':'object','additionalProperties':false,'properties':{'recipe':{'type':'string','minLength':1}},'required':['recipe']}},'required':['op','worktable','value'], 'description':'Add an ordinary production bill on an ordinary or heat-emitting player worktable. Other worktable types and specialized native bill types are unsupported.'}, 'expect_schema':{'type':'object','additionalProperties':false,'properties':{'bill_ids':{'type':'array','items':{'type':'string','minLength':1}}},'required':['bill_ids']}, 'observation':{'read':{'tool':'look','arguments':{'object':{'kind':'thing','id':{'arg':'worktable'}},'aspects':['state'],'focus':{'state':['production']},'detail':100},'native_operation':'inspect_state'},'extract':{'kind':'ordered_ids','object_id':{'arg':'worktable'},'path':['facets','production','bills'],'id_field':'bill_id','expect_field':'bill_ids','complete_path':['facets','production','complete'],'complete_value':true,'objects_path':['objects'],'identity_path':['target','id'],'status_path':['facets','production','status'],'status_value':'observed','require':[{'path':['facets','production','bill_owner','role'],'value':'worktable'}],'available':{'path':['facets','production','available_recipes'],'key_field':'recipe','key':{'value_path':['recipe']},'value_field':'available_now','value':true}}}}"), (r, o) => One(r.ResolveProductionBill(o), o)),
        new Registration(JObject.Parse("{\"op\":\"set_work_priority\",\"schema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"op\":{\"const\":\"set_work_priority\"},\"pawn\":{\"type\":\"string\",\"minLength\":1},\"work\":{\"type\":\"string\",\"minLength\":1},\"value\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":4}},\"required\":[\"op\",\"pawn\",\"work\",\"value\"]},\"observation\":{\"read\":{\"tool\":\"look\",\"arguments\":{\"object\":{\"kind\":\"pawn\",\"id\":{\"arg\":\"pawn\"}},\"aspects\":[\"work\"]},\"native_operation\":\"inspect_state\"},\"extract\":{\"kind\":\"keyed_row\",\"object_id\":{\"arg\":\"pawn\"},\"path\":[\"facets\",\"work\",\"work_types\"],\"key_field\":\"definition\",\"key\":{\"arg\":\"work\"},\"value_field\":\"priority\",\"allow_null\":false,\"objects_path\":[\"objects\"],\"identity_path\":[\"target\",\"id\"],\"status_path\":[\"facets\",\"work\",\"status\"],\"status_value\":\"observed\"}}}"), (r, o) => One(r.ResolveWorkPriority(o), o)),
        new Registration(JObject.Parse("{\"op\":\"set_medical_care\",\"schema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"op\":{\"const\":\"set_medical_care\"},\"pawn\":{\"type\":\"string\",\"minLength\":1},\"value\":{\"type\":\"string\",\"minLength\":1}},\"required\":[\"op\",\"pawn\",\"value\"]},\"observation\":{\"read\":{\"tool\":\"look\",\"arguments\":{\"object\":{\"kind\":\"pawn\",\"id\":{\"arg\":\"pawn\"}},\"aspects\":[\"work\"]},\"native_operation\":\"inspect_state\"},\"extract\":{\"kind\":\"field\",\"object_id\":{\"arg\":\"pawn\"},\"path\":[\"facets\",\"work\",\"medical_care\"],\"allow_null\":false,\"objects_path\":[\"objects\"],\"identity_path\":[\"target\",\"id\"],\"status_path\":[\"facets\",\"work\",\"status\"],\"status_value\":\"observed\"}}}"), (r, o) => One(r.ResolveMedical(o), o)),
        new Registration(JObject.Parse("{\"op\":\"set_bill\",\"schema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"op\":{\"const\":\"set_bill\"},\"worktable\":{\"type\":\"string\",\"minLength\":1},\"bill\":{\"type\":\"string\",\"minLength\":1},\"value\":{\"type\":\"object\",\"minProperties\":1,\"additionalProperties\":false,\"properties\":{\"suspended\":{\"type\":\"boolean\"},\"repeat_mode\":{\"type\":\"string\",\"enum\":[\"Forever\",\"RepeatCount\",\"TargetCount\"]},\"repeat_count\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2147483647},\"target_count\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2147483647}}}},\"required\":[\"op\",\"worktable\",\"bill\",\"value\"]},\"observation\":{\"read\":{\"tool\":\"look\",\"arguments\":{\"object\":{\"kind\":\"thing\",\"id\":{\"arg\":\"worktable\"}},\"aspects\":[\"state\"],\"focus\":{\"state\":[\"production\"]},\"detail\":100},\"native_operation\":\"inspect_state\"},\"extract\":{\"kind\":\"object_fields\",\"object_id\":{\"arg\":\"worktable\"},\"path\":[\"facets\",\"production\",\"bills\"],\"key_field\":\"bill_id\",\"key\":{\"arg\":\"bill\"},\"row_require\":{\"bill_kind\":\"production\"},\"fields_from_value\":true,\"objects_path\":[\"objects\"],\"identity_path\":[\"target\",\"id\"],\"status_path\":[\"facets\",\"production\",\"status\"],\"status_value\":\"observed\"}}}"), (r, o) => r.ResolveBillFields(o)),
        new Registration(JObject.Parse("{\"op\":\"set_storage_allow\",\"schema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"op\":{\"const\":\"set_storage_allow\"},\"storage\":{\"type\":\"string\",\"minLength\":1},\"map\":{\"type\":\"string\",\"minLength\":1},\"item\":{\"type\":\"string\",\"minLength\":1},\"value\":{\"type\":\"boolean\"}},\"required\":[\"op\",\"storage\",\"map\",\"item\",\"value\"]},\"observation\":{\"read\":{\"tool\":\"look\",\"arguments\":{\"object\":{\"kind\":\"storage\",\"id\":{\"arg\":\"storage\"},\"map\":{\"arg\":\"map\"}},\"aspects\":[\"allows\"],\"focus\":{\"items\":[{\"arg\":\"item\"}]}},\"native_operation\":\"inspect_storage\"},\"extract\":{\"kind\":\"keyed_row\",\"object_id\":{\"arg\":\"storage\"},\"path\":[\"allows\"],\"key_field\":\"definition\",\"key\":{\"arg\":\"item\"},\"value_field\":\"allowed\",\"allow_null\":false,\"objects_path\":[\"objects\"],\"identity_path\":[\"id\"],\"status_path\":[\"status\"],\"status_value\":\"observed\",\"match\":[{\"value\":{\"arg\":\"map\"},\"path\":[\"map_id\"]}]}}}"), (r, o) => One(r.ResolveStorage(o, "set_storage_allow"), o)),
        new Registration(JObject.Parse("{\"op\":\"set_allowed_area\",\"schema\":{\"type\":\"object\",\"additionalProperties\":false,\"description\":\"Requires a spawned eligible colonist with no current job and a safely restorable existing area. map_id must exactly identify the pawn current map; area_id must be null or an allowed area on that map. This operation cannot stop a job or perform rescue; waiting does not guarantee eligibility.\",\"properties\":{\"op\":{\"const\":\"set_allowed_area\"},\"pawn\":{\"type\":\"string\",\"minLength\":1},\"value\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"map_id\":{\"type\":\"string\",\"minLength\":1},\"area_id\":{\"type\":[\"integer\",\"null\"]}},\"required\":[\"map_id\",\"area_id\"]}},\"required\":[\"op\",\"pawn\",\"value\"]},\"observation\":{\"read\":{\"tool\":\"look\",\"arguments\":{\"object\":{\"kind\":\"pawn\",\"id\":{\"arg\":\"pawn\"}},\"aspects\":[\"work\"]},\"native_operation\":\"inspect_state\"},\"extract\":{\"kind\":\"field\",\"object_id\":{\"arg\":\"pawn\"},\"path\":[\"facets\",\"work\",\"write_allowed_area_key\"],\"allow_null\":false,\"objects_path\":[\"objects\"],\"identity_path\":[\"target\",\"id\"],\"status_path\":[\"facets\",\"work\",\"status\"],\"status_value\":\"observed\"}}}"), (r, o) => One(r.ResolveArea(o), o)),
        new Registration(JObject.Parse("{\"op\":\"set_forbidden\",\"schema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"op\":{\"const\":\"set_forbidden\"},\"thing\":{\"type\":\"string\",\"minLength\":1},\"value\":{\"type\":\"boolean\"}},\"required\":[\"op\",\"thing\",\"value\"]},\"observation\":{\"read\":{\"tool\":\"look\",\"arguments\":{\"object\":{\"kind\":\"thing\",\"id\":{\"arg\":\"thing\"}},\"aspects\":[\"state\"],\"focus\":{\"state\":[\"condition\"]}},\"native_operation\":\"inspect_state\"},\"extract\":{\"kind\":\"field\",\"object_id\":{\"arg\":\"thing\"},\"path\":[\"facets\",\"condition\",\"forbidden\"],\"allow_null\":false,\"objects_path\":[\"objects\"],\"identity_path\":[\"target\",\"id\"],\"status_path\":[\"facets\",\"condition\",\"status\"],\"status_value\":\"observed\"}}}"), (r, o) => One(r.ResolveForbidden(o), o)),
        new Registration(JObject.Parse("{\"op\":\"set_research\",\"schema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"op\":{\"const\":\"set_research\"},\"research\":{\"const\":\"current\"},\"value\":{\"type\":[\"string\",\"null\"]}},\"required\":[\"op\",\"research\",\"value\"]},\"observation\":{\"read\":{\"tool\":\"look\",\"arguments\":{\"object\":{\"kind\":\"research\"},\"aspects\":[\"progress\"]},\"native_operation\":\"inspect_research\"},\"extract\":{\"kind\":\"field\",\"object_id\":null,\"path\":[\"current_regular_project\"],\"allow_null\":true,\"objects_path\":[],\"identity_path\":[],\"status_path\":[],\"status_value\":\"ok\"}}}"), (r, o) => One(r.ResolveResearch(o), o)),
        new Registration(JObject.Parse("{\"op\":\"set_storage_priority\",\"schema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"op\":{\"const\":\"set_storage_priority\"},\"storage\":{\"type\":\"string\",\"minLength\":1},\"map\":{\"type\":\"string\",\"minLength\":1},\"value\":{\"type\":\"string\",\"minLength\":1}},\"required\":[\"op\",\"storage\",\"map\",\"value\"]},\"observation\":{\"read\":{\"tool\":\"look\",\"arguments\":{\"object\":{\"kind\":\"storage\",\"id\":{\"arg\":\"storage\"},\"map\":{\"arg\":\"map\"}},\"aspects\":[\"priority\"]},\"native_operation\":\"inspect_storage\"},\"extract\":{\"kind\":\"field\",\"object_id\":{\"arg\":\"storage\"},\"path\":[\"priority\"],\"allow_null\":false,\"objects_path\":[\"objects\"],\"identity_path\":[\"id\"],\"status_path\":[\"status\"],\"status_value\":\"observed\",\"match\":[{\"value\":{\"arg\":\"map\"},\"path\":[\"map_id\"]}]}}}"), (r, o) => One(r.ResolveStorage(o, "set_storage_priority"), o)),
    };
    internal static JObject DescribeCatalog()
    {
        var result = new JObject { ["capability"] = "colony_write.transactions.v1", ["version"] = ColonyWriteContract.Capability,
            ["modes"] = new JArray("dry_run", "commit", "undo"), ["operations"] = new JArray(Catalog.Select(x => x.Spec.DeepClone())),
            ["rules"] = JObject.Parse("{\"source\":\"native\",\"missing\":\"unknown\",\"null\":\"unknown unless allow_null is true\",\"issues\":\"affected paths unknown\",\"partial\":\"affected paths unknown\",\"truncation\":\"absence is unknown unless explicitly complete\"}") };
        result["catalog_id"] = GameQueryEngine.Hash(result.ToString(Newtonsoft.Json.Formatting.None));
        return result;
    }
    public IReadOnlyList<ResolvedWriteField> Resolve(JObject operation)
    {
        var kind = (string?)operation["op"];
        var entry = Catalog.SingleOrDefault(x => (string?)x.Spec["op"] == kind);
        if (entry == null) throw new ArgumentException("Unsupported write operation.");
        return entry.Resolve(this, operation);
    }

    // Recover the exact same field implementation used by writes; no validation or setter is invoked.
    internal IPreparedWriteField? ResolveLedgerField(string id, string field) {
        JObject op;
        if (field == "current_project" && id == "research:current") op = new JObject { ["op"] = "set_research", ["research"] = "current" };
        else if (field == "medical_care") op = new JObject { ["op"] = "set_medical_care", ["pawn"] = id };
        else if (field == "allowed_area") op = new JObject { ["op"] = "set_allowed_area", ["pawn"] = id };
        else if (field == "forbidden_flag") op = new JObject { ["op"] = "set_forbidden", ["thing"] = id };
        else if (field.StartsWith("work_priority:", StringComparison.Ordinal)) op = new JObject { ["op"] = "set_work_priority", ["pawn"] = id, ["work"] = field.Substring(14) };
        else if (field == "storage_priority") op = new JObject { ["op"] = "set_storage_priority", ["storage"] = id };
        else if (field.StartsWith("storage_allow:", StringComparison.Ordinal)) op = new JObject { ["op"] = "set_storage_allow", ["storage"] = id, ["item"] = field.Substring(14) };
        else if (field.StartsWith("bill:", StringComparison.Ordinal)) {
            var name = field.Substring(5);
            op = new JObject { ["op"] = "set_bill", ["bill"] = id, ["value"] = new JObject { [name] = JValue.CreateNull() }, ["expect"] = new JObject { [name] = JValue.CreateNull() } };
            var bill = Resolve(op).Single().Field;
            return bill.ObjectId == id && bill.Field == field ? bill : null;
        } else return null;
        op["value"] = JValue.CreateNull(); op["expect"] = JValue.CreateNull();
        var result = Resolve(op).Single().Field;
        return result.ObjectId == id && result.Field == field ? result : null;
    }

    private static IReadOnlyList<ResolvedWriteField> One(IPreparedWriteField field, JObject operation) =>
        new[] { new ResolvedWriteField(field, operation["expect"]!, operation["value"]!) };

}
