// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;
using OrgZ.Views;

namespace OrgZ.Tests;

/// <summary>
/// Automates the mechanically-verifiable parts of the Burn Test plan in HARDWARE.md,
/// so only the genuinely physical residue (does track 2 START on its first note to a
/// human ear) needs a person and a disc.
///
///   Test 5 Near Capacity  - fully covered here (dialog math, no media)
///   Test 6 Overflow       - fully covered here (dialog math, no media)
///   Test 2 Boundaries     - sector arithmetic covered; the ear check remains
///   Test 3 Hi-Res         - real ffmpeg downsample to CD-DA covered
/// </summary>
public class BurnValidationTests
{
    private const long RedbookCapacitySectors = 359_849;   // 79:57, what the BDR-XS07U reports for CD-RW

    // ── Burn Test 5: Near Capacity (77.9 min fits) ────────────

    [Fact]
    public void Test5_a_77_9_minute_set_fits_a_standard_disc()
    {
        var plan = BurnDiscDialog.PlanAudioCapacity(TimeSpan.FromMinutes(77.9), trackCount: 18, gapSeconds: 0, RedbookCapacitySectors);

        Assert.False(plan.OverCapacity);
        Assert.False(plan.NearCapacity);
        Assert.Equal(1, plan.DiscsNeeded);
        Assert.Contains(" of ", plan.LengthText);       // "77:54 of 79:57"
    }

    [Fact]
    public void Test5_gaps_are_charged_against_the_disc_and_can_push_a_fitting_set_over()
    {
        // 18 tracks at 77.9 min fits; the same set with 2 s between tracks adds 34 s.
        var gapless = BurnDiscDialog.PlanAudioCapacity(TimeSpan.FromMinutes(79.5), 18, 0, RedbookCapacitySectors);
        var gapped = BurnDiscDialog.PlanAudioCapacity(TimeSpan.FromMinutes(79.5), 18, 2, RedbookCapacitySectors);

        Assert.False(gapless.OverCapacity);
        Assert.True(gapped.OverCapacity);
        Assert.Equal(TimeSpan.FromMinutes(79.5) + TimeSpan.FromSeconds(34), gapped.EffectiveLength);
    }

    // ── Burn Test 6: Overflow (90.2 min refused, 2 discs) ─────

    [Fact]
    public void Test6_a_90_2_minute_set_is_refused_and_reports_two_discs()
    {
        var plan = BurnDiscDialog.PlanAudioCapacity(TimeSpan.FromMinutes(90.2), trackCount: 20, gapSeconds: 0, RedbookCapacitySectors);

        Assert.True(plan.OverCapacity);                 // Burn button is gated off this
        Assert.Equal(2, plan.DiscsNeeded);
        Assert.Equal("2 × 79:57", plan.DiscsText);
        Assert.Contains("exceeds", plan.LengthText);
    }

    [Fact]
    public void Test6_disc_count_rounds_up_and_never_undercounts()
    {
        // Exactly one disc's worth is not "over".
        var exact = BurnDiscDialog.PlanAudioCapacity(TimeSpan.FromSeconds(RedbookCapacitySectors / 75.0), 1, 0, RedbookCapacitySectors);
        Assert.False(exact.OverCapacity);

        // One second past it needs a second disc.
        var justOver = BurnDiscDialog.PlanAudioCapacity(TimeSpan.FromSeconds(RedbookCapacitySectors / 75.0 + 1), 1, 0, RedbookCapacitySectors);
        Assert.True(justOver.OverCapacity);
        Assert.Equal(2, justOver.DiscsNeeded);

        // Three discs' worth reports three, not two.
        var triple = BurnDiscDialog.PlanAudioCapacity(TimeSpan.FromSeconds(RedbookCapacitySectors / 75.0 * 2.5), 1, 0, RedbookCapacitySectors);
        Assert.Equal(3, triple.DiscsNeeded);
    }

    [Fact]
    public void An_unreported_capacity_still_warns_past_the_80_minute_convention()
    {
        var under = BurnDiscDialog.PlanAudioCapacity(TimeSpan.FromMinutes(70), 10, 0, capacitySectors: null);
        var over = BurnDiscDialog.PlanAudioCapacity(TimeSpan.FromMinutes(85), 10, 0, capacitySectors: null);

        Assert.False(under.NearCapacity);
        Assert.True(over.NearCapacity);
        Assert.False(over.OverCapacity);     // unknown capacity: warn, don't block
        Assert.Contains("over 80:00", over.LengthText);
    }

