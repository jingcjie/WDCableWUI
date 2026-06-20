using System;
using System.Buffers.Binary;

namespace WDCableWUI.Services;

public sealed record RtpPacket(
    byte PayloadType,
    ushort SequenceNumber,
    uint Timestamp,
    uint Ssrc,
    byte[] Payload,
    bool Marker = false)
{
    public const int HeaderSize = 12;

    public byte[] Encode()
    {
        var buffer = new byte[HeaderSize + Payload.Length];
        buffer[0] = 0x80;
        buffer[1] = (byte)((Marker ? 0x80 : 0) | (PayloadType & 0x7f));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), SequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4, 4), Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8, 4), Ssrc);
        Payload.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }

    public static bool TryDecode(ReadOnlySpan<byte> buffer, out RtpPacket packet)
    {
        packet = new RtpPacket(0, 0, 0, 0, []);
        if (buffer.Length < HeaderSize || (buffer[0] >> 6) != 2)
        {
            return false;
        }

        var csrcCount = buffer[0] & 0x0f;
        var hasExtension = (buffer[0] & 0x10) != 0;
        var hasPadding = (buffer[0] & 0x20) != 0;
        var headerLength = HeaderSize + csrcCount * 4;
        if (buffer.Length < headerLength)
        {
            return false;
        }

        if (hasExtension)
        {
            if (buffer.Length < headerLength + 4)
            {
                return false;
            }

            var extensionWords = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(headerLength + 2, 2));
            headerLength += 4 + extensionWords * 4;
            if (buffer.Length < headerLength)
            {
                return false;
            }
        }

        var payloadLength = buffer.Length - headerLength;
        if (hasPadding)
        {
            var padding = buffer[^1];
            if (padding == 0 || padding > payloadLength)
            {
                return false;
            }

            payloadLength -= padding;
        }

        var payload = buffer.Slice(headerLength, payloadLength).ToArray();
        packet = new RtpPacket(
            (byte)(buffer[1] & 0x7f),
            BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2)),
            BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(8, 4)),
            payload,
            (buffer[1] & 0x80) != 0);
        return true;
    }

    public static ushort NextSequence(ushort sequence)
    {
        return unchecked((ushort)(sequence + 1));
    }
}
