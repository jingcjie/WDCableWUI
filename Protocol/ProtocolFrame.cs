using System;

namespace WDCableWUI.Protocol;

public sealed class ProtocolFrame
{
    public ProtocolFrame(
        ProtocolFrameType type,
        ProtocolChannel channel,
        int flags = 0,
        long streamId = 0,
        long sequenceNumber = 0,
        Guid correlationId = default,
        string metadataJson = "",
        byte[]? payload = null)
    {
        Type = type;
        Channel = channel;
        Flags = flags;
        StreamId = streamId;
        SequenceNumber = sequenceNumber;
        CorrelationId = correlationId;
        MetadataJson = metadataJson ?? "";
        Payload = payload ?? [];
    }

    public ProtocolFrameType Type { get; }

    public ProtocolChannel Channel { get; }

    public int Flags { get; }

    public long StreamId { get; }

    public long SequenceNumber { get; }

    public Guid CorrelationId { get; }

    public string MetadataJson { get; }

    public byte[] Payload { get; }
}
