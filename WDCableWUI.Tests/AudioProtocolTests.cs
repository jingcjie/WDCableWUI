using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Protocol;
using WDCableWUI.Services;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class AudioProtocolTests
{
    [TestMethod]
    public void OfferUsesAndroidCompatibleMicrophoneSource()
    {
        var metadata = AudioProtocol.ParseMetadata(AudioProtocol.Offer(42, "offer-1"));
        var offer = AudioProtocol.ParseOffer(metadata);

        Assert.AreEqual(42, offer.StreamId);
        Assert.AreEqual("offer-1", offer.OfferId);
        Assert.AreEqual(AudioProtocol.SourceMicrophone, offer.Source);
        Assert.AreEqual(AudioProtocol.CodecOpus, offer.Codec);
        Assert.AreEqual(AudioProtocol.SampleRate, offer.SampleRate);
        Assert.AreEqual(AudioProtocol.Channels, offer.Channels);
        Assert.AreEqual(AudioProtocol.FrameDurationMs, offer.FrameDurationMs);
        Assert.AreEqual(AudioProtocol.BitrateBps, offer.BitrateBps);
    }

    [TestMethod]
    public void AcceptAndTransportParseRequiredFields()
    {
        var accept = AudioProtocol.ParseAccept(AudioProtocol.ParseMetadata(AudioProtocol.Accept(9, "offer-2")));
        var transport = AudioProtocol.ParseTransport(AudioProtocol.ParseMetadata(AudioProtocol.Transport(9, 50123)));

        Assert.AreEqual(9, accept.StreamId);
        Assert.AreEqual("offer-2", accept.OfferId);
        Assert.AreEqual(AudioProtocol.CodecOpus, accept.Codec);
        Assert.AreEqual(9, transport.StreamId);
        Assert.AreEqual(AudioProtocol.TransportTcp, transport.Transport);
        Assert.AreEqual(50123, transport.Port);
    }

    [TestMethod]
    public void MissingOfferFieldThrows()
    {
        var metadata = AudioProtocol.ParseMetadata("""{"kind":"audio.offer","streamId":1,"codec":"opus"}""");

        Assert.ThrowsException<FormatException>(() => AudioProtocol.ParseOffer(metadata));
    }

    [TestMethod]
    public void CodecConfigFrameParsesAsNonPlayableFrame()
    {
        var frame = new ProtocolFrame(
            ProtocolFrameType.AudioFrame,
            ProtocolChannel.Audio,
            streamId: 7,
            sequenceNumber: 3,
            metadataJson: AudioProtocol.FrameMetadata(1234, codecConfig: true),
            payload: [1, 2, 3]);

        var decoded = AudioProtocol.ParseAudioFrame(frame);

        Assert.IsTrue(decoded.CodecConfig);
        Assert.AreEqual(0, decoded.DurationMs);
        Assert.AreEqual(3, decoded.SequenceNumber);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, decoded.Payload);
    }

    [TestMethod]
    public void AndroidOpusCodecConfigPacketsUseMediaCodecLayout()
    {
        var packets = AudioProtocol.AndroidOpusCodecConfigPackets();

        Assert.AreEqual(3, packets.Count);
        CollectionAssert.AreEqual("OpusHead"u8.ToArray(), packets[0][..8]);
        Assert.AreEqual(1, packets[0][8]);
        Assert.AreEqual((byte)AudioProtocol.Channels, packets[0][9]);
        Assert.AreEqual(19, packets[0].Length);
        Assert.AreEqual(8, packets[1].Length);
        Assert.AreEqual(8, packets[2].Length);
        Assert.AreEqual(6_500_000L, AudioProtocol.OpusCodecDelayNs());
    }

    [TestMethod]
    public void PeerAudioCapabilityRequiresLinkAndOpus()
    {
        Assert.IsTrue(AudioProtocol.PeerSupportsAudio(
        [
            ProtocolConstants.CapabilityAudioLink,
            ProtocolConstants.CapabilityAudioCodecOpus
        ]));
        Assert.IsFalse(AudioProtocol.PeerSupportsAudio([ProtocolConstants.CapabilityAudioLink]));
    }

    [TestMethod]
    public void StopMetadataCarriesReasonAndStreamId()
    {
        var metadata = AudioProtocol.ParseMetadata(AudioProtocol.Stop(77, "local_stop"));

        Assert.AreEqual(AudioProtocol.KindStop, AudioProtocol.OptionalString(metadata, "kind"));
        Assert.AreEqual(77, AudioProtocol.OptionalInt64(metadata, "streamId"));
        Assert.AreEqual("local_stop", AudioProtocol.OptionalString(metadata, "reason"));
    }

    [TestMethod]
    public void TransportWithMissingPortParsesAsInvalidPort()
    {
        var metadata = AudioProtocol.ParseMetadata("""{"kind":"audio.transport","streamId":4,"transport":"tcp"}""");
        var transport = AudioProtocol.ParseTransport(metadata);

        Assert.AreEqual(4, transport.StreamId);
        Assert.AreEqual(AudioProtocol.TransportTcp, transport.Transport);
        Assert.AreEqual(-1, transport.Port);
    }
}
