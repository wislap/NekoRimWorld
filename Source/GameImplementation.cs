using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal static class GameImplementation
{
    // This binary's provider bodies and their read-side dependencies were inspected
    // before enabling calls. A new game build needs its own read-only audit.
    internal const string AuditedAssemblySha256 = "5cf1b5be399d5b1c9c56ca72c9d35b4ecf307feacf5859d04ac5a1aa5926356a";
    private static readonly Assembly GameAssembly = typeof(Pawn).Assembly;
    private static readonly Lazy<string?> AssemblyHash = new(() => {
        try { using var input = File.OpenRead(GameAssembly.Location); using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    });
    internal static readonly string[] InspectionTypes = {
        "RimWorld.FloatMenuOptionProvider_Equip", "RimWorld.FloatMenuOptionProvider_Wear", "RimWorld.FloatMenuOptionProvider_Ingest",
        "RimWorld.FloatMenuOptionProvider", "RimWorld.FloatMenuContext", "RimWorld.FloatMenuMakerMap", "RimWorld.FloatMenuUtility",
        "RimWorld.EquipmentUtility", "RimWorld.ApparelUtility", "RimWorld.FoodUtility", "RimWorld.IdeoUtility",
        "RimWorld.GenHostility", "RimWorld.ForbidUtility", "Verse.Pawn", "Verse.Thing", "Verse.GenUI"
    };

    internal static JObject Describe()
    {
        var patches = new JArray();
        foreach (var name in InspectionTypes) {
            var type = GameAssembly.GetType(name);
            if (type == null) continue;
            foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                var info = Harmony.GetPatchInfo(method);
                if (info == null || info.Owners.Count == 0) continue;
                patches.Add(new JObject { ["symbol"] = name + "." + method.Name, ["owners"] = new JArray(info.Owners.OrderBy(s => s, StringComparer.Ordinal)) });
            }
        }
        return new JObject {
            ["format"] = "game_implementation.v1", ["assembly_name"] = GameAssembly.GetName().Name,
            ["assembly_sha256"] = AssemblyHash.Value, ["module_id"] = GameAssembly.ManifestModule.ModuleVersionId.ToString("N"),
            ["game_version"] = VersionControl.CurrentVersionString, ["patched_methods"] = patches,
            ["patch_coverage"] = new JArray(InspectionTypes),
            ["inspection_preparation"] = PreparationStatus(),
            ["meaning"] = "Installed game implementation identity and Harmony patches on the inspected behavior types. Not live gameplay facts; patch coverage is not exhaustive for arbitrary Mod behavior."
        };
    }

    internal static string Source(string symbol) => (AssemblyHash.Value ?? "unavailable") + "/" + symbol;

    private static readonly Dictionary<string,Task<IReadOnlyCollection<MethodBase>>> dependencies = new();
    internal static void PrepareBehaviorReads()
    {
        // Reflection and IL only, prepared before a player's model execution.
        // Predicate calls still verify the binary and current patches each time.
        foreach(var action in ActionInspectionContract.Actions) Prepare(ActionInspection.ReadDependencies(action));
        Prepare(new[] { (typeof(GenHostility),nameof(GenHostility.AnyHostileActiveThreatToPlayer)) });
        Prepare(new[] { (typeof(GenHostility),nameof(GenHostility.IsActiveThreatToPlayer)) });
        Prepare(new[] { (typeof(Quest), "get_State") });
        Prepare(new[] { (typeof(FactionUtility), nameof(FactionUtility.HostileTo)) });
        foreach (var dependency in ObjectFactsData.ReadDependencies)
            Prepare(new[] { dependency });
    }
    private static JObject PreparationStatus() => new() {
        ["scheduled"] = dependencies.Count,
        ["pending"] = dependencies.Values.Count(task=>!task.IsCompleted),
        ["failed"] = dependencies.Values.Count(task=>task.IsFaulted || task.IsCanceled),
        ["ready"] = dependencies.Count>0 && dependencies.Values.All(task=>task.Status==TaskStatus.RanToCompletion),
        ["entries"] = new JArray(dependencies.Select(entry=>new JObject {
            ["roots"] = entry.Key,
            ["status"] = entry.Value.Status.ToString(),
            ["method_definitions"] = entry.Value.Status==TaskStatus.RanToCompletion ? entry.Value.Result.Count : (int?)null,
            ["error_type"] = entry.Value.Exception?.GetBaseException().GetType().Name,
            ["error"] = entry.Value.Exception?.GetBaseException().Message
        })),
        ["meaning"] = "Metadata preparation only; readiness does not authorize a patched predicate or establish action eligibility."
    };
    internal static void RequireKnownBinary()
    {
        if(AssemblyHash.Value!=AuditedAssemblySha256)throw ReadContext.Error("unsupported_game_implementation","This game binary has not been inspected for read-only queries");
    }
    internal static void RequireInspected(params (Type Type,string Method)[] calls)
    {
        RequireKnownBinary();
        if(calls.Length==0)throw ReadContext.Error("unsupported_read_dependencies","Read dependencies were not specified");
        var inspection=Prepare(calls);
        if(!inspection.IsCompleted)throw ReadContext.Error("inspection_metadata_pending","Read dependency metadata is still being prepared; no predicate invoked");
        if(inspection.IsFaulted || inspection.IsCanceled)throw ReadContext.Error("unsupported_read_dependencies","Read dependency inspection failed");
        var methods=inspection.Result;
        var identities=new HashSet<(Module,int,string)>(methods.Select(ReadMethodDependencies.Identity));
        var patched=Harmony.GetAllPatchedMethods().Where(m=>identities.Contains(ReadMethodDependencies.Identity(m))).ToArray();
        if(patched.Length>0)throw ReadContext.Error("unsupported_behavior_patch","The requested read depends on patched methods: "+string.Join(", ",patched.Take(8).Select(m=>m.DeclaringType?.FullName+"."+m.Name)));
    }
    private static Task<IReadOnlyCollection<MethodBase>> Prepare((Type Type,string Method)[] calls)
    {
        var key=string.Join(";",calls.Select(c=>c.Type.FullName+"."+c.Method));
        if(!dependencies.TryGetValue(key,out var inspection)) {
            var requested=calls.ToArray();
            inspection=Task.Run<IReadOnlyCollection<MethodBase>>(()=> {
            var roots=new List<MethodBase>();
            foreach(var call in requested) {
                var matches=call.Method==".ctor"?call.Type.GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Cast<MethodBase>().ToArray()
                    :call.Type.GetMethods(BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Where(m=>m.Name==call.Method).Cast<MethodBase>().ToArray();
                if(matches.Length==0)throw ReadContext.Error("unsupported_read_dependencies","Read method is unavailable: "+call.Type.FullName+"."+call.Method);
                roots.AddRange(matches);
            }
            // IL/reflection metadata only; no game object or method invocation.
            // The main thread must never wait for this graph traversal.
            return ReadMethodDependencies.Collect(roots,GameAssembly);
            });
            dependencies[key]=inspection;
        }
        return inspection;
    }
}
