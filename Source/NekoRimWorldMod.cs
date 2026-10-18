using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Neko.RimWorld;

public sealed class NekoRimWorldMod : Mod
{
    public const string PackageId = "neko.rimworld.bridge";
    public const string ModVersion = "3.2.0";

    private static readonly string[] Capabilities =
    {
        "query.execute.v2",
        "native_behavior.v1",
        "situation_snapshot.v1",
        "colony_state.v1",
    };

    private readonly NekoRimWorldSettings settings;
    private string portBuffer;
    private Vector2 settingsScrollPosition;
    private float settingsContentHeight = 600f;

    public NekoRimWorldMod(ModContentPack content) : base(content)
    {
        Runtime = BridgeRuntime.Instance;
        Runtime.SetLogger(WriteBridgeLog);

        settings = GetSettings<NekoRimWorldSettings>();
        WarnIfWeakTokenWasDisabled(settings.Normalize());
        portBuffer = settings.Port.ToString();

        new Harmony(PackageId).PatchAll(Assembly.GetExecutingAssembly());
        ApplyRuntimeConfiguration();
        Log.Message("[N.E.K.O RimWorld] Bridge mod loaded; enable and pair it in Mod Settings to connect.");
    }

    internal static BridgeRuntime Runtime { get; private set; } = BridgeRuntime.Instance;

    public override string SettingsCategory()
    {
        return "NekoRimWorld_SettingsTitle".Translate();
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var viewRect = new Rect(0f, 0f, inRect.width - 20f, Mathf.Max(inRect.height, settingsContentHeight));
        Widgets.BeginScrollView(inRect, ref settingsScrollPosition, viewRect);
        var listing = new Listing_Standard();
        listing.Begin(viewRect);
        listing.Label("NekoRimWorld_ConnectionHelp".Translate());
        listing.Gap();
        listing.CheckboxLabeled(
            "NekoRimWorld_EnableConnection".Translate(),
            ref settings.Enabled,
            "NekoRimWorld_EnableConnectionHelp".Translate());
        listing.TextFieldNumericLabeled(
            "NekoRimWorld_Port".Translate(),
            ref settings.Port,
            ref portBuffer,
            1,
            65_535);
        var enteredToken = listing.TextEntryLabeled(
            "NekoRimWorld_PairingCode".Translate(),
            settings.PairingToken ?? string.Empty,
            lineCount: 1).Trim();
        settings.PairingToken = enteredToken.Length > NekoRimWorldSettings.MaxPairingTokenLength
            ? enteredToken.Substring(0, NekoRimWorldSettings.MaxPairingTokenLength)
            : enteredToken;
        if (listing.ButtonText("NekoRimWorld_GeneratePairingCode".Translate()))
        {
            settings.PairingToken = Guid.NewGuid().ToString("N");
        }

        listing.Gap();
        listing.Label("NekoRimWorld_LocalAddress".Translate(settings.Port));
        listing.Label("NekoRimWorld_ConnectionState".Translate(
            ("NekoRimWorld_State_" + Runtime.State).Translate()));
        listing.Label("NekoRimWorld_PairingCodeHelp".Translate(
            NekoRimWorldSettings.MinPairingTokenLength, NekoRimWorldSettings.MaxPairingTokenLength));
        if (settings.Enabled &&
            settings.PairingToken.Length < NekoRimWorldSettings.MinPairingTokenLength)
        {
            listing.Label("NekoRimWorld_PairingCodeTooShort".Translate());
        }
        listing.Label("NekoRimWorld_SaveHelp".Translate());
        listing.Label("NekoRimWorld_PairingCodePrivacy".Translate());
        settingsContentHeight = listing.CurHeight + 8f;
        listing.End();
        Widgets.EndScrollView();
    }

    public override void WriteSettings()
    {
        WarnIfWeakTokenWasDisabled(settings.Normalize());
        portBuffer = settings.Port.ToString();
        base.WriteSettings();
        ApplyRuntimeConfiguration();
    }

    private void ApplyRuntimeConfiguration()
    {
        try
        {
            Runtime.Configure(
                settings.Enabled,
                settings.Port,
                settings.PairingToken,
                ModVersion,
                global::RimWorld.VersionControl.CurrentVersionStringWithRev,
                Capabilities);
        }
        catch (Exception exception)
        {
            settings.Enabled = false;
            Log.Error($"[N.E.K.O RimWorld] Bridge configuration rejected: {exception.Message}");
            Runtime.Configure(
                false,
                BridgeRuntime.DefaultPort,
                string.Empty,
                ModVersion,
                global::RimWorld.VersionControl.CurrentVersionStringWithRev,
                Capabilities);
        }
    }

    private static void WriteBridgeLog(BridgeLogLevel level, string message)
    {
        var text = $"[N.E.K.O RimWorld] {message}";
        switch (level)
        {
            case BridgeLogLevel.Error:
                Log.Error(text);
                break;
            case BridgeLogLevel.Warning:
                Log.Warning(text);
                break;
            default:
                Log.Message(text);
                break;
        }
    }

    private static void WarnIfWeakTokenWasDisabled(bool disabled)
    {
        if (disabled)
        {
            Log.Warning(
                "[N.E.K.O RimWorld] Bridge disabled: pairing token must contain at least 32 characters.");
        }
    }
}

public sealed class NekoRimWorldSettings : ModSettings
{
    internal const int MinPairingTokenLength = 32;
    internal const int MaxPairingTokenLength = 128;

    public bool Enabled;
    public int Port = BridgeRuntime.DefaultPort;
    public string PairingToken = string.Empty;

    public override void ExposeData()
    {
        Scribe_Values.Look(ref Enabled, "bridgeEnabled", false);
        Scribe_Values.Look(ref Port, "bridgePort", BridgeRuntime.DefaultPort);
        Scribe_Values.Look(ref PairingToken, "pairingToken", string.Empty);
    }

    internal bool Normalize()
    {
        if (Port < 1 || Port > 65_535)
        {
            Port = BridgeRuntime.DefaultPort;
        }

        PairingToken = (PairingToken ?? string.Empty).Trim();
        if (PairingToken.Length > MaxPairingTokenLength)
        {
            PairingToken = PairingToken.Substring(0, MaxPairingTokenLength);
        }

        if (Enabled && PairingToken.Length < MinPairingTokenLength)
        {
            Enabled = false;
            return true;
        }

        return false;
    }
}
