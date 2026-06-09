// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

/// <summary>
/// One node in the iTunesDB binary tree. Either a container (holds child chunks),
/// a leaf (holds an opaque <see cref="Body"/> payload, e.g. an MHOD's string), or
/// a list header (mhlt/mhlp/mhla - header only; see the size-field quirk below).
/// The raw <see cref="Header"/> bytes are preserved verbatim so anything we don't
/// model (smart-playlist rules, sort orders, unknown fields) survives a round trip.
/// </summary>
public sealed class ITunesDbChunk
{
    public string Magic { get; set; } = "";
    public byte[] Header { get; set; } = [];       // exactly headerSize bytes (incl. magic + size fields)
    public List<ITunesDbChunk> Children { get; } = [];
    public byte[] Body { get; set; } = [];         // opaque payload for leaf chunks

    /// <summary>
    /// mhlt/mhlp/mhla are "list headers": offset 8 is an item *count*, not a total
    /// size, and the items follow as siblings of the list header inside the parent
    /// MHSD rather than nested inside it.
    /// </summary>
    public bool IsListHeader => Magic is "mhlt" or "mhlp" or "mhla" or "mhli" or "mhlf";

    public bool IsContainer => Children.Count > 0;
    public int HeaderSize => Header.Length;

    /// <summary>Total bytes this chunk occupies in the serialized file.</summary>
    public int ConsumedSize =>
        IsListHeader ? HeaderSize
        : IsContainer ? HeaderSize + Children.Sum(c => c.ConsumedSize)
        : HeaderSize + Body.Length;

    public int ReadHeaderInt32(int offset) => ITunesDbChunkTree.ReadInt32(Header, offset);
    public void WriteHeaderInt32(int offset, int value) => ITunesDbChunkTree.WriteInt32(Header, offset, value);
}

/// <summary>
/// A parsed iTunesDB: the root MHBD chunk plus any trailing bytes after it
/// (normally none - MHBD.totalSize spans the whole file).
/// </summary>
public sealed class ITunesDbDocument
{
    public required ITunesDbChunk Root { get; init; }
    public byte[] Tail { get; init; } = [];
}

/// <summary>
/// Faithful round-trip parser/serializer for Apple's iTunesDB
/// (iPod_Control/iTunes/iTunesDB on stock-firmware iPods). Read -> Serialize is
/// byte-identical; mutators add chunks and <see cref="Normalize"/> recomputes the
/// size/count header fields bottom-up before serializing. Complements the
/// field-level <see cref="ITunesDbReader"/>.
///
/// Reference: https://www.ipodlinux.org/ITunesDB/
/// </summary>
public static class ITunesDbChunkTree
{
    public static ITunesDbDocument Parse(byte[] bytes)
    {
        // mhbd = iTunesDB root, mhfd = ArtworkDB root. Same chunk grammar otherwise.
        var rootMagic = bytes.Length >= 4 ? Ascii4(bytes, 0) : "";
        if (bytes.Length < 12 || (rootMagic != "mhbd" && rootMagic != "mhfd"))
        {
            throw new InvalidDataException("Not an iTunesDB/ArtworkDB (expected 'mhbd' or 'mhfd' root header).");
        }

        int pos = 0;
        var root = ParseChunk(bytes, ref pos);
        var tail = pos < bytes.Length ? bytes[pos..] : [];
        return new ITunesDbDocument { Root = root, Tail = tail };
    }

