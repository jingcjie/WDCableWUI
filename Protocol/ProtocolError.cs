namespace WDCableWUI.Protocol;

public enum ProtocolError
{
    PartialRead,
    MalformedMagic,
    UnsupportedVersion,
    InvalidHeaderSize,
    InvalidFrameType,
    InvalidChannel,
    InvalidLength,
    MetadataTooLarge,
    PayloadTooLarge
}
