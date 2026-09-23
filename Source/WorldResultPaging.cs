using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace Neko.RimWorld;

// Detached-result paging. Changing display size never changes observed totals.
internal static class WorldResultPaging
{
    internal static string Signature(JObject args)
    {
        var copy=(JObject)args.DeepClone();copy.Remove("cursor");copy.Remove("limit");
        JToken Ordered(JToken value)=>value is JObject o?new JObject(o.Properties().OrderBy(p=>p.Name,StringComparer.Ordinal).Select(p=>new JProperty(p.Name,Ordered(p.Value)))):value is JArray a?new JArray(a.Select(Ordered)):value.DeepClone();
        return Ordered(copy).ToString(Formatting.None);
    }
    internal static void PutInitial(JObject result,IReadOnlyList<JObject> rows,int limit,string cursorId,Func<bool> retain,int maxCharacters=78000)
    {
        Put(result,rows,0,limit,cursorId,maxCharacters);
        if((string?)result["page"]!["next_cursor"] != null && !retain()) {
            result["page"]!["next_cursor"] = null;
            result["page"]!["continuation_unavailable"] = true;
        }
    }
    internal static void Put(JObject result,IReadOnlyList<JObject> rows,int offset,int limit,string? cursorId,int maxCharacters=78000)
    {
        int count=Math.Min(limit,rows.Count-offset);
        while(true) {
            result["objects"]=new JArray(rows.Skip(offset).Take(count).Select(r=>r.DeepClone()));
            result["page"]=new JObject{
                ["offset"]=offset,["returned"]=count,["requested_limit"]=limit,["retained_rows"]=rows.Count,
                ["next_cursor"]=offset+count<rows.Count&&cursorId!=null?cursorId+":"+(offset+count):null,
                ["listing_complete"]=offset==0&&count==rows.Count&&(bool?)result["objects_summary"]?["detail_retention_complete"]!=false,
                ["reduced_for_size"]=count<Math.Min(limit,rows.Count-offset),
                ["continuation_unavailable"]=offset+count<rows.Count&&cursorId==null,
                ["meaning"]="Detached facts from the original observation. Page size may be reduced to fit transport; totals and selected scope are unchanged."
            };
            if(result.ToString(Formatting.None).Length<=maxCharacters||count<=1)return;
            count--;
        }
    }
}
