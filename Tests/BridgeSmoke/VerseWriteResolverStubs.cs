using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

// Metadata smoke compiles the production registrations but never invokes a
// native resolver; actual Verse objects are not present in BridgeSmoke.
internal sealed partial class VerseWriteFieldResolver
{
    private IPreparedWriteField ResolveWorkPriority(JObject o) => throw new NotSupportedException();
    private IPreparedWriteField ResolveMedical(JObject o) => throw new NotSupportedException();
    private IPreparedWriteField ResolveArea(JObject o) => throw new NotSupportedException();
    private IPreparedWriteField ResolveResearch(JObject o) => throw new NotSupportedException();
    private IPreparedWriteField ResolveForbidden(JObject o) => throw new NotSupportedException();
    private IPreparedWriteField ResolveStorage(JObject o, string kind) => throw new NotSupportedException();
    private IReadOnlyList<ResolvedWriteField> ResolveBillFields(JObject o) => throw new NotSupportedException();
}
