// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Buffers.Binary;
using System.Text;

namespace OrgZ.Tests;

/// <summary>
/// A DMAP reader that walks the WHOLE message, in order, whatever the field types are.
///
/// Written deliberately unlike the receiver rig that first caught our metadata bugs: that
/// one's parser terminates on any field it can't type, which made a numeric id look like a
/// fatal encoding error when it was really just that parser giving up. A correct reader is
/// what tells the difference between "our bytes are wrong" and "that receiver is fussy", so
/// this one types nothing it doesn't have to and never stops early.
/// </summary>
internal sealed class DmapReader
{
    private readonly List<(string Tag, byte[] Payload)> _fields = [];

    /// <summary>Container tags whose payload is itself a run of DMAP elements.</summary>
    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
    {
        // Now-playing, pushed to a receiver.
        "mlit", "mlcl", "mshl", "mlog", "msrv", "mccr", "mupd", "avdb", "abro", "adbs", "apso",

        // The answers a receiver's remote asks us for.
        "casp", "cmgt", "cmst", "mdcl", "caci", "cacr",
    };

    private DmapReader()
    {
    }

    /// <summary>Every element found, depth first, in wire order - including container tags.</summary>
    public IReadOnlyList<(string Tag, byte[] Payload)> Fields => _fields;

    /// <summary>The tags in the order they appear, which is itself part of the contract.</summary>
    public IReadOnlyList<string> Tags => [.. _fields.Select(f => f.Tag)];

    public bool Has(string tag) => _fields.Any(f => f.Tag == tag);

    /// <summary>A tag's payload as UTF-8 text, or null when the tag is absent.</summary>
    public string? Text(string tag)
    {
        foreach (var (t, payload) in _fields)
        {
            if (t == tag)
            {
                return Encoding.UTF8.GetString(payload);
            }
        }
        return null;
    }

    /// <summary>A tag's payload as a big-endian integer of whatever width it was sent at.</summary>
    public long? Number(string tag)
    {
        foreach (var (t, payload) in _fields)
        {
            if (t != tag)
            {
                continue;
            }

            long value = 0;
            foreach (var b in payload)
            {
                value = (value << 8) | b;
            }
            return value;
        }
        return null;
    }

    /// <summary>Parses a DMAP message, descending into containers. Throws on malformed framing.</summary>
    public static DmapReader Parse(ReadOnlySpan<byte> data)
    {
        var reader = new DmapReader();
        reader.Walk(data);
        return reader;
    }

    private void Walk(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset + 8 <= data.Length)
        {
            var tag = Encoding.ASCII.GetString(data.Slice(offset, 4));
            var length = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset + 4, 4));

            if (length < 0 || offset + 8 + length > data.Length)
            {
                throw new InvalidDataException($"DMAP element '{tag}' declares {length} bytes but only {data.Length - offset - 8} remain.");
            }

            var payload = data.Slice(offset + 8, length).ToArray();
            _fields.Add((tag, payload));

            if (Containers.Contains(tag))
            {
                Walk(payload);
            }

            offset += 8 + length;
        }

        if (offset != data.Length)
        {
            throw new InvalidDataException($"DMAP message has {data.Length - offset} trailing bytes that are not an element.");
        }
    }
}
