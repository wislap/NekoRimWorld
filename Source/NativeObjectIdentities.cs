using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

internal sealed class NativeObjectIdentity : IExposable
{
    internal string Id = "", NativeType = "", Token = "";
    internal ILoadReferenceable? Target;
    public NativeObjectIdentity() { }
    internal NativeObjectIdentity(ILoadReferenceable target, string token)
    {
        Target = target;
        Id = target.GetUniqueLoadID();
        NativeType = target.GetType().FullName ?? target.GetType().Name;
        Token = token;
    }
    public void ExposeData()
    {
        Scribe_Values.Look(ref Id, "id", "");
        Scribe_Values.Look(ref NativeType, "nativeType", "");
        Scribe_Values.Look(ref Token, "token", "");
        Scribe_References.Look(ref Target, "target");
    }
}

// Tokens belong to native objects in one save, rather than their reusable load
// IDs. A removed object may remain held by an undo receipt, but it must never be
// saved as an identity reference: native cross-reference resolution uses IDs.
internal sealed class NativeObjectIdentityRegistry
{
    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }
    private readonly Dictionary<object, string> tokens = new(new ReferenceComparer());
    private List<NativeObjectIdentity>? records = new();

    private static bool Exists(object native)
    {
        if (native is FoodPolicy policy)
            return Current.Game?.foodRestrictionDatabase?.AllFoodRestrictions.Any(p => ReferenceEquals(p, policy)) == true;
        if (native is Area area)
            return Find.Maps.Any(map => !map.Disposed && map.areaManager.AllAreas.Any(a => ReferenceEquals(a, area)));
        if (native is Zone zone)
            return Find.Maps.Any(map => !map.Disposed && map.zoneManager.AllZones.Any(z => ReferenceEquals(z, zone)));
        return false;
    }

    internal string Token(object native)
    {
        if (tokens.TryGetValue(native, out var token)) return token;
        if (native is not ILoadReferenceable || !Exists(native))
            throw new ArgumentException("Native identity requires an existing food policy, area or zone.");
        token = Guid.NewGuid().ToString("N");
        tokens.Add(native, token);
        return token;
    }

    internal void ExposeData()
    {
        if (Scribe.mode == LoadSaveMode.Saving)
        {
            // Epoch-local undo may still retain a removed original object.
            // Keep its RAM identity, but never persist its reusable native ID.
            records = tokens.Where(pair => Exists(pair.Key)).Select(pair => new NativeObjectIdentity((ILoadReferenceable)pair.Key, pair.Value))
                .OrderBy(record => record.NativeType, StringComparer.Ordinal).ThenBy(record => record.Id, StringComparer.Ordinal).ToList();
        }
        if (Scribe.mode == LoadSaveMode.LoadingVars) tokens.Clear();
        Scribe_Collections.Look(ref records, "nekoNativeObjectIdentities", LookMode.Deep);
        if (Scribe.mode != LoadSaveMode.PostLoadInit) return;

        // Do not recover by ID alone. A record must resolve to a supported live
        // native object, and ambiguous records cannot inherit an old token.
        tokens.Clear();
        records ??= new List<NativeObjectIdentity>();
        var valid = records.Where(record => record != null && record.Target != null
            && record.Target.GetUniqueLoadID() == record.Id
            && (record.Target.GetType().FullName ?? record.Target.GetType().Name) == record.NativeType
            && Guid.TryParseExact(record.Token, "N", out _) && Exists(record.Target)).ToArray();
        var tokenCounts = valid.GroupBy(record => record.Token, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var group in valid.GroupBy(record => (object)record.Target!, new ReferenceComparer()))
        {
            if (group.Count() != 1) continue;
            var record = group.Single();
            if (tokenCounts[record.Token] == 1) tokens.Add(record.Target!, record.Token);
        }
        // References used while loading should not keep removed objects alive.
        records.Clear();
    }
}

internal static class NativeObjectIdentities
{
    internal static string Token(object native) => Current.Game.GetComponent<NekoGameComponent>().Save.NativeIdentities.Token(native);
}
