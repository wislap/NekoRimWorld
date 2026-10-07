using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace Neko.RimWorld;
internal static class ReadDependencyTests
{
    [MethodImpl(MethodImplOptions.NoInlining)] private static int Root()=>Leaf();
    [MethodImpl(MethodImplOptions.NoInlining)] private static int Leaf()=>7;
    [MethodImpl(MethodImplOptions.NoInlining)] private static int Unrelated()=>99;
    private abstract class Reader { internal abstract int Read(); }
    private sealed class ConcreteReader : Reader { internal override int Read()=>Leaf(); }
    private class SharedReader : Reader { internal override int Read()=>Leaf(); }
    private sealed class InheritedReader : SharedReader { }
    private sealed class OtherInheritedReader : SharedReader { }
    private static class GenericReader<T> {
        [MethodImpl(MethodImplOptions.NoInlining)] internal static int Read(int remaining)=>remaining==0?Leaf():GenericReader<System.Collections.Generic.List<T>>.Read(remaining-1);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static int VirtualRoot(Reader reader)=>reader.Read();
    [MethodImpl(MethodImplOptions.NoInlining)] private static ConcreteReader[,] ArrayRoot()=>new ConcreteReader[1,1];
    private interface IReader { int Read(); }
    private interface IGenericReader<T> { int Read(); int Read(T value); }
    private class InterfaceReader : IReader { public int Read()=>Leaf(); }
    private sealed class ExplicitReader : IGenericReader<int> {
        int IGenericReader<int>.Read()=>Leaf();
        int IGenericReader<int>.Read(int value)=>Unrelated();
    }
    private class GenericInterfaceReader<T> : IGenericReader<T> {
        public int Read()=>Leaf();
        public int Read(T value)=>Unrelated();
    }
    private sealed class InheritedInterfaceReader : GenericInterfaceReader<int> { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static int InterfaceRoot(IReader reader)=>reader.Read();
    [MethodImpl(MethodImplOptions.NoInlining)] private static int GenericInterfaceRoot(IGenericReader<int> reader)=>reader.Read();
    internal static void Run()
    {
        var flags=BindingFlags.Static|BindingFlags.NonPublic;
        var root=typeof(ReadDependencyTests).GetMethod(nameof(Root),flags)!;
        var leaf=typeof(ReadDependencyTests).GetMethod(nameof(Leaf),flags)!;
        var unrelated=typeof(ReadDependencyTests).GetMethod(nameof(Unrelated),flags)!;
        var graph=ReadMethodDependencies.Collect(new[]{root},typeof(ReadDependencyTests).Assembly);
        if(!graph.Contains(root) || !graph.Contains(leaf) || graph.Contains(unrelated))
            throw new Exception("Read graph omitted a callee or included an unrelated method");
        var virtualRoot=typeof(ReadDependencyTests).GetMethod(nameof(VirtualRoot),flags)!;
        var implementation=typeof(ConcreteReader).GetMethod("Read",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var virtualGraph=ReadMethodDependencies.Collect(new[]{virtualRoot},typeof(ReadDependencyTests).Assembly);
        if(!virtualGraph.Contains(implementation) || !virtualGraph.Contains(leaf) || virtualGraph.Contains(unrelated))
            throw new Exception("Virtual read dependency omitted a reachable implementation");
        Parallel.For(0,8,_=> {
            var repeated=ReadMethodDependencies.Collect(new[]{virtualRoot},typeof(ReadDependencyTests).Assembly);
            if(!new HashSet<MethodBase>(virtualGraph).SetEquals(repeated))
                throw new Exception("Concurrent metadata reuse changed the reachable dependency set");
        });
        var shared=typeof(SharedReader).GetMethod("Read",BindingFlags.Instance|BindingFlags.NonPublic)!;
        if(virtualGraph.Count(m=>ReadMethodDependencies.Identity(m)==ReadMethodDependencies.Identity(shared))!=1)
            throw new Exception("Inherited reflected aliases inflated the dependency graph");
        var genericRoot=typeof(GenericReader<int>).GetMethod("Read",BindingFlags.Static|BindingFlags.NonPublic)!;
        var genericGraph=ReadMethodDependencies.Collect(new[]{genericRoot},typeof(ReadDependencyTests).Assembly);
        if(!genericGraph.Contains(leaf) || genericGraph.Count>10)
            throw new Exception("Recursively constructed generic instances did not converge to finite IL definitions");
        var arrayRoot=typeof(ReadDependencyTests).GetMethod(nameof(ArrayRoot),flags)!;
        var arrayGraph=ReadMethodDependencies.Collect(new[]{arrayRoot},typeof(ReadDependencyTests).Assembly);
        if(!arrayGraph.Contains(arrayRoot) || !arrayGraph.Any(m=>m.DeclaringType?.IsArray==true))
            throw new Exception("Runtime array intrinsics were lost or incorrectly indexed as source definitions");
        foreach(var name in new[]{nameof(InterfaceRoot),nameof(GenericInterfaceRoot)}) {
            var interfaceRoot=typeof(ReadDependencyTests).GetMethod(name,flags)!;
            var interfaceGraph=ReadMethodDependencies.Collect(new[]{interfaceRoot},typeof(ReadDependencyTests).Assembly);
            if(!interfaceGraph.Contains(leaf) || interfaceGraph.Contains(unrelated))
                throw new Exception("Interface dispatch missed a callee or conflated overloaded methods: "+name);
            var implementations=name==nameof(InterfaceRoot)
                ? new[]{typeof(InterfaceReader).GetMethod("Read")!}
                : new[]{typeof(ExplicitReader).GetMethods(BindingFlags.Instance|BindingFlags.NonPublic).Single(m=>m.Name.EndsWith(".Read") && m.GetParameters().Length==0),
                    typeof(GenericInterfaceReader<>).GetMethods().Single(m=>m.Name=="Read" && m.GetParameters().Length==0)};
            foreach(var expected in implementations)
                if(!interfaceGraph.Any(m=>ReadMethodDependencies.Identity(m)==ReadMethodDependencies.Identity(expected)))
                    throw new Exception("Interface dispatch omitted an explicit/open/inherited implementation: "+expected);
        }
        Console.WriteLine("Read dependency tests passed: exact roots and reachable callees.");
    }
}
