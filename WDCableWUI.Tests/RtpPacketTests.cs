using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Services;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class RtpPacketTests
{
    [TestMethod]
    public void RtpPacketRoundTripsHeaderAndPayload()
    {
        var packet = new RtpPacket(
            AudioProtocol.RtpPayloadType,
            65534,
            123456,
            0x10203040,
            [1, 2, 3],
            Marker: true);

        Assert.IsTrue(RtpPacket.TryDecode(packet.Encode(), out var decoded));
        Assert.AreEqual(AudioProtocol.RtpPayloadType, decoded.PayloadType);
        Assert.AreEqual(65534, decoded.SequenceNumber);
        Assert.AreEqual(123456u, decoded.Timestamp);
        Assert.AreEqual(0x10203040u, decoded.Ssrc);
        Assert.IsTrue(decoded.Marker);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, decoded.Payload);
    }

    [TestMethod]
    public void RtpSequenceWrapsAtUInt16Boundary()
    {
        Assert.AreEqual(0, RtpPacket.NextSequence(ushort.MaxValue));
    }

    [TestMethod]
    public void RtpTimestampIncrementIsTwentyMillisecondsAt48Khz()
    {
        Assert.AreEqual(960u, AudioProtocol.RtpTimestampIncrement);
    }
}
