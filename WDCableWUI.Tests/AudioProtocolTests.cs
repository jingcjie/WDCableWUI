using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;
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
                SessionTransportRole.Listener,
                AudioProtocol.LatencyModeStable,
                AudioProtocol.QualityHigh,
                AudioProtocol.BitrateHighBps));
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
        Assert.AreEqual(AudioProtocol.LatencyModeStable, offer.LatencyMode);
        Assert.AreEqual(AudioProtocol.QualityHigh, offer.QualityMode);
        Assert.AreEqual(AudioProtocol.BitrateHighBps, offer.BitrateBps);
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
                receiverProbeRequired: true,
                AudioProtocol.LatencyModeLow,
                AudioProtocol.QualityBalanced,
                AudioProtocol.BitrateBalancedBps));
        var accept = AudioProtocol.ParseAccept(metadata);

        Assert.AreEqual(9, accept.StreamId);
        Assert.AreEqual("offer-2", accept.OfferId);
        Assert.AreEqual(AudioProtocol.TransportRtpUdp, accept.Transport);
        Assert.AreEqual(AudioProtocol.CodecOpus, accept.Codec);
        Assert.AreEqual(AudioProtocol.CodecImplLibOpus, accept.CodecImpl);
        Assert.AreEqual(AudioProtocol.LatencyModeLow, accept.LatencyMode);
        Assert.AreEqual(AudioProtocol.QualityBalanced, accept.QualityMode);
        Assert.AreEqual(AudioProtocol.BitrateBalancedBps, accept.BitrateBps);
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
    public void AcceptEchoesOfferedLatencyQualityAndBitrate()
    {
        var offer = AudioProtocol.ParseOffer(AudioProtocol.ParseMetadata(
            AudioProtocol.Offer(
                42,
                "offer-echo",
                AudioProtocol.SourceSystemAudio,
                0x01020304,
                SessionTransportRole.Listener,
                AudioProtocol.LatencyModeStable,
                AudioProtocol.QualityNearLossless,
                AudioProtocol.BitrateNearLosslessBps)));

        var accept = AudioProtocol.ParseAccept(AudioProtocol.ParseMetadata(
            AudioProtocol.Accept(
                offer.StreamId,
                offer.OfferId,
                0x05060708,
                SessionTransportRole.Connector,
                receiverProbeRequired: true,
                offer.LatencyMode,
                offer.QualityMode,
                offer.BitrateBps)));

        Assert.AreEqual(offer.LatencyMode, accept.LatencyMode);
        Assert.AreEqual(offer.QualityMode, accept.QualityMode);
        Assert.AreEqual(offer.BitrateBps, accept.BitrateBps);
        Assert.IsTrue(AudioProtocol.IsCompatibleAccept(accept));
    }

    [TestMethod]
    [DataRow(AudioProtocol.QualityStandard, AudioProtocol.BitrateStandardBps)]
    [DataRow(AudioProtocol.QualityBalanced, AudioProtocol.BitrateBalancedBps)]
    [DataRow(AudioProtocol.QualityHigh, AudioProtocol.BitrateHighBps)]
    [DataRow(AudioProtocol.QualityNearLossless, AudioProtocol.BitrateNearLosslessBps)]
    public void QualityBitratePairsAreCompatible(string qualityMode, int bitrateBps)
    {
        var offer = AudioProtocol.ParseOffer(OfferMetadata(qualityMode, bitrateBps));
        var accept = AudioProtocol.ParseAccept(AcceptMetadata(qualityMode, bitrateBps));

        Assert.IsTrue(AudioProtocol.IsSupportedQualityBitratePair(qualityMode, bitrateBps));
        Assert.IsTrue(AudioProtocol.IsCompatibleOffer(offer));
        Assert.IsTrue(AudioProtocol.IsCompatibleAccept(accept));
    }

    [TestMethod]
    public void QualityValidationRejectsUnknownUnsupportedAndMismatchedPairs()
    {
        Assert.IsFalse(AudioProtocol.IsCompatibleOffer(
            AudioProtocol.ParseOffer(OfferMetadata("ultra", AudioProtocol.BitrateStandardBps))));
        Assert.IsFalse(AudioProtocol.IsCompatibleOffer(
            AudioProtocol.ParseOffer(OfferMetadata(AudioProtocol.QualityStandard, 48_000))));
        Assert.IsFalse(AudioProtocol.IsCompatibleOffer(
            AudioProtocol.ParseOffer(OfferMetadata(AudioProtocol.QualityHigh, AudioProtocol.BitrateStandardBps))));
    }

    [TestMethod]
    public void MissingQualityModeDefaultsOnlyForLegacyStandardBitrate()
    {
        var legacyOffer = AudioProtocol.ParseOffer(OfferMetadata(null, AudioProtocol.BitrateStandardBps));

        Assert.AreEqual(AudioProtocol.QualityStandard, legacyOffer.QualityMode);
        Assert.IsTrue(AudioProtocol.IsCompatibleOffer(legacyOffer));
        Assert.ThrowsException<FormatException>(
            () => AudioProtocol.ParseOffer(OfferMetadata(null, AudioProtocol.BitrateBalancedBps)));
    }

    [TestMethod]
    public void PeerAudioCapabilityDoesNotRequireQualitySelection()
    {
        Assert.IsTrue(AudioProtocol.PeerSupportsAudio(
        [
            ProtocolConstants.CapabilityAudioLink,
            ProtocolConstants.CapabilityAudioCodecOpus,
            ProtocolConstants.CapabilityAudioTransportRtp,
            ProtocolConstants.CapabilityAudioRtcp,
            ProtocolConstants.CapabilityAudioCodecLibOpus
        ]));
        Assert.IsFalse(AudioProtocol.PeerSupportsAudioQualitySelection(
        [
            ProtocolConstants.CapabilityAudioLink,
            ProtocolConstants.CapabilityAudioCodecOpus,
            ProtocolConstants.CapabilityAudioTransportRtp,
            ProtocolConstants.CapabilityAudioRtcp,
            ProtocolConstants.CapabilityAudioCodecLibOpus
        ]));
        Assert.IsTrue(AudioProtocol.PeerSupportsAudioQualitySelection(
        [
            ProtocolConstants.CapabilityAudioQualitySelect
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

    [TestMethod]
    public void HigherQualityRequiresQualitySelectionCapability()
    {
        Assert.IsFalse(AudioProtocol.RequiresQualitySelectionCapability(AudioProtocol.QualityStandard));
        Assert.IsTrue(AudioProtocol.RequiresQualitySelectionCapability(AudioProtocol.QualityBalanced));
        Assert.IsTrue(AudioProtocol.RequiresQualitySelectionCapability(AudioProtocol.QualityHigh));
        Assert.IsTrue(AudioProtocol.RequiresQualitySelectionCapability(AudioProtocol.QualityNearLossless));
    }

    private static IReadOnlyDictionary<string, JsonElement> OfferMetadata(string? qualityMode, int bitrateBps)
    {
        var values = BaseStreamMetadata(AudioProtocol.KindOffer, qualityMode, bitrateBps);
        values["source"] = AudioProtocol.SourceSystemAudio;
        return AudioProtocol.ParseMetadata(AudioProtocol.BuildMetadata(values));
    }

    private static IReadOnlyDictionary<string, JsonElement> AcceptMetadata(string qualityMode, int bitrateBps)
    {
        var values = BaseStreamMetadata(AudioProtocol.KindAccept, qualityMode, bitrateBps);
        values["receiverProbeRequired"] = true;
        return AudioProtocol.ParseMetadata(AudioProtocol.BuildMetadata(values));
    }

    private static Dictionary<string, object?> BaseStreamMetadata(string kind, string? qualityMode, int bitrateBps)
    {
        var values = new Dictionary<string, object?>
        {
            ["kind"] = kind,
            ["streamId"] = 1,
            ["offerId"] = "offer-test",
            ["transport"] = AudioProtocol.TransportRtpUdp,
            ["codec"] = AudioProtocol.CodecOpus,
            ["codecImpl"] = AudioProtocol.CodecImplLibOpus,
            ["sampleRate"] = AudioProtocol.SampleRate,
            ["channels"] = AudioProtocol.Channels,
            ["frameDurationMs"] = AudioProtocol.FrameDurationMs,
            ["latencyMode"] = AudioProtocol.LatencyModeLow,
            ["bitrateBps"] = bitrateBps,
            ["rtpPayloadType"] = AudioProtocol.RtpPayloadType,
            ["rtpClockRate"] = AudioProtocol.RtpClockRate,
            ["rtpSsrc"] = 0x01020304u,
            ["transportRole"] = SessionTransportRole.Listener.GetEventName()
        };
        if (qualityMode != null)
        {
            values["qualityMode"] = qualityMode;
        }

        return values;
    }
}
