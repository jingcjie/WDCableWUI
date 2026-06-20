using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Protocol;
using WDCableWUI.Services;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class SessionHandshakeMetadataTests
{
    [TestMethod]
    public void BaseMetadataIncludesWifiAndTransportRoles()
    {
        var metadata = SessionHandshakeMetadata.BuildBase(
            "windows",
            "1.0.0",
            "pc",
            SessionRole.Client,
            SessionTransportRole.Listener,
            "session-1",
            Channels());

        Assert.AreEqual("client", metadata["role"]);
        Assert.AreEqual("listener", metadata["transportRole"]);
    }

    [TestMethod]
    public void ValidHelloReturnsCapabilities()
    {
        var metadata = SessionHandshakeMetadata.BuildBase(
            "windows",
            "1.0.0",
            "pc",
            SessionRole.Client,
            SessionTransportRole.Listener,
            "session-1",
            Channels());
        metadata["protocolMin"] = ProtocolConstants.Version;
        metadata["protocolMax"] = ProtocolConstants.Version;

        var capabilities = SessionHandshakeMetadata.ValidateHello(
            JsonSerializer.Serialize(metadata),
            SessionRole.Client,
            SessionTransportRole.Listener);

        CollectionAssert.Contains(capabilities.ToList(), ProtocolConstants.CapabilityChat);
    }

    [TestMethod]
    public void ValidAckReturnsCapabilities()
    {
        var metadata = SessionHandshakeMetadata.BuildBase(
            "windows",
            "1.0.0",
            "pc",
            SessionRole.GroupOwner,
            SessionTransportRole.Connector,
            "session-1",
            Channels());
        metadata["protocolVersion"] = ProtocolConstants.Version;

        var capabilities = SessionHandshakeMetadata.ValidateAck(
            JsonSerializer.Serialize(metadata),
            SessionRole.GroupOwner,
            SessionTransportRole.Connector);

        CollectionAssert.Contains(capabilities.ToList(), ProtocolConstants.CapabilityBulkFile);
    }

    [TestMethod]
    public void MissingTransportRoleIsProtocolMismatch()
    {
        var metadata = SessionHandshakeMetadata.BuildBase(
            "windows",
            "1.0.0",
            "pc",
            SessionRole.Client,
            SessionTransportRole.Listener,
            "session-1",
            Channels());
        metadata["protocolMin"] = ProtocolConstants.Version;
        metadata["protocolMax"] = ProtocolConstants.Version;
        metadata.Remove("transportRole");

        AssertProtocolMismatch(() => SessionHandshakeMetadata.ValidateHello(
            JsonSerializer.Serialize(metadata),
            SessionRole.Client,
            SessionTransportRole.Listener));
    }

    [TestMethod]
    public void MismatchedRolesAreProtocolMismatch()
    {
        var metadata = SessionHandshakeMetadata.BuildBase(
            "windows",
            "1.0.0",
            "pc",
            SessionRole.GroupOwner,
            SessionTransportRole.Connector,
            "session-1",
            Channels());
        metadata["protocolMin"] = ProtocolConstants.Version;
        metadata["protocolMax"] = ProtocolConstants.Version;

        AssertProtocolMismatch(() => SessionHandshakeMetadata.ValidateHello(
            JsonSerializer.Serialize(metadata),
            SessionRole.Client,
            SessionTransportRole.Listener));
    }

    [TestMethod]
    public void ProtocolV1HelloIsProtocolMismatch()
    {
        var metadata = SessionHandshakeMetadata.BuildBase(
            "windows",
            "1.0.0",
            "pc",
            SessionRole.Client,
            SessionTransportRole.Listener,
            "session-1",
            Channels());
        metadata["protocolMin"] = 1;
        metadata["protocolMax"] = 1;

        AssertProtocolMismatch(() => SessionHandshakeMetadata.ValidateHello(
            JsonSerializer.Serialize(metadata),
            SessionRole.Client,
            SessionTransportRole.Listener));
    }

    private static Dictionary<string, object> Channels()
    {
        return new Dictionary<string, object>
        {
            ["control"] = new Dictionary<string, object>
            {
                ["transport"] = "tcp",
                ["port"] = ProtocolConstants.DefaultControlPort
            },
            ["bulk"] = new Dictionary<string, object>
            {
                ["transport"] = "tcp",
                ["port"] = ProtocolConstants.DefaultBulkPort
            }
        };
    }

    private static void AssertProtocolMismatch(Action action)
    {
        try
        {
            action();
            Assert.Fail("Expected protocol mismatch.");
        }
        catch (ProtocolException exception)
        {
            Assert.AreEqual(ProtocolError.ProtocolMismatch, exception.Error);
        }
    }
}
