namespace WDCableWUI.Services;

public enum SessionPhase
{
    WifiDirectConnected,
    ConnectingTransport,
    Handshaking,
    Ready,
    Degraded,
    Disconnecting,
    Disconnected,
    Failed
}

public static class SessionPhaseExtensions
{
    public static string GetEventName(this SessionPhase phase)
    {
        return phase switch
        {
            SessionPhase.WifiDirectConnected => "WifiDirectConnected",
            SessionPhase.ConnectingTransport => "ConnectingTransport",
            SessionPhase.Handshaking => "Handshaking",
            SessionPhase.Ready => "Ready",
            SessionPhase.Degraded => "Degraded",
            SessionPhase.Disconnecting => "Disconnecting",
            SessionPhase.Disconnected => "Disconnected",
            SessionPhase.Failed => "Failed",
            _ => phase.ToString()
        };
    }
}
