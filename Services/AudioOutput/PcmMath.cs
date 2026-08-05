// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.InteropServices;

namespace OrgZ.Services.AudioOutput;

/// <summary>
/// The sample-scaling kernels every audio path shares. These existed as verbatim copies in
/// the bus and in each platform sink - the one piece of code where a drift between copies
/// would mean a QUIET divergence in what a listener hears, and where "bit-perfect at unity"
/// has to hold identically everywhere.
///
/// Unity gain is handled by the CALLERS (they skip scaling and pass the source bytes through
/// untouched), which is what keeps the bit-perfect path bit-perfect. Deliberately scalar:
/// these run ~20×/second on a few KB, so there is no throughput problem to solve, and this
/// is the last code in the app worth trading exactness for cleverness.
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
        // Double math: float32's 24-bit mantissa can't hold a scaled 32-bit sample
        // exactly; double keeps the hi-res path honest under gain.
        var src = MemoryMarshal.Cast<byte, int>(source);
        var dst = MemoryMarshal.Cast<byte, int>(dest);
        for (var i = 0; i < src.Length; i++)
        {
            dst[i] = (int)Math.Clamp(src[i] * (double)gain, int.MinValue, int.MaxValue);
        }
    }
}
