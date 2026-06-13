using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Protocol;
using WDCableWUI.Services;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class ChatProtocolTests
{
    [TestMethod]
    public void CreateFrameUsesAndroidCompatibleChatShape()
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(123456);
        var messageId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var frame = ChatProtocol.CreateFrame("s1", "hello", messageId, timestamp);

        Assert.AreEqual(ProtocolFrameType.ControlMessage, frame.Type);
        Assert.AreEqual(ProtocolChannel.Control, frame.Channel);
        Assert.AreEqual(messageId, frame.CorrelationId);
        Assert.AreEqual("hello", Encoding.UTF8.GetString(frame.Payload));

        using var document = JsonDocument.Parse(frame.MetadataJson);
        var root = document.RootElement;
        Assert.AreEqual("chat", root.GetProperty("kind").GetString());
        Assert.AreEqual(messageId.ToString(), root.GetProperty("messageId").GetString());
        Assert.AreEqual(123456, root.GetProperty("timestamp").GetInt64());
        Assert.AreEqual("windows", root.GetProperty("senderPlatform").GetString());
        Assert.AreEqual("s1", root.GetProperty("sessionId").GetString());
    }

    [TestMethod]
    public void TryParseFrameReadsChatPayloadAndMetadata()
    {
        var frame = ChatProtocol.CreateFrame("s2", "hi there", Guid.Parse("11111111-2222-3333-4444-555555555555"));

        var parsed = ChatProtocol.TryParseFrame(frame, out var message);

        Assert.IsTrue(parsed);
        Assert.IsNotNull(message);
        Assert.AreEqual("hi there", message.Message);
        Assert.AreEqual("s2", message.SessionId);
        Assert.AreEqual("windows", message.SenderPlatform);
    }

    [TestMethod]
    public void TryParseFrameAcceptsAndroidSessionMetadata()
    {
        var frame = new ProtocolFrame(
            ProtocolFrameType.ControlMessage,
            ProtocolChannel.Control,
            correlationId: Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            metadataJson: """
                {"kind":"chat","messageId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","timestamp":123456,"senderPlatform":"android","sessionId":"android-local-session"}
                """,
            payload: Encoding.UTF8.GetBytes("from android"));

        var parsed = ChatProtocol.TryParseFrame(frame, out var message);

        Assert.IsTrue(parsed);
        Assert.IsNotNull(message);
        Assert.AreEqual("from android", message.Message);
        Assert.AreEqual("android", message.SenderPlatform);
        Assert.AreEqual("android-local-session", message.SessionId);
    }

    [TestMethod]
    public void TryParseFrameIgnoresNonChatControlMessages()
    {
        var frame = new ProtocolFrame(
            ProtocolFrameType.ControlMessage,
            ProtocolChannel.Control,
            metadataJson: """{"kind":"not-chat"}""");

        Assert.IsFalse(ChatProtocol.TryParseFrame(frame, out var message));
        Assert.IsNull(message);
    }
}
