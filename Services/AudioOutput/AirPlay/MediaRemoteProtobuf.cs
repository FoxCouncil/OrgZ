// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// Just enough protobuf to build MediaRemote messages - varints, strings and nested
/// messages, written by hand.
///
/// There is no schema and no generated code here on purpose: the only protobuf this app
/// ever emits is one fixed DEVICE_INFO message, and pulling in a protobuf toolchain plus
/// Apple's unpublished .proto files to encode ~20 constant fields would be a dependency in
/// exchange for nothing. Field numbers come from pyatv's reverse-engineered definitions.
/// </summary>
internal static class MediaRemoteProtobuf
{
    /// <summary>Wire type 0 - varint.</summary>
    private const int WireVarint = 0;

    /// <summary>Wire type 2 - length-delimited (strings, bytes, nested messages).</summary>
    private const int WireLengthDelimited = 2;

    public static void WriteVarint(List<byte> output, ulong value)
    {
        while (value >= 0x80)
        {
            output.Add((byte)(value | 0x80));
            value >>= 7;
        }

        output.Add((byte)value);
    }

    public static void WriteVarintField(List<byte> output, int field, ulong value)
    {
        WriteVarint(output, Tag(field, WireVarint));
        WriteVarint(output, value);
    }

    public static void WriteBoolField(List<byte> output, int field, bool value)
        => WriteVarintField(output, field, value ? 1UL : 0UL);

    public static void WriteStringField(List<byte> output, int field, string? value)
    {
        if (value is null)
        {
            return;
        }

        WriteBytesField(output, field, Encoding.UTF8.GetBytes(value));
    }

    public static void WriteBytesField(List<byte> output, int field, IReadOnlyCollection<byte> value)
    {
        WriteVarint(output, Tag(field, WireLengthDelimited));
        WriteVarint(output, (ulong)value.Count);
        output.AddRange(value);
    }

    /// <summary>Length-prefixes a message the way the MediaRemote channel frames one.</summary>
    public static byte[] LengthPrefixed(IReadOnlyCollection<byte> message)
    {
        var framed = new List<byte>(message.Count + 4);
        WriteVarint(framed, (ulong)message.Count);
        framed.AddRange(message);
        return framed.ToArray();
    }

    private static ulong Tag(int field, int wireType) => ((ulong)field << 3) | (uint)wireType;
}
