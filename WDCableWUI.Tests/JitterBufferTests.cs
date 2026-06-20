using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Services;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class JitterBufferTests
{
    [TestMethod]
    public void ReadOrdersFramesBySequence()
    {
        var buffer = new JitterBuffer(new AudioLatencyProfile("test", 40, 40, 100));
        buffer.Add(Frame(3));
        buffer.Add(Frame(1));
        buffer.Add(Frame(2));

        Assert.IsTrue(buffer.TryReadNext(out var first));
        Assert.AreEqual(1, first.SequenceNumber);
        Assert.IsTrue(buffer.TryReadNext(out var second));
        Assert.AreEqual(2, second.SequenceNumber);
        Assert.IsTrue(buffer.TryReadNext(out var third));
        Assert.AreEqual(3, third.SequenceNumber);
    }

    [TestMethod]
    public void BufferWaitsForInitialDelayBeforeFirstFrame()
    {
        var buffer = new JitterBuffer(new AudioLatencyProfile("test", 60, 40, 120));
        buffer.Add(Frame(1));
        buffer.Add(Frame(2));

        Assert.IsFalse(buffer.TryReadNext(out _));
        Assert.AreEqual(0, buffer.Snapshot().UnderflowCount);

        buffer.Add(Frame(3));
        Assert.IsTrue(buffer.TryReadNext(out var first));
        Assert.AreEqual(1, first.SequenceNumber);
    }

    [TestMethod]
    public void MissingSequenceReturnsPlcRead()
    {
        var buffer = new JitterBuffer(new AudioLatencyProfile("test", 20, 20, 120));
        buffer.Add(Frame(1));
        buffer.Add(Frame(3));

        Assert.IsTrue(buffer.TryReadNext(out var first));
        Assert.IsFalse(first.IsMissing);
        Assert.AreEqual(1, first.SequenceNumber);

        Assert.IsTrue(buffer.TryReadNext(out var missing));
        Assert.IsTrue(missing.IsMissing);
        Assert.AreEqual(2, missing.SequenceNumber);
        Assert.AreEqual(1, buffer.Snapshot().PlcCount);
    }

    [TestMethod]
    public void LatePacketAfterPlayoutIsDropped()
    {
        var buffer = new JitterBuffer(new AudioLatencyProfile("test", 20, 20, 120));
        buffer.Add(Frame(2));
        Assert.IsTrue(buffer.TryReadNext(out var first));
        Assert.AreEqual(2, first.SequenceNumber);

        buffer.Add(Frame(1));

        Assert.AreEqual(1, buffer.Snapshot().LatePacketDrops);
    }

    [TestMethod]
    public void StableModeUsesLargerInitialDelay()
    {
        var low = new JitterBuffer(AudioProtocol.LatencyModeLow);
        var stable = new JitterBuffer(AudioProtocol.LatencyModeStable);

        Assert.AreEqual(50, low.Snapshot().TargetDelayMs);
        Assert.AreEqual(100, stable.Snapshot().TargetDelayMs);
    }

    private static RtpAudioFrame Frame(ushort sequenceNumber)
    {
        return new RtpAudioFrame(
            sequenceNumber,
            sequenceNumber * AudioProtocol.RtpTimestampIncrement,
            ReceivedAtMs: sequenceNumber,
            Payload: [(byte)sequenceNumber]);
    }
}
