using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Neko.RimWorld;

// Inspect IL metadata only. Never invoke methods or construct game objects.
internal static class ReadMethodDependencies
{
    private static readonly ConcurrentDictionary<Assembly,Type[]> AssemblyTypes = new();
    private static readonly ConcurrentDictionary<Assembly,Lazy<int>> DefinitionBounds = new();
    private static readonly ConcurrentDictionary<(Module,int,string),MethodBase> Definitions = new();
    private static readonly ConcurrentDictionary<Type,Lazy<Dictionary<int,MethodBase>>> DeclaredMethods = new();
    private static readonly ConcurrentDictionary<string,Lazy<MethodBase[]>> VirtualImplementations = new();
    private static readonly ConcurrentDictionary<string,Lazy<Dictionary<(Module,int,string),MethodBase[]>>> DispatchIndexes = new();

    private static Dictionary<(Module,int,string),MethodBase[]> DispatchIndex(Type[] types)
    {
        var rows=new Dictionary<(Module,int,string),HashSet<MethodBase>>();
        foreach(var type in types.Where(t=>!t.IsInterface)) {
            var flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly;
            foreach(var method in type.GetMethods(flags).Where(m=>m.IsVirtual)) {
                var key=Identity(Definition(method.GetBaseDefinition()));
                if(!rows.TryGetValue(key,out var methods))rows[key]=methods=new HashSet<MethodBase>();
                methods.Add(Definition(method));
            }
        }
        return rows.ToDictionary(row=>row.Key,row=>row.Value.ToArray());
    }
    internal static (Module Module,int Token,string SyntheticSignature) Identity(MethodBase method) =>
        (method.Module,method.MetadataToken,method.MetadataToken==0?(method.DeclaringType?.AssemblyQualifiedName+":"+method):string.Empty);

