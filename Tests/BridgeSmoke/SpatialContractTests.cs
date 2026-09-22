using System;
using System.Linq;
using GraphQL;
using Newtonsoft.Json.Linq;
namespace Neko.RimWorld;
internal static class SpatialContractTests
{
    internal static void Run()
    {
        foreach (var target in new[]{"{kind:'thing',id:'t'}", "{kind:'thing',name:'Steel',map:'Map_0'}", "{kind:'cell',x:0,z:1,map:'current'}"})
            SpatialContract.Validate(JObject.Parse("{from:{kind:'pawn',id:'p'},to:"+target+"}"));
        foreach (var target in new[]{"{kind:'cell',x:999999999999999999999999999,z:1}","{kind:'cell',x:true,z:1}","{kind:'cell',x:2147483648,z:1}","{kind:'cell',id:'t',x:0,z:1}","{kind:'thing',id:'t',x:0}","{kind:'thing',id:'t',name:'x'}", "{kind:'thing',id:''}","{kind:'thing',id:null}","{kind:'thing',name:17}","{kind:'thing',id:'t',map:null}","{kind:'thing'}"}) {
            try { SpatialContract.Validate(JObject.Parse("{from:{kind:'pawn',id:'p'},to:"+target+"}")); }
            catch(ExecutionError e) when(e.Code=="invalid_arguments") { continue; }
            throw new Exception("Invalid spatial input accepted: "+target);
        }
        var catalog=SpatialContract.Catalog();
        if ((string?)catalog["version"]! != "spatial_query.v1" || catalog["parameters"]?["properties"]?["to"]?["oneOf"]?.Count()!=3)
            throw new Exception("Spatial contract discovery missing");
        Console.WriteLine("Spatial validation tests passed: identities, cells, bounds, strict selectors.");
    }
}
