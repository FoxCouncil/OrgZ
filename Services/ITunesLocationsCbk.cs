// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Security.Cryptography;

namespace OrgZ.Services;

/// <summary>
/// Builds the iPod Nano 5G <c>Locations.itdb.cbk</c> integrity file - the checksum the
/// firmware verifies before trusting the SQLite track database. Port of libgpod's
/// <c>mk_Locations_cbk</c> (itdb_sqlite.c).
///
/// Layout (hash72 / Nano 5G, header size 46):
///   [0..45]   46-byte hash72 signature over the final SHA1 (see <see cref="ITunesDbHash72"/>)
///   [46..65]  final SHA1 = SHA1( concatenated per-block SHA1s )
///   [66..]    SHA1 of each 1 KB block of Locations.itdb, in order (final block hashed as-is)
///
/// total = 46 + 20 + 20*ceil(len/1024). An 87 KB Locations.itdb → 46 + 20 + 1740 = 1806.
/// </summary>
public static class ITunesLocationsCbk
{
    private const int BlockSize = 1024;
    private const int Hash72HeaderSize = 46;

    /// <summary>SHA1 of each 1 KB block of <paramref name="locationsItdb"/>, concatenated in order.</summary>
    public static byte[] ComputeBlockSha1s(ReadOnlySpan<byte> locationsItdb)
    {
        int blocks = (locationsItdb.Length + BlockSize - 1) / BlockSize;
        var result = new byte[blocks * 20];
        for (int i = 0, off = 0; off < locationsItdb.Length; i++, off += BlockSize)
        {
            int len = Math.Min(BlockSize, locationsItdb.Length - off);
            SHA1.HashData(locationsItdb.Slice(off, len), result.AsSpan(i * 20, 20));
        }
        return result;
    }

    /// <summary>The final SHA1 the cbk signature signs: SHA1 of all the per-block SHA1s.</summary>
    public static byte[] ComputeFinalSha1(ReadOnlySpan<byte> locationsItdb)
        => SHA1.HashData(ComputeBlockSha1s(locationsItdb));

    /// <summary>
    /// Assembles a full cbk for <paramref name="locationsItdb"/> using the per-device
    /// <paramref name="iv"/>/<paramref name="rnd"/> (from <see cref="TryExtractSeed"/> or HashInfo).
    /// </summary>
    public static byte[] Build(ReadOnlySpan<byte> locationsItdb, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> rnd)
    {
        var blockSha1s = ComputeBlockSha1s(locationsItdb);
        var finalSha1 = SHA1.HashData(blockSha1s);
        var signature = ITunesDbHash72.Generate(finalSha1, iv, rnd);

        var cbk = new byte[Hash72HeaderSize + 20 + blockSha1s.Length];
        signature.CopyTo(cbk.AsSpan(0));
        finalSha1.CopyTo(cbk.AsSpan(Hash72HeaderSize));
        blockSha1s.CopyTo(cbk.AsSpan(Hash72HeaderSize + 20));
        return cbk;
    }

    /// <summary>
    /// Recovers the per-device hash72 seed (iv/rnd) from an existing iTunes-written cbk, using the
    /// final SHA1 recomputed from <paramref name="locationsItdb"/>. The bootstrap that lets us sign
    /// new databases for this device without a HashInfo file already present.
    /// </summary>
    public static bool TryExtractSeed(ReadOnlySpan<byte> locationsItdb, ReadOnlySpan<byte> existingCbk, out byte[] iv, out byte[] rnd)
    {
        iv = [];
        rnd = [];
        if (existingCbk.Length < Hash72HeaderSize)
        {
            return false;
        }
        var finalSha1 = ComputeFinalSha1(locationsItdb);
        return ITunesDbHash72.Extract(existingCbk[..Hash72HeaderSize], finalSha1, out iv, out rnd);
    }
}
