// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.ViewModels;

namespace OrgZ.Tests;

/// <summary>
/// The footer summaries. The bug this pins: GenericSummary (playlists, favorites, audiobooks, CD,
/// device, ...) used to omit total file size while the Music view showed it - an inconsistent footer.
/// It now carries the same count / duration / size shape, each trailing clause conditional.
/// </summary>
public class StatusBarSummaryTests
{
    [Fact]
    public void Generic_summary_now_includes_total_size_like_music_does()
    {
        var sb = new StatusBarViewModel
        {
            ItemCount = 12,
            ItemLabel = "tracks",
            ItemDuration = TimeSpan.FromHours(1) + TimeSpan.FromMinutes(13) + TimeSpan.FromSeconds(42),
            ItemFileSize = 82_400_000,
        };

        Assert.StartsWith("12 tracks,", sb.GenericSummary);
        Assert.Contains("MB", sb.GenericSummary);   // the size clause that was missing
    }

    [Fact]
    public void Generic_summary_drops_zero_duration_and_zero_size_clauses()
    {
        // A CD/radio-style view: tracks with a duration but no file size.
        var withDurationOnly = new StatusBarViewModel { ItemCount = 5, ItemLabel = "tracks", ItemDuration = TimeSpan.FromMinutes(20), ItemFileSize = 0 };
        Assert.DoesNotContain("B", withDurationOnly.GenericSummary.Replace("tracks", ""));   // no size unit
        Assert.Contains(",", withDurationOnly.GenericSummary);                                // duration clause present

        // Bare count only - neither clause.
        var countOnly = new StatusBarViewModel { ItemCount = 3, ItemLabel = "items", ItemDuration = TimeSpan.Zero, ItemFileSize = 0 };
        Assert.Equal("3 items", countOnly.GenericSummary);
    }

    [Fact]
    public void Size_appears_even_when_durations_are_missing()
    {
        // A playlist mid-scan: files sized but not yet analyzed for duration.
        var sb = new StatusBarViewModel { ItemCount = 4, ItemLabel = "tracks", ItemDuration = TimeSpan.Zero, ItemFileSize = 10_000_000 };
        Assert.StartsWith("4 tracks,", sb.GenericSummary);
        Assert.Contains("MB", sb.GenericSummary);
    }
}
