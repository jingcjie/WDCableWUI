using System;
using System.Collections.Generic;
using System.Linq;

namespace WDCableWUI.Services;

public sealed class WiFiDirectCapabilityReport
{
    public const string WiFiDirectDevice = "Wi-Fi Direct Device";
    public const string WiFiDirectGroupOwner = "Wi-Fi Direct GO";
    public const string WiFiDirectClient = "Wi-Fi Direct Client";
    public const string P2PDeviceDiscovery = "P2P Device Discovery";

    public static readonly string[] RequiredCapabilities =
    [
        WiFiDirectDevice,
        WiFiDirectGroupOwner,
        WiFiDirectClient,
        P2PDeviceDiscovery
    ];

    private readonly Dictionary<string, string> _capabilities;

    private WiFiDirectCapabilityReport(string rawOutput, string? interfaceName, Dictionary<string, string> capabilities)
    {
        RawOutput = rawOutput;
        InterfaceName = interfaceName;
        _capabilities = capabilities;
    }

    public string RawOutput { get; }

    public string? InterfaceName { get; }

    public IReadOnlyDictionary<string, string> Capabilities => _capabilities;

    public static WiFiDirectCapabilityReport Parse(string output)
    {
        var capabilities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? interfaceName = null;

        foreach (var rawLine in output.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            const string interfacePrefix = "Interface name:";
            if (line.StartsWith(interfacePrefix, StringComparison.OrdinalIgnoreCase))
            {
                interfaceName = line[interfacePrefix.Length..].Trim();
                continue;
            }

            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex == line.Length - 1)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (key.Length > 0 && value.Length > 0)
            {
                capabilities[key] = value;
            }
        }

        return new WiFiDirectCapabilityReport(output, interfaceName, capabilities);
    }

    public string? GetCapabilityValue(string capabilityName)
    {
        return _capabilities.TryGetValue(capabilityName, out var value) ? value : null;
    }

    public bool? IsCapabilitySupported(string capabilityName)
    {
        var value = GetCapabilityValue(capabilityName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.StartsWith("Not Supported", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("No", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (value.StartsWith("Supported", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("Yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return null;
    }

    public IReadOnlyList<string> GetMissingRequiredCapabilities()
    {
        return RequiredCapabilities
            .Where(capability => IsCapabilitySupported(capability) == false)
            .ToArray();
    }

    public string BuildRequiredCapabilitySummary()
    {
        return string.Join("; ", RequiredCapabilities.Select(capability =>
        {
            var value = GetCapabilityValue(capability) ?? "Not reported";
            return $"{capability}: {value}";
        }));
    }
}
