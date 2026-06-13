using System;
using System.Collections.Generic;

namespace WDCableWUI.Services;

public sealed class SessionStateMachine
{
    private static readonly IReadOnlyDictionary<SessionPhase, SessionPhase[]> AllowedTransitions =
        new Dictionary<SessionPhase, SessionPhase[]>
        {
            [SessionPhase.Disconnected] =
            [
                SessionPhase.WifiDirectConnected,
                SessionPhase.ConnectingTransport,
                SessionPhase.Disconnected
            ],
            [SessionPhase.WifiDirectConnected] =
            [
                SessionPhase.ConnectingTransport,
                SessionPhase.Disconnecting,
                SessionPhase.Disconnected,
                SessionPhase.Failed
            ],
            [SessionPhase.ConnectingTransport] =
            [
                SessionPhase.Handshaking,
                SessionPhase.Disconnecting,
                SessionPhase.Disconnected,
                SessionPhase.Failed
            ],
            [SessionPhase.Handshaking] =
            [
                SessionPhase.Ready,
                SessionPhase.Disconnecting,
                SessionPhase.Disconnected,
                SessionPhase.Failed
            ],
            [SessionPhase.Ready] =
            [
                SessionPhase.Degraded,
                SessionPhase.Disconnecting,
                SessionPhase.Disconnected,
                SessionPhase.Failed
            ],
            [SessionPhase.Degraded] =
            [
                SessionPhase.Ready,
                SessionPhase.Disconnecting,
                SessionPhase.Disconnected,
                SessionPhase.Failed
            ],
            [SessionPhase.Disconnecting] =
            [
                SessionPhase.Disconnected,
                SessionPhase.Failed
            ],
            [SessionPhase.Failed] =
            [
                SessionPhase.Disconnecting,
                SessionPhase.Disconnected,
                SessionPhase.WifiDirectConnected
            ]
        };

    public SessionPhase Phase { get; private set; } = SessionPhase.Disconnected;

    public void TransitionTo(SessionPhase next)
    {
        if (next == Phase)
        {
            return;
        }

        if (!AllowedTransitions.TryGetValue(Phase, out var allowed) || Array.IndexOf(allowed, next) < 0)
        {
            throw new InvalidOperationException($"Cannot transition session from {Phase} to {next}.");
        }

        Phase = next;
    }

    public void Reset(SessionPhase phase = SessionPhase.Disconnected)
    {
        Phase = phase;
    }
}
