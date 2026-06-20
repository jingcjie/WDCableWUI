using System;
using System.Collections.Generic;
using System.Text.Json;
using WDCableWUI.Protocol;

namespace WDCableWUI.Services;

internal static class SessionRendezvousPayload
{
    public static string Build(
        string rendezvousId,
        SessionRole wifiRole,
        SessionTransportRole transportRole,
        long? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rendezvousId);

        var metadata = new Dictionary<string, object?>
        {
            ["appId"] = ProtocolConstants.AppId,
            ["rendezvousVersion"] = 2,
            ["rendezvousId"] = rendezvousId,
            ["protocolMin"] = ProtocolConstants.Version,
            ["protocolMax"] = ProtocolConstants.Version,
            ["wifiRole"] = wifiRole.GetEventName(),
            ["transportRole"] = transportRole.GetEventName(),
            ["controlPort"] = ProtocolConstants.DefaultControlPort,
            ["bulkPort"] = ProtocolConstants.DefaultBulkPort,
            ["timestamp"] = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        return JsonSerializer.Serialize(metadata);
    }
}
