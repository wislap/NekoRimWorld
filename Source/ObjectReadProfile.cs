using System.Diagnostics;
using System.Linq;

namespace Neko.RimWorld;

/// <summary>Opt-in CPU attribution for object reads. Never enters a delivery or model input.</summary>
internal static class ObjectReadProfile
{
    internal enum Part { Drain,Merge,Measure,Envelope,FindRebuild,Getter,Explanations,Metadata,Finalize,Branch,Find,Count }
    internal static bool Enabled;
    /// <summary>Test mode: every tracked size is checked against a full serialization.</summary>
    internal static bool VerifySizes;
    internal static readonly long[] Ticks=new long[(int)Part.Count];
    internal static long Frames,Steps,Deliveries,Rebuilds,RebuildMembers,MeasuredBytes;
    internal static void Reset() { System.Array.Clear(Ticks,0,Ticks.Length);Frames=Steps=Deliveries=Rebuilds=RebuildMembers=MeasuredBytes=0; }
    internal static long Start()=>Enabled?Stopwatch.GetTimestamp():0;
    internal static void Stop(Part part,long started) { if(Enabled)Ticks[(int)part]+=Stopwatch.GetTimestamp()-started; }
    internal static double Milliseconds(Part part)=>Ticks[(int)part]*1000.0/Stopwatch.Frequency;
    internal static string Summary()=>"frames="+Frames+" steps="+Steps+" deliveries="+Deliveries+" rebuilds="+Rebuilds+" rebuild_members="+RebuildMembers+" measured_kb="+MeasuredBytes/1024+" "+
        string.Join(" ",Enumerable.Range(0,(int)Part.Count).Select(i=>((Part)i).ToString().ToLowerInvariant()+"_ms="+Milliseconds((Part)i).ToString("0.0")));
}
