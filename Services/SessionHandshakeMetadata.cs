using System.Collections.Generic;
using System.Text.Json;
using WDCableWUI.Protocol;

namespace WDCableWUI.Services;

internal static class SessionHandshakeMetadata
{
    public static Dictionary<string, object?> BuildBase(
        string platform,
        string appVersion,
        string deviceName,
        SessionRole role,
        SessionTransportRole transportRole,
        string sessionId,
        IReadOnlyDictionary<string, object> channels)
    {
        return new Dictionary<string, object?>
        {
            ["appId"] = ProtocolConstants.AppId,
            ["platform"] = platform,
            ["appVersion"] = appVersion,
            ["deviceName"] = deviceName,
            ["role"] = role.GetEventName(),
            ["transportRole"] = transportRole.GetEventName(),
            ["sessionId"] = sessionId,
            ["capabilities"] = ProtocolConstants.AdvertisedCapabilities,
            ["channels"] = channels
        };
    }

    public static IReadOnlyList<string> ValidateHello(
        string metadataJson,
        SessionRole expectedRole,
        SessionTransportRole expectedTransportRole)
    {
        using var document = JsonDocument.Parse(metadataJson);
        var root = document.RootElement;
        ValidateAppId(root);
        ValidateRoleMetadata(root, expectedRole, expectedTransportRole);

        var protocolMin = GetInt(root, "protocolMin", -1);
        var protocolMax = GetInt(root, "protocolMax", -1);
        if (ProtocolConstants.Version < protocolMin || ProtocolConstants.Version > protocolMax)
        {
            throw new ProtocolException(
                ProtocolError.ProtocolMismatch,
                $"Peer supports protocol {protocolMin}..{protocolMax}");
        }

        return ReadCapabilities(root);
    }

    public static IReadOnlyList<string> ValidateAck(
        string metadataJson,
        SessionRole expectedRole,
        SessionTransportRole expectedTransportRole)
    {
        using var document = JsonDocument.Parse(metadataJson);
        var root = document.RootElement;
        ValidateAppId(root);
        ValidateRoleMetadata(root, expectedRole, expectedTransportRole);

        var protocolVersion = GetInt(root, "protocolVersion", -1);
        if (protocolVersion != ProtocolConstants.Version)
        {
            throw new ProtocolException(
                ProtocolError.ProtocolMismatch,
                $"Peer selected unsupported protocol {protocolVersion}");
        }

        return ReadCapabilities(root);
    }

    private static void ValidateAppId(JsonElement root)
    {
        if (!root.TryGetProperty("appId", out var appId) ||
            appId.ValueKind != JsonValueKind.String ||
            appId.GetString() != ProtocolConstants.AppId)
        {
            throw new ProtocolException(ProtocolError.ProtocolMismatch, "Peer app id is not WDCable");
        }
    }

    private static void ValidateRoleMetadata(
        JsonElement root,
        SessionRole expectedRole,
        SessionTransportRole expectedTransportRole)
    {
        var role = GetRequiredString(root, "role");
        if (role != expectedRole.GetEventName())
        {
            throw new ProtocolException(
                ProtocolError.ProtocolMismatch,
                $"Peer WiFi Direct role was {role}, expected {expectedRole.GetEventName()}");
        }

        var transportRole = GetRequiredString(root, "transportRole");
        if (transportRole != expectedTransportRole.GetEventName())
        {
            throw new ProtocolException(
                ProtocolError.ProtocolMismatch,
                $"Peer transport role was {transportRole}, expected {expectedTransportRole.GetEventName()}");
        }
    }

    private static string GetRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            throw new ProtocolException(
                ProtocolError.ProtocolMismatch,
                $"Missing required handshake property: {propertyName}");
        }

        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ProtocolException(
                ProtocolError.ProtocolMismatch,
                $"Empty required handshake property: {propertyName}");
        }

        return value;
    }

    private static int GetInt(JsonElement root, string propertyName, int fallback)
    {
        return root.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : fallback;
    }

    private static IReadOnlyList<string> ReadCapabilities(JsonElement root)
    {
        if (!root.TryGetProperty("capabilities", out var capabilities) ||
            capabilities.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var capability in capabilities.EnumerateArray())
        {
            var value = capability.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value);
            }
        }

        return result;
    }
}
