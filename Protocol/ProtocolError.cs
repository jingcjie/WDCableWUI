namespace WDCableWUI.Protocol;

public enum ProtocolError
{
    PartialRead,
    MalformedMagic,
    UnsupportedVersion,
    ProtocolMismatch,
    InvalidHeaderSize,
    InvalidFrameType,
    InvalidChannel,
    InvalidLength,
    MetadataTooLarge,
    PayloadTooLarge
}
