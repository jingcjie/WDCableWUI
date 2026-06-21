using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Services;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class HistoryDataOperationsTests
{
    [TestMethod]
    public void ChatMessagesWithTheSameIdAreDeduplicated()
    {
        var timestamp = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var existing = new ChatMessageData
        {
            MessageId = "message-1",
            Type = 1,
            Content = "old",
            Timestamp = timestamp
        };
        var updated = new ChatMessageData
        {
            MessageId = "message-1",
            Type = 1,
            Content = "new",
            Timestamp = timestamp
        };

        var result = HistoryDataOperations.MergeChatHistory([existing], [updated]);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("new", result[0].Content);
    }

    [TestMethod]
    public void LegacyChatMessagesWithoutIdsAreRetained()
    {
        var legacy = new ChatMessageData
        {
            Type = 1,
            Content = "legacy",
            Timestamp = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Local)
        };

        var result = HistoryDataOperations.MergeChatHistory([legacy], []);

        Assert.AreEqual(1, result.Count);
        Assert.IsNull(result[0].MessageId);
        Assert.AreEqual("legacy", result[0].Content);
    }

    [TestMethod]
    public void TransferRecordsAreUpsertedByDirectionAndTransferId()
    {
        var original = Transfer("transfer-1", isSender: false, "Failed", 1);
        var replacement = Transfer("transfer-1", isSender: false, "Received", 2);
        var oppositeDirection = Transfer("transfer-1", isSender: true, "Sent", 3);

        var result = HistoryDataOperations.UpsertTransferRecord([original], replacement);
        result = HistoryDataOperations.UpsertTransferRecord(result, oppositeDirection);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("Sent", result[0].Status);
        Assert.AreEqual("Received", result[1].Status);
    }

    private static FileTransferRecordData Transfer(
        string transferId,
        bool isSender,
        string status,
        int minute)
    {
        return new FileTransferRecordData
        {
            TransferId = transferId,
            FileName = "example.bin",
            IsSender = isSender,
            Status = status,
            Timestamp = new DateTime(2026, 6, 21, 12, minute, 0, DateTimeKind.Utc)
        };
    }
}
