// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class CdAudioTranscoderTests
{
    private const int BytesPerSector = 2352;

    private static byte[] AssembleWav(byte[] pcm)
    {
        using var src = new MemoryStream(pcm);
        using var dest = new MemoryStream();
        CdAudioTranscoder.WriteCdAudioWav(src, pcm.Length, dest);
        return dest.ToArray();
    }

    [Fact]
    public void WriteCdAudioWav_Pads_Partial_Sector_To_Whole_Sector()
    {
        // One full sector plus a stray frame - must round up to two sectors.
        var pcm = new byte[BytesPerSector + 4];
        for (int i = 0; i < pcm.Length; i++) { pcm[i] = (byte)(i & 0xFF); }

        var wav = AssembleWav(pcm);

        using var ms = new MemoryStream(wav);
        var (offset, length) = CdBurnService.ParseCdAudioWav(ms, "<padded>");

        Assert.Equal(44, offset);
        Assert.Equal(BytesPerSector * 2, length);                 // rounded up
        Assert.Equal(44 + BytesPerSector * 2, wav.Length);        // header + padded data
    }

    [Fact]
    public void WriteCdAudioWav_Preserves_Payload_And_Zero_Fills_Tail()
    {
        var pcm = new byte[BytesPerSector + 100];
        for (int i = 0; i < pcm.Length; i++) { pcm[i] = (byte)(i % 251 + 1); } // non-zero payload

        var wav = AssembleWav(pcm);

        // Original samples copied verbatim right after the 44-byte header.
        for (int i = 0; i < pcm.Length; i++)
        {
            Assert.Equal(pcm[i], wav[44 + i]);
        }

        // Everything from the end of the payload to the sector boundary is silence.
        for (int i = 44 + pcm.Length; i < wav.Length; i++)
        {
            Assert.Equal(0, wav[i]);
        }
    }

    [Fact]
    public void WriteCdAudioWav_Leaves_Aligned_Pcm_Unpadded()
    {
        var pcm = new byte[BytesPerSector * 3];
        var wav = AssembleWav(pcm);

        using var ms = new MemoryStream(wav);
        var (_, length) = CdBurnService.ParseCdAudioWav(ms, "<aligned>");

        Assert.Equal(BytesPerSector * 3, length);
        Assert.Equal(44 + BytesPerSector * 3, wav.Length);
    }

    [Fact]
    public void WriteCdAudioWav_Output_Is_Accepted_By_Burn_Validator()
    {
        // The whole point: an arbitrary-length transcode must survive the burn path's
        // strict CD-DA validation (2ch / 44100 / 16-bit / sector-aligned).
        var pcm = new byte[5000];
        var wav = AssembleWav(pcm);

        using var ms = new MemoryStream(wav);
        var ex = Record.Exception(() => CdBurnService.ParseCdAudioWav(ms, "<accept>"));

        Assert.Null(ex);
    }
}
