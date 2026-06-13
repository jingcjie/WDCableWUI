using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Services;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class BulkProtocolTests
{
    [TestMethod]
    public void MetadataRoundTripsAndroidBulkShape()
    {
        var metadataJson = BulkProtocol.BuildMetadata(new Dictionary<string, object?>
        {
            ["kind"] = BulkProtocol.KindFile,
            ["transferId"] = "transfer-1",
            ["fileName"] = "notes.txt",
            ["sizeBytes"] = -1
        });

        var metadata = BulkProtocol.ParseMetadata(metadataJson);

        Assert.AreEqual(BulkProtocol.KindFile, BulkProtocol.GetString(metadata, "kind"));
        Assert.AreEqual("transfer-1", BulkProtocol.GetString(metadata, "transferId"));
        Assert.AreEqual("notes.txt", BulkProtocol.GetString(metadata, "fileName"));
        Assert.AreEqual(-1, BulkProtocol.GetInt64(metadata, "sizeBytes"));
    }

    [TestMethod]
    public void SafeFileNameStripsPathAndUsesFallback()
    {
        Assert.AreEqual("unknown_file", BulkProtocol.SafeFileName(""));
        Assert.AreEqual("report.txt", BulkProtocol.SafeFileName(Path.Combine("..", "folder", "report.txt")));
    }

    [TestMethod]
    public void DuplicateSafePathAvoidsExistingFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"WDCableWUI-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, "demo.txt"), "existing");

            var candidate = BulkProtocol.DuplicateSafePath(directory, "demo.txt");

            Assert.AreEqual(Path.Combine(directory, "demo (1).txt"), candidate);
            Assert.IsFalse(File.Exists(candidate));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Sha256HexUsesLowercaseHex()
    {
        var hex = BulkProtocol.Sha256Hex([0, 15, 16, 255]);

        Assert.AreEqual("000f10ff", hex);
    }

    [TestMethod]
    public void CalculateMbpsUsesBinaryMegabits()
    {
        var mbps = BulkProtocol.CalculateMbps(1024 * 1024, TimeSpan.FromSeconds(1));

        Assert.AreEqual(8.0, mbps, 0.001);
    }
}
