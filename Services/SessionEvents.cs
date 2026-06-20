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
        SessionTransportRole? transportRole,
        string? peerName,
        string? peerAddress,
        string? disconnectReason)
    {
        Phase = phase;
        SessionId = sessionId;
        Role = role;
        TransportRole = transportRole;
        PeerName = peerName;
        PeerAddress = peerAddress;
        DisconnectReason = disconnectReason;
    }

    public SessionPhase Phase { get; }

    public string StateName => Phase.GetEventName();

    public string? SessionId { get; }

    public SessionRole? Role { get; }

    public SessionTransportRole? TransportRole { get; }

    public string? PeerName { get; }

    public string? PeerAddress { get; }

    public string? DisconnectReason { get; }
}

public sealed class SessionReadyEventArgs : EventArgs
{
    public SessionReadyEventArgs(
        string sessionId,
        SessionRole role,
        SessionTransportRole transportRole,
        string? peerName,
        string? peerAddress,
        int protocolVersion,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string>? peerCapabilities = null)
    {
        SessionId = sessionId;
        Role = role;
        TransportRole = transportRole;
        PeerName = peerName;
        PeerAddress = peerAddress;
        ProtocolVersion = protocolVersion;
        Capabilities = capabilities;
        PeerCapabilities = peerCapabilities ?? [];
    }

    public string SessionId { get; }

    public SessionRole Role { get; }

    public SessionTransportRole TransportRole { get; }

    public string? PeerName { get; }

    public string? PeerAddress { get; }

    public int ProtocolVersion { get; }

    public IReadOnlyList<string> Capabilities { get; }

    public IReadOnlyList<string> PeerCapabilities { get; }
}

public sealed class SessionFailedEventArgs : EventArgs
{
    public SessionFailedEventArgs(
        string reason,
        string message,
        string? sessionId,
        SessionRole? role,
        SessionTransportRole? transportRole,
        bool isPeerProtocolMissing)
    {
        Reason = reason;
        Message = message;
        SessionId = sessionId;
        Role = role;
        TransportRole = transportRole;
        IsPeerProtocolMissing = isPeerProtocolMissing;
    }

    public string Reason { get; }

    public string Message { get; }

    public string? SessionId { get; }

    public SessionRole? Role { get; }

    public SessionTransportRole? TransportRole { get; }

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

public sealed class AudioSessionInfo
{
    public AudioSessionInfo(
        string sessionId,
        SessionRole role,
        string? peerAddress,
        IReadOnlyList<string> peerCapabilities)
    {
        SessionId = sessionId;
        Role = role;
        PeerAddress = peerAddress;
        PeerCapabilities = peerCapabilities;
    }

    public string SessionId { get; }

    public SessionRole Role { get; }

    public string? PeerAddress { get; }

    public IReadOnlyList<string> PeerCapabilities { get; }
}

public sealed class AudioTransportEventArgs : EventArgs
{
    public AudioTransportEventArgs(string sessionId, long streamId)
    {
        SessionId = sessionId;
        StreamId = streamId;
    }

    public string SessionId { get; }

    public long StreamId { get; }
}

public sealed class AudioTransportClosedEventArgs : EventArgs
{
    public AudioTransportClosedEventArgs(string sessionId, long streamId, string reason)
    {
        SessionId = sessionId;
        StreamId = streamId;
        Reason = reason;
    }

    public string SessionId { get; }

    public long StreamId { get; }

    public string Reason { get; }
}
