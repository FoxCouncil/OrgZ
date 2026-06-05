// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Numerics;

namespace OrgZ.Services.AudioVisualization;

/// <summary>
/// Accumulates decoded stereo PCM samples into fixed-size FFT windows and
/// exposes normalized per-band magnitudes for visualizers.  Everything here is
/// pure C# - no native interop - so it runs on whatever thread feeds it.
/// </summary>
/// <remarks>
/// <para>
/// The feeder (typically <see cref="AudioTap"/>) pushes whatever chunk size
/// LibVLC hands it; this class accumulates into a <see cref="FftSize"/>-point
/// window with 50% overlap (hop = <see cref="HopSize"/>), applies a Hann
/// window to reduce spectral leakage, runs a packed-real Cooley-Tukey FFT
/// at half the complex-FFT cost, then buckets the magnitude output into
/// logarithmically-spaced bands suitable for VU-meter display. Band sums
/// are vectorized with <see cref="Vector{T}"/> for SIMD throughput.
/// </para>
/// <para>
/// Band levels are smoothed with per-band lerp so values don't flicker on
/// every buffer boundary.  Callers read via <see cref="CopyBands"/> which is
/// safe to call from any thread - a small lock guards the snapshot. The mono
/// band path is derived from the L+R stereo bands post-FFT, so the hot
/// audio-thread cost is two FFTs per window instead of three.
/// </para>
/// </remarks>
public sealed class AudioAnalyzer
{
    public const int FftSize = 512;
    public const int DefaultBandCount = 24;

    // 50% overlap: emit a window every HopSize samples instead of every
    // FftSize. ~172 windows/sec at 44.1 kHz instead of 86 - the meter updates
    // twice as often, which is the single biggest factor in "tightness".
    public const int HopSize = FftSize / 2;

    // Packed-real FFT halves the work: pack N real samples as N/2 complex
    // samples, run an N/2-point complex FFT, then unpack into the N-point
    // result. We never compute the full complex N-point FFT on real input.
    private const int HalfFftSize = FftSize / 2;

    private readonly int _bandCount;
    private readonly float[] _windowCoefficients;
    private readonly int[] _bandEdges;
    private readonly float[] _accumulator = new float[FftSize];           // mono mix (RMS only)
    private readonly float[] _accumulatorL = new float[FftSize];          // left channel
    private readonly float[] _accumulatorR = new float[FftSize];          // right channel

    // Packed-real working buffers: size HalfFftSize complex samples per channel.
    private readonly float[] _packedRealL = new float[HalfFftSize];
    private readonly float[] _packedImagL = new float[HalfFftSize];
    private readonly float[] _packedRealR = new float[HalfFftSize];
    private readonly float[] _packedImagR = new float[HalfFftSize];

    // Unpacked spectrum buffers: bins 1..HalfFftSize used by binning.
    // Sized FftSize+1 to safely hold the Nyquist bin at index HalfFftSize.
    private readonly float[] _spectrumRealL = new float[FftSize + 1];
    private readonly float[] _spectrumImagL = new float[FftSize + 1];
    private readonly float[] _spectrumRealR = new float[FftSize + 1];
    private readonly float[] _spectrumImagR = new float[FftSize + 1];

    // Max-since-read band buffers. The audio thread writes the per-window peak
    // for each band; the renderer reads via CopyBands*, which atomically returns
    // the peak and zeroes it so the next read sees only fresh data.
    private readonly float[] _bandLevels;                                  // mono (derived from L+R)
    private readonly float[] _bandLevelsL;                                 // left
    private readonly float[] _bandLevelsR;                                 // right
    private readonly object _bandLock = new();
    private int _accumCount;

    // Precomputed tables for the N/2-point complex FFT used inside the
    // packed-real path. Built once in the constructor.
    private readonly int[] _halfBitReverse;
    private readonly float[] _halfTwiddleReal;
    private readonly float[] _halfTwiddleImag;
    private readonly int _halfLog2N;

    // Real-FFT unpack twiddles: W_N^k = exp(-2πj k / N) for k = 0..N/2-1.
    // Used once per window to recombine even/odd half-FFT results into the
    // first N/2+1 bins of the full N-point spectrum.
    private readonly float[] _unpackTwiddleReal;
    private readonly float[] _unpackTwiddleImag;

    // Base gain - multiplied by the adaptive factor below before reaching
    // the output bands.  History: 0.35 too hot, 0.28 still a touch hot,
    // 0.22 sits nicely - neutral-adaptive tracks peak around the upper
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
    // track lands the meter at a sensible level. We update once per HopSize
    // instead of per full window now (~172/sec instead of ~86/sec) so the
    // attack/release per-sample coefficients are halved to keep the same
    // wall-clock time constants.
    //   FftSize=512, HopSize=256 @ 44.1 kHz ≈ 172 windows/sec
    //   Rise α = 0.02  → ~95% in ≈ 150 frames ≈ 0.87 s  (attack)
    //   Fall α = 0.004 → ~95% in ≈ 750 frames ≈ 4.4 s  (release)
    private const float TargetRms = 0.12f;       // desired input loudness
    private const float MinGainMultiplier = 0.45f; // hottest masters get ~0.12 base output
    private const float MaxGainMultiplier = 2.0f;  // whispers boosted up to this much
    private const float ReferenceRise = 0.02f;
    private const float ReferenceFall = 0.004f;
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

