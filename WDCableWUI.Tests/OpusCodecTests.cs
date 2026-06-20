using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Services;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class OpusCodecTests
{
    [TestMethod]
    public void Mono48KhzTwentyMillisecondFrameRoundTripsThroughLibopus()
    {
        using var encoder = new LibOpusAudioEncoder();
        using var decoder = new LibOpusAudioDecoder();

        var pcm = new short[AudioProtocol.SamplesPerFrame];
        for (var i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)(Math.Sin(i / 8.0) * 8000);
        }

        var encoded = encoder.Encode(pcm);
        var decoded = decoder.Decode(encoded);

        Assert.IsTrue(encoded.Length > 0);
        Assert.AreEqual(AudioProtocol.SamplesPerFrame, decoded.Length);
        Assert.IsTrue(decoded.Any(sample => sample != 0));
    }

    [TestMethod]
    public void MissingPacketDecodeReturnsPlcOrSilenceFrame()
    {
        using var decoder = new LibOpusAudioDecoder();

        var decoded = decoder.DecodeMissing();

        Assert.AreEqual(AudioProtocol.SamplesPerFrame, decoded.Length);
    }
}
