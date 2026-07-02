// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// The ReplayGain math and ffmpeg-output parsing - the pure pieces of "Sound Check". The full
/// measure-and-tag round trip runs an external ffmpeg and is covered by the tool-gated smoke tests.
/// </summary>
public class ReplayGainServiceTests
{
    // ===== Gain math: bring a track to the -18 LUFS reference =====

    [Theory]
    [InlineData(-14.0, -4.0)]   // a loud master (-14 LUFS) is turned DOWN 4 dB
    [InlineData(-23.0, 5.0)]    // a quiet track (-23 LUFS) is turned UP 5 dB
    [InlineData(-18.0, 0.0)]    // already at reference - no change
    public void Gain_targets_minus_18_lufs(double integratedLufs, double expectedGain)
    {
        Assert.Equal(expectedGain, ReplayGainService.GainFromLoudness(integratedLufs), 3);
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
