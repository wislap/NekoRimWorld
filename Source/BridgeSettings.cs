using System;
using System.Linq;
namespace Neko.RimWorld;
/// <summary>The bridge's configured endpoint and identity; each change bumps the generation.</summary>
internal sealed class BridgeSettings
{
    private readonly object gate = new();
    private bool enabled;
    private int port = BridgeRuntime.DefaultPort;
    private string token = string.Empty;
    private string modVersion = BridgeRuntime.ModVersion;
    private string gameVersion = "unknown";
    private string[] capabilities = Array.Empty<string>();
    private long generation;
    public bool Enabled
    {
        get
        {
            lock (gate)
            {
                return enabled;
            }
        }
    }
    /// <summary>Stores already-normalized settings; returns false when nothing changed.</summary>
    public bool Apply(bool isEnabled, int configuredPort, string pairingToken, string currentModVersion,
                      string currentGameVersion, string[] advertisedCapabilities)
    {
        lock (gate)
        {
            if (enabled == isEnabled && port == configuredPort &&
                string.Equals(token, pairingToken, StringComparison.Ordinal) &&
                string.Equals(modVersion, currentModVersion, StringComparison.Ordinal) &&
                string.Equals(gameVersion, currentGameVersion, StringComparison.Ordinal) &&
                capabilities.SequenceEqual(advertisedCapabilities, StringComparer.Ordinal))
            {
                return false;
            }
            enabled = isEnabled;
            port = configuredPort;
            token = pairingToken;
            modVersion = currentModVersion;
            gameVersion = currentGameVersion;
            capabilities = advertisedCapabilities;
            generation++;
            return true;
        }
    }
    public BridgeConfig Read()
    {
        lock (gate)
        {
            return new BridgeConfig(enabled, port, token, modVersion, gameVersion, capabilities, generation);
        }
    }
}
internal readonly struct BridgeConfig
{
    public BridgeConfig(bool isEnabled, int configuredPort, string pairingToken, string currentModVersion,
                        string currentGameVersion, string[] advertisedCapabilities, long generation)
    {
        Enabled = isEnabled;
        Port = configuredPort;
        Token = pairingToken;
        ModVersion = currentModVersion;
        GameVersion = currentGameVersion;
        Capabilities = advertisedCapabilities;
        Generation = generation;
    }
    public bool Enabled { get; }
    public int Port { get; }
    public string Token { get; }
    public string ModVersion { get; }
    public string GameVersion { get; }
    public string[] Capabilities { get; }
    public long Generation { get; }
}
