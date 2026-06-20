using System;
using System.Collections.Generic;
using System.Linq;

namespace WDCableWUI.Services;

public sealed record RtpAudioFrame(
    ushort SequenceNumber,
    uint Timestamp,
    long ReceivedAtMs,
    byte[] Payload);

public sealed record JitterBufferSnapshot(
    int BufferLevelMs,
    long DroppedFrames,
    long UnderflowCount,
    int QueuedFrames,
    long SequenceGaps,
    long LatePacketDrops,
    long OverflowDrops,
    long DuplicateOrReorderedPackets,
    long PlcCount,
    int TargetDelayMs);

public abstract record JitterBufferPollResult
{
    public sealed record Packet(RtpAudioFrame Frame) : JitterBufferPollResult;

    public sealed record Missing(ushort SequenceNumber) : JitterBufferPollResult;

    public sealed record Wait(long WaitMs) : JitterBufferPollResult;
}

public sealed record AudioLatencyProfile(
    string Mode,
    int InitialDelayMs,
    int MinimumDelayMs,
    int MaximumDelayMs);

public sealed class JitterBuffer
{
    private readonly object _lock = new();
    private readonly SortedDictionary<long, RtpAudioFrame> _frames = new();
    private readonly AudioLatencyProfile _profile;
    private readonly Func<long> _clockMs;
    private long? _expectedExtendedSequence;
    private long? _highestExtendedSequence;
    private long? _initialPlayoutAtMs;
    private long? _nextPlayoutAtMs;
    private long _droppedFrames;
    private long _underflowCount;
    private long _sequenceGaps;
    private long _latePacketDrops;
    private long _overflowDrops;
    private long _duplicateOrReorderedPackets;
    private long _plcCount;
    private int _targetDelayMs;
    private bool _started;

    public JitterBuffer(string latencyMode = AudioProtocol.LatencyModeLow)
        : this(ProfileFor(latencyMode))
    {
    }

    public JitterBuffer(AudioLatencyProfile profile, Func<long>? clockMs = null)
    {
        _profile = profile;
        _clockMs = clockMs ?? (() => Environment.TickCount64);
        _targetDelayMs = profile.InitialDelayMs;
    }

    public static AudioLatencyProfile ProfileFor(string latencyMode)
    {
        return AudioProtocol.NormalizeLatencyMode(latencyMode) == AudioProtocol.LatencyModeStable
            ? new AudioLatencyProfile(AudioProtocol.LatencyModeStable, 100, 80, 220)
            : new AudioLatencyProfile(AudioProtocol.LatencyModeLow, 50, 40, 120);
    }

    public void Add(RtpAudioFrame frame)
    {
        lock (_lock)
        {
            var extended = ExtendSequenceLocked(frame.SequenceNumber);
            if (_expectedExtendedSequence.HasValue && extended < _expectedExtendedSequence.Value)
            {
                _latePacketDrops++;
                _droppedFrames++;
                return;
            }

            if (_frames.ContainsKey(extended))
            {
                _duplicateOrReorderedPackets++;
                _droppedFrames++;
                return;
            }

            if (_highestExtendedSequence.HasValue && extended < _highestExtendedSequence.Value)
            {
                _duplicateOrReorderedPackets++;
            }

            _frames[extended] = frame;
            if (!_highestExtendedSequence.HasValue || extended > _highestExtendedSequence.Value)
            {
                _highestExtendedSequence = extended;
            }

            if (_initialPlayoutAtMs == null && !_started)
            {
                _initialPlayoutAtMs = _clockMs() + _profile.InitialDelayMs;
            }

            TrimOverflowLocked();
        }
    }

    public JitterBufferPollResult PollForPlayback()
    {
        lock (_lock)
        {
            var nowMs = _clockMs();
            if (!_started)
            {
                if (_frames.Count == 0)
                {
                    return new JitterBufferPollResult.Wait(DefaultWaitMs);
                }

                var initialDeadline = _initialPlayoutAtMs ??
                    (nowMs + _profile.InitialDelayMs);
                _initialPlayoutAtMs = initialDeadline;
                if (nowMs < initialDeadline)
                {
                    return new JitterBufferPollResult.Wait(initialDeadline - nowMs);
                }

                _started = true;
                _expectedExtendedSequence = _frames.First().Key;
                _nextPlayoutAtMs = initialDeadline;
            }

            var playoutDeadline = _nextPlayoutAtMs ?? nowMs;
            if (nowMs < playoutDeadline)
            {
                return new JitterBufferPollResult.Wait(playoutDeadline - nowMs);
            }

            var expected = _expectedExtendedSequence ?? 0;
            _expectedExtendedSequence = expected + 1;
            _nextPlayoutAtMs = playoutDeadline + AudioProtocol.FrameDurationMs;
            if (_frames.TryGetValue(expected, out var frame))
            {
                _frames.Remove(expected);
                return new JitterBufferPollResult.Packet(frame);
            }

            _underflowCount++;
            _sequenceGaps++;
            _plcCount++;
            return new JitterBufferPollResult.Missing(unchecked((ushort)expected));
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _frames.Clear();
            _expectedExtendedSequence = null;
            _highestExtendedSequence = null;
            _initialPlayoutAtMs = null;
            _nextPlayoutAtMs = null;
            _droppedFrames = 0;
            _underflowCount = 0;
            _sequenceGaps = 0;
            _latePacketDrops = 0;
            _overflowDrops = 0;
            _duplicateOrReorderedPackets = 0;
            _plcCount = 0;
            _targetDelayMs = _profile.InitialDelayMs;
            _started = false;
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
                _frames.Count,
                _sequenceGaps,
                _latePacketDrops,
                _overflowDrops,
                _duplicateOrReorderedPackets,
                _plcCount,
                _targetDelayMs);
        }
    }

    private long ExtendSequenceLocked(ushort sequence)
    {
        var pivot = _expectedExtendedSequence ?? _highestExtendedSequence ?? sequence;
        var cycle = pivot & ~0xffffL;
        var candidate = cycle + sequence;
        if (candidate - pivot > 32767)
        {
            candidate -= 65536;
        }
        else if (pivot - candidate > 32768)
        {
            candidate += 65536;
        }

        return candidate;
    }

    private void TrimOverflowLocked()
    {
        while (BufferLevelLocked() > _profile.MaximumDelayMs && _frames.Count > 0)
        {
            var first = _frames.First();
            _frames.Remove(first.Key);
            _droppedFrames++;
            _overflowDrops++;
        }
    }

    private int BufferLevelLocked()
    {
        return _frames.Count * AudioProtocol.FrameDurationMs;
    }

    private const long DefaultWaitMs = 5;
}
