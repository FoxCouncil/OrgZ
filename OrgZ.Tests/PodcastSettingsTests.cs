// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.Podcast;

namespace OrgZ.Tests;

/// <summary>
/// Locks in the podcast rule model: how the global Settings tags map to typed rules, the
/// refresh-cadence scheduling, and the per-feed overrides (index 0 == "use the global
/// default"). Shares the "Settings" xUnit collection so it never races the other tests that
/// redirect the global settings directory.
/// </summary>
[Collection("Settings")]
public class PodcastSettingsTests : IDisposable
{
    private readonly string _tempDir;

    public PodcastSettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OrgZ-PodcastSettings-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Settings.OverrideSettingsDirectory(_tempDir);
        Settings.Clear();
    }

    public void Dispose()
    {
        Settings.OverrideSettingsDirectory(null);
        Settings.Clear();
        try { if (Directory.Exists(_tempDir)) { Directory.Delete(_tempDir, recursive: true); } } catch { }
    }

    // --- Pure helpers ---------------------------------------------------------------

    [Theory]
    [InlineData(PodcastKeepPolicy.Last1, 1)]
    [InlineData(PodcastKeepPolicy.Last2, 2)]
    [InlineData(PodcastKeepPolicy.Last5, 5)]
    [InlineData(PodcastKeepPolicy.Last10, 10)]
    public void KeepCount_returns_N_for_lastN(PodcastKeepPolicy keep, int expected)
        => Assert.Equal(expected, PodcastSettings.KeepCount(keep));

    [Theory]
    [InlineData(PodcastKeepPolicy.All)]
    [InlineData(PodcastKeepPolicy.Unplayed)]
    public void KeepCount_is_null_for_unbounded_policies(PodcastKeepPolicy keep)
        => Assert.Null(PodcastSettings.KeepCount(keep));

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1 MB")]
    [InlineData(1073741824L, "1 GB")]
    public void FormatBytes_uses_binary_units(long bytes, string expected)
        => Assert.Equal(expected, PodcastSettings.FormatBytes(bytes));

    [Fact]
    public void DownloadDir_is_null_when_root_unset()
    {
        Assert.Null(PodcastSettings.DownloadDir(null));
        Assert.Null(PodcastSettings.DownloadDir(""));
        Assert.Null(PodcastSettings.DownloadDir("   "));
    }

    [Fact]
    public void DownloadDir_is_dotpodcasts_under_root()
        => Assert.Equal(Path.Combine("X:\\Music", ".podcasts"), PodcastSettings.DownloadDir("X:\\Music"));

    // --- Global rule parsing --------------------------------------------------------

    [Theory]
    [InlineData("hour", PodcastCheckInterval.Hour)]
    [InlineData("day", PodcastCheckInterval.Day)]
    [InlineData("week", PodcastCheckInterval.Week)]
    [InlineData("manual", PodcastCheckInterval.Manual)]
    [InlineData("garbage", PodcastCheckInterval.Day)]
    public void CheckInterval_parses_tag(string tag, PodcastCheckInterval expected)
    {
        Settings.Set("OrgZ.Podcasts.CheckInterval", tag);
        Assert.Equal(expected, PodcastSettings.CheckInterval);
    }

    [Fact]
    public void CheckInterval_defaults_to_day_when_unset()
        => Assert.Equal(PodcastCheckInterval.Day, PodcastSettings.CheckInterval);

    [Theory]
    [InlineData("all", PodcastNewEpisodeAction.All)]
    [InlineData("recent", PodcastNewEpisodeAction.Recent)]
    [InlineData("none", PodcastNewEpisodeAction.None)]
    [InlineData("", PodcastNewEpisodeAction.Recent)]
    public void NewEpisodeAction_parses_tag(string tag, PodcastNewEpisodeAction expected)
    {
        Settings.Set("OrgZ.Podcasts.NewEpisodeAction", tag);
        Assert.Equal(expected, PodcastSettings.NewEpisodeAction);
    }

    [Theory]
    [InlineData("all", PodcastKeepPolicy.All)]
    [InlineData("unplayed", PodcastKeepPolicy.Unplayed)]
    [InlineData("last1", PodcastKeepPolicy.Last1)]
    [InlineData("last10", PodcastKeepPolicy.Last10)]
    [InlineData("", PodcastKeepPolicy.All)]
    public void Keep_parses_tag(string tag, PodcastKeepPolicy expected)
    {
        Settings.Set("OrgZ.Podcasts.Keep", tag);
        Assert.Equal(expected, PodcastSettings.Keep);
    }

    // --- Scheduling -----------------------------------------------------------------

    [Fact]
    public void Interval_is_null_for_manual()
    {
        Settings.Set("OrgZ.Podcasts.CheckInterval", "manual");
        Assert.Null(PodcastSettings.Interval);
    }

    [Fact]
    public void Interval_matches_cadence()
    {
        Settings.Set("OrgZ.Podcasts.CheckInterval", "hour");
        Assert.Equal(TimeSpan.FromHours(1), PodcastSettings.Interval);
        Settings.Set("OrgZ.Podcasts.CheckInterval", "week");
        Assert.Equal(TimeSpan.FromDays(7), PodcastSettings.Interval);
    }

    [Fact]
    public void IsDueForCheck_is_false_under_manual()
    {
        Settings.Set("OrgZ.Podcasts.CheckInterval", "manual");
        Assert.False(PodcastSettings.IsDueForCheck);
    }

    [Fact]
    public void IsDueForCheck_is_true_when_never_checked()
    {
        Settings.Set("OrgZ.Podcasts.CheckInterval", "day");
        Assert.True(PodcastSettings.IsDueForCheck);
    }

    [Fact]
    public void IsDueForCheck_is_false_right_after_MarkChecked()
    {
        Settings.Set("OrgZ.Podcasts.CheckInterval", "day");
        PodcastSettings.MarkChecked();
        Assert.NotNull(PodcastSettings.LastCheck);
        Assert.False(PodcastSettings.IsDueForCheck);
    }

    [Fact]
    public void IsDueForCheck_is_true_once_interval_has_elapsed()
    {
        Settings.Set("OrgZ.Podcasts.CheckInterval", "day");
        Settings.Set("OrgZ.Podcasts.LastCheck", DateTime.UtcNow.AddDays(-2).ToString("O"));
        Assert.True(PodcastSettings.IsDueForCheck);
    }

    // --- Per-feed overrides ---------------------------------------------------------

    [Fact]
    public void Feed_override_defaults_to_use_default_index_zero()
    {
        Assert.Equal(0, PodcastSettings.FeedActionIndex(42));
        Assert.Equal(0, PodcastSettings.FeedKeepIndex(42));
    }

    [Fact]
    public void Feed_action_override_round_trips_and_resolves()
    {
        Settings.Set("OrgZ.Podcasts.NewEpisodeAction", "none");   // global default
        PodcastSettings.SetFeedActionIndex(42, 2);                // index 2 == "recent"

        Assert.Equal(2, PodcastSettings.FeedActionIndex(42));
        Assert.Equal(PodcastNewEpisodeAction.Recent, PodcastSettings.NewEpisodeActionFor(42));
    }

    [Fact]
    public void Feed_action_use_default_falls_back_to_global()
    {
        Settings.Set("OrgZ.Podcasts.NewEpisodeAction", "all");
        PodcastSettings.SetFeedActionIndex(42, 0);   // "use default"

        Assert.Equal(0, PodcastSettings.FeedActionIndex(42));
        Assert.Equal(PodcastNewEpisodeAction.All, PodcastSettings.NewEpisodeActionFor(42));
    }

    [Fact]
    public void Feed_keep_override_round_trips_and_resolves()
    {
        Settings.Set("OrgZ.Podcasts.Keep", "all");
        PodcastSettings.SetFeedKeepIndex(7, 5);   // index 5 == "last5"

        Assert.Equal(5, PodcastSettings.FeedKeepIndex(7));
        Assert.Equal(PodcastKeepPolicy.Last5, PodcastSettings.KeepFor(7));
    }

    [Fact]
    public void Feed_overrides_are_isolated_per_feed()
    {
        PodcastSettings.SetFeedActionIndex(1, 1);   // all
        PodcastSettings.SetFeedActionIndex(2, 3);   // none

        Assert.Equal(1, PodcastSettings.FeedActionIndex(1));
        Assert.Equal(3, PodcastSettings.FeedActionIndex(2));
        Assert.Equal(0, PodcastSettings.FeedActionIndex(3));   // never touched
    }

    [Fact]
    public void Clearing_feed_override_reverts_to_default()
    {
        PodcastSettings.SetFeedKeepIndex(9, 4);   // last2
        Assert.Equal(4, PodcastSettings.FeedKeepIndex(9));

        PodcastSettings.SetFeedKeepIndex(9, 0);   // use default
        Assert.Equal(0, PodcastSettings.FeedKeepIndex(9));
    }
}
