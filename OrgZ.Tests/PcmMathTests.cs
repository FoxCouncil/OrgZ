// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.InteropServices;
using OrgZ.Services.AudioOutput;

namespace OrgZ.Tests;

/// <summary>
/// The sample-scaling kernels the bus and every platform sink now share. These carried
/// three verbatim copies before, so the point of the tests is that the ONE remaining
/// implementation still behaves exactly as the copies did - the clamp, the deliberate
/// double precision in the 32-bit path, and bit-exactness at unity gain.
/// </summary>
public class PcmMathTests
{
    private static byte[] S16(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        samples.AsSpan().CopyTo(MemoryMarshal.Cast<byte, short>(bytes.AsSpan()));
        return bytes;
    }

    private static short[] ReadS16(byte[] bytes) => MemoryMarshal.Cast<byte, short>(bytes).ToArray();

    private static byte[] S32(params int[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        samples.AsSpan().CopyTo(MemoryMarshal.Cast<byte, int>(bytes.AsSpan()));
        return bytes;
    }

    private static int[] ReadS32(byte[] bytes) => MemoryMarshal.Cast<byte, int>(bytes).ToArray();

    [Fact]
    public void ScaleS16_halves_every_sample()
    {
        var src = S16(0, 100, -100, 1000, -1000);
        var dst = new byte[src.Length];

        PcmMath.ScaleS16(src, dst, 0.5f);

        Assert.Equal([0, 50, -50, 500, -500], ReadS16(dst));
    }

    [Fact]
    public void ScaleS16_clamps_instead_of_wrapping()
    {
        // A ReplayGain boost can push past full scale; wrapping would turn a loud
        // passage into digital noise, so the kernel must saturate.
        var src = S16(short.MaxValue, short.MinValue, 20000, -20000);
        var dst = new byte[src.Length];

        PcmMath.ScaleS16(src, dst, 4f);

        Assert.Equal([short.MaxValue, short.MinValue, short.MaxValue, short.MinValue], ReadS16(dst));
    }

    [Fact]
    public void ScaleS32_computes_in_double_not_float()
    {
        // Locals, not consts: the compiler folds constant float expressions at
        // (possibly higher) compile-time precision, which would hide the very
        // difference this test exists to pin.
        var sample = 2_000_000_001;
        var gain = 0.3f;
        var src = S32(sample);
        var dst = new byte[src.Length];

        PcmMath.ScaleS32(src, dst, gain);

        // What the implementation promises: double math, truncated.
        Assert.Equal((int)Math.Clamp(sample * (double)gain, int.MinValue, int.MaxValue), ReadS32(dst)[0]);

        // And that promise is load-bearing - float32's 24-bit mantissa can't hold
        // this sample, so a float multiply lands somewhere else entirely.
        Assert.NotEqual((int)(sample * gain), ReadS32(dst)[0]);
    }

    [Fact]
    public void ScaleS32_clamps_at_the_rails()
    {
        var src = S32(int.MaxValue, int.MinValue);
        var dst = new byte[src.Length];

        PcmMath.ScaleS32(src, dst, 2f);

        Assert.Equal([int.MaxValue, int.MinValue], ReadS32(dst));
    }

    [Fact]
    public void Unity_gain_is_bit_exact_in_both_widths()
    {
        // Callers skip scaling entirely at unity, but the kernel must not be the thing
        // that breaks bit-perfect playback if one ever does call it with 1.0.
        var s16 = S16(0, 1, -1, 12345, -12345, short.MaxValue, short.MinValue);
        var out16 = new byte[s16.Length];
        PcmMath.ScaleS16(s16, out16, 1f);
        Assert.Equal(s16, out16);

        var s32 = S32(0, 1, -1, 123456789, -123456789, int.MaxValue, int.MinValue);
        var out32 = new byte[s32.Length];
        PcmMath.ScaleS32(s32, out32, 1f);
        Assert.Equal(s32, out32);
    }

    [Fact]
    public void Silence_is_produced_at_zero_gain()
    {
        var src = S16(short.MaxValue, short.MinValue, 1234);
        var dst = new byte[src.Length];

        PcmMath.ScaleS16(src, dst, 0f);

        Assert.Equal([0, 0, 0], ReadS16(dst));
    }
}
