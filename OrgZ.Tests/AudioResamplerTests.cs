// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.AudioOutput;

namespace OrgZ.Tests;

/// <summary>
/// The resampler that lets hi-res tracks reach a fixed-rate sink (AirPlay is 44.1 kHz
/// only). The properties that matter are frame count, level preservation, continuity
/// across buffers, and - the whole reason it isn't linear interpolation - that content
/// above the output Nyquist is REMOVED rather than folded back down as aliasing.
/// </summary>
public class AudioResamplerTests
{
    /// <summary>Interleaved stereo 16-bit LE tone; both channels identical.</summary>
    private static byte[] Tone(int rate, double hz, int frames, double amplitude = 0.5)
    {
        var bytes = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            var s = (short)(Math.Sin(2 * Math.PI * hz * i / rate) * amplitude * short.MaxValue);
            bytes[i * 4] = (byte)(s & 0xFF);
            bytes[(i * 4) + 1] = (byte)((s >> 8) & 0xFF);
            bytes[(i * 4) + 2] = bytes[i * 4];
            bytes[(i * 4) + 3] = bytes[(i * 4) + 1];
        }
        return bytes;
    }

    private static double[] LeftChannel(List<byte> pcm)
    {
        var frames = pcm.Count / 4;
        var result = new double[frames];
        for (var i = 0; i < frames; i++)
        {
            result[i] = (short)(pcm[i * 4] | (pcm[(i * 4) + 1] << 8)) / 32768.0;
        }
        return result;
    }

    /// <summary>Energy at one frequency, via a single-bin Goertzel-style projection.</summary>
    private static double Magnitude(double[] samples, int rate, double hz)
    {
        double re = 0, im = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var angle = 2 * Math.PI * hz * i / rate;
            re += samples[i] * Math.Cos(angle);
            im += samples[i] * Math.Sin(angle);
        }
        return 2 * Math.Sqrt((re * re) + (im * im)) / samples.Length;
    }

    [Fact]
    public void Matching_rates_pass_through_untouched()
    {
        var resampler = new AudioResampler(44100, 44100, 2);
        Assert.True(resampler.IsPassthrough);

        var input = Tone(44100, 1000, 128);
        var output = new List<byte>();
        resampler.Process(input, output);

        Assert.Equal(input, output);
    }

    [Theory]
    [InlineData(96000)]
    [InlineData(48000)]
    [InlineData(88200)]
    [InlineData(192000)]
    public void Downsampling_produces_the_expected_frame_count(int sourceRate)
    {
        var resampler = new AudioResampler(sourceRate, 44100, 2);
        var seconds = 0.5;
        var input = Tone(sourceRate, 440, (int)(sourceRate * seconds));
        var output = new List<byte>();

        resampler.Process(input, output);

        // Within a kernel's worth of the ideal - the tail waits for the next buffer.
        var expected = 44100 * seconds;
        var actual = output.Count / 4.0;
        Assert.InRange(actual, expected - 80, expected + 80);
    }

    [Fact]
    public void Upsampling_produces_the_expected_frame_count()
    {
        var resampler = new AudioResampler(22050, 44100, 2);
        var input = Tone(22050, 440, 22050);
        var output = new List<byte>();

        resampler.Process(input, output);

        Assert.InRange(output.Count / 4.0, 44100 - 160, 44100 + 160);
    }

    [Fact]
    public void An_audible_tone_survives_downsampling_at_the_same_level()
    {
        // 1 kHz at half scale through 96k -> 44.1k must come out at 1 kHz, same amplitude.
        var resampler = new AudioResampler(96000, 44100, 2);
        var output = new List<byte>();
        resampler.Process(Tone(96000, 1000, 96000, amplitude: 0.5), output);

        var samples = LeftChannel(output);
        var magnitude = Magnitude(samples[2000..], 44100, 1000);

        Assert.InRange(magnitude, 0.45, 0.55);
    }

    [Fact]
    public void Content_above_the_output_nyquist_is_removed_not_folded_back()
    {
        // THE point of a windowed-sinc filter. A 30 kHz tone can't exist at 44.1 kHz
        // (Nyquist 22.05 kHz). Linear interpolation would alias it to 44100-30000 =
        // 14.1 kHz - squarely audible. It must be attenuated instead.
        var resampler = new AudioResampler(96000, 44100, 2);
        var output = new List<byte>();
        resampler.Process(Tone(96000, 30000, 96000, amplitude: 0.8), output);

        var samples = LeftChannel(output)[2000..];
        var aliasMagnitude = Magnitude(samples, 44100, 14100);

        // Well under a thousandth of the input amplitude - inaudible, not merely reduced.
        Assert.True(aliasMagnitude < 0.001, $"alias at 14.1 kHz was {aliasMagnitude:0.00000}, expected < 0.001");
    }

    [Fact]
    public void Streaming_in_small_buffers_matches_one_big_buffer()
    {
        // The audio thread delivers small chunks; state must carry across calls or every
        // boundary clicks. Same input split 64 ways must give (essentially) same output.
        var input = Tone(96000, 1000, 96000);

        var whole = new List<byte>();
        new AudioResampler(96000, 44100, 2).Process(input, whole);

        var streamed = new List<byte>();
        var chunked = new AudioResampler(96000, 44100, 2);
        var chunk = input.Length / 64;
        chunk -= chunk % 4;   // whole frames
        for (var offset = 0; offset < input.Length; offset += chunk)
        {
            chunked.Process(input.AsSpan(offset, Math.Min(chunk, input.Length - offset)), streamed);
        }

        Assert.InRange(Math.Abs(streamed.Count - whole.Count), 0, 8);

        var a = LeftChannel(whole);
        var b = LeftChannel(streamed);
        var compare = Math.Min(a.Length, b.Length);
        for (var i = 0; i < compare; i++)
        {
            Assert.InRange(Math.Abs(a[i] - b[i]), 0, 0.002);
        }
    }

    [Fact]
    public void Reset_drops_retained_audio()
    {
        var resampler = new AudioResampler(96000, 44100, 2);
        var first = new List<byte>();
        resampler.Process(Tone(96000, 1000, 4800), first);

        resampler.Reset();

        // After a reset the next buffer starts a fresh stream - it must fill from scratch
        // rather than continuing where the old position pointed.
        var second = new List<byte>();
        resampler.Process(Tone(96000, 1000, 4800), second);

        Assert.InRange(Math.Abs(second.Count - first.Count), 0, 8);
    }

    [Fact]
    public void Silence_in_gives_silence_out()
    {
        var resampler = new AudioResampler(96000, 44100, 2);
        var output = new List<byte>();
        resampler.Process(new byte[96000 * 4], output);

        Assert.All(LeftChannel(output), s => Assert.InRange(Math.Abs(s), 0, 0.0001));
    }

    [Fact]
    public void A_tiny_buffer_is_safe()
    {
        var resampler = new AudioResampler(96000, 44100, 2);
        var output = new List<byte>();

        resampler.Process([], output);
        resampler.Process(new byte[4], output);

        Assert.Empty(output);   // not enough to fill a kernel yet - no crash, no output
    }
}
