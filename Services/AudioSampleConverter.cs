using System;
using NAudio.Wave;

namespace WDCableWUI.Services;

public static class AudioSampleConverter
{
    public static short[] ToMono48Pcm16(byte[] buffer, int bytesRecorded, WaveFormat sourceFormat)
    {
        if (bytesRecorded <= 0)
        {
            return [];
        }

        var mono = ToMonoFloat(buffer, bytesRecorded, sourceFormat);
        if (mono.Length == 0)
        {
            return [];
        }

        var resampled = sourceFormat.SampleRate == AudioProtocol.SampleRate
            ? mono
            : ResampleLinear(mono, sourceFormat.SampleRate, AudioProtocol.SampleRate);

        var pcm = new short[resampled.Length];
        for (var i = 0; i < resampled.Length; i++)
        {
            pcm[i] = FloatToPcm16(resampled[i]);
        }

        return pcm;
    }

    public static byte[] Pcm16ToBytes(short[] samples, int sampleCount)
    {
        var output = new byte[sampleCount * 2];
        for (var i = 0; i < sampleCount; i++)
        {
            var value = samples[i];
            output[i * 2] = (byte)(value & 0xff);
            output[i * 2 + 1] = (byte)((value >> 8) & 0xff);
        }

        return output;
    }

    private static float[] ToMonoFloat(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        var channels = Math.Max(format.Channels, 1);
        var blockAlign = Math.Max(format.BlockAlign, 1);
        var frameCount = bytesRecorded / blockAlign;
        var mono = new float[frameCount];

        for (var frame = 0; frame < frameCount; frame++)
        {
            var frameOffset = frame * blockAlign;
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += ReadSample(buffer, frameOffset, channel, format);
            }

            mono[frame] = sum / channels;
        }

        return mono;
    }

    private static float ReadSample(byte[] buffer, int frameOffset, int channel, WaveFormat format)
    {
        var bytesPerSample = Math.Max(format.BitsPerSample / 8, 1);
        var offset = frameOffset + channel * bytesPerSample;
        if (offset + bytesPerSample > buffer.Length)
        {
            return 0;
        }

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            return Math.Clamp(BitConverter.ToSingle(buffer, offset), -1f, 1f);
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            return BitConverter.ToInt16(buffer, offset) / 32768f;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 24)
        {
            var value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
            if ((value & 0x800000) != 0)
            {
                value |= unchecked((int)0xff000000);
            }

            return value / 8388608f;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 32)
        {
            return BitConverter.ToInt32(buffer, offset) / 2147483648f;
        }

        throw new NotSupportedException($"Unsupported system audio format: {format.Encoding} {format.BitsPerSample}-bit");
    }

    private static float[] ResampleLinear(float[] input, int inputRate, int outputRate)
    {
        if (input.Length == 0 || inputRate <= 0 || outputRate <= 0)
        {
            return [];
        }

        var outputLength = Math.Max(1, (int)Math.Round(input.Length * outputRate / (double)inputRate));
        var output = new float[outputLength];
        var scale = inputRate / (double)outputRate;
        for (var i = 0; i < output.Length; i++)
        {
            var source = i * scale;
            var index = (int)source;
            var fraction = source - index;
            var left = input[Math.Min(index, input.Length - 1)];
            var right = input[Math.Min(index + 1, input.Length - 1)];
            output[i] = (float)(left + (right - left) * fraction);
        }

        return output;
    }

    private static short FloatToPcm16(float sample)
    {
        var clamped = Math.Clamp(sample, -1f, 1f);
        return (short)Math.Round(clamped * short.MaxValue);
    }
}
