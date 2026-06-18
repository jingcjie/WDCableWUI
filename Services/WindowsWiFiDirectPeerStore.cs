using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;

namespace WDCableWUI.Services;

internal sealed class WindowsWiFiDirectPeerStore : IWiFiDirectPeerStore
{
    private static readonly string[] PairingProperties =
    [
        "System.Devices.Aep.IsPaired"
    ];

    private readonly Dictionary<string, DeviceInformation> _devices =
        new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<WiFiDirectPeerReference>> GetPairedPeersAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var selector = Windows.Devices.WiFiDirect.WiFiDirectDevice.GetDeviceSelector(
            WiFiDirectDeviceSelectorType.AssociationEndpoint);
        var devices = await DeviceInformation.FindAllAsync(
            selector,
            PairingProperties,
            DeviceInformationKind.AssociationEndpoint);

        cancellationToken.ThrowIfCancellationRequested();
        _devices.Clear();

        foreach (var device in devices.Where(device => device.Pairing?.IsPaired == true))
        {
            _devices[device.Id] = device;
        }

        return _devices.Values
            .Select(device => new WiFiDirectPeerReference(device.Id, device.Name))
            .ToArray();
    }

    public async Task<WiFiDirectPeerUnpairStatus> UnpairAsync(
        WiFiDirectPeerReference peer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_devices.TryGetValue(peer.Id, out var device) || device.Pairing == null)
        {
            return WiFiDirectPeerUnpairStatus.AlreadyUnpaired;
        }

        var result = await device.Pairing.UnpairAsync();
        cancellationToken.ThrowIfCancellationRequested();

        return result.Status switch
        {
            DeviceUnpairingResultStatus.Unpaired => WiFiDirectPeerUnpairStatus.Unpaired,
            DeviceUnpairingResultStatus.AlreadyUnpaired => WiFiDirectPeerUnpairStatus.AlreadyUnpaired,
            _ => WiFiDirectPeerUnpairStatus.Failed
        };
    }
}
