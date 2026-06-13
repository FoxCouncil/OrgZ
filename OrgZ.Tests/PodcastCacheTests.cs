// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;
using OrgZ.Services.Podcast;

namespace OrgZ.Tests;

/// <summary>
/// Round-trips the SQLite-backed podcast state - subscriptions (add/remove/list/membership)
/// and listen history (record / resume position / completed / recents). Redirects the cache
/// DB to a temp directory so it never touches the real library.db.
/// </summary>
public class PodcastCacheTests : IDisposable
{
    private readonly string _tempDir;

    public PodcastCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OrgZ-PodcastCache-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        PodcastCache.OverrideCacheDirectory(_tempDir);
        PodcastCache.EnsureCreated();
    }

    public void Dispose()
    {
        PodcastCache.OverrideCacheDirectory(null);
        try { if (Directory.Exists(_tempDir)) { Directory.Delete(_tempDir, recursive: true); } } catch { }
    }

    private static PodcastFeed Feed(long id, string? title = null) => new()
    {
        Id = id,
        Title = title ?? $"Feed {id}",
        Author = "Author",
        FeedUrl = $"https://example.com/{id}.xml",
    };

    private static PodcastEpisode Episode(long id) => new()
    {
        Id = id,
        Title = $"Ep {id}",
        EnclosureUrl = $"https://example.com/{id}.mp3",
        DurationSec = 1800,
    };

    // --- Subscriptions --------------------------------------------------------------

    [Fact]
    public void Not_subscribed_by_default()
        => Assert.False(PodcastCache.IsSubscribed(123));

    [Fact]
    public void AddSubscription_then_subscribed_and_listed()
    {
        PodcastCache.AddSubscription(Feed(123, "My Show"));

        Assert.True(PodcastCache.IsSubscribed(123));
        var subs = PodcastCache.GetSubscriptions();
        Assert.Single(subs);
        Assert.Equal(123, subs[0].FeedId);
        Assert.Equal("My Show", subs[0].Title);
    }

    [Fact]
    public void AddSubscription_is_an_idempotent_upsert()
    {
        PodcastCache.AddSubscription(Feed(123, "Original"));
        PodcastCache.AddSubscription(Feed(123, "Renamed"));

        var subs = PodcastCache.GetSubscriptions();
        Assert.Single(subs);
        Assert.Equal("Renamed", subs[0].Title);
    }

    [Fact]
    public void RemoveSubscription_drops_only_that_feed()
    {
        PodcastCache.AddSubscription(Feed(1));
        PodcastCache.AddSubscription(Feed(2));
        PodcastCache.RemoveSubscription(1);

        Assert.False(PodcastCache.IsSubscribed(1));
        Assert.True(PodcastCache.IsSubscribed(2));
        Assert.Single(PodcastCache.GetSubscriptions());
    }

    [Fact]
    public void GetSubscriptions_is_sorted_by_title_case_insensitively()
    {
        PodcastCache.AddSubscription(Feed(1, "Zebra"));
        PodcastCache.AddSubscription(Feed(2, "apple"));
        PodcastCache.AddSubscription(Feed(3, "Mango"));

        var titles = PodcastCache.GetSubscriptions().Select(s => s.Title).ToArray();
        Assert.Equal(new[] { "apple", "Mango", "Zebra" }, titles);
    }

    // --- Listen history / resume ----------------------------------------------------

    [Fact]
    public void GetListenPosition_is_null_before_any_play()
        => Assert.Null(PodcastCache.GetListenPosition(999));

    [Fact]
    public void RecordPlay_then_UpdateListenPosition_round_trips()
    {
        PodcastCache.RecordPlay(Feed(10), Episode(500));
        PodcastCache.UpdateListenPosition(500, 123456, completed: false);

        var pos = PodcastCache.GetListenPosition(500);
        Assert.NotNull(pos);
        Assert.Equal(123456, pos!.Value.PositionMs);
        Assert.False(pos.Value.Completed);
    }

    [Fact]
    public void UpdateListenPosition_marks_completed()
    {
        PodcastCache.RecordPlay(Feed(10), Episode(500));
        PodcastCache.UpdateListenPosition(500, 1_700_000, completed: true);

        var pos = PodcastCache.GetListenPosition(500);
        Assert.NotNull(pos);
        Assert.True(pos!.Value.Completed);
    }

    [Fact]
    public void GetRecentListens_returns_each_played_episode()
    {
        PodcastCache.RecordPlay(Feed(10), Episode(500));
        PodcastCache.RecordPlay(Feed(10), Episode(501));

        var recents = PodcastCache.GetRecentListens();
        Assert.Equal(2, recents.Count);
        Assert.Contains(recents, e => e.EpisodeId == 500);
        Assert.Contains(recents, e => e.EpisodeId == 501);
    }
}
