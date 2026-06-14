namespace WDCableWUI.Protocol;

public enum ProtocolChannel
{
    Control = 1,
    Bulk = 2,
    Audio = 3
}

public static class ProtocolChannelExtensions
{
    public static string GetProtocolName(this ProtocolChannel channel)
    {
        return channel switch
        {
            ProtocolChannel.Control => "control",
            ProtocolChannel.Bulk => "bulk",
            ProtocolChannel.Audio => "audio",
            _ => throw new ProtocolException(ProtocolError.InvalidChannel, $"Unknown protocol channel id: {(int)channel}")
        };
    }

    public static ProtocolChannel FromId(int id)
    {
        return id switch
        {
            1 => ProtocolChannel.Control,
            2 => ProtocolChannel.Bulk,
            3 => ProtocolChannel.Audio,
            _ => throw new ProtocolException(ProtocolError.InvalidChannel, $"Unknown protocol channel id: {id}")
        };
    }
}
