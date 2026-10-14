using System;
using GraphQL;
using Newtonsoft.Json.Linq;
namespace Neko.RimWorld;
internal static class SpatialRouteTests
{
    internal static void Run()
    {
        var zero = SpatialRouteSummary.Create(new[]{(4,5)},0,"map1");
        Require((int)zero["steps"]! == 0 && (double)zero["path_length"]!["value"]! == 0,"Same-cell path must have zero transitions");
        var route = SpatialRouteSummary.Create(new[]{(2,1),(1,0),(0,0)},77,"map1");
        Require(route.Property("grid_distance")==null,"Retired length alias remains in the current wire result");
        Require(route["path_length"]!["meaning"]!.ToString().Contains("not straight-line"),"Path length semantics missing");
        Require((int)route["steps"]! == 2 && Math.Abs((double)route["path_length"]!["value"]!-(1+Math.Sqrt(2)))<1e-9,"Diagonal length or transition count incorrect");
        Require((int)route["start"]!["x"]! == 0 && (int)route["end"]!["x"]! == 2,"Native reversed path endpoints swapped");
        Require((float)route["native_cost"]!["value"]! == 77 && route["eta"]==null,"Cost must not become an ETA");
        foreach(var nodes in new[]{Array.Empty<(int,int)>(), new[]{(0,0),(0,0)},new[]{(4,0),(0,0)}}) {
            try { SpatialRouteSummary.Create(nodes,1,"m"); }
            catch(ExecutionError) { continue; }
            throw new Exception("Invalid path geometry accepted");
        }
        foreach(var cost in new[]{float.NaN,float.PositiveInfinity,-1f}) {
            try { SpatialRouteSummary.Create(new[]{(0,0)},cost,"m"); }
            catch(ExecutionError) { continue; }
            throw new Exception("Invalid path cost accepted");
        }
        SpatialContract.Validate(JObject.Parse("{from:{kind:'pawn',id:'p'},to:{kind:'cell',x:0,z:0},detail:'route'}"));
        foreach(var value in new[]{"null","false","'path'","[]"}) {
            try { SpatialContract.Validate(JObject.Parse("{from:{kind:'pawn',id:'p'},to:{kind:'thing',id:'t'},detail:"+value+"}")); }
            catch(ExecutionError e) when(e.Code=="invalid_arguments") { continue; }
            throw new Exception("Invalid route detail accepted");
        }
        Console.WriteLine("Spatial route tests passed: zero/diagonal/reversed paths, cost units and invalid geometry.");
    }
    private static void Require(bool condition,string message) { if(!condition) throw new Exception(message); }
}
