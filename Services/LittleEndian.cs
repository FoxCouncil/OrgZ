// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

/// <summary>
/// The one little-endian byte-array codec for OrgZ's binary formats (iTunesDB, ArtworkDB,
/// firmware). Every reader/writer used to carry its own near-identical copy - this is the
/// single source of truth, bounds-guarded so an out-of-range offset yields 0 / no-op rather
/// than throwing (a strict superset of the old scattered variants, none of which relied on
/// the throw).
/// </summary>
internal static class LittleEndian
{
    public static int ReadInt32(byte[] b, int pos)
    {
        if (pos < 0 || pos + 4 > b.Length)
        {
            return 0;
        }
        return b[pos] | (b[pos + 1] << 8) | (b[pos + 2] << 16) | (b[pos + 3] << 24);
    }

    public static uint ReadUInt32(byte[] b, int pos) => unchecked((uint)ReadInt32(b, pos));

    public static int ReadUInt16(byte[] b, int pos)
    {
        if (pos < 0 || pos + 2 > b.Length)
        {
            return 0;
        }
        return b[pos] | (b[pos + 1] << 8);
    }

    public static ulong ReadUInt64(byte[] b, int pos)
    {
        if (pos < 0 || pos + 8 > b.Length)
        {
            return 0;
        }
        ulong v = 0;
        for (int i = 0; i < 8; i++)
        {
            v |= (ulong)b[pos + i] << (8 * i);
        }
        return v;
    }

    public static void WriteInt32(byte[] b, int pos, int value)
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

    public static void WriteUInt16(byte[] b, int pos, int value)
    {
        if (pos < 0 || pos + 2 > b.Length)
        {
            return;
        }
        b[pos] = (byte)(value & 0xFF);
        b[pos + 1] = (byte)((value >> 8) & 0xFF);
    }

    public static void WriteUInt64(byte[] b, int pos, ulong value)
    {
        if (pos < 0 || pos + 8 > b.Length)
        {
            return;
        }
        for (int i = 0; i < 8; i++)
        {
            b[pos + i] = (byte)((value >> (8 * i)) & 0xFF);
        }
    }
}
