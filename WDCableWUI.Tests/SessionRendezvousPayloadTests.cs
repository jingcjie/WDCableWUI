using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Protocol;
using WDCableWUI.Services;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class SessionRendezvousPayloadTests
{
    [TestMethod]
    public void ProtocolV2PortsAreAdvertised()
    {
        Assert.AreEqual(2, ProtocolConstants.Version);
        Assert.AreEqual(8987, ProtocolConstants.DefaultRendezvousPort);
        Assert.AreEqual(8988, ProtocolConstants.DefaultControlPort);
        Assert.AreEqual(8989, ProtocolConstants.DefaultBulkPort);
    }

    [TestMethod]
    public void PayloadContainsProtocolAndRoleDataWithoutIpAddress()
    {
        var payload = SessionRendezvousPayload.Build(
            "rv-1",
            SessionRole.Client,
            SessionTransportRole.Listener,
            timestamp: 1781890000000);

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        Assert.AreEqual(ProtocolConstants.AppId, root.GetProperty("appId").GetString());
        Assert.AreEqual(2, root.GetProperty("rendezvousVersion").GetInt32());
        Assert.AreEqual("rv-1", root.GetProperty("rendezvousId").GetString());
        Assert.AreEqual(2, root.GetProperty("protocolMin").GetInt32());
        Assert.AreEqual(2, root.GetProperty("protocolMax").GetInt32());
        Assert.AreEqual("client", root.GetProperty("wifiRole").GetString());
        Assert.AreEqual("listener", root.GetProperty("transportRole").GetString());
        Assert.AreEqual(8988, root.GetProperty("controlPort").GetInt32());
        Assert.AreEqual(8989, root.GetProperty("bulkPort").GetInt32());
        Assert.AreEqual(1781890000000, root.GetProperty("timestamp").GetInt64());
        Assert.IsFalse(root.TryGetProperty("localIp", out _));
        Assert.IsFalse(root.TryGetProperty("remoteIp", out _));
        Assert.IsFalse(root.TryGetProperty("selfIp", out _));
        Assert.IsFalse(root.TryGetProperty("ipAddress", out _));
    }

    [TestMethod]
    public void PayloadUsesSuppliedFreshRendezvousId()
    {
        var firstId = Guid.NewGuid().ToString();
        var secondId = Guid.NewGuid().ToString();

        using var first = JsonDocument.Parse(SessionRendezvousPayload.Build(
            firstId,
            SessionRole.Client,
            SessionTransportRole.Listener));
        using var second = JsonDocument.Parse(SessionRendezvousPayload.Build(
            secondId,
            SessionRole.Client,
            SessionTransportRole.Listener));

        Assert.AreNotEqual(
            first.RootElement.GetProperty("rendezvousId").GetString(),
            second.RootElement.GetProperty("rendezvousId").GetString());
    }
}
