using System.Collections.Generic;
using System.Linq;

namespace WDCableWUI.Services;

public sealed record EncodedAudioFrame(
    long SequenceNumber,
    long SentAtMs,
    int DurationMs,
    byte[] Payload,
    bool CodecConfig = false);

public sealed record JitterBufferSnapshot(
    int BufferLevelMs,
    long DroppedFrames,
    long UnderflowCount,
    int QueuedFrames);

public sealed class JitterBuffer
{
    private readonly object _lock = new();
    private readonly SortedDictionary<long, EncodedAudioFrame> _frames = new();
    private readonly int _targetBufferMs;
    private readonly int _maxBufferMs;
    private long _lastPoppedSequence = long.MinValue;
    private long _droppedFrames;
    private long _underflowCount;

    public JitterBuffer(int targetBufferMs = 60, int maxBufferMs = 200)
    {
        _targetBufferMs = targetBufferMs;
        _maxBufferMs = maxBufferMs;
    }

    public void Add(EncodedAudioFrame frame)
    {
        lock (_lock)
        {
            if (frame.SequenceNumber <= _lastPoppedSequence)
            {
                _droppedFrames++;
                return;
            }

            _frames[frame.SequenceNumber] = frame;
            TrimOverflowLocked();
        }
    }

    public EncodedAudioFrame? PollReady()
    {
        lock (_lock)
        {
            if (_frames.Count == 0)
            {
                _underflowCount++;
                return null;
            }

            if (BufferLevelLocked() < _targetBufferMs && _lastPoppedSequence == long.MinValue)
            {
                return null;
            }

            var first = _frames.First();
            _frames.Remove(first.Key);
            _lastPoppedSequence = first.Key;
            return first.Value;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _frames.Clear();
            _lastPoppedSequence = long.MinValue;
            _droppedFrames = 0;
            _underflowCount = 0;
        }
    }

    public JitterBufferSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new JitterBufferSnapshot(
                BufferLevelLocked(),
                _droppedFrames,
                _underflowCount,
                _frames.Count);
        }
    }

    private void TrimOverflowLocked()
    {
        while (BufferLevelLocked() > _maxBufferMs && _frames.Count > 0)
        {
            var first = _frames.First();
            _frames.Remove(first.Key);
            _droppedFrames++;
        }
    }

    private int BufferLevelLocked()
    {
        return _frames.Values.Sum(frame => frame.DurationMs);
    }
}
