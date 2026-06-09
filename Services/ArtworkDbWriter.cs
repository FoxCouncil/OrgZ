// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text;

namespace OrgZ.Services;

/// <summary>One on-disk thumbnail: which .ithmb format, its pixel size, and where in that file it lives.</summary>
public sealed record ArtThumb(int FormatId, int Width, int Height, int IthmbOffset, int ImageSize);

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
/// MHOD child - without it the firmware accepts the DB but renders the art blank.
/// </summary>
public static class ArtworkDbWriter
{
    private const int MhfdLen = 0x84;
    private const int MhsdLen = 0x60;
    private const int MhliLen = 0x5C;
    private const int MhiiLen = 0x98;
    private const int MhodLen = 0x18;   // 24 - container AND string mhods
    private const int MhniLen = 0x4C;
    private const int MhlfLen = 0x5C;
    private const int MhifLen = 0x7C;
    private const int MhlaLen = 0x5C;

    private const int MhodTypeThumbnail = 2;   // container holding an mhni
    private const int MhodTypeFileName  = 3;   // string holding the .ithmb filename

    public static ITunesDbDocument Build(ulong dbid, int imageId, IReadOnlyList<ArtThumb> thumbs, int origImgSize)
    {
        var mhfd = NewChunk("mhfd", MhfdLen);
        mhfd.WriteHeaderInt32(0x10, 2);             // unknown2 / version marker
        mhfd.WriteHeaderInt32(0x1C, imageId + 1);   // next_id
        mhfd.WriteHeaderInt32(0x30, 2);             // unknown_flag1

        // --- image list (index 1): one mhii, one mhod->mhni per thumbnail ---
        var imgList = NewMhsd(1);
        imgList.Children.Add(NewChunk("mhli", MhliLen));

        var mhii = NewChunk("mhii", MhiiLen);
        mhii.WriteHeaderInt32(0x10, imageId);
        WriteUInt64(mhii.Header, 0x14, dbid);       // song_id == track dbid
        mhii.WriteHeaderInt32(0x30, origImgSize);   // src/orig image size
        foreach (var t in thumbs)
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
            mhii.Children.Add(container);
        }
        imgList.Children.Add(mhii);

        // --- album list (index 2): present but empty ---
        var albumList = NewMhsd(2);
        albumList.Children.Add(NewChunk("mhla", MhlaLen));

        // --- file list (index 3): one mhif per .ithmb file ---
        var fileList = NewMhsd(3);
        fileList.Children.Add(NewChunk("mhlf", MhlfLen));
        foreach (var t in thumbs)
        {
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
}
