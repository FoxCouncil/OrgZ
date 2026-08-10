// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Buffers.Binary;
using System.Text;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// Apple binary property lists (<c>bplist00</c>) - the body format of AirPlay 2's
/// <c>/info</c> and stream <c>SETUP</c>. .NET has no built-in, and AirPlay 2 abandoned the
/// header-based transport RAOP used, so a sender can't negotiate ports without this.
///
/// Scoped to the types AirPlay actually exchanges: dictionaries, arrays, strings, data,
/// integers, booleans and reals. Anything else throws rather than emitting a plist the
/// receiver will silently misread.
/// </summary>
internal static class BinaryPlist
{
    private static readonly byte[] Magic = "bplist00"u8.ToArray();

    // ── Writing ──────────────────────────────────────────────

    public static byte[] Write(object? root)
    {
        // Flatten first: the format addresses objects by index, so every node needs an id
        // before any of them can be encoded.
        var objects = new List<object?>();
        Flatten(root, objects);

        var refSize = ByteWidth(objects.Count);
        var body = new MemoryStream();
        body.Write(Magic);

        var offsets = new long[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = body.Position;
            WriteObject(body, objects[i], objects, refSize);
        }

        var offsetTableStart = body.Position;
        var offsetSize = ByteWidth(offsetTableStart);
        foreach (var offset in offsets)
        {
            WriteSized(body, (ulong)offset, offsetSize);
        }

        // Trailer: 5 unused + sort version + offset width + ref width + counts.
        var trailer = new byte[32];
        trailer[6] = (byte)offsetSize;
        trailer[7] = (byte)refSize;
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(8), (ulong)objects.Count);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(16), 0);   // root is object 0
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(24), (ulong)offsetTableStart);
        body.Write(trailer);

        return body.ToArray();
    }

    /// <summary>Depth-first walk assigning object ids; the root always lands at index 0.</summary>
    private static void Flatten(object? value, List<object?> objects)
    {
        objects.Add(value);

        switch (value)
        {
            case IDictionary<string, object?> dict:
            {
                foreach (var key in dict.Keys)
                {
                    Flatten(key, objects);
                }
                foreach (var item in dict.Values)
                {
                    Flatten(item, objects);
                }
            }
            break;

            case IReadOnlyList<object?> list:
            {
                foreach (var item in list)
                {
                    Flatten(item, objects);
                }
            }
            break;
        }
    }

    private static void WriteObject(Stream output, object? value, List<object?> objects, int refSize)
    {
        switch (value)
        {
            case null:
            {
                output.WriteByte(0x00);
            }
            break;

            case bool b:
            {
                output.WriteByte(b ? (byte)0x09 : (byte)0x08);
            }
            break;

            case byte[] data:
            {
                WriteMarkerAndLength(output, 0x40, data.Length);
                output.Write(data);
            }
            break;

            case string text:
            {
                if (text.All(char.IsAscii))
                {
                    WriteMarkerAndLength(output, 0x50, text.Length);
                    output.Write(Encoding.ASCII.GetBytes(text));
                }
                else
                {
                    // Non-ASCII goes out as UTF-16BE, with the length in CHARACTERS.
                    var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
                    WriteMarkerAndLength(output, 0x60, utf16.Length / 2);
                    output.Write(utf16);
                }
            }
            break;

            case double real:
            {
                output.WriteByte(0x23);
                var bytes = new byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(bytes, real);
                output.Write(bytes);
            }
            break;

            case sbyte or short or int or long or byte or ushort or uint or ulong:
            {
                WriteInteger(output, Convert.ToInt64(value));
            }
            break;

            case IDictionary<string, object?> dict:
            {
                WriteMarkerAndLength(output, 0xD0, dict.Count);
                foreach (var key in dict.Keys)
                {
                    WriteSized(output, (ulong)IndexOfSame(objects, key), refSize);
                }
                foreach (var item in dict.Values)
                {
                    WriteSized(output, (ulong)IndexOfSame(objects, item), refSize);
                }
            }
            break;

            case IReadOnlyList<object?> list:
            {
                WriteMarkerAndLength(output, 0xA0, list.Count);
                foreach (var item in list)
                {
                    WriteSized(output, (ulong)IndexOfSame(objects, item), refSize);
                }
            }
            break;

            default:
            {
                throw new NotSupportedException($"BinaryPlist can't write {value.GetType().Name}.");
            }
        }
    }

    private static void WriteInteger(Stream output, long value)
    {
        // Widths are powers of two; negatives always take the full 8 bytes so the sign survives.
        if (value is >= 0 and <= byte.MaxValue)
        {
            output.WriteByte(0x10);
            output.WriteByte((byte)value);
        }
        else if (value is >= 0 and <= ushort.MaxValue)
        {
            output.WriteByte(0x11);
            var b = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)value);
            output.Write(b);
        }
        else if (value is >= 0 and <= uint.MaxValue)
        {
            output.WriteByte(0x12);
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)value);
            output.Write(b);
        }
        else
        {
            output.WriteByte(0x13);
            var b = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(b, value);
            output.Write(b);
        }
    }

    /// <summary>Marker nibble + length, spilling to a follow-on integer past 14.</summary>
    private static void WriteMarkerAndLength(Stream output, byte marker, int length)
    {
        if (length < 15)
        {
            output.WriteByte((byte)(marker | length));
            return;
        }

        output.WriteByte((byte)(marker | 0x0F));
        WriteInteger(output, length);
    }

    private static void WriteSized(Stream output, ulong value, int size)
    {
        for (var shift = (size - 1) * 8; shift >= 0; shift -= 8)
        {
            output.WriteByte((byte)((value >> shift) & 0xFF));
        }
    }

    /// <summary>
    /// Reference equality for containers, value equality for scalars - two equal strings
    /// were flattened as separate objects, so index lookup must match the same way.
    /// </summary>
    private static int IndexOfSame(List<object?> objects, object? value)
    {
        for (var i = 0; i < objects.Count; i++)
        {
            if (ReferenceEquals(objects[i], value) || (objects[i] is not null && objects[i]!.Equals(value)))
            {
                return i;
            }
        }
        return 0;
    }

    private static int ByteWidth(long count) => count switch
    {
        <= byte.MaxValue => 1,
        <= ushort.MaxValue => 2,
        <= uint.MaxValue => 4,
        _ => 8,
    };

    // ── Reading ──────────────────────────────────────────────

    /// <summary>Parses a bplist. Used on SETUP replies, which carry the receiver's ports.</summary>
    public static object? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 40 || !data[..8].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a binary plist.");
        }

        var trailer = data[^32..];
        int offsetSize = trailer[6];
        int refSize = trailer[7];
        var count = (int)BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
        var rootIndex = (int)BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
        var tableStart = (int)BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);

        var offsets = new int[count];
        for (var i = 0; i < count; i++)
        {
            offsets[i] = (int)ReadSized(data[(tableStart + (i * offsetSize))..], offsetSize);
        }

        return ReadObject(data, offsets, refSize, rootIndex);
    }

    private static object? ReadObject(ReadOnlySpan<byte> data, int[] offsets, int refSize, int index)
    {
        if (index < 0 || index >= offsets.Length)
        {
            return null;
        }

        var pos = offsets[index];
        var marker = data[pos];
        var type = marker & 0xF0;
        var length = marker & 0x0F;

        switch (type)
        {
            case 0x00:
            {
                return marker switch { 0x08 => false, 0x09 => true, _ => null };
            }

            case 0x10:
            {
                var width = 1 << length;
                return (long)ReadSized(data[(pos + 1)..], width);
            }

            case 0x20:
            {
                return BinaryPrimitives.ReadDoubleBigEndian(data[(pos + 1)..]);
            }

            case 0x40:
            {
                var (len, start) = ReadLength(data, pos, length);
                return data.Slice(start, len).ToArray();
            }

            case 0x50:
            {
                var (len, start) = ReadLength(data, pos, length);
                return Encoding.ASCII.GetString(data.Slice(start, len));
            }

            case 0x60:
            {
                var (len, start) = ReadLength(data, pos, length);
                return Encoding.BigEndianUnicode.GetString(data.Slice(start, len * 2));
            }

            case 0xA0:
            {
                var (len, start) = ReadLength(data, pos, length);
                var array = new List<object?>(len);
                for (var i = 0; i < len; i++)
                {
                    array.Add(ReadObject(data, offsets, refSize, (int)ReadSized(data[(start + (i * refSize))..], refSize)));
                }
                return array;
            }

            case 0xD0:
            {
                var (len, start) = ReadLength(data, pos, length);
                var dict = new Dictionary<string, object?>();
                for (var i = 0; i < len; i++)
                {
                    var keyIndex = (int)ReadSized(data[(start + (i * refSize))..], refSize);
                    var valueIndex = (int)ReadSized(data[(start + ((len + i) * refSize))..], refSize);
                    if (ReadObject(data, offsets, refSize, keyIndex) is string key)
                    {
                        dict[key] = ReadObject(data, offsets, refSize, valueIndex);
                    }
                }
                return dict;
            }
        }

        return null;
    }

    /// <summary>Resolves a marker's length, following the spill integer when the nibble is 0xF.</summary>
    private static (int Length, int Start) ReadLength(ReadOnlySpan<byte> data, int pos, int nibble)
    {
        if (nibble != 0x0F)
        {
            return (nibble, pos + 1);
        }

        var intMarker = data[pos + 1];
        var width = 1 << (intMarker & 0x0F);
        var length = (int)ReadSized(data[(pos + 2)..], width);
        return (length, pos + 2 + width);
    }

    private static ulong ReadSized(ReadOnlySpan<byte> data, int size)
    {
        ulong value = 0;
        for (var i = 0; i < size; i++)
        {
            value = (value << 8) | data[i];
        }
        return value;
    }
}