    private static MethodBase Definition(MethodBase method)
    {
        // Mono array intrinsics have token zero and no managed body. Preserve
        // their distinct signatures for patch checks, not a false token index.
        if(method.DeclaringType?.IsArray==true)return method;
        // ReflectedType aliases and recursively constructed generic instances
        // share one IL body. Walk the open definition, whose virtual/interface
        // dependencies remain conservatively expanded across native types.
        return Definitions.GetOrAdd(Identity(method),_=> {
        var type=method.DeclaringType;
        if(type?.IsGenericType==true && !type.IsGenericTypeDefinition)type=type.GetGenericTypeDefinition();
        if(type!=null) {
            var flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly;
            var members=DeclaredMethods.GetOrAdd(type,value=>new Lazy<Dictionary<int,MethodBase>>(()=>value.GetConstructors(flags).Cast<MethodBase>().Concat(value.GetMethods(flags)).ToDictionary(m=>m.MetadataToken))).Value;
            method=members[method.MetadataToken];
        }
        if(method is MethodInfo generic && generic.IsGenericMethod && !generic.IsGenericMethodDefinition)method=generic.GetGenericMethodDefinition();
        return method;
        });
    }
    private static readonly Dictionary<short,OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static)
        .Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)!).ToDictionary(c=>c.Value);

    internal static IReadOnlyCollection<MethodBase> Collect(IEnumerable<MethodBase> roots, Assembly assembly)
    {
        var rootList=roots.ToArray();var allowed=new HashSet<Assembly>(rootList.Select(m=>m.DeclaringType!.Assembly)) { assembly };
        var types=allowed.SelectMany(a=>AssemblyTypes.GetOrAdd(a,value=>value.GetTypes())).ToArray();
        var bound=allowed.Sum(a=>DefinitionBounds.GetOrAdd(a,value=>new Lazy<int>(()=> {
            var flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly;
            return AssemblyTypes.GetOrAdd(value,assembly=>assembly.GetTypes()).Sum(type=>type.GetMethods(flags).Length+type.GetConstructors(flags).Length);
        })).Value);
        var assemblyKey=string.Join(";",allowed.Select(a=>a.ManifestModule.ModuleVersionId.ToString()).OrderBy(value=>value,StringComparer.Ordinal));
        var found=new HashSet<MethodBase>();var visited=new HashSet<(Module,int,string)>();var pending=new Queue<MethodBase>(rootList);var definitionsVisited=0;
        while(pending.Count>0) {
            var method=pending.Dequeue();
            if(method.DeclaringType==null || !allowed.Contains(method.DeclaringType.Assembly))continue;
            method=Definition(method);
            if(!visited.Add(Identity(method)))continue;
            found.Add(method);
            if(method.MetadataToken!=0 && ++definitionsVisited>bound)throw new InvalidOperationException("Read dependency graph exceeds its finite assembly definition boundary");
            var bytes=method.DeclaringType?.IsArray==true?null:method.GetMethodBody()?.GetILAsByteArray();if(bytes==null)continue;
            for(int offset=0;offset<bytes.Length;) {
                short code=bytes[offset++];if(code==0xfe)code=(short)(0xfe00|bytes[offset++]);
                if(!Codes.TryGetValue(code,out var op))throw new InvalidOperationException("Unknown IL instruction");
                int size=op.OperandType switch {
                    OperandType.InlineNone=>0, OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,
                    OperandType.InlineVar=>2,OperandType.InlineI8 or OperandType.InlineR=>8,
                    OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(bytes,offset),_=>4
                };
                if(op.OperandType is OperandType.InlineMethod or OperandType.InlineTok) {
                    var typeArgs=method.DeclaringType?.IsGenericType==true?method.DeclaringType.GetGenericArguments():null;
                    var methodArgs=method.IsGenericMethod?method.GetGenericArguments():null;
                    var member=method.Module.ResolveMember(BitConverter.ToInt32(bytes,offset),typeArgs,methodArgs);
                    if(member is MethodBase dependency) {
                        if(dependency.DeclaringType!=null && allowed.Contains(dependency.DeclaringType.Assembly))dependency=Definition(dependency);
                        pending.Enqueue(dependency);
                        if(dependency is MethodInfo virtualMethod && virtualMethod.IsVirtual && allowed.Contains(virtualMethod.DeclaringType!.Assembly)) {
                            var implementationKey=assemblyKey+":"+virtualMethod.Module.ModuleVersionId+":"+virtualMethod.MetadataToken;
                            var implementations=VirtualImplementations.GetOrAdd(implementationKey,_=>new Lazy<MethodBase[]>(()=> {
                            if(!virtualMethod.DeclaringType!.IsInterface) {
                                var index=DispatchIndexes.GetOrAdd(assemblyKey,__=>new Lazy<Dictionary<(Module,int,string),MethodBase[]>>(()=>DispatchIndex(types))).Value;
                                return index.TryGetValue(Identity(Definition(virtualMethod.GetBaseDefinition())),out var overrides)?overrides:Array.Empty<MethodBase>();
                            }
                            var collected=new HashSet<MethodBase>();
                            var interfaceDefinition=virtualMethod.DeclaringType!;
                            foreach(var type in types.Where(t=>!t.IsInterface && !t.IsAbstract)) {
                                // The IL walk uses open definitions. Assignability to
                                // IReader<> misses classes implementing IReader<int>.
                                // Map each actual interface, then compare IL identities
                                // so explicit, inherited and generic implementations
                                // remain reachable without executing any game code.
                                foreach(var implemented in type.GetInterfaces()) {
                                    var definition=implemented.IsGenericType?implemented.GetGenericTypeDefinition():implemented;
                                    if(definition!=interfaceDefinition)continue;
                                    var map=type.GetInterfaceMap(implemented);
                                    for(int i=0;i<map.InterfaceMethods.Length;i++)
                                        if(Identity(Definition(map.InterfaceMethods[i]))==Identity(virtualMethod))
                                            collected.Add(map.TargetMethods[i]);
                                }
                            }
                            return collected.Select(Definition).Distinct().ToArray();
                            })).Value;
                            foreach(var implementation in implementations)pending.Enqueue(implementation);
                        }
                    }
                }
                offset+=size;
            }
        }
        return found;
    }
}
