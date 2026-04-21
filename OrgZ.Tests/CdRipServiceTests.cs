// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Buffers.Binary;

namespace OrgZ.Tests;

public class CdRipServiceTests
{
    [Fact]
    public void WriteWavHeader_Writes_Canonical_44Byte_CDDA_Header()
    {
        using var ms = new MemoryStream();
        long pcmBytes = 2352 * 1000;

        CdRipService.WriteWavHeader(ms, pcmBytes);

        Assert.Equal(44, ms.Length);
        var buf = ms.ToArray();

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(buf, 0, 4));
        Assert.Equal((uint)(pcmBytes + 36), BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4)));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(buf, 8, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(buf, 12, 4));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16, 4)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(20, 2)));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(22, 2)));
        Assert.Equal(44100u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(24, 4)));
        Assert.Equal(176400u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(28, 4)));
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(32, 2)));
        Assert.Equal((ushort)16, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(34, 2)));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(buf, 36, 4));
        Assert.Equal((uint)pcmBytes, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(40, 4)));
    }

    [Fact]
    public void WriteWavHeader_Rejects_Oversized_Payload()
    {
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => CdRipService.WriteWavHeader(ms, (long)uint.MaxValue));
    }

    [Theory]
    [InlineData(1, "Clean Title", "01 - Clean Title.wav")]
    [InlineData(12, "Has / slash", "12 - Has  slash.wav")]
    [InlineData(3, "Colons: bad", "03 - Colons bad.wav")]
    [InlineData(5, null, "05 - Track 05.wav")]
    [InlineData(9, "   ", "09 - Track 09.wav")]
    [InlineData(2, "Trailing.", "02 - Trailing.wav")]
    public void BuildFileName_Produces_Sanitized_TrackNN_Pattern(int trackNum, string? title, string expected)
    {
        Assert.Equal(expected, CdRipService.BuildFileName(trackNum, title));
    }

    [Fact]
    public void BuildFileName_Caps_Length_At_120_Plus_Extension()
    {
        var title = new string('A', 500);
        var result = CdRipService.BuildFileName(1, title);
        Assert.EndsWith(".wav", result);
        Assert.True(result.Length <= 124, $"Got length {result.Length}");
    }

    [Fact]
    public void SanitizeForFileName_Strips_Invalid_Path_Chars_And_Control_Chars()
    {
        var input = "Hello\u0000\u001F \"World\"?";
        var cleaned = CdRipService.SanitizeForFileName(input);
        Assert.Equal("Hello World", cleaned);
        Assert.False(cleaned.Contains('\u0000'));
        Assert.False(cleaned.Contains('\u001F'));
        Assert.False(cleaned.Contains('"'));
        Assert.False(cleaned.Contains('?'));
    }
}
