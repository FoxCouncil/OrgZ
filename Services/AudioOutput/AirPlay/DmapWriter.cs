// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Buffers.Binary;
using System.Text;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// Builds DMAP ("digital media access protocol") bodies - iTunes' tagging, used both for the
/// now-playing information pushed to a receiver and for the answers a receiver's remote
/// expects back from us.
///
/// Every element is a 4-character ASCII tag, a 32-bit big-endian length, then the payload;
/// containers carry encoded elements as their payload and their length counts only what is
/// inside them. Integer widths are part of the contract rather than a choice - a reader
/// takes a 4-byte tag as four bytes whatever value it holds, so a duration written as eight
/// is not a large number, it is a corrupt message and everything after it is lost.
/// </summary>
internal sealed class DmapWriter
{
    private readonly List<byte> _bytes = new(256);

    public int Length => _bytes.Count;

    public byte[] ToArray() => [.. _bytes];

    public DmapWriter String(string tag, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return this;
        }

        var payload = Encoding.UTF8.GetBytes(value);
        Header(tag, payload.Length);
        _bytes.AddRange(payload);
        return this;
    }

    public DmapWriter Char(string tag, byte value)
    {
        Header(tag, 1);
        _bytes.Add(value);
        return this;
    }

    public DmapWriter Short(string tag, short value)
    {
        Header(tag, 2);
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buffer, value);
        _bytes.AddRange(buffer);
        return this;
    }

    public DmapWriter Int(string tag, int value)
    {
        Header(tag, 4);
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        _bytes.AddRange(buffer);
        return this;
    }

    public DmapWriter Long(string tag, long value)
    {
        Header(tag, 8);
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        _bytes.AddRange(buffer);
        return this;
    }

    /// <summary>A tag with a zero-length payload - present in the frame, carrying nothing.</summary>
    public DmapWriter Empty(string tag)
    {
        Header(tag, 0);
        return this;
    }

    /// <summary>Nests another writer's contents under a container tag.</summary>
    public DmapWriter Container(string tag, DmapWriter contents)
    {
        Header(tag, contents.Length);
        _bytes.AddRange(contents._bytes);
        return this;
    }

    /// <summary>Wraps everything written so far in one container - the outermost frame.</summary>
    public byte[] Wrap(string tag)
    {
        var message = new DmapWriter();
        message.Header(tag, _bytes.Count);
        message._bytes.AddRange(_bytes);
        return message.ToArray();
    }

    private void Header(string tag, int length)
    {
        _bytes.AddRange(Encoding.ASCII.GetBytes(tag));

        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size, length);
        _bytes.AddRange(size);
    }
}
