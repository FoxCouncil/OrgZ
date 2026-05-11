// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services.AudioVisualization;

/// <summary>
/// Accumulates decoded stereo PCM samples into fixed-size FFT windows and
/// exposes normalized per-band magnitudes for visualizers.  Everything here is
/// pure C# — no native interop — so it runs on whatever thread feeds it.
/// </summary>
/// <remarks>
/// <para>
/// The feeder (typically <see cref="AudioTap"/>) pushes whatever chunk size
/// LibVLC hands it; this class accumulates into a <see cref="FftSize"/>-point
/// window, applies a Hann window to reduce spectral leakage, runs a radix-2
/// Cooley-Tukey FFT, then buckets the magnitude-squared output into
/// logarithmically-spaced bands suitable for VU-meter display.
/// </para>
/// <para>
/// Band levels are smoothed with per-band lerp so values don't flicker on
/// every buffer boundary.  Callers read via <see cref="CopyBands"/> which is
/// safe to call from any thread — a small lock guards the snapshot.
/// </para>
/// </remarks>
public sealed class AudioAnalyzer
{
    public const int FftSize = 512;
    public const int DefaultBandCount = 24;

    private readonly int _bandCount;
    private readonly float[] _windowCoefficients;
    private readonly int[] _bandEdges;
    private readonly float[] _accumulator = new float[FftSize];           // mono mix
    private readonly float[] _accumulatorL = new float[FftSize];          // left channel
    private readonly float[] _accumulatorR = new float[FftSize];          // right channel
    private readonly float[] _fftReal = new float[FftSize];
    private readonly float[] _fftImag = new float[FftSize];
    private readonly float[] _bandLevels;                                  // mono
    private readonly float[] _bandLevelsL;                                 // left
    private readonly float[] _bandLevelsR;                                 // right
    private readonly object _bandLock = new();
    private int _accumCount;
    private const float SmoothingFactor = 0.45f;

    // Base gain — multiplied by the adaptive factor below before reaching
    // the output bands.  History: 0.35 too hot, 0.28 still a touch hot,
    // 0.22 sits nicely — neutral-adaptive tracks peak around the upper
    // two-thirds of the meter and hot masters bounce around mid-meter.
    private const float BaseGain = 0.22f;

    // --- Loudness normalization (slow AGC) -----------------------------
    // Loud commercial masters slam every bar; quiet acoustic tracks leave
    // the meter lifeless.  We track a slow-moving RMS reference of the
    // incoming audio and divide it into a target loudness to get an
    // adaptive multiplier.  The multiplier is clamped so near-silence
    // doesn't blow up into noise and brick-walled material never gets
    // full attenuation.
    //
    // Rates are chosen to be slow enough that individual beats and drum
    // transients don't modulate the scale (which would "pump" the meter)
    // while still being responsive enough that the first second of a new
    // track lands the meter at a sensible level:
    //   FftSize=512 @ 44.1 kHz ≈ 86 windows/sec
    //   Rise α = 0.04  → ~95% in ≈ 75 frames ≈ 0.87 s  (attack)
    //   Fall α = 0.008 → ~95% in ≈ 375 frames ≈ 4.4 s  (release)
    private const float TargetRms = 0.12f;       // desired input loudness
    private const float MinGainMultiplier = 0.45f; // hottest masters get ~0.12 base output
    private const float MaxGainMultiplier = 2.0f;  // whispers boosted up to this much
    private const float ReferenceRise = 0.04f;
    private const float ReferenceFall = 0.008f;
    private const float ReferenceSeed = TargetRms;
    private float _referenceLevel = ReferenceSeed;

    public AudioAnalyzer(int bandCount = DefaultBandCount)
    {
        if (bandCount < 1 || bandCount > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(bandCount));
        }

