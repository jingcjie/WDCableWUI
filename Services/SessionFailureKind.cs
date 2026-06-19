namespace WDCableWUI.Services;

public enum SessionFailureKind
{
    Unknown,
    TransportSetup,
    PeerProtocolMissing,
    ProtocolError,
    UnsupportedVersion,
    Canceled
}
