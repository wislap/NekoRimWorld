using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class BedWriteCatalog
{
    internal static readonly JObject[] Operations = {
        JObject.Parse(@"{'op':'set_bed_assignment','schema':{'type':'object','additionalProperties':false,'properties':{'op':{'const':'set_bed_assignment'},'pawn':{'type':'string','minLength':1},'value':{'type':['string','null']}},'required':['op','pawn','value'],'description':'Assign a configurable living free colonist to a supported ordinary player bed with a free slot, or clear their bed assignment. Native assignment eligibility is checked; no other owner is displaced.'},'observation':{'read':{'tool':'look','arguments':{'object':{'kind':'pawn','id':{'arg':'pawn'}},'aspects':['work']},'native_operation':'inspect_state'},'extract':{'kind':'field','object_id':{'arg':'pawn'},'path':['facets','work','assigned_bed_id'],'allow_null':true,'objects_path':['objects'],'identity_path':['target','id'],'status_path':['facets','work','status'],'status_value':'observed'}}}"),
        JObject.Parse(@"{'op':'set_bed_medical','schema':{'type':'object','additionalProperties':false,'properties':{'op':{'const':'set_bed_medical'},'bed':{'type':'string','minLength':1},'value':{'type':'boolean'}},'required':['op','bed','value'],'description':'Set medical designation of a supported ordinary player colonist bed. Enabling requires an empty ownership list; clear assignments explicitly first.'},'observation':{'read':{'tool':'look','arguments':{'object':{'kind':'thing','id':{'arg':'bed'}},'aspects':['state'],'focus':{'state':['bed']}},'native_operation':'inspect_state'},'extract':{'kind':'field','object_id':{'arg':'bed'},'path':['facets','bed','medical_designation'],'allow_null':false,'objects_path':['objects'],'identity_path':['target','id'],'status_path':['facets','bed','status'],'status_value':'observed'}}}")
    };
}
