// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;
using OrgZ.Services.Podcast;

namespace OrgZ.Tests;

/// <summary>
/// The refresh engine's decision logic, isolated from network/disk: how many newest
/// episodes a refresh downloads under each action+keep combination, and which downloaded
/// episodes a keep policy prunes.
/// </summary>
public class PodcastSubscriptionServiceTests
{
    [Theory]
    [InlineData(PodcastNewEpisodeAction.None, PodcastKeepPolicy.All, 0)]
    [InlineData(PodcastNewEpisodeAction.None, PodcastKeepPolicy.Last5, 0)]
    [InlineData(PodcastNewEpisodeAction.Recent, PodcastKeepPolicy.All, 1)]
    [InlineData(PodcastNewEpisodeAction.Recent, PodcastKeepPolicy.Last10, 1)]
    [InlineData(PodcastNewEpisodeAction.All, PodcastKeepPolicy.Last5, 5)]
    [InlineData(PodcastNewEpisodeAction.All, PodcastKeepPolicy.Last10, 10)]
    [InlineData(PodcastNewEpisodeAction.All, PodcastKeepPolicy.All, 25)]        // capped
    [InlineData(PodcastNewEpisodeAction.All, PodcastKeepPolicy.Unplayed, 25)]   // capped (no count)
    public void DownloadCount_follows_action_and_keep(PodcastNewEpisodeAction action, PodcastKeepPolicy keep, int expected)
        => Assert.Equal(expected, PodcastSubscriptionService.DownloadCount(action, keep));

    private static List<PodcastEpisode> Episodes(params long[] ids)
        => ids.Select(id => new PodcastEpisode { Id = id, Title = $"Ep {id}" }).ToList();

    [Fact]
    public void Prune_keeps_everything_under_All()
    {
        var prune = PodcastSubscriptionService.EpisodesToPrune(Episodes(5, 4, 3, 2, 1), PodcastKeepPolicy.All, _ => false);
        Assert.Empty(prune);
    }

    [Fact]
    public void Prune_drops_everything_past_the_last_N()
    {
        // Newest first; Last2 keeps 5 & 4, prunes the rest.
        var prune = PodcastSubscriptionService.EpisodesToPrune(Episodes(5, 4, 3, 2, 1), PodcastKeepPolicy.Last2, _ => false);
        Assert.Equal(new long[] { 3, 2, 1 }, prune.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void Prune_keeps_all_when_fewer_than_N_downloaded()
    {
        var prune = PodcastSubscriptionService.EpisodesToPrune(Episodes(3, 2, 1), PodcastKeepPolicy.Last5, _ => false);
        Assert.Empty(prune);
    }

    [Fact]
    public void Prune_under_Unplayed_drops_only_completed_episodes()
    {
        var completed = new HashSet<long> { 4, 2 };
        var prune = PodcastSubscriptionService.EpisodesToPrune(Episodes(5, 4, 3, 2, 1), PodcastKeepPolicy.Unplayed, completed.Contains);
        Assert.Equal(new long[] { 4, 2 }, prune.Select(e => e.Id).ToArray());
    }
}
