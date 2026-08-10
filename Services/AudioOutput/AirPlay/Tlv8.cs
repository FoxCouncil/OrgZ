// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// HomeKit's TLV8 encoding, the body format of every <c>/pair-setup</c> exchange.
///
/// One quirk drives the whole implementation: a value longer than 255 bytes is split
/// into consecutive fragments that share a type, and readers must concatenate them.
/// SRP's public keys are 384 bytes, so this is the normal case here, not an edge case.
/// </summary>
internal static class Tlv8
{
    // The subset of HAP TLV types the pairing exchange uses.
    public const byte Method = 0x00;
    public const byte Identifier = 0x01;
    public const byte Salt = 0x02;
    public const byte PublicKey = 0x03;
    public const byte Proof = 0x04;
    public const byte EncryptedData = 0x05;
    public const byte State = 0x06;
    public const byte Error = 0x07;
    public const byte Signature = 0x0A;
    public const byte Flags = 0x13;

    private const int MaxFragment = 255;

    public static byte[] Encode(params (byte Type, byte[] Value)[] entries)
    {
        var output = new List<byte>();
        foreach (var (type, value) in entries)
        {
            if (value.Length == 0)
            {
                output.Add(type);
                output.Add(0);
                continue;
            }

            var offset = 0;
            while (offset < value.Length)
            {
                var take = Math.Min(MaxFragment, value.Length - offset);
                output.Add(type);
                output.Add((byte)take);
                output.AddRange(value.Skip(offset).Take(take));
                offset += take;
            }
        }

        return [.. output];
    }

    /// <summary>Parses TLV8, joining fragmented values back together in order.</summary>
    public static Dictionary<byte, byte[]> Decode(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<byte, byte[]>();
        var parts = new Dictionary<byte, List<byte>>();

        var offset = 0;
        while (offset + 2 <= data.Length)
        {
            var type = data[offset];
            var length = data[offset + 1];
            offset += 2;

            if (offset + length > data.Length)
            {
                break;   // truncated - keep what parsed rather than throwing at a peer's mistake
            }

            if (!parts.TryGetValue(type, out var accumulated))
            {
                accumulated = [];
                parts[type] = accumulated;
            }

            accumulated.AddRange(data[offset..(offset + length)].ToArray());
            offset += length;
        }

        foreach (var (type, value) in parts)
        {
            result[type] = [.. value];
        }

        return result;
    }
}
