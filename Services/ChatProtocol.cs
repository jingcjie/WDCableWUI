using System;
using System.Text;
using System.Text.Json;
using WDCableWUI.Protocol;

namespace WDCableWUI.Services;

public sealed class ChatControlMessage
{
    public ChatControlMessage(
        string message,
        string messageId,
        DateTimeOffset timestamp,
        string senderPlatform,
        string sessionId)
    {
        Message = message;
        MessageId = messageId;
        Timestamp = timestamp;
        SenderPlatform = senderPlatform;
        SessionId = sessionId;
    }

    public string Message { get; }

    public string MessageId { get; }

    public DateTimeOffset Timestamp { get; }

    public string SenderPlatform { get; }

    public string SessionId { get; }
}

public static class ChatProtocol
{
    public const string Kind = "chat";
    public const string SenderPlatform = "windows";

    public static ProtocolFrame CreateFrame(
        string sessionId,
        string message,
        Guid? messageId = null,
        DateTimeOffset? timestamp = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        }

        var id = messageId ?? Guid.NewGuid();
        var sentAt = timestamp ?? DateTimeOffset.UtcNow;
        var metadata = new
        {
            kind = Kind,
            messageId = id.ToString(),
            timestamp = sentAt.ToUnixTimeMilliseconds(),
            senderPlatform = SenderPlatform,
            sessionId
        };

        return new ProtocolFrame(
            ProtocolFrameType.ControlMessage,
            ProtocolChannel.Control,
            correlationId: id,
            metadataJson: JsonSerializer.Serialize(metadata),
            payload: Encoding.UTF8.GetBytes(message));
    }

    public static bool TryParseFrame(ProtocolFrame frame, out ChatControlMessage? message)
    {
        message = null;

        if (frame.Type != ProtocolFrameType.ControlMessage || frame.Channel != ProtocolChannel.Control)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(frame.MetadataJson) ? "{}" : frame.MetadataJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != Kind)
            {
                return false;
            }

            var messageId = GetString(root, "messageId", frame.CorrelationId.ToString());
            var timestampMs = GetInt64(root, "timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var senderPlatform = GetString(root, "senderPlatform", "unknown");
            var sessionId = GetString(root, "sessionId", "");

            message = new ChatControlMessage(
                Encoding.UTF8.GetString(frame.Payload),
                messageId,
                DateTimeOffset.FromUnixTimeMilliseconds(timestampMs),
                senderPlatform,
                sessionId);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string GetString(JsonElement root, string propertyName, string fallback)
    {
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
    }

    private static long GetInt64(JsonElement root, string propertyName, long fallback)
    {
        return root.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : fallback;
    }
}
