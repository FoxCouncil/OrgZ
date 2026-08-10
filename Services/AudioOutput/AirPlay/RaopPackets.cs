// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Buffers.Binary;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// The RTP-shaped packets a RAOP sender puts on the wire. All three ports carry
/// RTP-like framing with RAOP-specific payload types:
///   audio (data port)  : type 96, the encrypted ALAC frame
///   sync  (control port): type 84, NTP now + the RTP timestamp that audio corresponds to
///   timing reply       : type 83, the receiver's clock query answered with three NTP stamps
/// Every multi-byte field is big-endian, as RTP requires.
/// </summary>
internal static class RaopPackets
{
    public const byte PayloadAudio = 0x60;        // 96
    public const byte PayloadSync = 0x54;         // 84
    public const byte PayloadTimingRequest = 0x52;  // 82
    public const byte PayloadTimingReply = 0x53;    // 83

    private const byte MarkerBit = 0x80;

    /// <summary>NTP epoch (1900-01-01) - what the AirPlay clock counts from.</summary>
    private static readonly DateTime NtpEpoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Now as a 64-bit NTP timestamp: seconds since 1900 in the high half, fraction in the low.</summary>
    public static ulong NtpNow() => ToNtp(DateTime.UtcNow);

    public static ulong ToNtp(DateTime utc)
    {
        var span = utc - NtpEpoch;
        var seconds = (ulong)span.TotalSeconds;
        var fraction = (ulong)((span.TotalSeconds - seconds) * 0x100000000UL);
        return (seconds << 32) | (fraction & 0xFFFFFFFF);
    }

    /// <summary>An audio packet: 12-byte RTP header then the (already encrypted) ALAC payload.</summary>
    public static byte[] BuildAudio(ushort sequence, uint timestamp, uint ssrc, ReadOnlySpan<byte> payload, bool first)
    {
        var packet = new byte[12 + payload.Length];
        packet[0] = 0x80;
        // The marker bit on the first packet tells the receiver the stream starts here.
        packet[1] = (byte)(PayloadAudio | (first ? MarkerBit : 0));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ssrc);
        payload.CopyTo(packet.AsSpan(12));
        return packet;
    }

    /// <summary>
    /// A sync packet for the control port: ties an NTP instant to the RTP timestamp that
    /// will be playing then, which is how the receiver keeps its clock aligned. Sent once
    /// at stream start (with the marker bit) and roughly once a second after.
    /// </summary>
    public static byte[] BuildSync(uint nowTimestamp, ulong ntpNow, uint nextTimestamp, bool first)
    {
        var packet = new byte[20];
        packet[0] = (byte)(0x80 | (first ? 0x10 : 0));
        packet[1] = PayloadSync | MarkerBit;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 7);   // fixed sequence, per the protocol
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), nowTimestamp);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), ntpNow);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), nextTimestamp);
        return packet;
    }

    /// <summary>
    /// Answers a receiver's timing query (type 82). The reply echoes the query's transmit
    /// stamp as "origin", then our receive and transmit stamps - a three-stamp exchange the
    /// receiver uses to work out the offset between the two clocks.
    /// </summary>
    public static byte[] BuildTimingReply(ReadOnlySpan<byte> request, ulong receivedNtp, ulong transmitNtp)
    {
        var packet = new byte[32];
        packet[0] = 0x80;
        packet[1] = PayloadTimingReply | MarkerBit;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 7);
        // bytes 4-7 stay zero (the "dummy" word); the query's transmit stamp is its last 8.
        var origin = request.Length >= 32 ? BinaryPrimitives.ReadUInt64BigEndian(request[24..]) : 0UL;
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), origin);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(16), receivedNtp);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(24), transmitNtp);
        return packet;
    }

    /// <summary>True when a control-port datagram is the receiver asking for our clock.</summary>
    public static bool IsTimingRequest(ReadOnlySpan<byte> packet)
        => packet.Length >= 2 && (packet[1] & 0x7F) == PayloadTimingRequest;
}
