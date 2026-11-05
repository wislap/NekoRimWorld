using System.Threading;
namespace Neko.RimWorld;
/// <summary>The bridge connection state machine; each transition is logged once, throttled.</summary>
internal sealed class BridgeStatus
{
    private readonly BridgeLog log;
    private int state = (int)BridgeConnectionState.Stopped;
    public BridgeStatus(BridgeLog log) => this.log = log;
    public BridgeConnectionState State => (BridgeConnectionState)Volatile.Read(ref state);
    public void Set(BridgeConnectionState value, string message)
    {
        if (Interlocked.Exchange(ref state, (int)value) == (int)value)
        {
            return;
        }
        log.StateChanged(message);
    }
}
public enum BridgeConnectionState
{
    Stopped,
    Disabled,
    Connecting,
    Handshaking,
    Ready,
    Backoff
}
