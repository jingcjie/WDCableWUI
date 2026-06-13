namespace WDCableWUI.Protocol;

public static class ProtocolConstants
{
    public const int Magic = 0x57444342; // WDCB
    public const int Version = 1;
    public const int HeaderSize = 56;
    public const int MaxMetadataBytes = 64 * 1024;
    public const int MaxPayloadBytes = 1024 * 1024;

    public const string AppId = "wdcable";

    public const int DefaultControlPort = 8988;
    public const int DefaultBulkPort = 8989;
    public const int DefaultRealtimePort = 8990;

    public const string CapabilityChat = "control.chat";
    public const string CapabilityBulkFile = "bulk.file";
    public const string CapabilityBulkSpeed = "bulk.speed";
    public const string CapabilityDiagnosticsExport = "diagnostics.export";

    public static readonly string[] AdvertisedCapabilities =
    [
        CapabilityChat,
        CapabilityBulkFile,
        CapabilityBulkSpeed,
        CapabilityDiagnosticsExport
    ];
}