        _bandCount = bandCount;
        _bandLevels = new float[bandCount];
        _bandLevelsL = new float[bandCount];
        _bandLevelsR = new float[bandCount];
        _windowCoefficients = BuildHannWindow(FftSize);
        _bandEdges = BuildLogBandEdges(FftSize / 2, bandCount);
    }

    public int BandCount => _bandCount;

    /// <summary>
    /// Copies the current smoothed band levels into <paramref name="destination"/>.
    /// Values are normalized to roughly 0..1 but may occasionally exceed 1 for
    /// loud material; callers should clamp when rendering.
    /// </summary>
    public void CopyBands(Span<float> destination)
    {
        var n = Math.Min(destination.Length, _bandCount);
        lock (_bandLock)
        {
            for (int i = 0; i < n; i++)
            {
                destination[i] = _bandLevels[i];
            }
        }
    }

    /// <summary>
    /// Copies separate L + R band levels for stereo-aware visualizations.
    /// Both destinations are bounded by <see cref="BandCount"/> and the
    /// caller's span length.
    /// </summary>
    public void CopyBandsStereo(Span<float> left, Span<float> right)
    {
        lock (_bandLock)
        {
            var n = Math.Min(Math.Min(left.Length, right.Length), _bandCount);
            for (int i = 0; i < n; i++)
            {
                left[i] = _bandLevelsL[i];
                right[i] = _bandLevelsR[i];
            }
        }
    }

    /// <summary>
    /// Clears accumulated state.  Call when playback stops / changes track so
    /// the analyzer doesn't blend samples across a seek boundary.
    /// </summary>
    public void Reset()
    {
        _accumCount = 0;
        Array.Clear(_accumulator);
        Array.Clear(_accumulatorL);
        Array.Clear(_accumulatorR);
        // Reseed the loudness reference so the adaptive gain starts from
        // a neutral state on a seek / track change instead of carrying the
        // previous track's loudness estimate into the first few seconds.
        _referenceLevel = ReferenceSeed;
        lock (_bandLock)
        {
            Array.Clear(_bandLevels);
            Array.Clear(_bandLevelsL);
            Array.Clear(_bandLevelsR);
        }
    }

    /// <summary>
    /// Feeds interleaved stereo float samples into the analyzer.  The
    /// analyzer mixes down to mono internally, accumulates an
    /// <see cref="FftSize"/>-point window, then runs one FFT pass when the
    /// window fills.
    /// </summary>
    public void FeedInterleavedStereo(ReadOnlySpan<float> interleaved)
    {
        for (int i = 0; i + 1 < interleaved.Length; i += 2)
        {
            var l = interleaved[i];
            var r = interleaved[i + 1];
            _accumulator[_accumCount] = (l + r) * 0.5f;
            _accumulatorL[_accumCount] = l;
            _accumulatorR[_accumCount] = r;
            _accumCount++;

            if (_accumCount >= FftSize)
            {
                ComputeAllChannels();
                _accumCount = 0;
            }
        }
    }

    private void ComputeAllChannels()
    {
        // Update loudness reference ONCE per window using the mono mix, then
        // apply the same adaptive gain to all three channel paths so L / R /
        // mono share the same scale (otherwise the left + right meters would
        // drift independently against each other).
        float rms = ComputeRms(_accumulator);
        if (rms > _referenceLevel)
        {
            _referenceLevel += (rms - _referenceLevel) * ReferenceRise;
        }
        else
        {
            _referenceLevel += (rms - _referenceLevel) * ReferenceFall;
        }

        // target / current → boost when reference is low, cut when high.
        // Clamp so digital-silence windows (reference → 0) don't blow up.
        float adaptive = Math.Clamp(TargetRms / MathF.Max(_referenceLevel, 0.002f), MinGainMultiplier, MaxGainMultiplier);
        float effectiveGain = BaseGain * adaptive;

        ComputeWindow(_accumulator, _bandLevels, effectiveGain);
        ComputeWindow(_accumulatorL, _bandLevelsL, effectiveGain);
        ComputeWindow(_accumulatorR, _bandLevelsR, effectiveGain);
    }

    private static float ComputeRms(float[] buffer)
    {
        float sumSq = 0f;
        for (int i = 0; i < buffer.Length; i++)
        {
            sumSq += buffer[i] * buffer[i];
        }
        return MathF.Sqrt(sumSq / buffer.Length);
    }

    private void ComputeWindow(float[] source, float[] destBands, float gain)
    {
        for (int i = 0; i < FftSize; i++)
        {
            _fftReal[i] = source[i] * _windowCoefficients[i];
            _fftImag[i] = 0f;
        }

        Fft(_fftReal, _fftImag);

        Span<float> rawBands = stackalloc float[_bandCount];
        for (int b = 0; b < _bandCount; b++)
        {
            int start = Math.Max(1, _bandEdges[b]);
            int end = Math.Max(start + 1, _bandEdges[b + 1]);
            float sum = 0f;
            for (int k = start; k < end; k++)
            {
                sum += MathF.Sqrt(_fftReal[k] * _fftReal[k] + _fftImag[k] * _fftImag[k]);
            }
            rawBands[b] = (sum / (end - start)) * gain;
        }

        lock (_bandLock)
        {
            for (int b = 0; b < _bandCount; b++)
            {
                var target = rawBands[b];
                if (target > destBands[b])
                {
                    destBands[b] = target;
                }
                else
                {
                    destBands[b] += (target - destBands[b]) * SmoothingFactor;
                }
            }
        }
    }

    /// <summary>
    /// In-place radix-2 Cooley-Tukey FFT.  Input arrays are modified to hold
    /// the transform.  Length must be a power of two.
    /// </summary>
    internal static void Fft(float[] real, float[] imag)
    {
        int n = real.Length;
        int log2N = (int)Math.Log2(n);

        // Bit-reversal permutation — swap each pair once.
        for (int i = 0; i < n; i++)
        {
            int j = BitReverse(i, log2N);
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        // Butterflies: log2(n) passes, each doubling the sub-transform size.
        for (int s = 1; s <= log2N; s++)
        {
            int m = 1 << s;
            int mh = m >> 1;
            float wmReal = MathF.Cos(-2f * MathF.PI / m);
            float wmImag = MathF.Sin(-2f * MathF.PI / m);

            for (int k = 0; k < n; k += m)
            {
                float wReal = 1f;
                float wImag = 0f;
                for (int j = 0; j < mh; j++)
                {
                    int i1 = k + j;
                    int i2 = i1 + mh;

                    float tReal = wReal * real[i2] - wImag * imag[i2];
                    float tImag = wReal * imag[i2] + wImag * real[i2];

                    float uReal = real[i1];
                    float uImag = imag[i1];

                    real[i1] = uReal + tReal;
                    imag[i1] = uImag + tImag;
                    real[i2] = uReal - tReal;
                    imag[i2] = uImag - tImag;

                    float nextWReal = wReal * wmReal - wImag * wmImag;
                    wImag = wReal * wmImag + wImag * wmReal;
                    wReal = nextWReal;
                }
            }
        }
    }

    private static int BitReverse(int x, int bits)
    {
        int result = 0;
        for (int i = 0; i < bits; i++)
        {
            result = (result << 1) | (x & 1);
            x >>= 1;
        }
        return result;
    }

    private static float[] BuildHannWindow(int size)
    {
        var w = new float[size];
        for (int i = 0; i < size; i++)
        {
            w[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (size - 1)));
        }
        return w;
    }

    /// <summary>
    /// Computes logarithmically-spaced band edges across bins 1..<paramref name="binCount"/>.
    /// Returns <paramref name="bandCount"/> + 1 edges so band <c>b</c> covers
    /// bins <c>[edges[b], edges[b+1])</c>.
    /// </summary>
    internal static int[] BuildLogBandEdges(int binCount, int bandCount)
    {
        var edges = new int[bandCount + 1];
        double logMin = Math.Log(1);
        double logMax = Math.Log(Math.Max(2, binCount));

        for (int i = 0; i <= bandCount; i++)
        {
            double t = i / (double)bandCount;
            edges[i] = (int)Math.Round(Math.Exp(logMin + t * (logMax - logMin)));
        }

        // Guarantee monotonic strictly-increasing edges so empty bands don't
        // happen at low frequencies where the log curve bunches up.
        for (int i = 1; i < edges.Length; i++)
        {
            if (edges[i] <= edges[i - 1])
            {
                edges[i] = edges[i - 1] + 1;
            }
        }

        return edges;
    }
}
