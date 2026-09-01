// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.DeviceLimits;

namespace OrgZ.Tests;

/// <summary>
/// The post-sync checks, driven through their pure half with hand-built measurements. Each of the
/// three August 2026 bugs is pinned as a failing case, so the check that would have caught it can't
/// quietly stop firing.
/// </summary>
public class DeviceVerifierTests
{
    private static readonly IReadOnlyList<DeviceLimit> ClassicLimits =
        DeviceLimitCatalog.For(new ConnectedDevice { MountPath = @"L:\", DeviceType = DeviceType.StockIPod, Name = "POD", IpodGeneration = "Classic 7G" });

    private static readonly IReadOnlyList<DeviceLimit> RockboxLimits =
        DeviceLimitCatalog.For(new ConnectedDevice { MountPath = @"L:\", DeviceType = DeviceType.RockboxIPod, Name = "RB" });

    /// <summary>A healthy 29k-track Classic, roughly what a real one measured after the fixes.</summary>
    private static DeviceMeasurements Healthy() => new()
    {
        FileSystem = "FAT32",
        VolumeBytes = 1_990_000_000_000,
        FreeBytes = 900_000_000_000,
        ArtworkFiles = [("F1060_1.ithmb", 674_611_200), ("F1055_1.ithmb", 107_937_792)],
        MusicFolders = Enumerable.Range(0, 50).Select(i => ($"F{i:00}", 600)).ToList(),
        DatabaseInspected = true,
        DatabaseBytes = 29_641_534,
        TrackCount = 29_277,
        PlaylistCount = 12,
        ArtworkEntries = 29_277,
    };

    private static Finding Only(VerificationReport report, string id) => Assert.Single(report.Findings, f => f.Id == id);

    [Fact]
    public void A_healthy_device_passes_with_one_expected_warning()
    {
        var report = DeviceVerifier.Evaluate(Healthy(), ClassicLimits);

        // 29.6 MB is fine on a 64 MB Classic but over what a 32 MB one loads; the check says so
        // rather than pretending both are the same machine.
        var db = Only(report, "database-fits-in-ram");
        Assert.Equal(FindingLevel.Warning, db.Level);
        Assert.Contains("32 MB", db.Message);

        Assert.All(report.Findings.Where(f => f.Id != "database-fits-in-ram"), f => Assert.Equal(FindingLevel.Ok, f.Level));
        Assert.Equal(FindingLevel.Warning, report.Worst);
    }

    // ── the three bugs that shipped together ──

    [Fact]
    public void Music_rows_without_a_media_type_fail()
    {
        var report = DeviceVerifier.Evaluate(Healthy() with { TracksWithoutMediaType = 29_277 }, ClassicLimits);
        var finding = Only(report, "tracks-have-media-type");
        Assert.Equal(FindingLevel.Failed, finding.Level);
        Assert.Contains("29277", finding.Message.Replace(",", ""));
    }

    [Fact]
    public void An_artwork_file_at_the_FAT32_ceiling_fails()
    {
        var stuck = Healthy() with { ArtworkFiles = [("F1060_1.ithmb", 4_294_860_800)] };   // the real number
        var finding = Only(DeviceVerifier.Evaluate(stuck, ClassicLimits), "artwork-files-under-ceiling");
        Assert.Equal(FindingLevel.Failed, finding.Level);
        Assert.Contains("F1060_1.ithmb", finding.Message);
    }

    [Fact]
    public void Art_claims_with_no_stored_cover_fail()
    {
        var finding = Only(DeviceVerifier.Evaluate(Healthy() with { ArtworkClaimsWithoutEntry = 10_485 }, ClassicLimits), "artwork-claims-have-entries");
        Assert.Equal(FindingLevel.Failed, finding.Level);
    }

    // ── the rest ──

    [Fact]
    public void An_artwork_file_past_the_rollover_point_only_warns()
    {
        var big = Healthy() with { ArtworkFiles = [("F1060_1.ithmb", Services.IPodArtworkFiles.DefaultMaxFileBytes + 1)] };
        Assert.Equal(FindingLevel.Warning, Only(DeviceVerifier.Evaluate(big, ClassicLimits), "artwork-files-under-ceiling").Level);
    }

    [Fact]
    public void A_folder_at_the_entry_limit_fails_and_near_it_warns()
    {
        var full = Healthy() with { MusicFolders = [("F00", 65_534)] };
        Assert.Equal(FindingLevel.Failed, Only(DeviceVerifier.Evaluate(full, ClassicLimits), "music-folders-under-limit").Level);

        var near = Healthy() with { MusicFolders = [("F00", 60_000)] };
        Assert.Equal(FindingLevel.Warning, Only(DeviceVerifier.Evaluate(near, ClassicLimits), "music-folders-under-limit").Level);
    }

    [Fact]
    public void Dangling_playlist_entries_fail()
    {
        Assert.Equal(FindingLevel.Failed, Only(DeviceVerifier.Evaluate(Healthy() with { PlaylistItemsWithoutTrack = 1 }, ClassicLimits), "playlist-items-resolve").Level);
    }

    [Fact]
    public void A_database_past_the_RAM_budget_warns_with_the_budget()
    {
        var finding = Only(DeviceVerifier.Evaluate(Healthy() with { DatabaseBytes = 60L * 1024 * 1024 }, ClassicLimits), "database-fits-in-ram");
        Assert.Equal(FindingLevel.Warning, finding.Level);
        Assert.Contains("50 MB", finding.Message);
    }

    [Fact]
    public void Track_count_near_and_past_the_budget_warns()
    {
        Assert.Equal(FindingLevel.Warning, Only(DeviceVerifier.Evaluate(Healthy() with { TrackCount = 46_000 }, ClassicLimits), "track-count-within-budget").Level);
        Assert.Equal(FindingLevel.Warning, Only(DeviceVerifier.Evaluate(Healthy() with { TrackCount = 51_000 }, ClassicLimits), "track-count-within-budget").Level);
        Assert.Equal(FindingLevel.Ok, Only(DeviceVerifier.Evaluate(Healthy() with { TrackCount = 10_000 }, ClassicLimits), "track-count-within-budget").Level);
    }

    [Fact]
    public void Low_free_space_warns()
    {
        Assert.Equal(FindingLevel.Warning, Only(DeviceVerifier.Evaluate(Healthy() with { FreeBytes = 50_000_000 }, ClassicLimits), "free-space").Level);
    }

    [Fact]
    public void A_non_FAT_volume_warns()
    {
        Assert.Equal(FindingLevel.Warning, Only(DeviceVerifier.Evaluate(Healthy() with { FileSystem = "NTFS" }, ClassicLimits), "filesystem").Level);
        Assert.Equal(FindingLevel.Ok, Only(DeviceVerifier.Evaluate(Healthy() with { FileSystem = "FAT32" }, ClassicLimits), "filesystem").Level);
    }

    [Fact]
    public void Database_checks_are_not_applicable_when_the_database_was_not_inspected()
    {
        var uninspected = Healthy() with { DatabaseInspected = false, TracksWithoutMediaType = 5, ArtworkClaimsWithoutEntry = 5, PlaylistItemsWithoutTrack = 5 };
        var report = DeviceVerifier.Evaluate(uninspected, ClassicLimits);
        foreach (var id in new[] { "tracks-have-media-type", "artwork-claims-have-entries", "playlist-items-resolve", "database-fits-in-ram", "track-count-within-budget" })
        {
            Assert.Equal(FindingLevel.Ok, Only(report, id).Level);
        }
    }

    [Fact]
    public void Rockbox_folders_past_the_browser_limit_warn_and_iPod_ones_do_not()
    {
        var m = new DeviceMeasurements { FileSystem = "FAT32", VolumeBytes = 1, FreeBytes = 1_000_000_000, MusicFolders = [("Music/Big Box Set", 900)] };

        Assert.Equal(FindingLevel.Warning, Only(DeviceVerifier.Evaluate(m, RockboxLimits), "rockbox-folders-browsable").Level);
        Assert.Equal(FindingLevel.Ok, Only(DeviceVerifier.Evaluate(m, ClassicLimits), "rockbox-folders-browsable").Level);   // no such limit on a stock iPod
    }

    [Fact]
    public void Preflight_projection_adds_the_planned_load_evenly()
    {
        var projected = DeviceVerifier.Project(Healthy(), bytesToAdd: 100_000_000_000, tracksToAdd: 5_000);

        Assert.Equal(800_000_000_000, projected.FreeBytes);
        Assert.Equal(34_277, projected.TrackCount);
        Assert.Equal(29_641_534 + 5_000 * 1024, projected.DatabaseBytes);
        Assert.All(projected.MusicFolders, f => Assert.Equal(700, f.Entries));   // 5,000 over 50 folders
    }

    [Fact]
    public void Summary_leads_with_the_worst_finding_and_counts_the_rest()
    {
        var report = DeviceVerifier.Evaluate(Healthy() with { TracksWithoutMediaType = 3, FreeBytes = 1 }, ClassicLimits);
        var summary = report.Summary();
        Assert.StartsWith("3 track(s) have no media type", summary);
        Assert.Contains("more", summary);

        Assert.Equal("Device checks passed.", new VerificationReport([new Finding("x", FindingLevel.Ok, "fine")]).Summary());
    }
}
