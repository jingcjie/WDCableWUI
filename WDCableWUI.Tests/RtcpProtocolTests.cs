using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Services;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class RtcpProtocolTests
{
    [TestMethod]
    public void SenderReportRoundTrips()
    {
        var report = new RtcpSenderReport(
            0x01020304,
            0x1122334455667788,
            960,
            10,
            200);

        Assert.IsTrue(RtcpProtocol.TryDecode(RtcpProtocol.EncodeSenderReport(report), out var decoded));
        var senderReport = decoded as RtcpSenderReport;
        Assert.IsNotNull(senderReport);
        Assert.AreEqual(report.Ssrc, senderReport.Ssrc);
        Assert.AreEqual(report.NtpTimestamp, senderReport.NtpTimestamp);
        Assert.AreEqual(report.RtpTimestamp, senderReport.RtpTimestamp);
        Assert.AreEqual(report.PacketCount, senderReport.PacketCount);
        Assert.AreEqual(report.OctetCount, senderReport.OctetCount);
    }

    [TestMethod]
    public void ReceiverReportRoundTrips()
    {
        var report = new RtcpReceiverReport(
            0x01020304,
            0x05060708,
            23,
            -2,
            65540,
            321,
            123,
            456);

        Assert.IsTrue(RtcpProtocol.TryDecode(RtcpProtocol.EncodeReceiverReport(report), out var decoded));
        var receiverReport = decoded as RtcpReceiverReport;
        Assert.IsNotNull(receiverReport);
        Assert.AreEqual(report.Ssrc, receiverReport.Ssrc);
        Assert.AreEqual(report.ReportedSsrc, receiverReport.ReportedSsrc);
        Assert.AreEqual(report.FractionLost, receiverReport.FractionLost);
        Assert.AreEqual(report.CumulativePacketsLost, receiverReport.CumulativePacketsLost);
        Assert.AreEqual(report.HighestSequenceReceived, receiverReport.HighestSequenceReceived);
        Assert.AreEqual(report.InterarrivalJitter, receiverReport.InterarrivalJitter);
        Assert.AreEqual(report.LastSenderReport, receiverReport.LastSenderReport);
        Assert.AreEqual(report.DelaySinceLastSenderReport, receiverReport.DelaySinceLastSenderReport);
    }
}
