using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Neko.RimWorld;

// Both sources must be installed on the audited game binary. Unknown patches
// can change the audited control flow, so fail closed when they are present.
internal static class NativeEventHookSupport
{
    internal static bool KnownBinary() {
        try { GameImplementation.RequireKnownBinary(); return true; }
        catch (Exception error) when (ReadContext.Recoverable(error)) { return false; }
    }
    internal static bool Ready() {
        if (!KnownBinary()) return false;
        return Installed(AccessTools.Method(typeof(TimeControls), nameof(TimeControls.DoTimeControlsGUI)), typeof(NativeTimeControlEvents)) &&
               Installed(AccessTools.Method(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), new Type[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) }), typeof(NativeLetterEvents));
    }
    private static bool Installed(MethodBase? method, Type owner) {
        if (method == null) return false;
        var info = Harmony.GetPatchInfo(method);
        if (info == null) return false;
        var all = info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers);
        return info.Prefixes.Any(p => p.owner == NekoRimWorldMod.PackageId && p.PatchMethod.DeclaringType == owner) &&
               info.Postfixes.Any(p => p.owner == NekoRimWorldMod.PackageId && p.PatchMethod.DeclaringType == owner) &&
               all.All(p => p.owner == NekoRimWorldMod.PackageId);
    }
}

// The GUI scope identifies real time-control input; polling alone cannot identify a user.
[HarmonyPatch(typeof(TimeControls), nameof(TimeControls.DoTimeControlsGUI))]
internal static class NativeTimeControlEvents
{
    private static bool Prepare() => NativeEventHookSupport.KnownBinary();
    private static void Prefix(out bool __state) => __state = Find.TickManager?.CurTimeSpeed == TimeSpeed.Paused;
    private static void Postfix(bool __state) => Current.Game?.GetComponent<NekoGameComponent>()?.Pause.ObserveUserPause(__state);
}

[HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), new Type[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) })]
internal static class NativeLetterEvents
{
    private static bool Prepare() => NativeEventHookSupport.KnownBinary();
    private static void Prefix(Letter let, int delayTicks, out bool __state) {
        __state = Find.TickManager?.CurTimeSpeed == TimeSpeed.Paused;
    }
    private static void Postfix(Letter let, int delayTicks, bool __state) {
        if (delayTicks <= 0 && let != null && Find.LetterStack.LettersListForReading.Contains(let)) {
            var component = Current.Game?.GetComponent<NekoGameComponent>();
            component?.Pause.ObserveLetterPause(__state);
            component?.Pause.ReceiveLetter(let);
        }
    }
}

[HarmonyPatch(typeof(WindowStack), nameof(WindowStack.Add), new Type[] { typeof(Window) })]
internal static class NativePauseWindowOpenHook
{
    private static bool Prepare() => NativeEventHookSupport.KnownBinary();
    private static void Prefix(Window window, out string[] __state) => __state = window != null && window.forcePause
        && UnityData.IsInMainThread && Current.ProgramState == ProgramState.Playing && Current.Game != null
        ? NativePauseWindows.CaptureFrames() : Array.Empty<string>();
    private static void Postfix(WindowStack __instance, Window window, string[] __state) => NativePauseWindows.Opened(__instance, window, __state);
}
