// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;
using Serilog;

namespace OrgZ.Services.Podcast;

/// <summary>
/// Applies the user's global podcast rules (<see cref="PodcastSettings"/>) to every
/// subscribed feed: refresh the feed's episode list, auto-download new episodes per the
/// NewEpisodeAction policy, then prune downloads past the Keep policy. Driven on demand by
/// the Settings "Refresh now" button and on a cadence by the app on startup when
/// <see cref="PodcastSettings.IsDueForCheck"/>.
///
/// Singleton + a single-flight guard, mirroring <see cref="PodcastDownloadService"/>: a
/// second refresh request while one is running is ignored rather than queued.
/// </summary>
public sealed class PodcastSubscriptionService
{
    private static readonly ILogger _log = Logging.For("PodcastSubs");

    // Hard cap on how many episodes a single "Download all" pass pulls, so subscribing to a
    // 500-episode back catalogue can't kick off a download storm.
    private const int DownloadAllCap = 25;

    public static PodcastSubscriptionService Instance { get; } = new();

    private int _running;

    /// <summary>Raised after a refresh pass finishes (downloads may still be in flight).</summary>
    public event Action? RefreshCompleted;

    public bool IsRefreshing => Volatile.Read(ref _running) != 0;

    /// <summary>
    /// Refresh every subscription against the current rules. No-op (logged) if the library
    /// root is unset or a refresh is already running.
    /// </summary>
    public async Task RefreshNowAsync(string? libraryRoot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            _log.Warning("Refresh skipped: library root unset");
            return;
        }
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            _log.Information("Refresh already in progress; ignoring re-entry");
            return;
        }

        try
        {
            var subs = PodcastCache.GetSubscriptions();
            var action = PodcastSettings.NewEpisodeAction;
            var keep = PodcastSettings.Keep;
            _log.Information("Refreshing {Count} subscription(s) [action={Action} keep={Keep}]", subs.Count, action, keep);

            foreach (var sub in subs)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await RefreshSubscriptionAsync(sub, libraryRoot, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Refresh failed for feed {Id} ({Title})", sub.FeedId, sub.Title);
                }
            }

            PodcastSettings.MarkChecked();
        }
        catch (OperationCanceledException)
        {
            _log.Information("Refresh cancelled");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            RefreshCompleted?.Invoke();
        }
    }

    private static async Task RefreshSubscriptionAsync(
        PodcastSubscription sub,
        string libraryRoot,
        CancellationToken ct)
    {
        // Each feed's rules are its per-feed overrides, falling back to the global defaults.
        var action = PodcastSettings.NewEpisodeActionFor(sub.FeedId);
        var keep = PodcastSettings.KeepFor(sub.FeedId);

        // Reconstruct the minimal feed the download service needs (it keys the on-disk path
        // off feed.Id, and surfaces the title/art in events).
        var feed = new PodcastFeed
        {
            Id      = sub.FeedId,
            Title   = sub.Title,
            Author  = sub.Author,
            Image   = sub.ImageUrl,
            Artwork = sub.ImageUrl,
        };

        var episodes = await PodcastIndexClient.GetEpisodesByFeedIdAsync(sub.FeedId, max: 200, ct);
        if (episodes.Count == 0)
        {
            return;
        }

        // Newest first — both auto-download and retention reason in this order.
        episodes.Sort((a, b) => b.DatePublishedEpoch.CompareTo(a.DatePublishedEpoch));

        // --- Auto-download per NewEpisodeAction ---
        var want = action switch
        {
            PodcastNewEpisodeAction.Recent => 1,
            PodcastNewEpisodeAction.All    => Math.Min(PodcastSettings.KeepCount(keep) ?? DownloadAllCap, DownloadAllCap),
            _                              => 0,
        };

        for (var i = 0; i < want && i < episodes.Count; i++)
        {
            var ep = episodes[i];
            if (string.IsNullOrWhiteSpace(ep.EnclosureUrl))
            {
                continue;
            }
            if (PodcastDownloadService.GetState(feed, ep, libraryRoot) == PodcastDownloadState.NotDownloaded)
            {
                await PodcastDownloadService.Instance.EnqueueAsync(feed, ep, libraryRoot, ct);
            }
        }

        // --- Retention per Keep ---
        ApplyRetention(feed, episodes, keep, libraryRoot);
    }

    /// <summary>
    /// Deletes downloaded episodes that fall outside the Keep policy. Only ever touches the
    /// predictable <c>{root}/.podcasts/{feedId}/{episodeId}.*</c> paths the download service
    /// owns; an in-use (currently-playing) file simply fails to delete and is retried next
    /// pass. "All" keeps everything (never deletes).
    /// </summary>
    private static void ApplyRetention(PodcastFeed feed, List<PodcastEpisode> episodesNewestFirst, PodcastKeepPolicy keep, string libraryRoot)
    {
        if (keep == PodcastKeepPolicy.All)
        {
            return;
        }

        var downloaded = episodesNewestFirst
            .Where(ep => PodcastDownloadService.GetState(feed, ep, libraryRoot) == PodcastDownloadState.Downloaded)
            .ToList();

        List<PodcastEpisode> toDelete;
        if (keep == PodcastKeepPolicy.Unplayed)
        {
            toDelete = downloaded
                .Where(ep => PodcastCache.GetListenPosition(ep.Id) is { } lp && lp.Completed)
                .ToList();
        }
        else
        {
            var n = PodcastSettings.KeepCount(keep) ?? int.MaxValue;
            toDelete = downloaded.Skip(n).ToList();
        }

        foreach (var ep in toDelete)
        {
            var path = PodcastDownloadService.GetLocalPath(feed, ep, libraryRoot);
            if (path is null || !File.Exists(path))
            {
                continue;
            }
            try
            {
                File.Delete(path);
                _log.Information("Retention removed {Title} -> {Path}", ep.Title, path);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Retention could not delete {Path} (in use?)", path);
            }
        }
    }
}
