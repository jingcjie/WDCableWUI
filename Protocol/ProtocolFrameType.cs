namespace WDCableWUI.Protocol;

public enum ProtocolFrameType
{
    HandshakeHello = 1,
    HandshakeAck = 2,
    HeartbeatPing = 3,
    HeartbeatPong = 4,
    Close = 5,
    Error = 6,
    ControlMessage = 10,
    Ack = 11,
    BulkStart = 20,
    BulkChunk = 21,
    BulkComplete = 22,
    BulkCancel = 23,
    RealtimeStart = 30,
    RealtimeData = 31,
    RealtimeStop = 32
}

public static class ProtocolFrameTypeExtensions
{
    public static string GetProtocolName(this ProtocolFrameType type)
    {
        return type switch
        {
            ProtocolFrameType.HandshakeHello => "handshake.hello",
            ProtocolFrameType.HandshakeAck => "handshake.ack",
            ProtocolFrameType.HeartbeatPing => "heartbeat.ping",
            ProtocolFrameType.HeartbeatPong => "heartbeat.pong",
            ProtocolFrameType.Close => "close",
            ProtocolFrameType.Error => "error",
            ProtocolFrameType.ControlMessage => "control.message",
            ProtocolFrameType.Ack => "ack",
            ProtocolFrameType.BulkStart => "bulk.start",
            ProtocolFrameType.BulkChunk => "bulk.chunk",
            ProtocolFrameType.BulkComplete => "bulk.complete",
            ProtocolFrameType.BulkCancel => "bulk.cancel",
            ProtocolFrameType.RealtimeStart => "realtime.start",
            ProtocolFrameType.RealtimeData => "realtime.data",
            ProtocolFrameType.RealtimeStop => "realtime.stop",
            _ => throw new ProtocolException(ProtocolError.InvalidFrameType, $"Unknown protocol frame type id: {(int)type}")
        };
    }

    public static ProtocolFrameType FromId(int id)
    {
        return id switch
        {
            1 => ProtocolFrameType.HandshakeHello,
            2 => ProtocolFrameType.HandshakeAck,
            3 => ProtocolFrameType.HeartbeatPing,
            4 => ProtocolFrameType.HeartbeatPong,
            5 => ProtocolFrameType.Close,
            6 => ProtocolFrameType.Error,
            10 => ProtocolFrameType.ControlMessage,
            11 => ProtocolFrameType.Ack,
            20 => ProtocolFrameType.BulkStart,
            21 => ProtocolFrameType.BulkChunk,
            22 => ProtocolFrameType.BulkComplete,
            23 => ProtocolFrameType.BulkCancel,
            30 => ProtocolFrameType.RealtimeStart,
            31 => ProtocolFrameType.RealtimeData,
            32 => ProtocolFrameType.RealtimeStop,
            _ => throw new ProtocolException(ProtocolError.InvalidFrameType, $"Unknown protocol frame type id: {id}")
        };
    }
}
