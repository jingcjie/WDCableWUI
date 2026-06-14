using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WDCableWUI.Protocol;

namespace WDCableWUI.Services;

public static class AudioProtocol
{
    public const string KindReceiveReady = "audio.receive.ready";
    public const string KindReceiveStopped = "audio.receive.stopped";
    public const string KindOffer = "audio.offer";
    public const string KindAccept = "audio.accept";
    public const string KindTransport = "audio.transport";
    public const string KindStop = "audio.stop";

    public const string ErrorUnsupported = "audio_unsupported";
    public const string ErrorReceiverNotReady = "audio_receiver_not_ready";
    public const string ErrorBusy = "audio_busy";
    public const string ErrorPermissionDenied = "audio_permission_denied";
    public const string ErrorCodecUnavailable = "audio_codec_unavailable";
    public const string ErrorTransportFailed = "audio_transport_failed";
    public const string ErrorCaptureFailed = "audio_capture_failed";
    public const string ErrorPlaybackFailed = "audio_playback_failed";

    public const string SourceMicrophone = "microphone";
    public const string SourceSystemAudio = "systemAudio";
    public const string CodecOpus = "opus";
    public const string TransportTcp = "tcp";

    public const int SampleRate = 48_000;
    public const int Channels = 1;
    public const int FrameDurationMs = 20;
    public const int BitrateBps = 24_000;
    public const int SamplesPerFrame = SampleRate * FrameDurationMs / 1000;

    public static bool PeerSupportsAudio(IReadOnlyList<string> capabilities)
    {
        return capabilities.Contains(ProtocolConstants.CapabilityAudioLink) &&
               capabilities.Contains(ProtocolConstants.CapabilityAudioCodecOpus);
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

    public static string Offer(long streamId, string offerId)
    {
        return BuildMetadata(new Dictionary<string, object?>
        {
            ["kind"] = KindOffer,
            ["streamId"] = streamId,
            ["offerId"] = offerId,
            // Compatibility with the current Android receiver: Windows captures
            // system audio, but the current Android build accepts microphone only.
            ["source"] = SourceMicrophone,
            ["codec"] = CodecOpus,
            ["sampleRate"] = SampleRate,
            ["channels"] = Channels,
            ["frameDurationMs"] = FrameDurationMs,
            ["bitrateBps"] = BitrateBps
        });
    }

    public static string Accept(long streamId, string offerId)
    {
        return BuildMetadata(new Dictionary<string, object?>
        {
            ["kind"] = KindAccept,
            ["streamId"] = streamId,
            ["offerId"] = offerId,
            ["codec"] = CodecOpus,
            ["sampleRate"] = SampleRate,
            ["channels"] = Channels,
            ["frameDurationMs"] = FrameDurationMs
        });
    }

    public static string Transport(long streamId, int port)
    {
        return BuildMetadata(new Dictionary<string, object?>
        {
            ["kind"] = KindTransport,
            ["streamId"] = streamId,
            ["transport"] = TransportTcp,
            ["port"] = port
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

    public static string FrameMetadata(long sentAtMs, bool codecConfig = false)
    {
        return BuildMetadata(new Dictionary<string, object?>
        {
            ["codec"] = CodecOpus,
            ["sentAtMs"] = sentAtMs,
            ["durationMs"] = codecConfig ? 0 : FrameDurationMs,
            ["codecConfig"] = codecConfig
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
        return new AudioOffer(
            RequiredInt64(metadata, "streamId"),
            RequiredString(metadata, "offerId"),
            RequiredString(metadata, "source"),
            RequiredString(metadata, "codec"),
            OptionalInt32(metadata, "sampleRate"),
            OptionalInt32(metadata, "channels"),
            OptionalInt32(metadata, "frameDurationMs"),
            OptionalInt32(metadata, "bitrateBps"));
    }

    public static AudioAccept ParseAccept(IReadOnlyDictionary<string, JsonElement> metadata)
    {
        RequireKind(metadata, KindAccept);
        return new AudioAccept(
            RequiredInt64(metadata, "streamId"),
            RequiredString(metadata, "offerId"),
            RequiredString(metadata, "codec"),
            OptionalInt32(metadata, "sampleRate"),
            OptionalInt32(metadata, "channels"),
            OptionalInt32(metadata, "frameDurationMs"));
    }

    public static AudioTransportOffer ParseTransport(IReadOnlyDictionary<string, JsonElement> metadata)
    {
        RequireKind(metadata, KindTransport);
        return new AudioTransportOffer(
            RequiredInt64(metadata, "streamId"),
            RequiredString(metadata, "transport"),
            OptionalInt32(metadata, "port", -1));
    }

    public static EncodedAudioFrame ParseAudioFrame(ProtocolFrame frame)
    {
        var metadata = ParseMetadata(frame.MetadataJson);
        var codec = OptionalString(metadata, "codec");
        if (codec != CodecOpus)
        {
            throw new FormatException($"Unsupported audio codec: {codec}");
        }

        var codecConfig = OptionalBoolean(metadata, "codecConfig");
        return new EncodedAudioFrame(
            frame.SequenceNumber,
            OptionalInt64(metadata, "sentAtMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            OptionalInt32(metadata, "durationMs", codecConfig ? 0 : FrameDurationMs),
            frame.Payload,
            codecConfig);
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

    public static string RequiredString(IReadOnlyDictionary<string, JsonElement> metadata, string key)
    {
        var value = OptionalString(metadata, key);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new FormatException($"Missing {key}");
    }

    private static void RequireKind(IReadOnlyDictionary<string, JsonElement> metadata, string kind)
    {
        var actual = OptionalString(metadata, "kind");
        if (actual != kind)
        {
            throw new FormatException($"Expected {kind}, received {actual}");
        }
    }
}

public sealed record AudioOffer(
    long StreamId,
    string OfferId,
    string Source,
    string Codec,
    int SampleRate,
    int Channels,
    int FrameDurationMs,
    int BitrateBps);

public sealed record AudioAccept(
    long StreamId,
    string OfferId,
    string Codec,
    int SampleRate,
    int Channels,
    int FrameDurationMs);

public sealed record AudioTransportOffer(
    long StreamId,
    string Transport,
    int Port);
