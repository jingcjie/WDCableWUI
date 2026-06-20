using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Protocol;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class ProtocolCodecTests
{
    [TestMethod]
    public void ValidFrameRoundTrips()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var frame = new ProtocolFrame(
            type: ProtocolFrameType.ControlMessage,
            channel: ProtocolChannel.Control,
            flags: 7,
            streamId: 11,
            sequenceNumber: 12,
            correlationId: Guid.Parse("12345678-1234-5678-1234-567812345678"),
            metadataJson: """{"kind":"chat","messageId":"m1"}""",
            payload: payload);

        var decoded = ProtocolCodec.ReadFrame(new MemoryStream(ProtocolCodec.Encode(frame)));

        Assert.IsNotNull(decoded);
        Assert.AreEqual(frame.Type, decoded.Type);
        Assert.AreEqual(frame.Channel, decoded.Channel);
        Assert.AreEqual(frame.Flags, decoded.Flags);
        Assert.AreEqual(frame.StreamId, decoded.StreamId);
        Assert.AreEqual(frame.SequenceNumber, decoded.SequenceNumber);
        Assert.AreEqual(frame.CorrelationId, decoded.CorrelationId);
        Assert.AreEqual(frame.MetadataJson, decoded.MetadataJson);
        CollectionAssert.AreEqual(payload, decoded.Payload);
    }

    [TestMethod]
    public void EmptyInputReturnsNull()
    {
        Assert.IsNull(ProtocolCodec.ReadFrame(new MemoryStream()));
    }

    [TestMethod]
    public void ChunkedStreamReadsCompleteFrame()
    {
        var frame = new ProtocolFrame(
            ProtocolFrameType.HeartbeatPing,
            ProtocolChannel.Control,
            metadataJson: """{"chunked":true}""",
            payload: [9, 8, 7]);
        using var stream = new ChunkedReadStream(ProtocolCodec.Encode(frame), maxChunkSize: 2);

        var decoded = ProtocolCodec.ReadFrame(stream);

        Assert.IsNotNull(decoded);
        Assert.AreEqual(frame.MetadataJson, decoded.MetadataJson);
        CollectionAssert.AreEqual(frame.Payload, decoded.Payload);
    }

    [TestMethod]
    public void PartialHeaderThrowsTypedError()
    {
        var encoded = ProtocolCodec.Encode(new ProtocolFrame(ProtocolFrameType.HeartbeatPing, ProtocolChannel.Control));
        var partial = encoded[..(ProtocolConstants.HeaderSize - 2)];

        AssertProtocolError(ProtocolError.PartialRead, () => ProtocolCodec.ReadFrame(new MemoryStream(partial)));
    }

    [TestMethod]
    public void MalformedMagicThrowsTypedError()
    {
        var encoded = ProtocolCodec.Encode(new ProtocolFrame(ProtocolFrameType.HeartbeatPing, ProtocolChannel.Control));
        encoded[0] = 0;

        AssertProtocolError(ProtocolError.MalformedMagic, () => ProtocolCodec.ReadFrame(new MemoryStream(encoded)));
    }

    [TestMethod]
    public void UnsupportedVersionThrowsTypedError()
    {
        var encoded = ProtocolCodec.Encode(new ProtocolFrame(ProtocolFrameType.HeartbeatPing, ProtocolChannel.Control));
        encoded[5] = 1;

        AssertProtocolError(ProtocolError.UnsupportedVersion, () => ProtocolCodec.ReadFrame(new MemoryStream(encoded)));
    }

    [TestMethod]
    public void InvalidHeaderSizeThrowsTypedError()
    {
        var encoded = ProtocolCodec.Encode(new ProtocolFrame(ProtocolFrameType.HeartbeatPing, ProtocolChannel.Control));
        encoded[7] = ProtocolConstants.HeaderSize - 1;

        AssertProtocolError(ProtocolError.InvalidHeaderSize, () => ProtocolCodec.ReadFrame(new MemoryStream(encoded)));
    }

    [TestMethod]
    public void OversizedMetadataRejectedOnEncode()
    {
        var metadata = new string('x', ProtocolConstants.MaxMetadataBytes + 1);

        AssertProtocolError(
            ProtocolError.MetadataTooLarge,
            () => ProtocolCodec.Encode(
                new ProtocolFrame(
                    ProtocolFrameType.ControlMessage,
                    ProtocolChannel.Control,
                    metadataJson: metadata)));
    }

    [TestMethod]
    public void OversizedMetadataRejectedOnDecode()
    {
        var header = HeaderWithLengths(ProtocolConstants.MaxMetadataBytes + 1, 0);

        AssertProtocolError(ProtocolError.MetadataTooLarge, () => ProtocolCodec.ReadFrame(new MemoryStream(header)));
    }

    [TestMethod]
    public void OversizedPayloadRejectedOnEncode()
    {
        var payload = new byte[ProtocolConstants.MaxPayloadBytes + 1];

        AssertProtocolError(
            ProtocolError.PayloadTooLarge,
            () => ProtocolCodec.Encode(
                new ProtocolFrame(
                    ProtocolFrameType.BulkChunk,
                    ProtocolChannel.Bulk,
                    payload: payload)));
    }

    [TestMethod]
    public void OversizedPayloadRejectedOnDecode()
    {
        var header = HeaderWithLengths(0, ProtocolConstants.MaxPayloadBytes + 1);

        AssertProtocolError(ProtocolError.PayloadTooLarge, () => ProtocolCodec.ReadFrame(new MemoryStream(header)));
    }

    [TestMethod]
    public void ZeroLengthPayloadRoundTrips()
    {
        var decoded = ProtocolCodec.ReadFrame(
            new MemoryStream(
                ProtocolCodec.Encode(
                    new ProtocolFrame(
                        ProtocolFrameType.Ack,
                        ProtocolChannel.Control,
                        metadataJson: """{"ok":true}"""))));

        Assert.IsNotNull(decoded);
        Assert.AreEqual(0, decoded.Payload.Length);
        Assert.AreEqual("""{"ok":true}""", decoded.MetadataJson);
    }

    [TestMethod]
    public void AudioFrameRoundTripsOnAudioChannel()
    {
        var payload = new byte[] { 0x11, 0x22, 0x33 };
        var frame = new ProtocolFrame(
            ProtocolFrameType.AudioFrame,
            ProtocolChannel.Audio,
            streamId: 101,
            sequenceNumber: 5,
            metadataJson: """{"codec":"opus","sentAtMs":1234,"durationMs":20}""",
            payload: payload);

        var decoded = ProtocolCodec.ReadFrame(new MemoryStream(ProtocolCodec.Encode(frame)));

        Assert.IsNotNull(decoded);
        Assert.AreEqual(ProtocolFrameType.AudioFrame, decoded.Type);
        Assert.AreEqual(ProtocolChannel.Audio, decoded.Channel);
        Assert.AreEqual("audio.frame", decoded.Type.GetProtocolName());
        Assert.AreEqual("audio", decoded.Channel.GetProtocolName());
        Assert.AreEqual(101, decoded.StreamId);
        Assert.AreEqual(5, decoded.SequenceNumber);
        Assert.AreEqual(frame.MetadataJson, decoded.MetadataJson);
        CollectionAssert.AreEqual(payload, decoded.Payload);
    }

    [TestMethod]
    public void WinUIAdvertisesAudioAfterRuntimeIsImplemented()
    {
        CollectionAssert.Contains(
            ProtocolConstants.AdvertisedCapabilities,
            ProtocolConstants.CapabilityAudioLink);
        CollectionAssert.Contains(
            ProtocolConstants.AdvertisedCapabilities,
            ProtocolConstants.CapabilityAudioCodecOpus);
        CollectionAssert.Contains(
            ProtocolConstants.AdvertisedCapabilities,
            ProtocolConstants.CapabilityAudioTransportRtp);
        CollectionAssert.Contains(
            ProtocolConstants.AdvertisedCapabilities,
            ProtocolConstants.CapabilityAudioRtcp);
        CollectionAssert.Contains(
            ProtocolConstants.AdvertisedCapabilities,
            ProtocolConstants.CapabilityAudioCodecLibOpus);
    }

    [TestMethod]
    public void JsonMetadataRoundTripPreservesUtf8()
    {
        var metadata = JsonSerializer.Serialize(new
        {
            deviceName = "Pixel 8",
            message = "hello \uD83D\uDC4B"
        });

        var decoded = ProtocolCodec.ReadFrame(
            new MemoryStream(
                ProtocolCodec.Encode(
                    new ProtocolFrame(
                        ProtocolFrameType.HandshakeHello,
                        ProtocolChannel.Control,
                        metadataJson: metadata))));

        Assert.IsNotNull(decoded);
        Assert.AreEqual(metadata, decoded.MetadataJson);
    }

    [TestMethod]
    public void PartialPayloadThrowsTypedError()
    {
        var encoded = ProtocolCodec.Encode(
            new ProtocolFrame(
                ProtocolFrameType.BulkChunk,
                ProtocolChannel.Bulk,
                payload: [9, 8, 7, 6]));
        var partial = encoded[..^1];

        AssertProtocolError(ProtocolError.PartialRead, () => ProtocolCodec.ReadFrame(new MemoryStream(partial)));
    }

    [TestMethod]
    public void HeaderLayoutMatchesAndroidBigEndianFields()
    {
        var encoded = ProtocolCodec.Encode(
            new ProtocolFrame(
                ProtocolFrameType.ControlMessage,
                ProtocolChannel.Control,
                flags: 2,
                streamId: 3,
                sequenceNumber: 4,
                correlationId: Guid.Parse("12345678-1234-5678-1234-567812345678"),
                metadataJson: "{}",
                payload: [1]));

        Assert.AreEqual(ProtocolConstants.Magic, BinaryPrimitives.ReadInt32BigEndian(encoded.AsSpan(0, 4)));
        Assert.AreEqual(ProtocolConstants.Version, BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(4, 2)));
        Assert.AreEqual(ProtocolConstants.HeaderSize, BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(6, 2)));
        Assert.AreEqual((ushort)ProtocolFrameType.ControlMessage, BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(8, 2)));
        Assert.AreEqual(2, BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(10, 2)));
        Assert.AreEqual((ushort)ProtocolChannel.Control, BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(12, 2)));
        Assert.AreEqual(3, BinaryPrimitives.ReadInt64BigEndian(encoded.AsSpan(16, 8)));
        Assert.AreEqual(4, BinaryPrimitives.ReadInt64BigEndian(encoded.AsSpan(24, 8)));
        CollectionAssert.AreEqual(
            new byte[]
            {
                0x12, 0x34, 0x56, 0x78,
                0x12, 0x34,
                0x56, 0x78,
                0x12, 0x34,
                0x56, 0x78, 0x12, 0x34, 0x56, 0x78
            },
            encoded[32..48]);
        Assert.AreEqual(2, BinaryPrimitives.ReadInt32BigEndian(encoded.AsSpan(48, 4)));
        Assert.AreEqual(1, BinaryPrimitives.ReadInt32BigEndian(encoded.AsSpan(52, 4)));
        Assert.AreEqual(ProtocolConstants.HeaderSize, 56);
    }

    private static byte[] HeaderWithLengths(int metadataLength, int payloadLength)
    {
        var header = new byte[ProtocolConstants.HeaderSize];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), ProtocolConstants.Magic);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), ProtocolConstants.Version);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), ProtocolConstants.HeaderSize);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8, 2), (ushort)ProtocolFrameType.ControlMessage);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(12, 2), (ushort)ProtocolChannel.Control);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(14, 2), 0);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(48, 4), metadataLength);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(52, 4), payloadLength);
        return header;
    }

    private static void AssertProtocolError(ProtocolError expected, Action action)
    {
        try
        {
            action();
            Assert.Fail($"Expected protocol error {expected}");
        }
        catch (ProtocolException exception)
        {
            Assert.AreEqual(expected, exception.Error);
        }
    }

    private sealed class ChunkedReadStream : MemoryStream
    {
        private readonly int _maxChunkSize;

        public ChunkedReadStream(byte[] buffer, int maxChunkSize)
            : base(buffer)
        {
            _maxChunkSize = maxChunkSize;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return base.Read(buffer, offset, Math.Min(count, _maxChunkSize));
        }

        public override int Read(Span<byte> buffer)
        {
            return base.Read(buffer[..Math.Min(buffer.Length, _maxChunkSize)]);
        }
    }
}
