using System;
using System.Collections.Generic;
using System.Linq;

namespace WDCableWUI.Services;

public sealed class ChatMessageData
{
    public string? MessageId { get; set; }
    public int Type { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

public sealed class FileTransferRecordData
{
    public string TransferId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public bool IsSender { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string? ErrorMessage { get; set; }
}

public static class HistoryDataOperations
{
    public static List<ChatMessageData> MergeChatHistory(
        IEnumerable<ChatMessageData> existing,
        IEnumerable<ChatMessageData> incoming)
    {
        var messages = new Dictionary<string, ChatMessageData>(StringComparer.Ordinal);
        var legacyIndex = 0;

        foreach (var message in existing.Concat(incoming))
        {
            var key = ChatKey(message, legacyIndex++);
            messages[key] = message;
        }

        return messages.Values
            .OrderBy(message => message.Timestamp)
            .ThenBy(message => message.MessageId, StringComparer.Ordinal)
            .ToList();
    }

    public static List<FileTransferRecordData> UpsertTransferRecord(
        IEnumerable<FileTransferRecordData> existing,
        FileTransferRecordData record)
    {
        var records = new Dictionary<string, FileTransferRecordData>(StringComparer.Ordinal);
        foreach (var item in existing)
        {
            records[TransferKey(item)] = item;
        }
        records[TransferKey(record)] = record;

        return records.Values
            .OrderByDescending(item => item.Timestamp)
            .ThenBy(item => item.TransferId, StringComparer.Ordinal)
            .ToList();
    }

    public static string TransferKey(FileTransferRecordData record)
    {
        var id = string.IsNullOrWhiteSpace(record.TransferId)
            ? $"{record.FileName}:{record.Timestamp.Ticks}"
            : record.TransferId;
        return $"{(record.IsSender ? "send" : "receive")}:{id}";
    }

    private static string ChatKey(ChatMessageData message, int legacyIndex)
    {
        if (!string.IsNullOrWhiteSpace(message.MessageId))
        {
            return $"id:{message.MessageId}";
        }

        return $"legacy:{message.Type}:{message.Timestamp.Ticks}:{message.Content}:{legacyIndex}";
    }
}
