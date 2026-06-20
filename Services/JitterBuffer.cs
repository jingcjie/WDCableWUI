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
    long DuplicateOrReorderedPackets,
    long PlcCount,
    int TargetDelayMs);

public sealed record JitterBufferReadResult(
    ushort SequenceNumber,
    uint Timestamp,
    byte[]? Payload,
    bool IsMissing);

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
    private long? _expectedExtendedSequence;
    private long? _highestExtendedSequence;
    private long _droppedFrames;
    private long _underflowCount;
    private long _sequenceGaps;
    private long _latePacketDrops;
    private long _duplicateOrReorderedPackets;
    private long _plcCount;
    private int _targetDelayMs;
    private bool _started;

    public JitterBuffer(string latencyMode = AudioProtocol.LatencyModeLow)
        : this(ProfileFor(latencyMode))
    {
    }

    public JitterBuffer(AudioLatencyProfile profile)
    {
        _profile = profile;
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

            TrimOverflowLocked();
        }
    }

    public bool TryReadNext(out JitterBufferReadResult result)
    {
        lock (_lock)
        {
            if (!_started)
            {
                if (BufferLevelLocked() < _targetDelayMs)
                {
                    result = new JitterBufferReadResult(0, 0, null, false);
                    return false;
                }

                _started = true;
                _expectedExtendedSequence = _frames.Count > 0 ? _frames.First().Key : 0;
            }

            var expected = _expectedExtendedSequence ?? 0;
            while (_frames.Count > 0 && _frames.First().Key < expected)
            {
                _frames.Remove(_frames.First().Key);
                _latePacketDrops++;
                _droppedFrames++;
            }

            if (_frames.TryGetValue(expected, out var frame))
            {
                _frames.Remove(expected);
                _expectedExtendedSequence = expected + 1;
                result = new JitterBufferReadResult(
                    frame.SequenceNumber,
                    frame.Timestamp,
                    frame.Payload,
                    IsMissing: false);
                return true;
            }

            _underflowCount++;
            _sequenceGaps++;
            _plcCount++;
            _expectedExtendedSequence = expected + 1;
            result = new JitterBufferReadResult(
                unchecked((ushort)expected),
                0,
                null,
                IsMissing: true);
            IncreaseTargetAfterUnderflowLocked();
            return true;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _frames.Clear();
            _expectedExtendedSequence = null;
            _highestExtendedSequence = null;
            _droppedFrames = 0;
            _underflowCount = 0;
            _sequenceGaps = 0;
            _latePacketDrops = 0;
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
            _latePacketDrops++;
            if (!_expectedExtendedSequence.HasValue || first.Key >= _expectedExtendedSequence.Value)
            {
                _expectedExtendedSequence = first.Key + 1;
            }
        }
    }

    private void IncreaseTargetAfterUnderflowLocked()
    {
        if (_targetDelayMs < _profile.MaximumDelayMs)
        {
            _targetDelayMs = Math.Min(_profile.MaximumDelayMs, _targetDelayMs + AudioProtocol.FrameDurationMs);
        }
    }

    private int BufferLevelLocked()
    {
        return _frames.Count * AudioProtocol.FrameDurationMs;
    }
}
