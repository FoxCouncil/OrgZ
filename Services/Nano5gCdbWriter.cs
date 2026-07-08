// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.IO.Compression;
using System.Security.Cryptography;

namespace OrgZ.Services;

/// <summary>
/// Reads and writes the iPod Nano 5G <c>iTunesCDB</c> - the firmware's MASTER database: a zlib-
/// compressed binary iTunesDB (mhbd) carrying a hash72 signature in its 244-byte header. The Nano 5G
/// keeps both this CDB and the SQLite "iTunes Library.itlp"; on boot it reconciles, rebuilding the
/// SQLite from the CDB. So edits only persist when the CDB is written too.
///
/// Synthesizing a CDB from scratch is fragile - iTunes wants a precise multi-dataset structure
/// (5 datasets at version 0x73) and rejects a stub as corrupt. So callers reuse an existing iTunes-
/// or firmware-written CDB as a TEMPLATE: <see cref="FromTemplate"/> decodes it, the caller edits the
/// mhbd tree (<see cref="ITunesDbWriter.ClearLibrary"/> + <see cref="ITunesDbWriter.AddTrack"/>), then
/// <see cref="Emit"/> recompresses + re-signs. The template's datasets, version, and db id are kept;
/// only the track list, the library playlist, and the signature change.
///
/// Header (libgpod <c>itdb_zlib.c</c> / <c>itdb_hash72.c</c>): 0x04=header_len(244),
/// 0x08=total_len(=compressed file size), 0x0C=2, 0x10=version, 0x30=hashing_scheme(2=HASH72),
/// 0x72=hash72[46], 0xA8=1 (compressed flag). Signature = SHA1 over the whole compressed file with
/// db_id(0x18,8)/hash58(0x58,20)/hash72(0x72,46) zeroed, db_id restored, signature written at 0x72.
/// </summary>
public static class Nano5gCdbWriter
{
    private const int CdbHeaderLen = 0xF4;   // 244

    /// <summary>Decodes an iTunesCDB (zlib body behind the 244-byte header) into its uncompressed
    /// mhbd tree, ready to edit.</summary>
    public static ITunesDbDocument FromTemplate(byte[] templateCdb)
        => ITunesDbChunkTree.Parse(Decompress(templateCdb));

    /// <summary>Re-emits an (edited) mhbd tree as a signed, zlib-compressed iTunesCDB, signed with the
    /// device <paramref name="fireWireGuid"/>. The CDB is validated by <b>hash58</b> (scheme 1, the
    /// FireWire-GUID HMAC) computed over the WHOLE COMPRESSED FILE - verified byte-identical to a real
    /// iTunes-written CDB. (NOT hash72 - that scheme is the separate Locations.itdb.cbk's.) When the
    /// GUID is absent (offline tests) the CDB is left unsigned.</summary>
    public static byte[] Emit(ITunesDbDocument doc, string? fireWireGuid)
    {
        ITunesDbChunkTree.Normalize(doc.Root);
        // mhbd dataset count lives at 0x14 for both the CDB and the legacy iTunesDB (verified against
        // real iTunes output), so Normalize now sets it. Re-assert it here in case a caller mutated the
        // dataset list (e.g. added an empty dataset 9) between Normalize and Serialize.
        doc.Root.WriteHeaderInt32(0x14, doc.Root.Children.Count(c => c.Magic == "mhsd"));
        var plain = ITunesDbChunkTree.Serialize(doc);
        if (plain.Length < CdbHeaderLen)
        {
            throw new InvalidDataException("CDB tree is smaller than the 244-byte mhbd header.");
        }

        // zlib (level 1) everything after the plaintext header.
        byte[] body;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                z.Write(plain, CdbHeaderLen, plain.Length - CdbHeaderLen);
            }
            body = ms.ToArray();
        }

        var cdb = new byte[CdbHeaderLen + body.Length];
        Array.Copy(plain, 0, cdb, 0, CdbHeaderLen);
        Array.Copy(body, 0, cdb, CdbHeaderLen, body.Length);
        cdb[0xA8] = 1;                       // compressed flag
        WriteI32(cdb, 0x08, cdb.Length);     // total_len = compressed file size

        // Sign with hash58 over the assembled compressed file. ITunesDbHash58.Apply sets scheme@0x30=1,
        // zeroes db_id(0x18)/unk(0x32)/hash58(0x58) for the HMAC, then writes the 20-byte result to 0x58.
        if (!string.IsNullOrEmpty(fireWireGuid))
        {
            ITunesDbHash58.Apply(cdb, fireWireGuid);
        }
        return cdb;
    }

    /// <summary>Inflates the compressed body back behind the verbatim 244-byte header.</summary>
    private static byte[] Decompress(byte[] cdb)
    {
        if (cdb.Length < CdbHeaderLen)
        {
            throw new InvalidDataException("Not an iTunesCDB (shorter than the 244-byte header).");
        }
        if (cdb[0xA8] != 1)
        {
            return cdb;   // already plain
        }
        using var src = new MemoryStream(cdb, CdbHeaderLen, cdb.Length - CdbHeaderLen);
        using var z = new ZLibStream(src, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        z.CopyTo(outMs);
        var inflated = outMs.ToArray();

        var plain = new byte[CdbHeaderLen + inflated.Length];
        Array.Copy(cdb, 0, plain, 0, CdbHeaderLen);
        Array.Copy(inflated, 0, plain, CdbHeaderLen, inflated.Length);
        WriteI32(plain, 0x08, plain.Length);   // total_len reflects the now-uncompressed size
        plain[0xA8] = 1;                        // keep the flag (Emit re-asserts it after recompress)
        return plain;
    }

    private static void WriteI32(byte[] b, int offset, int value)
    {
        b[offset] = (byte)value;
        b[offset + 1] = (byte)(value >> 8);
        b[offset + 2] = (byte)(value >> 16);
        b[offset + 3] = (byte)(value >> 24);
    }
}
