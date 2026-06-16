// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Security.Cryptography;

namespace OrgZ.Services;

/// <summary>
/// hash72 - the checksum the iPod Nano 5G requires on its SQLite
/// <c>Locations.itdb.cbk</c> (and on the iTunesCDB mhbd). Without a valid hash72 the
/// firmware shows "0 songs". Direct port of libgpod's <c>itdb_hash72.c</c>.
///
/// The algorithm was never fully reverse-engineered; what IS known is the wrapper
/// around Apple's per-device IV, and that wrapper is plain AES-128-CBC under a fixed
/// embedded key. The per-device IV (and a 12-byte random part) are <see cref="Extract"/>ed
/// from any existing iTunes-signed signature already on the device, then reused via
/// <see cref="Generate"/> to sign new databases.
///
/// 46-byte signature layout:
///   [0]=0x01 [1]=0x00 || rnd[12] (=[2..13]) || AES-CBC(key, iv, sha1[20] || rnd[12])[32] (=[14..45])
///
/// HashInfo persistence file (<c>iPod_Control/Device/HashInfo</c>, 54 bytes, packed):
///   "HASHv0"(6) || uuid[20] || rnd[12] || iv[16].
///
/// hashAB (Nano 6G/7G, 4th-gen iOS) is a DIFFERENT scheme that libgpod can only sign
/// via a proprietary extracted blob (<c>libhashab</c>) - that's task #12, not this.
/// </summary>
public static class ITunesDbHash72
{
    /// <summary>The fixed AES-128 key embedded in the hash72 scheme (libgpod <c>AES_KEY</c>).</summary>
    private static readonly byte[] AesKey =
    [
        0x61, 0x8C, 0xA1, 0x0D, 0xC7, 0xF5, 0x7F, 0xD3,
        0xB4, 0x72, 0x3E, 0x08, 0x15, 0x74, 0x63, 0xD7,
    ];

    public const int SignatureLength = 46;
    private static readonly byte[] HashInfoMagic = "HASHv0"u8.ToArray();
    public const int HashInfoLength = 6 + 20 + 12 + 16; // 54

    /// <summary>
    /// Builds the 46-byte hash72 signature over a 20-byte <paramref name="sha1"/>, using the
    /// per-device <paramref name="iv"/> (16) and <paramref name="rnd"/> (12) recovered via
    /// <see cref="Extract"/>. Mirrors libgpod's <c>hash_generate</c>.
    /// </summary>
    public static byte[] Generate(ReadOnlySpan<byte> sha1, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> rnd)
    {
        if (sha1.Length != 20) throw new ArgumentException("sha1 must be 20 bytes", nameof(sha1));
        if (iv.Length != 16) throw new ArgumentException("iv must be 16 bytes", nameof(iv));
        if (rnd.Length != 12) throw new ArgumentException("rnd must be 12 bytes", nameof(rnd));

        // plaintext = sha1(20) || rnd(12), then AES-CBC encrypt the 32 bytes.
        var plaintext = new byte[32];
        sha1.CopyTo(plaintext);
        rnd.CopyTo(plaintext.AsSpan(20));
        var cipher = AesCbc(encrypt: true, iv, plaintext);

        var sig = new byte[SignatureLength];
        sig[0] = 0x01;
        sig[1] = 0x00;
        rnd.CopyTo(sig.AsSpan(2));      // rnd  -> [2..13]
        cipher.CopyTo(sig.AsSpan(14));  // ct   -> [14..45]
        return sig;
    }

    /// <summary>
    /// Recovers the per-device <paramref name="iv"/> (16) and <paramref name="rnd"/> (12) from an
    /// existing iTunes-signed 46-byte <paramref name="signature"/> that signs <paramref name="sha1"/>.
    /// CBC identity: decrypting the first ciphertext block with IV = P0 (= sha1[0..15]) yields the
    /// real device IV. Mirrors libgpod's <c>hash_extract</c>. False if the prefix isn't 01 00.
    /// </summary>
    public static bool Extract(ReadOnlySpan<byte> signature, ReadOnlySpan<byte> sha1, out byte[] iv, out byte[] rnd)
    {
        iv = new byte[16];
        rnd = new byte[12];
        if (signature.Length < SignatureLength || signature[0] != 0x01 || signature[1] != 0x00 || sha1.Length != 20)
        {
            return false;
        }

        var c0 = signature.Slice(14, 16).ToArray();   // first ciphertext block
        var p0 = sha1.Slice(0, 16).ToArray();          // known plaintext P0
        var recovered = AesCbc(encrypt: false, p0, c0); // Dec(C0) XOR P0 == iv
        Array.Copy(recovered, iv, 16);
        signature.Slice(2, 12).CopyTo(rnd);
        return true;
    }

    /// <summary>Serializes a 54-byte HashInfo file from a device uuid + recovered iv/rnd.</summary>
    public static byte[] BuildHashInfo(ReadOnlySpan<byte> uuid20, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> rnd)
    {
        if (uuid20.Length != 20) throw new ArgumentException("uuid must be 20 bytes", nameof(uuid20));
        var buf = new byte[HashInfoLength];
        HashInfoMagic.CopyTo(buf.AsSpan(0));
        uuid20.CopyTo(buf.AsSpan(6));
        rnd.CopyTo(buf.AsSpan(26));
        iv.CopyTo(buf.AsSpan(38));
        return buf;
    }

    /// <summary>Parses a 54-byte HashInfo file. False if magic/length is wrong.</summary>
    public static bool TryParseHashInfo(ReadOnlySpan<byte> data, out byte[] uuid20, out byte[] iv, out byte[] rnd)
    {
        uuid20 = new byte[20];
        iv = new byte[16];
        rnd = new byte[12];
        if (data.Length < HashInfoLength || !data[..6].SequenceEqual(HashInfoMagic))
        {
            return false;
        }
        data.Slice(6, 20).CopyTo(uuid20);
        data.Slice(26, 12).CopyTo(rnd);
        data.Slice(38, 16).CopyTo(iv);
        return true;
    }

    private static byte[] AesCbc(bool encrypt, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> data)
    {
        using var aes = Aes.Create();
        aes.Key = AesKey;
        aes.IV = iv.ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var xform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        return xform.TransformFinalBlock(data.ToArray(), 0, data.Length);
    }
}
