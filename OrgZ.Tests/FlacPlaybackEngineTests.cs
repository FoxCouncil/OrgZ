// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// The bit-perfect claim lives or dies in <see cref="FlacPlaybackEngine.WidenToS32Stereo"/>:
/// every source bit must land in the S32 output, shifted but untouched, and
/// shifting back must reproduce the source exactly.
/// </summary>
public class FlacPlaybackEngineTests
{
    [Theory]
    [InlineData(short.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(short.MaxValue)]
    [InlineData(-12345)]
    public void Widen16_Shifts_Losslessly(short sample)
    {
        var raw = new byte[4];
        BitConverter.GetBytes(sample).CopyTo(raw, 0);
        BitConverter.GetBytes(sample).CopyTo(raw, 2);
        var dest = new byte[8];

        var bytes = FlacPlaybackEngine.WidenToS32Stereo(raw, dest, 16, 2);

        Assert.Equal(8, bytes);
        var left = BitConverter.ToInt32(dest, 0);
        var right = BitConverter.ToInt32(dest, 4);
        Assert.Equal(sample << 16, left);
        Assert.Equal(sample << 16, right);
        Assert.Equal(sample, (short)(left >> 16));
    }

    [Theory]
    [InlineData(-8388608)]   // 24-bit min
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8388607)]    // 24-bit max
    [InlineData(-4177526)]
    public void Widen24_Shifts_Losslessly(int sample24)
    {
        var raw = new byte[6];
        raw[0] = (byte)sample24;
        raw[1] = (byte)(sample24 >> 8);
        raw[2] = (byte)(sample24 >> 16);
        raw[3] = raw[0];
        raw[4] = raw[1];
        raw[5] = raw[2];
        var dest = new byte[8];

        var bytes = FlacPlaybackEngine.WidenToS32Stereo(raw, dest, 24, 2);

        Assert.Equal(8, bytes);
        var left = BitConverter.ToInt32(dest, 0);
        var right = BitConverter.ToInt32(dest, 4);
        Assert.Equal(sample24 << 8, left);
        Assert.Equal(sample24 << 8, right);
        Assert.Equal(sample24, left >> 8);
    }

    [Fact]
    public void Widen_Mono_Duplicates_To_Both_Channels()
    {
        var raw = new byte[3];
        const int sample24 = 0x123456;
        raw[0] = unchecked((byte)sample24);
        raw[1] = unchecked((byte)(sample24 >> 8));
        raw[2] = unchecked((byte)(sample24 >> 16));
        var dest = new byte[8];

        var bytes = FlacPlaybackEngine.WidenToS32Stereo(raw, dest, 24, 1);

        Assert.Equal(8, bytes);
        Assert.Equal(BitConverter.ToInt32(dest, 0), BitConverter.ToInt32(dest, 4));
        Assert.Equal(sample24 << 8, BitConverter.ToInt32(dest, 0));
    }

    [Fact]
    public void Widen_Roundtrips_A_Full_24Bit_Ramp()
    {
        // Every value in a representative stripe of the 24-bit range must
        // survive widen → shift-back exactly.
        const int frames = 4096;
        var raw = new byte[frames * 3 * 2];
        var expected = new int[frames * 2];
        int v = -8388608;
        int step = 16777215 / frames;
        for (int f = 0; f < frames; f++, v += step)
        {
            for (int c = 0; c < 2; c++)
            {
                int i = (f * 2 + c) * 3;
                raw[i] = (byte)v;
                raw[i + 1] = (byte)(v >> 8);
                raw[i + 2] = (byte)(v >> 16);
                expected[f * 2 + c] = v;
            }
        }
        var dest = new byte[frames * 2 * 4];

        var bytes = FlacPlaybackEngine.WidenToS32Stereo(raw, dest, 24, 2);

        Assert.Equal(dest.Length, bytes);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], BitConverter.ToInt32(dest, i * 4) >> 8);
        }
    }
}
