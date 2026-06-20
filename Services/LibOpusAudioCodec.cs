using System;
using OpusSharp.Core;

namespace WDCableWUI.Services;

public sealed class LibOpusAudioEncoder : IDisposable
{
    private readonly OpusEncoder _encoder;
    private bool _isDisposed;

    public LibOpusAudioEncoder()
    {
        _encoder = new OpusEncoder(
            AudioProtocol.SampleRate,
            AudioProtocol.Channels,
            OpusPredefinedValues.OPUS_APPLICATION_AUDIO);
        _encoder.Ctl(EncoderCTL.OPUS_SET_BITRATE, AudioProtocol.BitrateBps);
        _encoder.Ctl(EncoderCTL.OPUS_SET_VBR, 0);
    }

    public byte[] Encode(short[] pcm)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (pcm.Length < AudioProtocol.SamplesPerFrame)
        {
            throw new ArgumentException("PCM frame is shorter than one Audio Link frame.", nameof(pcm));
        }

        var output = new byte[1275];
        var encodedBytes = _encoder.Encode(
            pcm.AsSpan(0, AudioProtocol.SamplesPerFrame),
            AudioProtocol.SamplesPerFrame,
            output,
            output.Length);
        if (encodedBytes <= 0)
        {
            return [];
        }

        return output[..encodedBytes];
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _encoder.Dispose();
    }
}

public sealed class LibOpusAudioDecoder : IDisposable
{
    private readonly OpusDecoder _decoder;
    private bool _isDisposed;

    public LibOpusAudioDecoder()
    {
        _decoder = new OpusDecoder(AudioProtocol.SampleRate, AudioProtocol.Channels);
    }

    public short[] Decode(byte[] payload)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        var pcm = new short[AudioProtocol.SamplesPerFrame];
        var decodedSamples = _decoder.Decode(
            payload,
            payload.Length,
            pcm,
            AudioProtocol.SamplesPerFrame,
            decode_fec: false);
        return NormalizeDecodedFrame(pcm, decodedSamples);
    }

    public short[] DecodeMissing()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        var pcm = new short[AudioProtocol.SamplesPerFrame];
        try
        {
            var decodedSamples = _decoder.Decode(
                Span<byte>.Empty,
                0,
                pcm,
                AudioProtocol.SamplesPerFrame,
                decode_fec: false);
            return NormalizeDecodedFrame(pcm, decodedSamples);
        }
        catch
        {
            return pcm;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _decoder.Dispose();
    }

    private static short[] NormalizeDecodedFrame(short[] pcm, int decodedSamples)
    {
        if (decodedSamples <= 0)
        {
            return new short[AudioProtocol.SamplesPerFrame];
        }

        if (decodedSamples == AudioProtocol.SamplesPerFrame)
        {
            return pcm;
        }

        var normalized = new short[AudioProtocol.SamplesPerFrame];
        Array.Copy(pcm, normalized, Math.Min(decodedSamples, normalized.Length));
        return normalized;
    }
}
