using Concentus;
using Concentus.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Services;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class OpusCodecTests
{
    [TestMethod]
    public void Mono48KhzTwentyMillisecondFrameRoundTripsThroughOpus()
    {
        var encoder = OpusCodecFactory.CreateEncoder(
            AudioProtocol.SampleRate,
            AudioProtocol.Channels,
            OpusApplication.OPUS_APPLICATION_AUDIO,
            null);
        encoder.Bitrate = AudioProtocol.BitrateBps;
        var decoder = OpusCodecFactory.CreateDecoder(AudioProtocol.SampleRate, AudioProtocol.Channels, null);

        var pcm = new short[AudioProtocol.SamplesPerFrame];
        for (var i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)(Math.Sin(i / 8.0) * 8000);
        }

        var encoded = new byte[4096];
        var encodedBytes = encoder.Encode(pcm, AudioProtocol.SamplesPerFrame, encoded, encoded.Length);
        var decoded = new short[AudioProtocol.SamplesPerFrame];
        var decodedSamples = decoder.Decode(encoded.AsSpan(0, encodedBytes), decoded, AudioProtocol.SamplesPerFrame, false);

        Assert.IsTrue(encodedBytes > 0);
        Assert.AreEqual(AudioProtocol.SamplesPerFrame, decodedSamples);
        Assert.IsTrue(decoded.Any(sample => sample != 0));
    }
}
