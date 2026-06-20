using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Services;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class WiFiDirectEndpointSelectorTests
{
    [TestMethod]
    public void LowerNumericIpv4IsGroupOwnerConnector()
    {
        var selection = WiFiDirectEndpointSelector.Select(
            [new WiFiDirectEndpointPair("10.0.0.2", "10.0.1.1")],
            LocalAddresses("10.0.0.2"));

        Assert.IsNotNull(selection);
        Assert.AreEqual(SessionRole.GroupOwner, selection.Role);
        Assert.AreEqual(SessionTransportRole.Connector, selection.TransportRole);
    }

    [TestMethod]
    public void HigherNumericIpv4IsClientListener()
    {
        var selection = WiFiDirectEndpointSelector.Select(
            [new WiFiDirectEndpointPair("192.168.137.48", "192.168.137.1")],
            LocalAddresses("192.168.137.48"));

        Assert.IsNotNull(selection);
        Assert.AreEqual(SessionRole.Client, selection.Role);
        Assert.AreEqual(SessionTransportRole.Listener, selection.TransportRole);
    }

    [TestMethod]
    public void RejectsEndpointWhenLocalAddressIsNotOnMachine()
    {
        var selection = WiFiDirectEndpointSelector.Select(
            [new WiFiDirectEndpointPair("192.168.137.48", "192.168.137.1")],
            LocalAddresses("10.0.0.8"));

        Assert.IsNull(selection);
    }

    [TestMethod]
    public void RejectsEqualIpv4Endpoints()
    {
        var selection = WiFiDirectEndpointSelector.Select(
            [new WiFiDirectEndpointPair("192.168.137.1", "192.168.137.1")],
            LocalAddresses("192.168.137.1"));

        Assert.IsNull(selection);
    }

    [TestMethod]
    public void SelectsFirstValidLocalIpv4Pair()
    {
        var selection = WiFiDirectEndpointSelector.Select(
            [
                new WiFiDirectEndpointPair("fe80::1", "fe80::2"),
                new WiFiDirectEndpointPair("192.168.137.20", "192.168.137.1")
            ],
            LocalAddresses("192.168.137.20"));

        Assert.IsNotNull(selection);
        Assert.AreEqual("192.168.137.20", selection.LocalIP);
        Assert.AreEqual("192.168.137.1", selection.RemoteIP);
        Assert.AreEqual(SessionRole.Client, selection.Role);
    }

    private static IReadOnlySet<string> LocalAddresses(params string[] addresses)
    {
        return addresses.ToHashSet(StringComparer.Ordinal);
    }
}
