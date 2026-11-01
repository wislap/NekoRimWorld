using System;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;
using Verse;

namespace Neko.RimWorld;

internal static class NativePauseWindows
{
    private static readonly NativePauseWindowRecords Records = new NativePauseWindowRecords();
    internal static JObject Capture() {
        int? tick = null;
        try {
            if (!UnityData.IsInMainThread || Current.ProgramState != ProgramState.Playing || Find.TickManager == null || Find.WindowStack == null)
                return Records.Capture(null, null, null, null);
            tick = Find.TickManager.TicksGame;
            var stack = Find.WindowStack;
            var rows = stack.Windows.Select((window, index) => new { window, index }).Where(x => x.window.forcePause)
                .Select(x => new NativePauseWindowRecords.Row { Instance = x.window, Id = x.window.ID,
                    StackIndex = x.index, Type = x.window.GetType().FullName ?? "unknown" }).ToArray();
            return Records.Capture(stack, Current.Game, tick, rows);
        } catch (Exception) {
            return Records.Capture(null, null, tick, null);
        }
    }
    internal static string[] CaptureFrames() {
        try {
            return new StackTrace().GetFrames()?.Select(f => f.GetMethod())
                .Where(m => m?.DeclaringType?.FullName != null)
                .Select(m => m!.DeclaringType!.FullName + "." + m.Name)
                .Where(name => !name.StartsWith("Neko.RimWorld.NativePauseWindow") && !name.StartsWith("HarmonyLib.") && !name.StartsWith("System."))
                .Take(8).ToArray() ?? Array.Empty<string>();
        } catch (Exception) { return Array.Empty<string>(); }
    }
    internal static void Opened(WindowStack stack, Window window, string[] frames) {
        try {
            if (UnityData.IsInMainThread && Current.ProgramState == ProgramState.Playing && Current.Game != null && window.forcePause && stack.Windows.Contains(window))
                Records.Opened(stack, Current.Game, window, window.ID, Find.TickManager?.TicksGame, frames);
        } catch (Exception) { /* Evidence capture cannot alter window behavior. */ }
    }
}
