using System;
using System.Collections.Generic;
using WDCableWUI.Protocol;

namespace WDCableWUI.Services;

public sealed class SessionStateChangedEventArgs : EventArgs
{
    public SessionStateChangedEventArgs(
        SessionPhase phase,
        string? sessionId,
        SessionRole? role,
        string? peerName,
        string? peerAddress,
        string? disconnectReason)
    {
        Phase = phase;
        SessionId = sessionId;
        Role = role;
        PeerName = peerName;
        PeerAddress = peerAddress;
        DisconnectReason = disconnectReason;
    }

    public SessionPhase Phase { get; }

    public string StateName => Phase.GetEventName();

    public string? SessionId { get; }

    public SessionRole? Role { get; }

    public string? PeerName { get; }

    public string? PeerAddress { get; }

    public string? DisconnectReason { get; }
}

public sealed class SessionReadyEventArgs : EventArgs
{
    public SessionReadyEventArgs(
        string sessionId,
        SessionRole role,
        string? peerName,
        string? peerAddress,
        int protocolVersion,
        IReadOnlyList<string> capabilities)
    {
        SessionId = sessionId;
        Role = role;
        PeerName = peerName;
        PeerAddress = peerAddress;
        ProtocolVersion = protocolVersion;
        Capabilities = capabilities;
    }

    public string SessionId { get; }

    public SessionRole Role { get; }

    public string? PeerName { get; }

    public string? PeerAddress { get; }

    public int ProtocolVersion { get; }

    public IReadOnlyList<string> Capabilities { get; }
}

public sealed class SessionFailedEventArgs : EventArgs
{
    public SessionFailedEventArgs(
        string reason,
        string message,
        string? sessionId,
        SessionRole? role,
        bool isPeerProtocolMissing)
    {
        Reason = reason;
        Message = message;
        SessionId = sessionId;
        Role = role;
        IsPeerProtocolMissing = isPeerProtocolMissing;
    }

    public string Reason { get; }

    public string Message { get; }

    public string? SessionId { get; }

    public SessionRole? Role { get; }

    public bool IsPeerProtocolMissing { get; }
}

public sealed class SessionDisconnectEventArgs : EventArgs
{
    public SessionDisconnectEventArgs(string reason, string? sessionId)
    {
        Reason = reason;
        SessionId = sessionId;
    }

    public string Reason { get; }

    public string? SessionId { get; }
}

public sealed class ProtocolFrameReceivedEventArgs : EventArgs
{
    public ProtocolFrameReceivedEventArgs(string sessionId, ProtocolFrame frame)
    {
        SessionId = sessionId;
        Frame = frame;
    }

    public string SessionId { get; }

    public ProtocolFrame Frame { get; }
}
