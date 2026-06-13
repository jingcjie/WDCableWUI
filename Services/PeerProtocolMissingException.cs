using System;
using System.IO;

namespace WDCableWUI.Services;

public sealed class PeerProtocolMissingException : IOException
{
    public PeerProtocolMissingException(string message)
        : base(message)
    {
    }

    public PeerProtocolMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
