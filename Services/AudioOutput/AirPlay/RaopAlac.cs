// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// Packs 16-bit stereo LE PCM into the "uncompressed ALAC" frame RAOP receivers expect.
///
/// Every AirPlay receiver accepts an ALAC frame whose header says "this frame is stored
/// raw", which lets a sender skip a real ALAC encoder entirely: the payload is just the
/// PCM samples, big-endian, shifted left by one bit (the header ends mid-byte, so the
/// whole sample run is bit-shifted to follow it). That shift is why this is a hand-rolled
/// packer rather than a memcpy.
///
/// Layout, for a frame of <c>blockSize</c> samples (RAOP's default is 352):
///   byte 0: 0x20                      - element tag
///   byte 1: 0x00
///   byte 2: 0x12 | (blockSize b31)    - "uncompressed" flag + top block-size bit
///   bytes 3-6: blockSize b30..b0, each byte carrying 8 bits of the left-shifted value
///   byte 6 also carries the first sample's top bit; samples continue from there.
///   The tail sets the final data bit and appends a 0xC0 terminator byte.
///
/// Ported from the reference implementation used by libcodecs / RAOP-Player
/// (<c>pcm_to_alac_raw</c>), which is the de-facto shape every open sender emits.
/// </summary>
internal static class RaopAlac
{
    /// <summary>Frames per packet - RAOP's canonical chunk, and what the SDP advertises.</summary>
    public const int FramesPerPacket = 352;

    /// <summary>Bytes of 16-bit stereo PCM in one full packet.</summary>
    public const int PcmBytesPerPacket = FramesPerPacket * 4;

    /// <summary>
    /// Encodes up to <paramref name="blockSize"/> stereo frames of 16-bit LE PCM.
    /// A short <paramref name="pcm"/> is zero-padded to the full block, matching what
    /// receivers expect for the final packet of a stream.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> pcm, int blockSize = FramesPerPacket)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1);

        var frames = Math.Min(pcm.Length / 4, blockSize);
        var output = new byte[blockSize * 4 + 16];
        var p = 0;

        output[p++] = 1 << 5;
        output[p++] = 0;
        output[p++] = (byte)((1 << 4) | (1 << 1) | (uint)((blockSize & unchecked((int)0x80000000)) >> 31 & 1));
        output[p++] = (byte)(((uint)(blockSize & 0x7f800000) << 1) >> 24);
        output[p++] = (byte)(((uint)(blockSize & 0x007f8000) << 1) >> 16);
        output[p++] = (byte)(((uint)(blockSize & 0x00007f80) << 1) >> 8);
        output[p] = (byte)((blockSize & 0x0000007f) << 1);

        // From here every byte straddles two samples: the header consumed an odd number of
        // bits, so each output byte is (7 low bits of one sample byte) | (top bit of the
        // next). A frame reads as one LE uint32 - low half = left channel, high = right.
        if (frames > 0)
        {
            output[p++] |= (byte)((ReadFrame(pcm, 0) & 0x00008000) >> 15);

            for (var i = 0; i < frames; i++)
            {
                var s = ReadFrame(pcm, i);
                var next = i + 1 < frames ? ReadFrame(pcm, i + 1) : 0u;

                output[p++] = (byte)((s & 0x00007f80) >> 7);                                    // L hi 7 | L lo b7
                output[p++] = (byte)(((s & 0x0000007f) << 1) | ((s & 0x80000000) >> 31));       // L lo 7 | R hi b7
                output[p++] = (byte)((s & 0x7f800000) >> 23);                                   // R hi 7 | R lo b7
                output[p++] = (byte)(((s & 0x007f0000) >> 15) | ((next & 0x00008000) >> 15));   // R lo 7 | next L b7
            }
        }
        else
        {
            p++;
        }

        // Silence out to the full block - a receiver decodes exactly blockSize frames.
        for (var pad = (blockSize - frames) * 4; pad > 0; pad--)
        {
            output[p++] = 0;
        }

        output[p - 1] |= 1;
        output[p] = (7 >> 1) << 6;

        return output[..(p + 1)];
    }

    private static uint ReadFrame(ReadOnlySpan<byte> pcm, int index)
    {
        var o = index * 4;
        return (uint)(pcm[o] | (pcm[o + 1] << 8) | (pcm[o + 2] << 16) | (pcm[o + 3] << 24));
    }
}
