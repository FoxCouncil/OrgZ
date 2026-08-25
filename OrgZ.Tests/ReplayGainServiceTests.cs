// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// The ReplayGain math and ffmpeg-output parsing - the pure pieces of "Sound Check". The full
/// measure-and-tag round trip runs an external ffmpeg and is covered by the tool-gated smoke tests.
/// </summary>
public class ReplayGainServiceTests
{
    // ===== Gain math: bring a track to the -14 LUFS reference =====

    [Theory]
    [InlineData(-8.0, -6.0)]    // a loud modern master (-8 LUFS) is turned DOWN 6 dB
    [InlineData(-18.0, 4.0)]    // a quiet track (-18 LUFS) is turned UP 4 dB
    [InlineData(-14.0, 0.0)]    // already at reference - no change
    public void Gain_targets_minus_14_lufs(double integratedLufs, double expectedGain)
    {
        Assert.Equal(expectedGain, ReplayGainService.GainFromLoudness(integratedLufs), 3);
    }

    [Fact]
    public void Attenuation_is_unbounded_but_boost_is_capped()
    {
        // Turning a track DOWN can never distort it, so a brickwalled master gets the full cut.
        Assert.Equal(-16.0, ReplayGainService.GainFromLoudness(2.0), 3);

        // Lifting one can, so a near-silent recording is raised, not amplified into its own hiss.
        Assert.Equal(6.0, ReplayGainService.GainFromLoudness(-40.0), 3);
    }

    [Fact]
    public void A_boost_is_held_back_so_the_true_peak_cannot_clip()
    {
        // -20 LUFS wants +6 dB, but the track already peaks at -2 dBFS: the playback scaler
        // CLAMPS rather than wraps, so the untethered boost would hard-clip every peak.
        // Ceiling is -1 dBFS, so only +1 dB is available.
        Assert.Equal(1.0, ReplayGainService.GainFromLoudness(-20.0, truePeakDbfs: -2.0), 3);

        // Plenty of headroom - the loudness target wins, not the peak.
        Assert.Equal(4.0, ReplayGainService.GainFromLoudness(-18.0, truePeakDbfs: -30.0), 3);

        // A track already over the ceiling is pushed DOWN to it even when loudness says otherwise.
        Assert.Equal(-1.0, ReplayGainService.GainFromLoudness(-14.0, truePeakDbfs: 0.0), 3);

        // No peak reported - the limit simply isn't applied, rather than a peak being assumed.
        Assert.Equal(4.0, ReplayGainService.GainFromLoudness(-18.0, truePeakDbfs: null), 3);
    }

    [Fact]
    public void Parses_the_true_peak_from_the_summary()
    {
        var stderr = """
            [Parsed_ebur128_0 @ 0x55] Summary:

              Integrated loudness:
                I:         -9.2 LUFS
              True peak:
                Peak:      -0.3 dBFS
            """;
        Assert.Equal(-0.3, ReplayGainService.ParseTruePeak(stderr));
        Assert.Null(ReplayGainService.ParseTruePeak("no peak here"));
    }

    // ===== Parsing ffmpeg's ebur128 summary (stderr) =====

    [Fact]
    public void Parses_the_final_integrated_loudness_from_the_summary()
    {
        // ebur128 prints running frames then a final Summary block; the last "I:" is the answer.
        var stderr = """
            [Parsed_ebur128_0 @ 0x55] t: 5   I: -21.3 LUFS
            [Parsed_ebur128_0 @ 0x55] t: 10  I: -20.8 LUFS
            [Parsed_ebur128_0 @ 0x55] Summary:

              Integrated loudness:
                I:         -20.5 LUFS
                Threshold: -30.6 LUFS
            """;
        Assert.Equal(-20.5, ReplayGainService.ParseIntegratedLoudness(stderr));
    }

    [Fact]
    public void Silence_and_garbage_yield_no_measurement()
    {
        Assert.Null(ReplayGainService.ParseIntegratedLoudness("I: -70.0 LUFS"));   // ebur128's silence floor
        Assert.Null(ReplayGainService.ParseIntegratedLoudness("no loudness here"));
        Assert.Null(ReplayGainService.ParseIntegratedLoudness(""));
    }

    [Fact]
    public void A_normal_measurement_just_above_the_floor_is_kept()
    {
        Assert.Equal(-69.5, ReplayGainService.ParseIntegratedLoudness("I: -69.5 LUFS"));
    }
}
