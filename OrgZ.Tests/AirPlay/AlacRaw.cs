// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

/// <summary>
/// Decodes the "uncompressed ALAC" frame <see cref="OrgZ.Services.AudioOutput.AirPlay.RaopAlac"/>
/// produces, back to 16-bit stereo little-endian PCM.
///
/// This is the half a receiver performs, written independently from the encoder's own
/// arithmetic so a round-trip actually proves something: the encoder shifts the whole sample
/// run one bit left to follow an odd-length header, and a decoder that inherited that
/// expression would agree with a wrong one just as happily.
/// </summary>
internal static class AlacRaw
{
    /// <summary>
    /// Reads <paramref name="frames"/> stereo frames out of a raw-ALAC payload.
    /// Throws when the header doesn't say "stored uncompressed" - a receiver would run its
    /// real decoder over the bytes and emit noise, which is worth failing loudly for.
    /// </summary>
    public static byte[] Decode(ReadOnlySpan<byte> alac, int frames = 352)
    {
        if (alac.Length < 8)
        {
            throw new InvalidDataException($"ALAC frame is {alac.Length} bytes - too short to hold a header.");
        }

        if (alac[0] != 0x20)
        {
            throw new InvalidDataException($"ALAC element tag is 0x{alac[0]:X2}, expected 0x20.");
        }

        // Byte 2 carries the "uncompressed" flag (bit 4) plus the 16-bit sample-size marker.
        if ((alac[2] & 0x10) == 0)
        {
            throw new InvalidDataException("ALAC frame is not flagged as stored uncompressed.");
        }

        var pcm = new byte[frames * 4];

        // Byte 6 holds the block size's low bits and, in its LSB, the first sample's top bit -
        // the header ends mid-byte, which is the whole reason this can't be a memcpy.
        var carry = alac[6] & 1;

        for (var i = 0; i < frames; i++)
        {
            var o = 7 + (i * 4);
            if (o + 4 > alac.Length)
            {
                throw new InvalidDataException($"ALAC frame holds {(alac.Length - 7) / 4} frames, expected {frames}.");
            }

            int b0 = alac[o], b1 = alac[o + 1], b2 = alac[o + 2], b3 = alac[o + 3];

            var left = (ushort)((carry << 15) | (b0 << 7) | (b1 >> 1));
            var right = (ushort)(((b1 & 1) << 15) | (b2 << 7) | (b3 >> 1));
            carry = b3 & 1;

            var p = i * 4;
            pcm[p] = (byte)(left & 0xFF);
            pcm[p + 1] = (byte)(left >> 8);
            pcm[p + 2] = (byte)(right & 0xFF);
            pcm[p + 3] = (byte)(right >> 8);
        }

        return pcm;
    }
}
