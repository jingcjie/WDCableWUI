using System;

namespace WDCableWUI.Services;

public sealed class ChatMessageReceivedEventArgs : EventArgs
{
    public ChatMessageReceivedEventArgs(ChatControlMessage message)
    {
        Message = message;
    }

    public ChatControlMessage Message { get; }
}

public sealed class ChatSendResult
{
    public ChatSendResult(
        bool success,
        string? messageId = null,
        DateTimeOffset? timestamp = null,
        string? errorMessage = null)
    {
        Success = success;
        MessageId = messageId;
        Timestamp = timestamp;
        ErrorMessage = errorMessage;
    }

    public bool Success { get; }

    public string? MessageId { get; }

    public DateTimeOffset? Timestamp { get; }

    public string? ErrorMessage { get; }
}
