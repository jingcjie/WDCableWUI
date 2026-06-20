using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using WDCableWUI.Protocol;

namespace WDCableWUI.Services;

public static class AudioProtocol
{
    private static readonly Lazy<bool> AudioRuntimeAvailable = new(CheckAudioRuntimeAvailable);

    public const string KindReceiveReady = "audio.receive.ready";
    public const string KindReceiveStopped = "audio.receive.stopped";
    public const string KindOffer = "audio.offer";
    public const string KindAccept = "audio.accept";
    public const string KindStop = "audio.stop";

    public const string ErrorUnsupported = "audio_unsupported";
    public const string ErrorReceiverNotReady = "audio_receiver_not_ready";
    public const string ErrorBusy = "audio_busy";
    public const string ErrorPermissionDenied = "audio_permission_denied";
    public const string ErrorCodecUnavailable = "audio_codec_unavailable";
    public const string ErrorTransportFailed = "audio_transport_failed";
    public const string ErrorCaptureFailed = "audio_capture_failed";
    public const string ErrorPlaybackFailed = "audio_playback_failed";
    public const string ErrorRtpBindFailed = "audio_rtp_bind_failed";
    public const string ErrorRtpProbeTimeout = "audio_rtp_probe_timeout";
    public const string ErrorRtpSendFailed = "audio_rtp_send_failed";
    public const string ErrorRtpReceiveFailed = "audio_rtp_receive_failed";
    public const string ErrorRtpUnsupported = "audio_rtp_unsupported";

    public const string SourceMicrophone = "microphone";
    public const string SourceSystemAudio = "systemAudio";
    public const string CodecOpus = "opus";
    public const string CodecImplLibOpus = "libopus";
    public const string TransportRtpUdp = "rtp-udp";

    public const string LatencyModeLow = "lowLatency";
    public const string LatencyModeStable = "stable";

    public const string QualityStandard = "standard";
    public const string QualityBalanced = "balanced";
    public const string QualityHigh = "high";
    public const string QualityNearLossless = "nearLossless";

    public const int BitrateStandardBps = 32_000;
    public const int BitrateBalancedBps = 64_000;
    public const int BitrateHighBps = 128_000;
    public const int BitrateNearLosslessBps = 256_000;

    public const int RtpPort = 8990;
    public const int RtcpPort = 8991;
    public const byte RtpPayloadType = 111;
    public const int RtpClockRate = 48_000;
    public const int SampleRate = 48_000;
    public const int Channels = 1;
    public const int FrameDurationMs = 20;
    public const int BitrateBps = BitrateStandardBps;
    public const int SamplesPerFrame = SampleRate * FrameDurationMs / 1000;
    public const uint RtpTimestampIncrement = SamplesPerFrame;