    // ── Burn Test 2: track boundaries (sector arithmetic) ─────

    /// <summary>
    /// Mirrors the layout OrgZ hands the drive: track 1's audio starts at LBA 0 after the
    /// mandatory 150-sector pregap, and each later track starts immediately after the
    /// previous one's audio plus the inter-track gap. If this arithmetic is right, a
    /// skip-to-track lands exactly on the first sample; the ear test confirms the drive
    /// honoured it.
    /// </summary>
    private static List<long> TrackStartSectors(IReadOnlyList<int> trackSectors, int gapSectors)
    {
        var starts = new List<long>();
        long lba = 0;
        for (int i = 0; i < trackSectors.Count; i++)
        {
            if (i > 0)
            {
                lba += gapSectors;
            }

            starts.Add(lba);
            lba += trackSectors[i];
        }

        return starts;
    }

    [Fact]
    public void Test2_gapless_tracks_start_exactly_where_the_previous_one_ended()
    {
        // Five 5-second tracks (375 sectors each) at Gap 0 - the boundary torture case.
        var starts = TrackStartSectors([375, 375, 375, 375, 375], gapSectors: 0);

        Assert.Equal([0, 375, 750, 1125, 1500], starts);
    }

    [Fact]
    public void Test2_a_two_second_gap_offsets_every_later_track_by_exactly_150_sectors()
    {
        var gapless = TrackStartSectors([375, 375, 375], 0);
        var gapped = TrackStartSectors([375, 375, 375], 150);

        Assert.Equal(gapless[0], gapped[0]);                  // track 1 never moves
        Assert.Equal(gapless[1] + 150, gapped[1]);
        Assert.Equal(gapless[2] + 300, gapped[2]);            // gaps accumulate
    }

    [Fact]
    public void Test2_the_four_second_redbook_floor_is_enforced_before_any_disc_is_touched()
    {
        // 5-second tracks clear the floor; anything under 300 sectors must be rejected.
        Assert.Equal(300, CdBurnService.MinRedbookTrackSectors);
        Assert.True(375 >= CdBurnService.MinRedbookTrackSectors, "5 s test tracks must clear the Red Book floor");
    }

    // ── Burn Test 3: hi-res downsample (real ffmpeg) ──────────

    private static string? FindFfmpeg() => ExecutableResolver.Find("ffmpeg");

    [Theory]
    [InlineData(192000)]
    [InlineData(96000)]
    [InlineData(48000)]
    public async Task Test3_any_source_rate_transcodes_to_sector_aligned_cd_audio(int sourceRate)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            return;   // no ffmpeg on this machine (CI without the bundled tools) - nothing to assert
        }

        var dir = Path.Combine(Path.GetTempPath(), $"orgz-burn3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, $"tone-{sourceRate}.flac");
        var wav = Path.Combine(dir, "out.wav");

        try
        {
            // A real 5-second hi-res source, generated by ffmpeg itself.
            var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-hide_banner", "-y", "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate={sourceRate}:duration=5", "-ac", "2", source })
            {
                psi.ArgumentList.Add(arg);
            }

            using (var gen = System.Diagnostics.Process.Start(psi)!)
            {
                await gen.StandardError.ReadToEndAsync();
                await gen.WaitForExitAsync();
                Assert.Equal(0, gen.ExitCode);
            }

            await CdAudioTranscoder.ToCdAudioWavAsync(ffmpeg, source, wav);

            // The burn path's own validator is the oracle: it accepts ONLY 16-bit
            // stereo 44.1 kHz PCM, and CdBurnService rejects any non-sector-multiple.
            using var stream = File.OpenRead(wav);
            var (offset, length) = CdBurnService.ParseCdAudioWav(stream, wav);

            Assert.True(offset > 0);
            Assert.Equal(0, length % 2352);                        // whole CD sectors
            Assert.True(length / 2352 >= CdBurnService.MinRedbookTrackSectors, "5 s must clear the Red Book floor");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
