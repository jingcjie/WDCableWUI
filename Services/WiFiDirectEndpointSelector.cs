using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WDCableWUI.Services;

internal sealed record WiFiDirectEndpointPair(string? LocalIP, string? RemoteIP);

internal sealed record WiFiDirectEndpointSelection(
    string LocalIP,
    string RemoteIP,
    SessionRole Role,
    SessionTransportRole TransportRole);

internal static class WiFiDirectEndpointSelector
{
    public static WiFiDirectEndpointSelection? Select(
        IEnumerable<WiFiDirectEndpointPair> endpointPairs,
        IReadOnlySet<string> localIpv4Addresses)
    {
        ArgumentNullException.ThrowIfNull(endpointPairs);
        ArgumentNullException.ThrowIfNull(localIpv4Addresses);

        foreach (var pair in endpointPairs)
        {
            if (!TryParseIpv4(pair.LocalIP, out var localAddress) ||
                !TryParseIpv4(pair.RemoteIP, out var remoteAddress))
            {
                continue;
            }

            if (!localIpv4Addresses.Contains(localAddress.ToString()))
            {
                continue;
            }

            var comparison = CompareIpv4(localAddress, remoteAddress);
            if (comparison == 0)
            {
                continue;
            }

            var role = comparison < 0 ? SessionRole.GroupOwner : SessionRole.Client;
            return new WiFiDirectEndpointSelection(
                localAddress.ToString(),
                remoteAddress.ToString(),
                role,
                role.GetTransportRole());
        }

        return null;
    }

    public static IReadOnlySet<string> GetLocalIpv4Addresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(GetUnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(address => address.Address.ToString())
            .ToHashSet(StringComparer.Ordinal);
    }

    internal static int CompareIpv4(IPAddress left, IPAddress right)
    {
        var leftValue = ToIpv4Number(left);
        var rightValue = ToIpv4Number(right);
        return leftValue.CompareTo(rightValue);
    }

    private static bool TryParseIpv4(string? value, out IPAddress address)
    {
        if (IPAddress.TryParse(value, out var parsed) &&
            parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            address = parsed;
            return true;
        }

        address = IPAddress.None;
        return false;
    }

    private static uint ToIpv4Number(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }

    private static IEnumerable<UnicastIPAddressInformation> GetUnicastAddresses(
        NetworkInterface networkInterface)
    {
        try
        {
            return networkInterface.GetIPProperties().UnicastAddresses;
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}