    public static readonly IReadOnlyDictionary<string, int> QualityBitrates =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [QualityStandard] = BitrateStandardBps,
            [QualityBalanced] = BitrateBalancedBps,
            [QualityHigh] = BitrateHighBps,
            [QualityNearLossless] = BitrateNearLosslessBps
        };

    public static readonly byte[] RtpProbePayload = "WDA2RTP"u8.ToArray();
    public static readonly byte[] RtcpProbePayload = "WDA2RTCP"u8.ToArray();

    public static bool PeerSupportsAudio(IReadOnlyList<string> capabilities)
    {
        return capabilities.Contains(ProtocolConstants.CapabilityAudioLink) &&
               capabilities.Contains(ProtocolConstants.CapabilityAudioCodecOpus) &&
               capabilities.Contains(ProtocolConstants.CapabilityAudioTransportRtp) &&
               capabilities.Contains(ProtocolConstants.CapabilityAudioRtcp) &&
               capabilities.Contains(ProtocolConstants.CapabilityAudioCodecLibOpus);
    }

    public static bool PeerSupportsAudioQualitySelection(IReadOnlyList<string> capabilities)
    {
        return capabilities.Contains(ProtocolConstants.CapabilityAudioQualitySelect);
    }

    public static IReadOnlyList<string> AdvertisedCapabilitiesForRuntime()
    {
        var capabilities = ProtocolConstants.AdvertisedCapabilities
            .Where(capability => !IsAudioCapability(capability))
            .ToList();
        if (AudioRuntimeAvailable.Value)
        {
            capabilities.Add(ProtocolConstants.CapabilityAudioLink);
            capabilities.Add(ProtocolConstants.CapabilityAudioCodecOpus);
            capabilities.Add(ProtocolConstants.CapabilityAudioTransportRtp);
            capabilities.Add(ProtocolConstants.CapabilityAudioRtcp);
            capabilities.Add(ProtocolConstants.CapabilityAudioCodecLibOpus);
            capabilities.Add(ProtocolConstants.CapabilityAudioQualitySelect);
        }

        return capabilities;
    }

    public static bool OwnsFixedAudioPorts(SessionTransportRole transportRole)
    {
        return transportRole == SessionTransportRole.Listener;
    }

    public static bool ReceiverProbeRequired(SessionTransportRole receiverTransportRole)
    {
        return receiverTransportRole == SessionTransportRole.Connector;
    }

    public static uint NewSsrc()
    {
        var value = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        return unchecked((uint)value);
    }

    public static string ReceiveReady(long streamId)
    {
        return BuildMetadata(new Dictionary<string, object?>
        {
            ["kind"] = KindReceiveReady,
            ["streamId"] = streamId
        });
    }

    public static string ReceiveStopped(long streamId)
    {
        return BuildMetadata(new Dictionary<string, object?>
        {
            ["kind"] = KindReceiveStopped,
            ["streamId"] = streamId
        });
    }

    public static string Offer(
        long streamId,
        string offerId,
        string source,
        uint senderSsrc,
        SessionTransportRole transportRole,
        string latencyMode = LatencyModeLow,
        string qualityMode = QualityStandard,
        int bitrateBps = BitrateBps)
    {
        var normalizedLatencyMode = NormalizeLatencyMode(latencyMode);
        var normalizedQualityMode = NormalizeQualityMode(qualityMode);
        return BuildMetadata(new Dictionary<string, object?>
        {
            ["kind"] = KindOffer,
            ["streamId"] = streamId,
            ["offerId"] = offerId,
            ["transport"] = TransportRtpUdp,
            ["source"] = source,
            ["codec"] = CodecOpus,
            ["codecImpl"] = CodecImplLibOpus,
            ["sampleRate"] = SampleRate,
            ["channels"] = Channels,
            ["frameDurationMs"] = FrameDurationMs,
            ["latencyMode"] = normalizedLatencyMode,
            ["qualityMode"] = normalizedQualityMode,
            ["bitrateBps"] = bitrateBps,
            ["rtpPayloadType"] = RtpPayloadType,
            ["rtpClockRate"] = RtpClockRate,
            ["rtpSsrc"] = senderSsrc,
            ["transportRole"] = transportRole.GetEventName()
        });
    }

    public static string Accept(
        long streamId,
        string offerId,
        uint receiverSsrc,
        SessionTransportRole transportRole,
        bool receiverProbeRequired,
        string latencyMode = LatencyModeLow,
        string qualityMode = QualityStandard,
        int bitrateBps = BitrateBps)
    {
        var normalizedLatencyMode = NormalizeLatencyMode(latencyMode);
        var normalizedQualityMode = NormalizeQualityMode(qualityMode);
        return BuildMetadata(new Dictionary<string, object?>
        {
            ["kind"] = KindAccept,
            ["streamId"] = streamId,
            ["offerId"] = offerId,
            ["transport"] = TransportRtpUdp,
            ["codec"] = CodecOpus,
            ["codecImpl"] = CodecImplLibOpus,
            ["sampleRate"] = SampleRate,
            ["channels"] = Channels,
            ["frameDurationMs"] = FrameDurationMs,
            ["latencyMode"] = normalizedLatencyMode,
            ["qualityMode"] = normalizedQualityMode,
            ["bitrateBps"] = bitrateBps,
            ["rtpPayloadType"] = RtpPayloadType,
            ["rtpClockRate"] = RtpClockRate,
            ["rtpSsrc"] = receiverSsrc,
            ["transportRole"] = transportRole.GetEventName(),
            ["receiverProbeRequired"] = receiverProbeRequired
        });
    }

    public static string Stop(long streamId, string reason)
    {
        return BuildMetadata(new Dictionary<string, object?>
        {
            ["kind"] = KindStop,
            ["streamId"] = streamId,
            ["reason"] = reason
        });
    }

    public static string BuildMetadata(IDictionary<string, object?> values)
    {
        return JsonSerializer.Serialize(values);
    }

    public static Dictionary<string, JsonElement> ParseMetadata(string metadataJson)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return result;
        }

        using var document = JsonDocument.Parse(metadataJson);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    public static AudioOffer ParseOffer(IReadOnlyDictionary<string, JsonElement> metadata)
    {
        RequireKind(metadata, KindOffer);
        var bitrateBps = RequiredInt32(metadata, "bitrateBps");
        return new AudioOffer(
            RequiredInt64(metadata, "streamId"),
            RequiredString(metadata, "offerId"),
            RequiredString(metadata, "transport"),
            RequiredString(metadata, "source"),
            RequiredString(metadata, "codec"),
            RequiredString(metadata, "codecImpl"),
            RequiredInt32(metadata, "sampleRate"),
            RequiredInt32(metadata, "channels"),
            RequiredInt32(metadata, "frameDurationMs"),
            OptionalLatencyMode(metadata),
            OptionalQualityMode(metadata, bitrateBps),
            bitrateBps,
            RequiredInt32(metadata, "rtpPayloadType"),
            RequiredInt32(metadata, "rtpClockRate"),
            RequiredUInt32(metadata, "rtpSsrc"),
            RequiredTransportRole(metadata, "transportRole"));
    }

    public static AudioAccept ParseAccept(IReadOnlyDictionary<string, JsonElement> metadata)
    {
        RequireKind(metadata, KindAccept);
        var bitrateBps = RequiredInt32(metadata, "bitrateBps");
        return new AudioAccept(
            RequiredInt64(metadata, "streamId"),
            RequiredString(metadata, "offerId"),
            RequiredString(metadata, "transport"),
            RequiredString(metadata, "codec"),
            RequiredString(metadata, "codecImpl"),
            RequiredInt32(metadata, "sampleRate"),
            RequiredInt32(metadata, "channels"),
            RequiredInt32(metadata, "frameDurationMs"),
            OptionalLatencyMode(metadata),
            OptionalQualityMode(metadata, bitrateBps),
            bitrateBps,
            RequiredInt32(metadata, "rtpPayloadType"),
            RequiredInt32(metadata, "rtpClockRate"),
            RequiredUInt32(metadata, "rtpSsrc"),
            RequiredTransportRole(metadata, "transportRole"),
            RequiredBoolean(metadata, "receiverProbeRequired"));
    }

    public static bool IsCompatibleOffer(AudioOffer offer)
    {
        return TryValidateOffer(offer, out _);
    }

    public static bool IsCompatibleAccept(AudioAccept accept)
    {
        return TryValidateAccept(accept, out _);
    }

    public static bool TryValidateOffer(AudioOffer offer, out string reason)
    {
        if (offer.Transport != TransportRtpUdp)
        {
            reason = $"transport={offer.Transport}; expected={TransportRtpUdp}";
            return false;
        }

        if (!IsSupportedSource(offer.Source))
        {
            reason = $"source={offer.Source}; supported={SourceMicrophone},{SourceSystemAudio}";
            return false;
        }

        if (offer.Codec != CodecOpus)
        {
            reason = $"codec={offer.Codec}; expected={CodecOpus}";
            return false;
        }

        if (offer.CodecImpl != CodecImplLibOpus)
        {
            reason = $"codecImpl={offer.CodecImpl}; expected={CodecImplLibOpus}";
            return false;
        }

        if (offer.SampleRate != SampleRate)
        {
            reason = $"sampleRate={offer.SampleRate}; expected={SampleRate}";
            return false;
        }

        if (offer.Channels != Channels)
        {
            reason = $"channels={offer.Channels}; expected={Channels}";
            return false;
        }

        if (offer.FrameDurationMs != FrameDurationMs)
        {
            reason = $"frameDurationMs={offer.FrameDurationMs}; expected={FrameDurationMs}";
            return false;
        }

        if (!IsSupportedLatencyMode(offer.LatencyMode))
        {
            reason = $"latencyMode={offer.LatencyMode}; supported={LatencyModeLow},{LatencyModeStable}";
            return false;
        }

        if (!IsSupportedQualityBitratePair(offer.QualityMode, offer.BitrateBps))
        {
            reason = $"qualityMode={offer.QualityMode},bitrateBps={offer.BitrateBps}; expected a supported quality/bitrate pair";
            return false;
        }

        if (offer.RtpPayloadType != RtpPayloadType)
        {
            reason = $"rtpPayloadType={offer.RtpPayloadType}; expected={RtpPayloadType}";
            return false;
        }

        if (offer.RtpClockRate != RtpClockRate)
        {
            reason = $"rtpClockRate={offer.RtpClockRate}; expected={RtpClockRate}";
            return false;
        }

        reason = "";
        return true;
    }

    public static bool TryValidateAccept(AudioAccept accept, out string reason)
    {
        if (accept.Transport != TransportRtpUdp)
        {
            reason = $"transport={accept.Transport}; expected={TransportRtpUdp}";
            return false;
        }

        if (accept.Codec != CodecOpus)
        {
            reason = $"codec={accept.Codec}; expected={CodecOpus}";
            return false;
        }

        if (accept.CodecImpl != CodecImplLibOpus)
        {
            reason = $"codecImpl={accept.CodecImpl}; expected={CodecImplLibOpus}";
            return false;
        }

        if (accept.SampleRate != SampleRate)
        {
            reason = $"sampleRate={accept.SampleRate}; expected={SampleRate}";
            return false;
        }

        if (accept.Channels != Channels)
        {
            reason = $"channels={accept.Channels}; expected={Channels}";
            return false;
        }

        if (accept.FrameDurationMs != FrameDurationMs)
        {
            reason = $"frameDurationMs={accept.FrameDurationMs}; expected={FrameDurationMs}";
            return false;
        }

        if (!IsSupportedLatencyMode(accept.LatencyMode))
        {
            reason = $"latencyMode={accept.LatencyMode}; supported={LatencyModeLow},{LatencyModeStable}";
            return false;
        }

        if (!IsSupportedQualityBitratePair(accept.QualityMode, accept.BitrateBps))
        {
            reason = $"qualityMode={accept.QualityMode},bitrateBps={accept.BitrateBps}; expected a supported quality/bitrate pair";
            return false;
        }

        if (accept.RtpPayloadType != RtpPayloadType)
        {
            reason = $"rtpPayloadType={accept.RtpPayloadType}; expected={RtpPayloadType}";
            return false;
        }

        if (accept.RtpClockRate != RtpClockRate)
        {
            reason = $"rtpClockRate={accept.RtpClockRate}; expected={RtpClockRate}";
            return false;
        }

        reason = "";
        return true;
    }

    public static bool IsSupportedSource(string source)
    {
        return source is SourceMicrophone or SourceSystemAudio;
    }

    public static bool IsRtpProbePayload(ReadOnlySpan<byte> payload)
    {
        return payload.SequenceEqual(RtpProbePayload);
    }

    public static bool IsRtcpProbePayload(ReadOnlySpan<byte> payload)
    {
        return payload.SequenceEqual(RtcpProbePayload);
    }

    public static bool IsSameNegotiation(
        long currentStreamId,
        string currentOfferId,
        long incomingStreamId,
        string incomingOfferId)
    {
        return currentStreamId == incomingStreamId &&
               string.Equals(currentOfferId, incomingOfferId, StringComparison.Ordinal);
    }

    public static string NormalizeLatencyMode(string? value)
    {
        return value == LatencyModeStable ? LatencyModeStable : LatencyModeLow;
    }

    public static bool IsSupportedLatencyMode(string value)
    {
        return value is LatencyModeLow or LatencyModeStable;
    }

    public static bool IsSupportedQualityMode(string value)
    {
        return QualityBitrates.ContainsKey(value);
    }

    public static bool IsSupportedBitrateBps(int value)
    {
        return QualityBitrates.Values.Contains(value);
    }

    public static bool IsSupportedQualityBitratePair(string qualityMode, int bitrateBps)
    {
        return QualityBitrates.TryGetValue(qualityMode, out var expectedBitrate) &&
               expectedBitrate == bitrateBps;
    }

    public static string NormalizeQualityMode(string? value)
    {
        return value != null && IsSupportedQualityMode(value)
            ? value
            : QualityStandard;
    }

    public static int BitrateForQualityMode(string? qualityMode)
    {
        return QualityBitrates.TryGetValue(qualityMode ?? "", out var bitrateBps)
            ? bitrateBps
            : BitrateBps;
    }

    public static bool RequiresQualitySelectionCapability(string qualityMode)
    {
        return NormalizeQualityMode(qualityMode) != QualityStandard;
    }

    public static string OptionalString(IReadOnlyDictionary<string, JsonElement> metadata, string key, string fallback = "")
    {
        return metadata.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    public static long OptionalInt64(IReadOnlyDictionary<string, JsonElement> metadata, string key, long fallback = 0)
    {
        return metadata.TryGetValue(key, out var value) && value.TryGetInt64(out var number)
            ? number
            : fallback;
    }

    public static int OptionalInt32(IReadOnlyDictionary<string, JsonElement> metadata, string key, int fallback = 0)
    {
        return metadata.TryGetValue(key, out var value) && value.TryGetInt32(out var number)
            ? number
            : fallback;
    }

    public static bool OptionalBoolean(IReadOnlyDictionary<string, JsonElement> metadata, string key, bool fallback = false)
    {
        return metadata.TryGetValue(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
    }

    public static long RequiredInt64(IReadOnlyDictionary<string, JsonElement> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || !value.TryGetInt64(out var number))
        {
            throw new FormatException($"Missing {key}");
        }

        return number;
    }

    public static int RequiredInt32(IReadOnlyDictionary<string, JsonElement> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || !value.TryGetInt32(out var number))
        {
            throw new FormatException($"Missing {key}");
        }

        return number;
    }

    public static uint RequiredUInt32(IReadOnlyDictionary<string, JsonElement> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || !value.TryGetUInt32(out var number))
        {
            throw new FormatException($"Missing {key}");
        }

        return number;
    }

    public static bool RequiredBoolean(IReadOnlyDictionary<string, JsonElement> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new FormatException($"Missing {key}");
        }

        return value.GetBoolean();
    }

    public static string RequiredString(IReadOnlyDictionary<string, JsonElement> metadata, string key)
    {
        var value = OptionalString(metadata, key);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new FormatException($"Missing {key}");
    }

    public static SessionTransportRole RequiredTransportRole(
        IReadOnlyDictionary<string, JsonElement> metadata,
        string key)
    {
        var role = RequiredString(metadata, key);
        return role switch
        {
            "listener" => SessionTransportRole.Listener,
            "connector" => SessionTransportRole.Connector,
            _ => throw new FormatException($"Unsupported {key}: {role}")
        };
    }

    private static void RequireKind(IReadOnlyDictionary<string, JsonElement> metadata, string kind)
    {
        var actual = OptionalString(metadata, "kind");
        if (actual != kind)
        {
            throw new FormatException($"Expected {kind}, received {actual}");
        }
    }

    private static string OptionalLatencyMode(IReadOnlyDictionary<string, JsonElement> metadata)
    {
        var latencyMode = OptionalString(metadata, "latencyMode");
        return string.IsNullOrWhiteSpace(latencyMode) ? LatencyModeLow : latencyMode;
    }

    private static string OptionalQualityMode(IReadOnlyDictionary<string, JsonElement> metadata, int bitrateBps)
    {
        var qualityMode = OptionalString(metadata, "qualityMode");
        if (!string.IsNullOrWhiteSpace(qualityMode))
        {
            return qualityMode;
        }

        return bitrateBps == BitrateBps
            ? QualityStandard
            : throw new FormatException("Missing qualityMode");
    }

    private static bool IsAudioCapability(string capability)
    {
        return capability is ProtocolConstants.CapabilityAudioLink
            or ProtocolConstants.CapabilityAudioCodecOpus
            or ProtocolConstants.CapabilityAudioTransportRtp
            or ProtocolConstants.CapabilityAudioRtcp
            or ProtocolConstants.CapabilityAudioCodecLibOpus
            or ProtocolConstants.CapabilityAudioQualitySelect;
    }

    private static bool CheckAudioRuntimeAvailable()
    {
        try
        {
            using var encoder = new LibOpusAudioEncoder();
            using var decoder = new LibOpusAudioDecoder();
            var encoded = encoder.Encode(new short[SamplesPerFrame]);
            _ = encoded.Length > 0 ? decoder.Decode(encoded) : decoder.DecodeMissing();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record AudioOffer(
    long StreamId,
    string OfferId,
    string Transport,
    string Source,
    string Codec,
    string CodecImpl,
    int SampleRate,
    int Channels,
    int FrameDurationMs,
    string LatencyMode,
    string QualityMode,
    int BitrateBps,
    int RtpPayloadType,
    int RtpClockRate,
    uint RtpSsrc,
    SessionTransportRole TransportRole);

public sealed record AudioAccept(
    long StreamId,
    string OfferId,
    string Transport,
    string Codec,
    string CodecImpl,
    int SampleRate,
    int Channels,
    int FrameDurationMs,
    string LatencyMode,
    string QualityMode,
    int BitrateBps,
    int RtpPayloadType,
    int RtpClockRate,
    uint RtpSsrc,
    SessionTransportRole TransportRole,
    bool ReceiverProbeRequired);
