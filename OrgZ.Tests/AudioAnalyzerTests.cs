// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.AudioVisualization;

namespace OrgZ.Tests;

public class AudioAnalyzerTests
{
    [Fact]
    public void Fft_Of_DC_Concentrates_Energy_In_Bin_0()
    {
        var real = new float[64];
        var imag = new float[64];
        for (int i = 0; i < real.Length; i++)
        {
            real[i] = 1.0f;
        }

        AudioAnalyzer.Fft(real, imag);

        // DC component (bin 0) should hold all the energy (n * amplitude).
        Assert.Equal(64f, real[0], 3);
        Assert.Equal(0f, imag[0], 3);

        for (int i = 1; i < real.Length; i++)
        {
            Assert.True(MathF.Abs(real[i]) < 1e-3f, $"Bin {i} real {real[i]} should be ~0");
            Assert.True(MathF.Abs(imag[i]) < 1e-3f, $"Bin {i} imag {imag[i]} should be ~0");
        }
    }

    [Fact]
    public void Fft_Of_Sine_Concentrates_Energy_In_Matching_Bin()
    {
        // Pure sine at bin k=5 of a 64-point transform.
        int n = 64;
        int k = 5;
        var real = new float[n];
        var imag = new float[n];
        for (int i = 0; i < n; i++)
        {
            real[i] = MathF.Sin(2f * MathF.PI * k * i / n);
        }

        AudioAnalyzer.Fft(real, imag);

        // Energy should be concentrated at bins k and n-k (complex conjugate).
        // Each carries magnitude n/2.
        float magK = MathF.Sqrt(real[k] * real[k] + imag[k] * imag[k]);
        float magNminusK = MathF.Sqrt(real[n - k] * real[n - k] + imag[n - k] * imag[n - k]);

        Assert.Equal(n / 2f, magK, 1);
        Assert.Equal(n / 2f, magNminusK, 1);

        // Every other bin should be near zero.
        for (int i = 0; i < n; i++)
        {
            if (i == k || i == n - k) continue;
            float mag = MathF.Sqrt(real[i] * real[i] + imag[i] * imag[i]);
            Assert.True(mag < 1e-2f, $"Bin {i} magnitude {mag} should be ~0");
        }
    }

    [Fact]
    public void LogBandEdges_Are_Monotonic_And_Cover_Range()
    {
        var edges = AudioAnalyzer.BuildLogBandEdges(binCount: 256, bandCount: 24);

        Assert.Equal(25, edges.Length);
        Assert.True(edges[0] >= 1);
        Assert.True(edges[^1] <= 256);

        for (int i = 1; i < edges.Length; i++)
        {
            Assert.True(edges[i] > edges[i - 1], $"Edge {i}={edges[i]} not greater than edge {i - 1}={edges[i - 1]}");
        }
    }

    [Fact]
    public void FeedSamples_Produces_NonZero_Bands_For_Mid_Frequency_Sine()
    {
        var analyzer = new AudioAnalyzer();

        // Generate 2 seconds of a 1 kHz sine at full scale in stereo interleaved.
        int sampleRate = 44100;
        int frameCount = sampleRate * 2;
        var samples = new float[frameCount * 2];
        float freq = 1000f;
        for (int i = 0; i < frameCount; i++)
        {
            float v = MathF.Sin(2f * MathF.PI * freq * i / sampleRate);
            samples[i * 2] = v;
            samples[i * 2 + 1] = v;
        }

        analyzer.FeedInterleavedStereo(samples);

        var bands = new float[analyzer.BandCount];
        analyzer.CopyBands(bands);

        // At least one band should have meaningful energy (1 kHz lands in the
        // middle bands of a log-spaced 22 kHz decomposition).
        float sum = 0;
        for (int i = 0; i < bands.Length; i++)
        {
            sum += bands[i];
        }
        Assert.True(sum > 0.1f, $"Expected non-trivial spectrum energy, got sum={sum}");

        // Find the peak band and verify it's not band 0 (DC) or the last band.
        int peak = 0;
        for (int i = 1; i < bands.Length; i++)
        {
            if (bands[i] > bands[peak]) peak = i;
        }
        Assert.True(peak > 0 && peak < bands.Length - 1, $"Peak band {peak} should be interior");
    }

    [Fact]
    public void Reset_Clears_Accumulated_Bands()
    {
        var analyzer = new AudioAnalyzer();
        var samples = new float[4096];
        for (int i = 0; i < samples.Length; i++) { samples[i] = MathF.Sin(i * 0.1f); }

        analyzer.FeedInterleavedStereo(samples);
        analyzer.Reset();

        var bands = new float[analyzer.BandCount];
        analyzer.CopyBands(bands);

        foreach (var b in bands)
        {
            Assert.Equal(0f, b);
        }
    }

    [Fact]
    public void CopyBands_Respects_Destination_Length()
    {
        var analyzer = new AudioAnalyzer(bandCount: 16);
        var small = new float[4];
        // Should not throw; just writes the first 4 band values.
        analyzer.CopyBands(small);
    }
}