    public static byte[] Serialize(ITunesDbDocument doc)
    {
        using var ms = new MemoryStream();
        WriteChunk(ms, doc.Root);
        if (doc.Tail.Length > 0)
        {
            ms.Write(doc.Tail, 0, doc.Tail.Length);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Recomputes the count and total-size header fields from the current tree
    /// shape, bottom-up. A no-op on a freshly-parsed, unmutated, well-formed DB
    /// (it writes back the same values); after a mutation it fixes up every size
    /// and count the firmware reads. Leaves list-header item counts derived from
    /// the parent MHSD's item children, so adding/removing items just works.
    /// </summary>
    public static void Normalize(ITunesDbChunk c)
    {
        foreach (var child in c.Children)
        {
            Normalize(child);
        }

        switch (c.Magic)
        {
            case "mhsd":
                // First child is the list header (mhlt/mhlp/mhla); the rest are its
                // items. Item count lives in the list header at offset 8.
                if (c.Children.Count > 0 && c.Children[0].IsListHeader)
                {
                    c.Children[0].WriteHeaderInt32(8, c.Children.Count - 1);
                }
                break;

            case "mhit":
                c.WriteHeaderInt32(12, c.Children.Count);   // MHOD count
                break;

            case "mhyp":
                c.WriteHeaderInt32(12, c.Children.Count(k => k.Magic == "mhod"));   // MHOD count
                c.WriteHeaderInt32(16, c.Children.Count(k => k.Magic == "mhip"));   // MHIP count
                break;

            case "mhip":
                c.WriteHeaderInt32(12, c.Children.Count(k => k.Magic == "mhod"));   // MHOD count
                break;

            // --- ArtworkDB chunks ---
            case "mhfd":
                c.WriteHeaderInt32(0x14, c.Children.Count);   // number of MHSDs
                break;
            case "mhii":
                c.WriteHeaderInt32(0x0C, c.Children.Count);   // number of MHODs
                break;
            case "mhni":
                c.WriteHeaderInt32(0x0C, c.Children.Count);   // number of child MHODs
                break;
        }

        // List headers carry a count (not a size) at offset 8 - set above via the
        // parent MHSD. Everything else stores its total size there.
        if (!c.IsListHeader)
        {
            c.WriteHeaderInt32(8, c.ConsumedSize);
        }
    }

    private static ITunesDbChunk ParseChunk(byte[] b, ref int pos)
    {
        int start = pos;
        string magic = Ascii4(b, start);
        int headerSize = ReadInt32(b, start + 4);

        if (headerSize < 12 || start + headerSize > b.Length)
        {
            throw new InvalidDataException($"Bad header size {headerSize} for '{magic}' at offset {start}.");
        }

        var chunk = new ITunesDbChunk
        {
            Magic = magic,
            Header = b[start..(start + headerSize)],
        };

        // List headers have no total-size field and no enclosed children - the
        // items follow as siblings, parsed by the parent's child loop.
        if (chunk.IsListHeader)
        {
            pos = start + headerSize;
            return chunk;
        }

        int totalSize = ReadInt32(b, start + 8);
        if (totalSize < headerSize || start + totalSize > b.Length)
        {
            throw new InvalidDataException($"Bad total size {totalSize} for '{magic}' at offset {start}.");
        }

        int childStart = start + headerSize;
        int childEnd = start + totalSize;

        // A region beginning with a chunk magic that tiles cleanly is treated as
        // child chunks; anything else (an MHOD string descriptor, smart-playlist
        // rule data, ...) is preserved as an opaque body.
        if (childEnd - childStart >= 12
            && LooksLikeChunk(b, childStart)
            && TryParseChildren(b, childStart, childEnd, out var children))
        {
            chunk.Children.AddRange(children);
        }
        else
        {
            chunk.Body = b[childStart..childEnd];
        }

        pos = childEnd;
        return chunk;
    }

    private static bool TryParseChildren(byte[] b, int start, int end, out List<ITunesDbChunk> children)
    {
        children = [];
        try
        {
            int pos = start;
            while (pos < end)
            {
                if (!LooksLikeChunk(b, pos))
                {
                    children = [];
                    return false;
                }
                children.Add(ParseChunk(b, ref pos));
            }
            if (pos != end)
            {
                children = [];
                return false;
            }
            return true;
        }
        catch (InvalidDataException)
        {
            // Misidentified body that merely looked like a chunk - fall back to opaque.
            children = [];
            return false;
        }
    }

    private static void WriteChunk(Stream s, ITunesDbChunk c)
    {
        s.Write(c.Header, 0, c.Header.Length);
        if (c.IsListHeader)
        {
            return;
        }
        if (c.IsContainer)
        {
            foreach (var child in c.Children)
            {
                WriteChunk(s, child);
            }
        }
        else
        {
            s.Write(c.Body, 0, c.Body.Length);
        }
    }

    private static bool LooksLikeChunk(byte[] b, int pos)
    {
        if (pos + 8 > b.Length)
        {
            return false;
        }
        // All iTunesDB chunk magics are "mh" + two lowercase letters.
        return b[pos] == (byte)'m'
            && b[pos + 1] == (byte)'h'
            && b[pos + 2] is >= (byte)'a' and <= (byte)'z'
            && b[pos + 3] is >= (byte)'a' and <= (byte)'z';
    }

    private static string Ascii4(byte[] b, int pos)
        => $"{(char)b[pos]}{(char)b[pos + 1]}{(char)b[pos + 2]}{(char)b[pos + 3]}";

    internal static int ReadInt32(byte[] b, int pos)
    {
        if (pos < 0 || pos + 4 > b.Length)
        {
            return 0;
        }
        return b[pos] | (b[pos + 1] << 8) | (b[pos + 2] << 16) | (b[pos + 3] << 24);
    }

    internal static void WriteInt32(byte[] b, int pos, int value)
    {
        if (pos < 0 || pos + 4 > b.Length)
        {
            return;
        }
        b[pos] = (byte)(value & 0xFF);
        b[pos + 1] = (byte)((value >> 8) & 0xFF);
        b[pos + 2] = (byte)((value >> 16) & 0xFF);
        b[pos + 3] = (byte)((value >> 24) & 0xFF);
    }
}
