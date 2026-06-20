using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Services;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class JitterBufferTests
{
    private long _nowMs;

    [TestMethod]
    public void InitialPacketWaitsUntilSenderSelectedPlayoutDeadline()
    {
        var buffer = CreateBuffer(initialDelayMs: 50);
        buffer.Add(Frame(1));

        var wait = buffer.PollForPlayback() as JitterBufferPollResult.Wait;
        Assert.IsNotNull(wait);
        Assert.AreEqual(50, wait.WaitMs);

        _nowMs += 49;
        Assert.IsInstanceOfType<JitterBufferPollResult.Wait>(buffer.PollForPlayback());

        _nowMs++;
        var packet = buffer.PollForPlayback() as JitterBufferPollResult.Packet;
        Assert.IsNotNull(packet);
        Assert.AreEqual(1, packet.Frame.SequenceNumber);
    }

    [TestMethod]
    public void RepeatedPollingBeforeDeadlineDoesNotAdvanceOrCreatePlc()
    {
        var buffer = CreateBuffer(initialDelayMs: 50);
        buffer.Add(Frame(1));

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.IsInstanceOfType<JitterBufferPollResult.Wait>(buffer.PollForPlayback());
        }

        _nowMs += 50;
        Assert.IsInstanceOfType<JitterBufferPollResult.Packet>(buffer.PollForPlayback());
        Assert.AreEqual(0, buffer.Snapshot().PlcCount);
        Assert.AreEqual(0, buffer.Snapshot().LatePacketDrops);
    }

    [TestMethod]
    public void MissingPacketCreatesExactlyOnePlcPerDueTick()
    {
        var buffer = CreateBuffer(initialDelayMs: 50);
        buffer.Add(Frame(1));
        buffer.Add(Frame(3));
        _nowMs += 50;

        Assert.AreEqual(1, ReadPacket(buffer).SequenceNumber);
        Assert.IsInstanceOfType<JitterBufferPollResult.Wait>(buffer.PollForPlayback());

        _nowMs += AudioProtocol.FrameDurationMs;
        var missing = buffer.PollForPlayback() as JitterBufferPollResult.Missing;
        Assert.IsNotNull(missing);
        Assert.AreEqual(2, missing.SequenceNumber);
        Assert.IsInstanceOfType<JitterBufferPollResult.Wait>(buffer.PollForPlayback());

        _nowMs += AudioProtocol.FrameDurationMs;
        Assert.AreEqual(3, ReadPacket(buffer).SequenceNumber);
        Assert.AreEqual(1, buffer.Snapshot().PlcCount);
    }

    [TestMethod]
    public void PacketBeforeDeadlineIsNotLate()
    {
        var buffer = CreateBuffer(initialDelayMs: 50);
        buffer.Add(Frame(1));
        _nowMs += 50;
        Assert.AreEqual(1, ReadPacket(buffer).SequenceNumber);

        _nowMs += AudioProtocol.FrameDurationMs - 5;
        buffer.Add(Frame(2));

        Assert.AreEqual(0, buffer.Snapshot().LatePacketDrops);
        _nowMs += 5;
        Assert.AreEqual(2, ReadPacket(buffer).SequenceNumber);
    }

    [TestMethod]
    public void PacketAfterMissedDeadlineIsLate()
    {
        var buffer = CreateBuffer(initialDelayMs: 50);
        buffer.Add(Frame(1));
        _nowMs += 50;
        Assert.AreEqual(1, ReadPacket(buffer).SequenceNumber);

        _nowMs += AudioProtocol.FrameDurationMs;
        Assert.IsInstanceOfType<JitterBufferPollResult.Missing>(buffer.PollForPlayback());
        buffer.Add(Frame(2));

        var snapshot = buffer.Snapshot();
        Assert.AreEqual(1, snapshot.LatePacketDrops);
        Assert.AreEqual(1, snapshot.DroppedFrames);
    }

    [TestMethod]
    public void OverflowDropsAreNotReportedAsLate()
    {
        var buffer = CreateBuffer(initialDelayMs: 50, maximumDelayMs: 60);
        for (ushort sequence = 1; sequence <= 6; sequence++)
        {
            buffer.Add(Frame(sequence));
        }

        var snapshot = buffer.Snapshot();
        Assert.IsTrue(snapshot.BufferLevelMs <= 60);
        Assert.IsTrue(snapshot.OverflowDrops > 0);
        Assert.AreEqual(snapshot.DroppedFrames, snapshot.OverflowDrops);
        Assert.AreEqual(0, snapshot.LatePacketDrops);
    }

    [TestMethod]
    public void SequenceWrapPreservesPlayoutOrder()
    {
        var buffer = CreateBuffer(initialDelayMs: 20);
        buffer.Add(Frame(ushort.MaxValue));
        buffer.Add(Frame(0));
        _nowMs += 20;

        Assert.AreEqual(ushort.MaxValue, ReadPacket(buffer).SequenceNumber);
        _nowMs += AudioProtocol.FrameDurationMs;
        Assert.AreEqual(0, ReadPacket(buffer).SequenceNumber);
    }

    [TestMethod]
    public void StableModeUsesLargerInitialDelay()
    {
        var low = new JitterBuffer(AudioProtocol.LatencyModeLow);
        var stable = new JitterBuffer(AudioProtocol.LatencyModeStable);

        Assert.AreEqual(50, low.Snapshot().TargetDelayMs);
        Assert.AreEqual(100, stable.Snapshot().TargetDelayMs);
    }

    private JitterBuffer CreateBuffer(
        int initialDelayMs,
        int maximumDelayMs = 120)
    {
        _nowMs = 1_000;
        return new JitterBuffer(
            new AudioLatencyProfile("test", initialDelayMs, initialDelayMs, maximumDelayMs),
            () => _nowMs);
    }

    private static RtpAudioFrame ReadPacket(JitterBuffer buffer)
    {
        var packet = buffer.PollForPlayback() as JitterBufferPollResult.Packet;
        Assert.IsNotNull(packet);
        return packet.Frame;
    }

    private static RtpAudioFrame Frame(ushort sequenceNumber)
    {
        return new RtpAudioFrame(
            sequenceNumber,
            unchecked((uint)(sequenceNumber * AudioProtocol.RtpTimestampIncrement)),
            ReceivedAtMs: sequenceNumber,
            Payload: [(byte)sequenceNumber]);
    }
}
