using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Services;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class JitterBufferTests
{
    [TestMethod]
    public void PollReadyOrdersFramesBySequence()
    {
        var buffer = new JitterBuffer(targetBufferMs: 40, maxBufferMs: 200);
        buffer.Add(Frame(3));
        buffer.Add(Frame(1));
        buffer.Add(Frame(2));

        Assert.AreEqual(1, buffer.PollReady()?.SequenceNumber);
        Assert.AreEqual(2, buffer.PollReady()?.SequenceNumber);
        Assert.AreEqual(3, buffer.PollReady()?.SequenceNumber);
    }

    [TestMethod]
    public void BufferWaitsForTargetBeforeFirstPlayableFrame()
    {
        var buffer = new JitterBuffer(targetBufferMs: 60, maxBufferMs: 200);
        buffer.Add(Frame(1));

        Assert.IsNull(buffer.PollReady());
        Assert.AreEqual(0, buffer.Snapshot().UnderflowCount);

        buffer.Add(Frame(2));
        buffer.Add(Frame(3));
        Assert.AreEqual(1, buffer.PollReady()?.SequenceNumber);
    }

    [TestMethod]
    public void OverflowDropsOldestFrames()
    {
        var buffer = new JitterBuffer(targetBufferMs: 20, maxBufferMs: 40);
        buffer.Add(Frame(1));
        buffer.Add(Frame(2));
        buffer.Add(Frame(3));

        var snapshot = buffer.Snapshot();
        Assert.AreEqual(1, snapshot.DroppedFrames);
        Assert.AreEqual(40, snapshot.BufferLevelMs);
        Assert.AreEqual(2, buffer.PollReady()?.SequenceNumber);
    }

    [TestMethod]
    public void StaleFrameAfterPopIsDropped()
    {
        var buffer = new JitterBuffer(targetBufferMs: 20, maxBufferMs: 200);
        buffer.Add(Frame(2));
        Assert.AreEqual(2, buffer.PollReady()?.SequenceNumber);

        buffer.Add(Frame(1));

        Assert.AreEqual(1, buffer.Snapshot().DroppedFrames);
    }

    private static EncodedAudioFrame Frame(long sequenceNumber)
    {
        return new EncodedAudioFrame(
            sequenceNumber,
            SentAtMs: sequenceNumber,
            DurationMs: AudioProtocol.FrameDurationMs,
            Payload: [(byte)sequenceNumber]);
    }
}