        _halfLog2N = (int)Math.Log2(HalfFftSize);
        _halfBitReverse = BuildBitReverseTable(HalfFftSize, _halfLog2N);
        (_halfTwiddleReal, _halfTwiddleImag) = BuildTwiddleTables(_halfLog2N);
        (_unpackTwiddleReal, _unpackTwiddleImag) = BuildUnpackTwiddles(FftSize);
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
                _bandLevels[i] = 0f;
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
                _bandLevelsL[i] = 0f;
                _bandLevelsR[i] = 0f;
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
                // 50% overlap: shift the second half down to the first half
                // and reset count to HopSize so the next FftSize-window has
                // the previous half plus a fresh HopSize samples.
                Array.Copy(_accumulator,  HopSize, _accumulator,  0, HopSize);
                Array.Copy(_accumulatorL, HopSize, _accumulatorL, 0, HopSize);
                Array.Copy(_accumulatorR, HopSize, _accumulatorR, 0, HopSize);
                _accumCount = HopSize;
            }
        }
    }

    private void ComputeAllChannels()
    {
        // Update loudness reference using the mono mix RMS (no FFT needed
        // here - just an O(N) sum-of-squares). Apply the same adaptive
        // gain to L and R so the two meters share the same scale.
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

        // Run real-input FFTs for L and R only. Mono bands are derived from
        // L+R below so the third FFT (~33% of FFT cost) is eliminated.
        FftReal(_accumulatorL, _packedRealL, _packedImagL, _spectrumRealL, _spectrumImagL);
        FftReal(_accumulatorR, _packedRealR, _packedImagR, _spectrumRealR, _spectrumImagR);

        Span<float> bandsL = stackalloc float[_bandCount];
        Span<float> bandsR = stackalloc float[_bandCount];
        BinSpectrum(_spectrumRealL, _spectrumImagL, bandsL, effectiveGain);
        BinSpectrum(_spectrumRealR, _spectrumImagR, bandsR, effectiveGain);

        lock (_bandLock)
        {
            for (int b = 0; b < _bandCount; b++)
            {
                float monoTarget = (bandsL[b] + bandsR[b]) * 0.5f;
                if (bandsL[b]  > _bandLevelsL[b]) _bandLevelsL[b] = bandsL[b];
                if (bandsR[b]  > _bandLevelsR[b]) _bandLevelsR[b] = bandsR[b];
                if (monoTarget > _bandLevels[b])  _bandLevels[b]  = monoTarget;
            }
        }
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

    /// <summary>
    /// Window + packed-real FFT: pack <paramref name="source"/> as a half-size
    /// complex sequence (even samples → real, odd → imag), run the half-size
    /// complex FFT, then unpack into the first N/2+1 bins of the full N-point
    /// spectrum at <paramref name="spectrumReal"/> / <paramref name="spectrumImag"/>.
    /// Bins 0 (DC) and N/2 (Nyquist) are written but the band-binning loop
    /// starts at bin 1, so DC doesn't pollute low bands.
    /// </summary>
    private void FftReal(float[] source, float[] packedReal, float[] packedImag, float[] spectrumReal, float[] spectrumImag)
    {
        int M = HalfFftSize;
        var win = _windowCoefficients;

        // Pack windowed even/odd samples into the complex buffer.
        for (int n = 0; n < M; n++)
        {
            int idx = n << 1;
            packedReal[n] = source[idx]     * win[idx];
            packedImag[n] = source[idx + 1] * win[idx + 1];
        }

        // Half-size complex FFT in place.
        FftHalf(packedReal, packedImag);

        // Unpack bins 1..M-1 using:
        //   Yk = packed[k], YMk = packed[M-k]
        //   Ar = 0.5*(Yr + Mr),  Ai = 0.5*(Yi - Mi)         (X_e[k])
        //   Br = 0.5*(Yi + Mi),  Bi = 0.5*(Mr - Yr)         (X_o[k])
        //   W = (Wr, Wi) = exp(-2πj k / N)
        //   X[k]_real = Ar + Wr*Br - Wi*Bi
        //   X[k]_imag = Ai + Wr*Bi + Wi*Br
        var twR = _unpackTwiddleReal;
        var twI = _unpackTwiddleImag;
        for (int k = 1; k < M; k++)
        {
            float Yr = packedReal[k];
            float Yi = packedImag[k];
            float Mr = packedReal[M - k];
            float Mi = packedImag[M - k];

            float Ar = 0.5f * (Yr + Mr);
            float Ai = 0.5f * (Yi - Mi);
            float Br = 0.5f * (Yi + Mi);
            float Bi = 0.5f * (Mr - Yr);

            float Wr = twR[k];
            float Wi = twI[k];

            spectrumReal[k] = Ar + Wr * Br - Wi * Bi;
            spectrumImag[k] = Ai + Wr * Bi + Wi * Br;
        }

        // Special bins. DC and Nyquist are both purely real after the unpack:
        //   X[0]   = Re(Y[0]) + Im(Y[0])
        //   X[M]   = Re(Y[0]) - Im(Y[0])
        spectrumReal[0] = packedReal[0] + packedImag[0];
        spectrumImag[0] = 0f;
        spectrumReal[M] = packedReal[0] - packedImag[0];
        spectrumImag[M] = 0f;
    }

    private void BinSpectrum(float[] spectrumReal, float[] spectrumImag, Span<float> dest, float gain)
    {
        for (int b = 0; b < _bandCount; b++)
        {
            int start = Math.Max(1, _bandEdges[b]);
            int end = Math.Max(start + 1, _bandEdges[b + 1]);
            float sum = SumMagnitudes(spectrumReal, spectrumImag, start, end);
            dest[b] = (sum / (end - start)) * gain;
        }
    }

    /// <summary>
    /// Sums <c>sqrt(re² + im²)</c> across bins [start, end). Vectorized with
    /// <see cref="Vector{T}"/> when the run is wide enough - on AVX2 that's
    /// 8 magnitudes per pass through the inner loop. Tail handled scalar.
    /// </summary>
    private static float SumMagnitudes(float[] real, float[] imag, int start, int end)
    {
        int width = Vector<float>.Count;
        Vector<float> vsum = Vector<float>.Zero;
        int k = start;
        int simdEnd = start + ((end - start) / width) * width;
        for (; k < simdEnd; k += width)
        {
            var vr = new Vector<float>(real, k);
            var vi = new Vector<float>(imag, k);
            vsum += Vector.SquareRoot(vr * vr + vi * vi);
        }
        float sum = Vector.Sum(vsum);
        for (; k < end; k++)
        {
            sum += MathF.Sqrt(real[k] * real[k] + imag[k] * imag[k]);
        }
        return sum;
    }

    /// <summary>
    /// In-place radix-2 Cooley-Tukey FFT of size <see cref="HalfFftSize"/>,
    /// using precomputed bit-reverse and twiddle tables. Used by the
    /// packed-real path; not exposed publicly (the static <see cref="Fft"/>
    /// is the public test-facing API and works on any power-of-two size).
    /// </summary>
    private void FftHalf(float[] real, float[] imag)
    {
        int n = HalfFftSize;
        var bitRev = _halfBitReverse;
        var twR = _halfTwiddleReal;
        var twI = _halfTwiddleImag;

        for (int i = 0; i < n; i++)
        {
            int j = bitRev[i];
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        int twIndex = 0;
        for (int s = 1; s <= _halfLog2N; s++)
        {
            int m = 1 << s;
            int mh = m >> 1;
            float wmReal = twR[twIndex];
            float wmImag = twI[twIndex];
            twIndex++;

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

    private static int[] BuildBitReverseTable(int n, int bits)
    {
        var table = new int[n];
        for (int i = 0; i < n; i++)
        {
            table[i] = BitReverse(i, bits);
        }
        return table;
    }

    private static (float[] real, float[] imag) BuildTwiddleTables(int log2N)
    {
        var real = new float[log2N];
        var imag = new float[log2N];
        for (int s = 1; s <= log2N; s++)
        {
            int m = 1 << s;
            real[s - 1] = MathF.Cos(-2f * MathF.PI / m);
            imag[s - 1] = MathF.Sin(-2f * MathF.PI / m);
        }
        return (real, imag);
    }

    private static (float[] real, float[] imag) BuildUnpackTwiddles(int n)
    {
        // W_N^k = exp(-2πj k / N) for k = 0..N/2-1.
        int half = n / 2;
        var real = new float[half];
        var imag = new float[half];
        for (int k = 0; k < half; k++)
        {
            float ang = -2f * MathF.PI * k / n;
            real[k] = MathF.Cos(ang);
            imag[k] = MathF.Sin(ang);
        }
        return (real, imag);
    }

    /// <summary>
    /// In-place radix-2 Cooley-Tukey FFT.  Input arrays are modified to hold
    /// the transform.  Length must be a power of two. Public for tests; the
    /// hot audio path uses the precomputed-table instance variant.
    /// </summary>
    internal static void Fft(float[] real, float[] imag)
    {
        int n = real.Length;
        int log2N = (int)Math.Log2(n);

        // Bit-reversal permutation - swap each pair once.
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
