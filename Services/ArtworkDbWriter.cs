// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text;

namespace OrgZ.Services;

/// <summary>One on-disk thumbnail: which .ithmb format, its pixel size, and where in that file it lives.</summary>
public sealed record ArtThumb(int FormatId, int Width, int Height, int IthmbOffset, int ImageSize);

/// <summary>One artwork entry: a track's dbid, its image id, and the thumbnails (one per format).</summary>
public sealed record ArtImage(ulong Dbid, int ImageId, IReadOnlyList<ArtThumb> Thumbs, int OrigImgSize);

/// <summary>
/// Builds an iPod ArtworkDB (iPod_Control/Artwork/ArtworkDB) from scratch for a
/// single image, reusing the <see cref="ITunesDbChunkTree"/> chunk model (the
/// ArtworkDB is the same binary grammar, rooted at 'mhfd').
///
/// Layout + field offsets + header sizes are matched to the working iOpenPod
/// implementation (ArtworkDB_Writer/artworkdb_chunks.py) and verified against an
/// iPod Video 5.5G:
///   mhfd (0x84; @0x10=2, num_mhsd@0x14, next_id@0x1C, @0x30=2)
///   ├ mhsd index=1 (image list, 0x60)
///   │  └ mhli (0x5C, list header)
///   │     └ mhii (0x98; num_mhod@0x0C, image_id@0x10, song_id/dbid@0x14, src_img_size@0x30)
///   │        ├ mhod type=2 (0x18) -> mhni
///   │        └ mhod type=2 (0x18) -> mhni
///   │              mhni (0x4C; num_children=1@0x0C, format_id@0x10, offset@0x14,
///   │                    image_size@0x18, vpad@0x1C, hpad@0x1E, h@0x20, w@0x22, image_size@0x28)
///   │                 └ mhod type=3 (0x18) -> filename string ":F{id}_1.ithmb" (UTF-16LE)
///   ├ mhsd index=2 (album list, 0x60) -> mhla (empty)
///   └ mhsd index=3 (file list, 0x60) -> mhlf -> mhif (0x7C; format_id@0x10, image_size@0x14)
///
/// The mhii's <c>song_id</c> must equal the iTunesDB track's dbid (see
/// <see cref="ITunesDbWriter"/>). Critically, each mhni MUST carry its filename
/// MHOD child — without it the firmware accepts the DB but renders the art blank.
/// </summary>
public static class ArtworkDbWriter
{
    private const int MhfdLen = 0x84;
    private const int MhsdLen = 0x60;
    private const int MhliLen = 0x5C;
    private const int MhiiLen = 0x98;
    private const int MhodLen = 0x18;   // 24 — container AND string mhods
    private const int MhniLen = 0x4C;
    private const int MhlfLen = 0x5C;
    private const int MhifLen = 0x7C;
    private const int MhlaLen = 0x5C;

    private const int MhodTypeThumbnail = 2;   // container holding an mhni
    private const int MhodTypeFileName  = 3;   // string holding the .ithmb filename

    /// <summary>Builds an ArtworkDB for a single image (the from-scratch case).</summary>
    public static ITunesDbDocument Build(ulong dbid, int imageId, IReadOnlyList<ArtThumb> thumbs, int origImgSize)
        => BuildFromImages([new ArtImage(dbid, imageId, thumbs, origImgSize)]);

    /// <summary>
    /// Builds a complete ArtworkDB from a list of image entries — one mhii per
    /// image, one mhif per distinct .ithmb format across all of them. Used to
    /// rebuild the DB after appending a new image to the existing set (multi-track
    /// sync), so previously-written art is preserved rather than clobbered.
    /// </summary>
    public static ITunesDbDocument BuildFromImages(IReadOnlyList<ArtImage> images)
    {
        int nextId = (images.Count > 0 ? images.Max(i => i.ImageId) : 99) + 1;

        var mhfd = NewChunk("mhfd", MhfdLen);
        mhfd.WriteHeaderInt32(0x10, 2);          // unknown2 / version marker
        mhfd.WriteHeaderInt32(0x1C, nextId);     // next_id
        mhfd.WriteHeaderInt32(0x30, 2);          // unknown_flag1

        // --- image list (index 1): one mhii per image ---
        var imgList = NewMhsd(1);
        imgList.Children.Add(NewChunk("mhli", MhliLen));
        foreach (var img in images)
        {
            var mhii = NewChunk("mhii", MhiiLen);
            mhii.WriteHeaderInt32(0x10, img.ImageId);
            WriteUInt64(mhii.Header, 0x14, img.Dbid);       // song_id == track dbid
            mhii.WriteHeaderInt32(0x30, img.OrigImgSize);   // src/orig image size
            foreach (var t in img.Thumbs)
            {
                mhii.Children.Add(BuildThumbContainer(t));
            }
            imgList.Children.Add(mhii);
        }

        // --- album list (index 2): present but empty ---
        var albumList = NewMhsd(2);
        albumList.Children.Add(NewChunk("mhla", MhlaLen));

        // --- file list (index 3): one mhif per distinct .ithmb format ---
        var fileList = NewMhsd(3);
        fileList.Children.Add(NewChunk("mhlf", MhlfLen));
        var seenFormats = new HashSet<int>();
        foreach (var t in images.SelectMany(i => i.Thumbs))
        {
            if (!seenFormats.Add(t.FormatId))
            {
                continue;
            }
            var mhif = NewChunk("mhif", MhifLen);
            mhif.WriteHeaderInt32(0x10, t.FormatId);
            mhif.WriteHeaderInt32(0x14, t.ImageSize);
            fileList.Children.Add(mhif);
        }

        mhfd.Children.Add(imgList);
        mhfd.Children.Add(albumList);
        mhfd.Children.Add(fileList);

        return new ITunesDbDocument { Root = mhfd };
    }

