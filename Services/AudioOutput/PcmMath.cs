// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.InteropServices;

namespace OrgZ.Services.AudioOutput;

/// <summary>
/// The sample-scaling kernels every audio path shares. These were verbatim copies in the bus
/// and in each platform sink, where drift between copies would change what a listener hears
/// on one platform and not another.
///
/// Callers handle unity gain by skipping scaling entirely and passing the source bytes
/// through, which is what keeps the bit-perfect path bit-perfect. Scalar on purpose: these
/// run ~20×/second on a few KB, so there's no throughput problem to solve.
/// </summary>
internal static class PcmMath
{
    /// <summary>Scales interleaved signed 16-bit PCM by <paramref name="gain"/>, clamping to range.</summary>
    public static void ScaleS16(ReadOnlySpan<byte> source, Span<byte> dest, float gain)
    {
        var src = MemoryMarshal.Cast<byte, short>(source);
        var dst = MemoryMarshal.Cast<byte, short>(dest);
        for (var i = 0; i < src.Length; i++)
        {
            dst[i] = (short)Math.Clamp(src[i] * gain, short.MinValue, short.MaxValue);
        }
    }

    /// <summary>Scales interleaved signed 32-bit PCM by <paramref name="gain"/>, clamping to range.</summary>
    public static void ScaleS32(ReadOnlySpan<byte> source, Span<byte> dest, float gain)
    {
        // Double math: float32's 24-bit mantissa can't hold a scaled 32-bit sample exactly.
        var src = MemoryMarshal.Cast<byte, int>(source);
        var dst = MemoryMarshal.Cast<byte, int>(dest);
        for (var i = 0; i < src.Length; i++)
        {
            dst[i] = (int)Math.Clamp(src[i] * (double)gain, int.MinValue, int.MaxValue);
        }
    }
}
