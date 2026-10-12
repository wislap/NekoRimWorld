using System;
using System.Collections.Generic;
using System.Linq;
using GraphQL.Types;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

/// <summary>One executable field vocabulary; no separate business selector table.</summary>
internal static class ObjectReadContract
{
    internal const string Version = "object_read.v1";
    internal static readonly Dictionary<string,string> Roots = new(StringComparer.Ordinal) {
        ["game"]="Game", ["maps"]="Map", ["colony"]="Colony", ["pawns"]="Pawn",
        ["pawn"]="Pawn", ["colonists"]="Pawn", ["things"]="Thing", ["defs"]="ThingDef", ["cells"]="ObjectCell", ["situation"]="ObjectSituation"
    };
    internal static JObject Catalog(PublicFields registry)
    {
        var types=new JObject();
        foreach(var property in registry.CompactDirectory(Roots.Values.Distinct()).Properties()) {
            var fields=new JObject();
            foreach(var entry in registry.Fields(property.Name)) {
                var f=entry.Value;
                fields[entry.Key]=new JObject { ["type"]=PublicFields.ValueType(f).Name,
                    ["scalar"]=PublicFields.ValueType(f) is ScalarGraphType, ["collection"]=PublicFields.IsList(f),
                    ["capture"]=Capture(PublicFields.ValueType(f).Name,f),
                    ["roles"]=f.Metadata.TryGetValue("object_roles",out var roles)?JToken.FromObject(roles!):null,
                    ["semantic_role"]=f.Metadata.TryGetValue("object_semantic_role",out var semanticRole)?(string)semanticRole!:null,
                    ["null_status"]=f.Metadata.TryGetValue("object_null_status",out var nullStatus)?(string)nullStatus!:null,
                    ["args"]=new JArray((f.Arguments?.ToArray()??Array.Empty<QueryArgument>())
                        .Where(a=>f.Name!="stats" && a.Name is not ("where" or "first" or "after" or "offset"))
                        .Select(a=>new JObject { ["name"]=PublicFields.Name(a.Name),["required"]=a.ResolvedType is NonNullGraphType })) };
            }
            fields["self"]=new JObject { ["type"]=property.Name,["scalar"]=false,["collection"]=false,["capture"]="identity",["args"]=new JArray() };
            types[property.Name]=fields;
        }
        return new JObject { ["version"]=Version,["operation"]="read_objects",
            ["roots"]=JObject.FromObject(Roots),["types"]=types,
            ["parameters"]=JObject.Parse(@"{'type':'object','properties':{
              'target':{'type':'object','properties':{'kind':{'type':'string'},'scope':{'type':'object'},'where':{'type':'object'}},'required':['kind'],'additionalProperties':false},'select':{'type':'array','items':{'type':'object','properties':{
                'field':{'type':'string'},'where':{'type':'object'},'columns':{'type':'array','items':{'type':'string'}},
                'children':{'type':'array'},'page':{'type':'object'}},'required':['field'],'additionalProperties':false}},
              'page':{'type':'object'},'context':{'type':'object'},'lease_id':{'type':'string'}},'required':['lease_id'],
              'anyOf':[{'required':['target','select']},{'required':['page'],'properties':{'page':{'required':['cursor']}}}], 'additionalProperties':false}"),
            ["constraints"]=new JObject { ["stats"]=new JObject { ["source"]="StatDef identities",["value_sampling"]="Selected page values; value predicates add necessary getter work." } },
            ["scope_defaults"]=new JObject { ["map"]="current",["placement"]="any",["map_visibility"]="all" },
            ["root_semantics"]=new JObject { ["colonists"]="Native spawned free-colonist population; held pawns, prisoners and caravans are outside it.",["pawns"]="Native Pawns in selected loaded maps and their real holders: humans, animals and mechanoids. Pawn does not imply human or player faction.",["things"]="Native Thing records in the actual physical scope; includes non-items. Containment is not ownership, usability or action eligibility.",["defs"]="ThingDef definitions, not physical instances.",["colony"]="Selected game/map overview; loaded-map relation respects the declared map scope." },
            ["rules"]="Fresh reads require target/select; select[] reads root anchors only. field selects a relationship; columns select its direct values; self reads root columns. Omitted/empty columns read anchors only. Independent selections compose without overlap rejection. page size defaults20; cursor-only requests restore the bound selection/scope/context/ancestor. No automatic filtered total. Long text and packets are losslessly continued. Unknown predicate is not false. Snapshot-local anchors are not cross-observation IDs." };
    }
    internal static string Capture(string type,FieldType? field=null) => field!=null && field.Metadata.TryGetValue("object_capture",out var capture) ? (string)capture! == "values" ? "local_identity" : (string)capture! :
        type is "Thought" or "ThoughtGroup" or "Trait" or "Alert" or "ObjectAlert" or "ResourceCountEntry" ? "local_identity" :
        type is "ThingDef" or "BodyPart" or "StatDefinition" ? "immutable" : "identity";
    internal static void Keys(JObject value,params string[] allowed) {
        var unknown=value.Properties().FirstOrDefault(p=>!allowed.Contains(p.Name));
        if(unknown!=null)throw ReadContext.Error("invalid_arguments","Unknown object-read option: "+unknown.Name);
    }
    internal static int Size(JObject? page) {
        if(page==null)return 20;
        Keys(page,"size","cursor");
        if(page["size"]==null)return 20;
        if(page["size"]!.Type!=JTokenType.Integer || (long)page["size"]!<1 || (long)page["size"]!>100)
            throw ReadContext.Error("invalid_arguments","page.size must be 1..100 (a lossless display window).");
        return (int)page["size"]!;
    }
}
