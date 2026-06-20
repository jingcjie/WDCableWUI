using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Protocol;
using WDCableWUI.Services;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class AudioProtocolTests
{
    [TestMethod]
    public void OfferCarriesRtpLibopusMetadata()
    {
        var metadata = AudioProtocol.ParseMetadata(
            AudioProtocol.Offer(
                42,
                "offer-1",
                AudioProtocol.SourceSystemAudio,
                0x01020304,
                SessionTransportRole.Listener));
        var offer = AudioProtocol.ParseOffer(metadata);

        Assert.AreEqual(42, offer.StreamId);
        Assert.AreEqual("offer-1", offer.OfferId);
        Assert.AreEqual(AudioProtocol.TransportRtpUdp, offer.Transport);
        Assert.AreEqual(AudioProtocol.SourceSystemAudio, offer.Source);
        Assert.AreEqual(AudioProtocol.CodecOpus, offer.Codec);
        Assert.AreEqual(AudioProtocol.CodecImplLibOpus, offer.CodecImpl);
        Assert.AreEqual(AudioProtocol.SampleRate, offer.SampleRate);
        Assert.AreEqual(AudioProtocol.Channels, offer.Channels);
        Assert.AreEqual(AudioProtocol.FrameDurationMs, offer.FrameDurationMs);
        Assert.AreEqual(AudioProtocol.BitrateBps, offer.BitrateBps);
        Assert.AreEqual(AudioProtocol.RtpPayloadType, offer.RtpPayloadType);
        Assert.AreEqual(AudioProtocol.RtpClockRate, offer.RtpClockRate);
        Assert.AreEqual(0x01020304u, offer.RtpSsrc);
        Assert.AreEqual(SessionTransportRole.Listener, offer.TransportRole);
        Assert.IsTrue(AudioProtocol.IsCompatibleOffer(offer));
    }

    [TestMethod]
    public void AcceptCarriesReceiverProbeRequirement()
    {
        var metadata = AudioProtocol.ParseMetadata(
            AudioProtocol.Accept(
                9,
                "offer-2",
                0x05060708,
                SessionTransportRole.Connector,
                receiverProbeRequired: true));
        var accept = AudioProtocol.ParseAccept(metadata);

        Assert.AreEqual(9, accept.StreamId);
        Assert.AreEqual("offer-2", accept.OfferId);
        Assert.AreEqual(AudioProtocol.TransportRtpUdp, accept.Transport);
        Assert.AreEqual(AudioProtocol.CodecOpus, accept.Codec);
        Assert.AreEqual(AudioProtocol.CodecImplLibOpus, accept.CodecImpl);
        Assert.AreEqual(0x05060708u, accept.RtpSsrc);
        Assert.AreEqual(SessionTransportRole.Connector, accept.TransportRole);
        Assert.IsTrue(accept.ReceiverProbeRequired);
        Assert.IsTrue(AudioProtocol.IsCompatibleAccept(accept));
    }

    [TestMethod]
    public void MissingOfferFieldThrows()
    {
        var metadata = AudioProtocol.ParseMetadata("""{"kind":"audio.offer","streamId":1,"codec":"opus"}""");

        Assert.ThrowsException<FormatException>(() => AudioProtocol.ParseOffer(metadata));
    }

    [TestMethod]
    public void PeerAudioCapabilityRequiresRtpRtcpAndLibopus()
    {
        Assert.IsTrue(AudioProtocol.PeerSupportsAudio(
        [
            ProtocolConstants.CapabilityAudioLink,
            ProtocolConstants.CapabilityAudioCodecOpus,
            ProtocolConstants.CapabilityAudioTransportRtp,
            ProtocolConstants.CapabilityAudioRtcp,
            ProtocolConstants.CapabilityAudioCodecLibOpus
        ]));
        Assert.IsFalse(AudioProtocol.PeerSupportsAudio(
        [
            ProtocolConstants.CapabilityAudioLink,
            ProtocolConstants.CapabilityAudioCodecOpus
        ]));
    }

    [TestMethod]
    public void AudioPortOwnershipUsesTransportRole()
    {
        Assert.IsTrue(AudioProtocol.OwnsFixedAudioPorts(SessionTransportRole.Listener));
        Assert.IsFalse(AudioProtocol.OwnsFixedAudioPorts(SessionTransportRole.Connector));
        Assert.IsFalse(AudioProtocol.ReceiverProbeRequired(SessionTransportRole.Listener));
        Assert.IsTrue(AudioProtocol.ReceiverProbeRequired(SessionTransportRole.Connector));
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
    public void StableLatencyModeNormalizesExplicitly()
    {
        Assert.AreEqual(AudioProtocol.LatencyModeLow, AudioProtocol.NormalizeLatencyMode(null));
        Assert.AreEqual(AudioProtocol.LatencyModeLow, AudioProtocol.NormalizeLatencyMode("other"));
        Assert.AreEqual(AudioProtocol.LatencyModeStable, AudioProtocol.NormalizeLatencyMode(AudioProtocol.LatencyModeStable));
    }
}
