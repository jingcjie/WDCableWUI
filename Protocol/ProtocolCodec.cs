using System.Buffers.Binary;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WDCableWUI.Protocol;

public static class ProtocolCodec
{
    public static byte[] Encode(ProtocolFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var metadataBytes = Encoding.UTF8.GetBytes(frame.MetadataJson);
        var payload = frame.Payload;
        ValidateLengths(metadataBytes.Length, payload.Length);
        ValidateUnsignedShort(frame.Flags, nameof(frame.Flags));

        var output = new byte[ProtocolConstants.HeaderSize + metadataBytes.Length + payload.Length];
        var header = output.AsSpan(0, ProtocolConstants.HeaderSize);

        BinaryPrimitives.WriteInt32BigEndian(header[0..4], ProtocolConstants.Magic);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..6], ProtocolConstants.Version);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..8], ProtocolConstants.HeaderSize);
        BinaryPrimitives.WriteUInt16BigEndian(header[8..10], (ushort)frame.Type);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..12], (ushort)frame.Flags);
        BinaryPrimitives.WriteUInt16BigEndian(header[12..14], (ushort)frame.Channel);
        BinaryPrimitives.WriteUInt16BigEndian(header[14..16], 0);
        BinaryPrimitives.WriteInt64BigEndian(header[16..24], frame.StreamId);
        BinaryPrimitives.WriteInt64BigEndian(header[24..32], frame.SequenceNumber);
        WriteGuidBigEndian(frame.CorrelationId, header[32..48]);
        BinaryPrimitives.WriteInt32BigEndian(header[48..52], metadataBytes.Length);
        BinaryPrimitives.WriteInt32BigEndian(header[52..56], payload.Length);

        metadataBytes.CopyTo(output.AsSpan(ProtocolConstants.HeaderSize));
        payload.CopyTo(output.AsSpan(ProtocolConstants.HeaderSize + metadataBytes.Length));

        return output;
    }

    public static void WriteFrame(ProtocolFrame frame, Stream outputStream)
    {
        ArgumentNullException.ThrowIfNull(outputStream);

        var encoded = Encode(frame);
        outputStream.Write(encoded, 0, encoded.Length);
        outputStream.Flush();
    }

    public static async Task WriteFrameAsync(
        ProtocolFrame frame,
        Stream outputStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputStream);

        var encoded = Encode(frame);
        await outputStream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
        await outputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static ProtocolFrame? ReadFrame(Stream inputStream)
    {
        ArgumentNullException.ThrowIfNull(inputStream);

        var header = new byte[ProtocolConstants.HeaderSize];
        var firstByte = inputStream.ReadByte();
        if (firstByte == -1)
        {
            return null;
        }

        header[0] = (byte)firstByte;
        ReadFully(inputStream, header, 1, ProtocolConstants.HeaderSize - 1, "header");

        var decodedHeader = DecodeHeader(header);
        var metadataBytes = ReadSegment(inputStream, decodedHeader.MetadataLength, "metadata");
        var payload = ReadSegment(inputStream, decodedHeader.PayloadLength, "payload");

        return new ProtocolFrame(
            decodedHeader.Type,
            decodedHeader.Channel,
            decodedHeader.Flags,
            decodedHeader.StreamId,
            decodedHeader.SequenceNumber,
            decodedHeader.CorrelationId,
            Encoding.UTF8.GetString(metadataBytes),
            payload);
    }

    public static async Task<ProtocolFrame?> ReadFrameAsync(
        Stream inputStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputStream);

        var header = new byte[ProtocolConstants.HeaderSize];
        var firstByteBuffer = new byte[1];
        var firstByteCount = await inputStream.ReadAsync(firstByteBuffer, cancellationToken).ConfigureAwait(false);
        if (firstByteCount == 0)
        {
            return null;
        }

        header[0] = firstByteBuffer[0];
        await ReadFullyAsync(
            inputStream,
            header.AsMemory(1, ProtocolConstants.HeaderSize - 1),
            "header",
            cancellationToken).ConfigureAwait(false);

        var decodedHeader = DecodeHeader(header);
        var metadataBytes = await ReadSegmentAsync(
            inputStream,
            decodedHeader.MetadataLength,
            "metadata",
            cancellationToken).ConfigureAwait(false);
        var payload = await ReadSegmentAsync(
            inputStream,
            decodedHeader.PayloadLength,
            "payload",
            cancellationToken).ConfigureAwait(false);

        return new ProtocolFrame(
            decodedHeader.Type,
            decodedHeader.Channel,
            decodedHeader.Flags,
            decodedHeader.StreamId,
            decodedHeader.SequenceNumber,
            decodedHeader.CorrelationId,
            Encoding.UTF8.GetString(metadataBytes),
            payload);
    }

    private static DecodedHeader DecodeHeader(ReadOnlySpan<byte> header)
    {
        var magic = BinaryPrimitives.ReadInt32BigEndian(header[0..4]);
        if (magic != ProtocolConstants.Magic)
        {
            throw new ProtocolException(ProtocolError.MalformedMagic, $"Malformed protocol magic: 0x{magic:x8}");
        }

        var version = BinaryPrimitives.ReadUInt16BigEndian(header[4..6]);
        if (version != ProtocolConstants.Version)
        {
            throw new ProtocolException(ProtocolError.UnsupportedVersion, $"Unsupported protocol version: {version}");
        }

        var headerSize = BinaryPrimitives.ReadUInt16BigEndian(header[6..8]);
        if (headerSize != ProtocolConstants.HeaderSize)
        {
            throw new ProtocolException(ProtocolError.InvalidHeaderSize, $"Invalid protocol header size: {headerSize}");
        }

        var type = ProtocolFrameTypeExtensions.FromId(BinaryPrimitives.ReadUInt16BigEndian(header[8..10]));
        var flags = BinaryPrimitives.ReadUInt16BigEndian(header[10..12]);
        var channel = ProtocolChannelExtensions.FromId(BinaryPrimitives.ReadUInt16BigEndian(header[12..14]));
        var streamId = BinaryPrimitives.ReadInt64BigEndian(header[16..24]);
        var sequenceNumber = BinaryPrimitives.ReadInt64BigEndian(header[24..32]);
        var correlationId = ReadGuidBigEndian(header[32..48]);
        var metadataLength = BinaryPrimitives.ReadInt32BigEndian(header[48..52]);
        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(header[52..56]);

        ValidateLengths(metadataLength, payloadLength);

        return new DecodedHeader(
            type,
            channel,
            flags,
            streamId,
            sequenceNumber,
            correlationId,
            metadataLength,
            payloadLength);
    }

    private static void ValidateLengths(int metadataLength, int payloadLength)
    {
        if (metadataLength < 0 || payloadLength < 0)
        {
            throw new ProtocolException(ProtocolError.InvalidLength, "Negative metadata or payload length");
        }

        if (metadataLength > ProtocolConstants.MaxMetadataBytes)
        {
            throw new ProtocolException(
                ProtocolError.MetadataTooLarge,
                $"Metadata length {metadataLength} exceeds {ProtocolConstants.MaxMetadataBytes}");
        }

        if (payloadLength > ProtocolConstants.MaxPayloadBytes)
        {
            throw new ProtocolException(
                ProtocolError.PayloadTooLarge,
                $"Payload length {payloadLength} exceeds {ProtocolConstants.MaxPayloadBytes}");
        }
    }

    private static void ValidateUnsignedShort(int value, string name)
    {
        if (value is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(name, value, "Value must fit in an unsigned 16-bit field.");
        }
    }

    private static byte[] ReadSegment(Stream inputStream, int length, string label)
    {
        if (length == 0)
        {
            return [];
        }

        var buffer = new byte[length];
        ReadFully(inputStream, buffer, 0, length, label);
        return buffer;
    }

    private static async Task<byte[]> ReadSegmentAsync(
        Stream inputStream,
        int length,
        string label,
        CancellationToken cancellationToken)
    {
        if (length == 0)
        {
            return [];
        }

        var buffer = new byte[length];
        await ReadFullyAsync(inputStream, buffer, label, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    private static void ReadFully(Stream inputStream, byte[] buffer, int offset, int length, string label)
    {
        var totalRead = 0;
        while (totalRead < length)
        {
            var read = inputStream.Read(buffer, offset + totalRead, length - totalRead);
            if (read <= 0)
            {
                throw new ProtocolException(
                    ProtocolError.PartialRead,
                    $"Unexpected end of stream while reading {label}");
            }

            totalRead += read;
        }
    }

    private static async Task ReadFullyAsync(
        Stream inputStream,
        Memory<byte> buffer,
        string label,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await inputStream.ReadAsync(buffer[totalRead..], cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new ProtocolException(
                    ProtocolError.PartialRead,
                    $"Unexpected end of stream while reading {label}");
            }

            totalRead += read;
        }
    }

    private static void WriteGuidBigEndian(Guid value, Span<byte> destination)
    {
        if (!value.TryWriteBytes(destination, bigEndian: true, out var bytesWritten) || bytesWritten != 16)
        {
            throw new InvalidOperationException("Failed to encode protocol correlation id.");
        }
    }

    private static Guid ReadGuidBigEndian(ReadOnlySpan<byte> source)
    {
        return new Guid(source, bigEndian: true);
    }

    private sealed record DecodedHeader(
        ProtocolFrameType Type,
        ProtocolChannel Channel,
        int Flags,
        long StreamId,
        long SequenceNumber,
        Guid CorrelationId,
        int MetadataLength,
        int PayloadLength);
}
