using System;
using System.Buffers.Binary;

namespace WDCableWUI.Services;

public abstract record RtcpPacket(uint Ssrc);

public sealed record RtcpSenderReport(
    uint Ssrc,
    ulong NtpTimestamp,
    uint RtpTimestamp,
    uint PacketCount,
    uint OctetCount) : RtcpPacket(Ssrc);

public sealed record RtcpReceiverReport(
    uint Ssrc,
    uint ReportedSsrc,
    byte FractionLost,
    int CumulativePacketsLost,
    uint HighestSequenceReceived,
    uint InterarrivalJitter,
    uint LastSenderReport,
    uint DelaySinceLastSenderReport) : RtcpPacket(Ssrc);

public static class RtcpProtocol
{
    public const byte SenderReportPacketType = 200;
    public const byte ReceiverReportPacketType = 201;

    private static readonly DateTimeOffset NtpEpoch = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static byte[] EncodeSenderReport(RtcpSenderReport report)
    {
        var buffer = new byte[28];
        buffer[0] = 0x80;
        buffer[1] = SenderReportPacketType;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), 6);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4, 4), report.Ssrc);
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(8, 8), report.NtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(16, 4), report.RtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(20, 4), report.PacketCount);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(24, 4), report.OctetCount);
        return buffer;
    }

    public static byte[] EncodeReceiverReport(RtcpReceiverReport report)
    {
        var buffer = new byte[32];
        buffer[0] = 0x81;
        buffer[1] = ReceiverReportPacketType;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), 7);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4, 4), report.Ssrc);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8, 4), report.ReportedSsrc);
        buffer[12] = report.FractionLost;
        WriteSigned24(buffer.AsSpan(13, 3), report.CumulativePacketsLost);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(16, 4), report.HighestSequenceReceived);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(20, 4), report.InterarrivalJitter);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(24, 4), report.LastSenderReport);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(28, 4), report.DelaySinceLastSenderReport);
        return buffer;
    }

    public static bool TryDecode(ReadOnlySpan<byte> buffer, out RtcpPacket? packet)
    {
        packet = null;
        if (buffer.Length < 8 || (buffer[0] >> 6) != 2)
        {
            return false;
        }

        var reportCount = buffer[0] & 0x1f;
        var packetType = buffer[1];
        var lengthWords = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2));
        var packetLength = (lengthWords + 1) * 4;
        if (buffer.Length < packetLength)
        {
            return false;
        }

        switch (packetType)
        {
            case SenderReportPacketType when packetLength >= 28:
                packet = new RtcpSenderReport(
                    BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4)),
                    BinaryPrimitives.ReadUInt64BigEndian(buffer.Slice(8, 8)),
                    BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(16, 4)),
                    BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(20, 4)),
                    BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(24, 4)));
                return true;
            case ReceiverReportPacketType when reportCount > 0 && packetLength >= 32:
                packet = new RtcpReceiverReport(
                    BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4)),
                    BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(8, 4)),
                    buffer[12],
                    ReadSigned24(buffer.Slice(13, 3)),
                    BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(16, 4)),
                    BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(20, 4)),
                    BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(24, 4)),
                    BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(28, 4)));
                return true;
            default:
                return false;
        }
    }

    public static ulong NtpTimestampNow()
    {
        var elapsed = DateTimeOffset.UtcNow - NtpEpoch;
        var seconds = (ulong)elapsed.TotalSeconds;
        var fraction = (ulong)((ulong)(elapsed.Ticks % TimeSpan.TicksPerSecond) * 0x100000000UL / (ulong)TimeSpan.TicksPerSecond);
        return (seconds << 32) | fraction;
    }

    public static uint CompactNtp(ulong ntpTimestamp)
    {
        return (uint)(((ntpTimestamp >> 16) & 0xffff0000UL) | ((ntpTimestamp >> 16) & 0x0000ffffUL));
    }

    public static uint CompactNtpNow()
    {
        return CompactNtp(NtpTimestampNow());
    }

    public static long CompactNtpToMilliseconds(uint compact)
    {
        return (long)(compact * 1000.0 / 65536.0);
    }

    public static uint DelaySince(uint compactNtpAtReceive)
    {
        return unchecked(CompactNtpNow() - compactNtpAtReceive);
    }

    private static void WriteSigned24(Span<byte> buffer, int value)
    {
        var clamped = Math.Clamp(value, -0x800000, 0x7fffff);
        var encoded = clamped < 0 ? (1 << 24) + clamped : clamped;
        buffer[0] = (byte)((encoded >> 16) & 0xff);
        buffer[1] = (byte)((encoded >> 8) & 0xff);
        buffer[2] = (byte)(encoded & 0xff);
    }

    private static int ReadSigned24(ReadOnlySpan<byte> buffer)
    {
        var value = (buffer[0] << 16) | (buffer[1] << 8) | buffer[2];
        return (value & 0x800000) != 0 ? value - 0x1000000 : value;
    }
}
