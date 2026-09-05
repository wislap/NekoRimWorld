using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Neko.RimWorld;

public sealed class NekoRimWorldMod : Mod
{
    public const string PackageId = "neko.rimworld.bridge";
    public const string ModVersion = "0.1.0-dev";

    private static readonly string[] Capabilities =
    {
        "query.colony_digest.v1",
    };

    private readonly NekoRimWorldSettings settings;
    private string portBuffer;

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
        return "N.E.K.O RimWorld Bridge";
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var listing = new Listing_Standard();
        listing.Begin(inRect);
        listing.CheckboxLabeled(
            "Enable local N.E.K.O bridge",
            ref settings.Enabled,
            "Connects only to 127.0.0.1. Enabling requires a pairing token of at least 32 characters.");
        listing.TextFieldNumericLabeled(
            "Port",
            ref settings.Port,
            ref portBuffer,
            1,
            65_535);
        var enteredToken = listing.TextEntryLabeled(
            "Pairing token",
            settings.PairingToken ?? string.Empty,
            lineCount: 1).Trim();
        settings.PairingToken = enteredToken.Length > NekoRimWorldSettings.MaxPairingTokenLength
            ? enteredToken.Substring(0, NekoRimWorldSettings.MaxPairingTokenLength)
            : enteredToken;
        if (listing.ButtonText("Generate new pairing token"))
        {
            settings.PairingToken = Guid.NewGuid().ToString("N");
        }

        listing.Gap();
        listing.Label($"Endpoint: 127.0.0.1:{settings.Port}");
        listing.Label($"Runtime: {Runtime.State}");
        listing.Label("Generate creates a 32-character random token; custom tokens must be 32-128 characters.");
        if (settings.Enabled &&
            settings.PairingToken.Length < NekoRimWorldSettings.MinPairingTokenLength)
        {
            listing.Label("The bridge will be disabled when settings are saved because this token is too short.");
        }
        listing.Label("The token is local authentication material. Do not paste it into logs or chat.");
        listing.End();
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