    /// <summary>
    /// Reads the image entries out of an already-parsed ArtworkDB so new art can be
    /// appended without losing the existing entries. Tolerates the iPod's own
    /// rewritten layout (mhii are siblings of mhli under the image-list mhsd).
    /// </summary>
    public static List<ArtImage> ReadImages(ITunesDbDocument doc)
    {
        var images = new List<ArtImage>();
        var imgList = doc.Root.Children.FirstOrDefault(c =>
                          c.Magic == "mhsd" && (ReadInt32(c.Header, 0x0C) & 0xFFFF) == 1)
                      ?? doc.Root.Children.FirstOrDefault(c => c.Children.Any(x => x.Magic == "mhii"));
        if (imgList is null)
        {
            return images;
        }

        foreach (var mhii in imgList.Children.Where(c => c.Magic == "mhii"))
        {
            int imageId = ReadInt32(mhii.Header, 0x10);
            ulong dbid = ReadUInt64(mhii.Header, 0x14);
            int origSize = ReadInt32(mhii.Header, 0x30);

            var thumbs = new List<ArtThumb>();
            foreach (var container in mhii.Children.Where(c => c.Magic == "mhod"))
            {
                var mhni = container.Children.FirstOrDefault(c => c.Magic == "mhni");
                if (mhni is null)
                {
                    continue;
                }
                thumbs.Add(new ArtThumb(
                    FormatId:    ReadInt32(mhni.Header, 0x10),
                    Width:       ReadUInt16(mhni.Header, 0x22),
                    Height:      ReadUInt16(mhni.Header, 0x20),
                    IthmbOffset: ReadInt32(mhni.Header, 0x14),
                    ImageSize:   ReadInt32(mhni.Header, 0x18)));
            }
            images.Add(new ArtImage(dbid, imageId, thumbs, origSize));
        }
        return images;
    }

    /// <summary>Next free image id given the current entries (iTunes starts at 100).</summary>
    public static int NextImageId(IReadOnlyList<ArtImage> images)
        => (images.Count > 0 ? images.Max(i => i.ImageId) : 99) + 1;

    private static ITunesDbChunk BuildThumbContainer(ArtThumb t)
    {
        var mhni = NewChunk("mhni", MhniLen);
        mhni.WriteHeaderInt32(0x10, t.FormatId);
        mhni.WriteHeaderInt32(0x14, t.IthmbOffset);
        mhni.WriteHeaderInt32(0x18, t.ImageSize);
        WriteUInt16(mhni.Header, 0x20, (ushort)t.Height);
        WriteUInt16(mhni.Header, 0x22, (ushort)t.Width);
        mhni.WriteHeaderInt32(0x28, t.ImageSize);   // image_size is stored twice
        // The filename child is mandatory for the firmware to render the image.
        mhni.Children.Add(BuildFileNameMhod($":F{t.FormatId}_1.ithmb"));

        var container = NewChunk("mhod", MhodLen);
        WriteUInt16(container.Header, 0x0C, MhodTypeThumbnail);
        container.Children.Add(mhni);
        return container;
    }

    /// <summary>
    /// A string MHOD (type 3): 24-byte chunk header, then a body of
    /// [byteLen u32][encoding=2 u8][3 pad][4 pad][UTF-16LE bytes][pad to /4].
    /// </summary>
    private static ITunesDbChunk BuildFileNameMhod(string text)
    {
        var encoded = Encoding.Unicode.GetBytes(text);   // UTF-16LE
        int pad = (4 - (encoded.Length % 4)) % 4;
        var body = new byte[12 + encoded.Length + pad];
        ITunesDbChunkTree.WriteInt32(body, 0, encoded.Length);
        body[4] = 2;   // encoding: UTF-16LE
        Buffer.BlockCopy(encoded, 0, body, 12, encoded.Length);

        var mhod = NewChunk("mhod", MhodLen);
        WriteUInt16(mhod.Header, 0x0C, MhodTypeFileName);
        mhod.Body = body;
        return mhod;
    }

    private static ITunesDbChunk NewMhsd(int index)
    {
        var mhsd = NewChunk("mhsd", MhsdLen);
        mhsd.WriteHeaderInt32(0x0C, index);   // dataset index (16-bit field; high bytes stay 0)
        return mhsd;
    }

    private static ITunesDbChunk NewChunk(string magic, int headerLen)
    {
        var c = new ITunesDbChunk { Magic = magic, Header = new byte[headerLen] };
        for (int i = 0; i < 4; i++)
        {
            c.Header[i] = (byte)magic[i];
        }
        c.WriteHeaderInt32(0x04, headerLen);
        // total size (0x08) and the count fields are filled by ITunesDbChunkTree.Normalize.
        return c;
    }

    private static void WriteUInt16(byte[] b, int o, ushort v)
    {
        b[o] = (byte)(v & 0xFF);
        b[o + 1] = (byte)((v >> 8) & 0xFF);
    }

    private static void WriteUInt64(byte[] b, int o, ulong v)
    {
        for (int i = 0; i < 8; i++)
        {
            b[o + i] = (byte)((v >> (8 * i)) & 0xFF);
        }
    }

    private static int ReadInt32(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

    private static int ReadUInt16(byte[] b, int o) => b[o] | (b[o + 1] << 8);

    private static ulong ReadUInt64(byte[] b, int o)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++)
        {
            v |= (ulong)b[o + i] << (8 * i);
        }
        return v;
    }
}
