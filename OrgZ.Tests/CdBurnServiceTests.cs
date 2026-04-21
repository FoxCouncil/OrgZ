// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Buffers.Binary;

namespace OrgZ.Tests;

public class CdBurnServiceTests
{
    private static byte[] BuildCdAudioWav(int pcmByteCount, byte fill = 0xAB)
    {
        var buf = new byte[44 + pcmByteCount];
        CdRipService.WriteWavHeader(new MemoryStream(buf, 0, 44, writable: true), pcmByteCount);
        for (int i = 44; i < buf.Length; i++)
        {
            buf[i] = fill;
        }
        return buf;
    }

    [Fact]
    public void ParseCdAudioWav_Returns_Data_Range_For_Valid_Header()
    {
        var pcm = 2352 * 75 * 3;
        var bytes = BuildCdAudioWav(pcm);
        using var ms = new MemoryStream(bytes);

        var (offset, length) = CdBurnService.ParseCdAudioWav(ms, "<test>");

        Assert.Equal(44, offset);
        Assert.Equal(pcm, length);
    }

    [Fact]
    public void ParseCdAudioWav_Rejects_Non_Redbook_Format()
    {
        using var ms = new MemoryStream();
        var bytes = BuildCdAudioWav(2352);
        // Mutate sample rate from 44100 to 48000
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), 48000u);
        ms.Write(bytes);
        ms.Position = 0;

        var ex = Assert.Throws<InvalidDataException>(() => CdBurnService.ParseCdAudioWav(ms, "file.wav"));
        Assert.Contains("44100Hz", ex.Message);
    }

    [Fact]
    public void ParseCdAudioWav_Rejects_Missing_RIFF_Magic()
    {
        var bytes = new byte[44];
        using var ms = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => CdBurnService.ParseCdAudioWav(ms, "file.wav"));
    }

    [Fact]
    public void ParseCdAudioWav_Handles_Extra_LIST_Chunk_Before_Data()
    {
        using var ms = new MemoryStream();
        var header = BuildCdAudioWav(2352 * 10);

        // Write RIFF + WAVE
        ms.Write(header.AsSpan(0, 12));
        // Write fmt chunk (16 body bytes + 8 header = 24 total)
        ms.Write(header.AsSpan(12, 24));
        // Inject a LIST chunk (8 + 12 bytes, even length)
        ms.Write(new byte[] { (byte)'L', (byte)'I', (byte)'S', (byte)'T' });
        Span<byte> listLen = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(listLen, 12);
        ms.Write(listLen);
        ms.Write(new byte[12]);
        // data chunk
        ms.Write(header.AsSpan(36));
        ms.Position = 0;

        // Also patch RIFF size so we don't break the chunk walker at length check
        var (offset, length) = CdBurnService.ParseCdAudioWav(ms, "<list>");

        Assert.Equal(24 * 2352 / 2352 * 0 + 64, offset); // 12 + 24 + 8 + 12 + 8 = 64
        Assert.Equal(2352 * 10, length);
    }

    [Fact]
    public void SubStream_Exposes_Only_Requested_Range()
    {
        var backing = new byte[200];
        for (byte i = 0; i < backing.Length; i++) { backing[i] = i; }
        using var fs = new MemoryStream(backing);

        using var sub = new CdBurnService.SubStream(fs, 50, 100);

        Assert.Equal(100, sub.Length);

        var readBuf = new byte[100];
        Assert.Equal(100, sub.Read(readBuf, 0, 100));

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal((byte)(50 + i), readBuf[i]);
        }

        Assert.Equal(0, sub.Read(readBuf, 0, 100));
    }

    [Fact]
    public void SubStream_Seek_Respects_Bounds()
    {
        using var fs = new MemoryStream(new byte[500]);
        using var sub = new CdBurnService.SubStream(fs, 100, 200);

        Assert.Equal(50, sub.Seek(50, SeekOrigin.Begin));
        Assert.Equal(60, sub.Seek(10, SeekOrigin.Current));
        Assert.Equal(200, sub.Seek(0, SeekOrigin.End));
        Assert.Throws<IOException>(() => sub.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<IOException>(() => sub.Seek(201, SeekOrigin.Begin));
    }

    [Fact]
    public void SubStream_Rejects_Out_Of_Bounds_Range()
    {
        using var fs = new MemoryStream(new byte[100]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CdBurnService.SubStream(fs, 50, 100));
    }
}
