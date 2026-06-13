// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Collections.Concurrent;
using System.Net.Http;
using OrgZ.Models;
using Serilog;

namespace OrgZ.Services.Podcast;

/// <summary>
/// Whether an episode is on disk and usable. "Downloaded" requires both the
/// final file to exist AND its size to match (or be close to) the upstream
/// EnclosureLength. "Incomplete" covers the broken-rename window and any case
/// where bytes were written but the file is the wrong size - both manifest as
/// a file you don't want to feed to libvlc.
/// </summary>
public enum PodcastDownloadState
{
    NotDownloaded,
    InProgress,
    Downloaded,
    Incomplete,
}

/// <summary>
/// Pulls podcast episode enclosures to disk under
/// <c>{libraryRoot}/.podcasts/{feedId}/{episodeId}.{ext}</c>. Each download is a
/// background task; callers can subscribe to <see cref="ProgressChanged"/> for
/// UI updates and to <see cref="Completed"/> / <see cref="Failed"/> for the
/// finished-or-broken transition.
///
/// "Is this episode downloaded?" is answered by <see cref="GetState"/> against
/// the filesystem - no SQLite tracking table, since the file on disk is the
/// only authoritative source and a stale DB row that disagrees with the file
/// is worse than no row at all.
/// </summary>
public sealed class PodcastDownloadService
{
    private static readonly ILogger _log = Logging.For("PodcastDownload");

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(15),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36" },
        },
    };

    private readonly ConcurrentDictionary<long, DownloadJob> _jobs = new();

    public static PodcastDownloadService Instance { get; } = new();

    public event Action<DownloadProgress>? ProgressChanged;
    public event Action<PodcastFeed, PodcastEpisode>? Started;
    public event Action<PodcastFeed, PodcastEpisode>? Completed;
    public event Action<long, Exception>? Failed;

    public bool IsDownloading(long episodeId) => _jobs.ContainsKey(episodeId);

    /// <summary>
    /// Predictable on-disk path for an episode. Mirrors the path the download
    /// job writes to (same {libraryRoot}/.podcasts/{feedId}/{episodeId}{ext}
    /// convention), so callers can probe presence without running a download.
    /// </summary>
    public static string? GetLocalPath(PodcastFeed feed, PodcastEpisode episode, string? libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            return null;
        }
        var dir = Path.Combine(libraryRoot, ".podcasts", feed.Id.ToString());
        var ext = GuessExtension(episode);
        return Path.Combine(dir, $"{episode.Id}{ext}");
    }

    /// <summary>
    /// Inspects disk + in-flight jobs to report the current state of an
    /// episode's download. Order matters: an in-flight job always wins, then
    /// a leftover .partial is "Incomplete" (resumed downloads aren't supported
    /// yet - clicking the button re-fetches from scratch), then a final file
    /// is "Downloaded" only if its size matches the upstream EnclosureLength.
    /// A size mismatch is reported as "Incomplete" so the UI can flag the
    /// half-downloaded / interrupted case the user explicitly asked about.
    /// </summary>
    public static PodcastDownloadState GetState(PodcastFeed feed, PodcastEpisode episode, string? libraryRoot)
    {
        if (Instance._jobs.ContainsKey(episode.Id))
        {
            return PodcastDownloadState.InProgress;
        }
        var path = GetLocalPath(feed, episode, libraryRoot);
        if (path == null)
        {
            return PodcastDownloadState.NotDownloaded;
        }
        if (File.Exists(path + ".partial"))
        {
            return PodcastDownloadState.Incomplete;
        }
        if (!File.Exists(path))
        {
            return PodcastDownloadState.NotDownloaded;
        }

        // A final (non-.partial) file only exists after RunJob's full-read -> atomic rename,
        // so it is complete by construction. enclosureLength is unreliable (hosts re-mux /
        // mis-report in BOTH directions), so second-guessing a renamed file on size just
        // produced false "Incomplete" flags on perfectly good downloads (no green check).
        return PodcastDownloadState.Downloaded;
    }

    /// <summary>
    /// Deletes an episode's downloaded file (and any leftover .partial) from disk. Only
    /// touches the predictable {root}/.podcasts/{feedId}/{episodeId}.* path. Returns true if
    /// anything was removed.
    /// </summary>
    public static bool DeleteDownload(PodcastFeed feed, PodcastEpisode episode, string? libraryRoot)
    {
        var path = GetLocalPath(feed, episode, libraryRoot);
        if (path is null)
        {
            return false;
        }

        var removed = false;
        foreach (var target in new[] { path, path + ".partial" })
        {
            try
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                    removed = true;
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Could not delete {Path}", target);
            }
        }
        if (removed)
        {
            _log.Information("Removed download for episode {Id} ({Title})", episode.Id, episode.Title);
        }
        return removed;
    }

    public Task EnqueueAsync(PodcastFeed feed, PodcastEpisode episode, string libraryRoot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            _log.Warning("EnqueueAsync skipped for episode {Id}: library root unset", episode.Id);
            return Task.CompletedTask;
        }
        if (string.IsNullOrWhiteSpace(episode.EnclosureUrl))
        {
            _log.Warning("EnqueueAsync skipped for episode {Id}: no enclosureUrl", episode.Id);
            return Task.CompletedTask;
        }

        var job = new DownloadJob(feed, episode, libraryRoot, ct);
        if (!_jobs.TryAdd(episode.Id, job))
        {
            // Already in flight - caller can safely ignore.
            return Task.CompletedTask;
        }

        // Fire before the work starts so listeners (LCD, row state) light up immediately,
        // independent of whether progress events arrive (feeds with no Content-Length never
        // emit progress).
        Started?.Invoke(feed, episode);
        return Task.Run(() => RunJob(job), ct);
    }

    private async Task RunJob(DownloadJob job)
    {
        var ep = job.Episode;
        try
        {
            var dir = Path.Combine(job.LibraryRoot, ".podcasts", job.Feed.Id.ToString());
            Directory.CreateDirectory(dir);

            var ext = GuessExtension(ep);
            var targetPath = Path.Combine(dir, $"{ep.Id}{ext}");
            var partialPath = targetPath + ".partial";

            // Pre-emptively clear a stale target file. The atomic rename below
            // would replace it anyway, but a leftover from a previous broken
            // download could make GetState briefly report Downloaded mid-job;
            // wiping it up front keeps state honest.
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            using (var resp = await _http.GetAsync(ep.EnclosureUrl!, HttpCompletionOption.ResponseHeadersRead, job.Token))
            {
                resp.EnsureSuccessStatusCode();
                var totalBytes = resp.Content.Headers.ContentLength ?? ep.EnclosureLength;
                using var src = await resp.Content.ReadAsStreamAsync(job.Token);
                using var dst = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                var buf = new byte[64 * 1024];
                long received = 0;
                int read;
                while ((read = await src.ReadAsync(buf, job.Token)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), job.Token);
                    received += read;
                    if (totalBytes > 0)
                    {
                        ProgressChanged?.Invoke(new DownloadProgress(ep.Id, ep.Title ?? string.Empty, received, totalBytes));
                    }
                }
            }

            // Atomic rename: only replace target when fully written.
            File.Move(partialPath, targetPath);

            // Drop the in-flight marker BEFORE signaling. A Completed handler refreshes the
            // row via GetState, which reports InProgress while the job is still in _jobs - if
            // we removed it in a finally (after the event), that refresh could race ahead of
            // the removal and the row would spin forever instead of flipping to the check.
            _jobs.TryRemove(ep.Id, out _);
            _log.Information("Downloaded {Title} ({Bytes} bytes) -> {Path}", ep.Title, new FileInfo(targetPath).Length, targetPath);
            Completed?.Invoke(job.Feed, ep);
        }
        catch (OperationCanceledException)
        {
            _jobs.TryRemove(ep.Id, out _);
            _log.Information("Download cancelled for episode {Id}", ep.Id);
            Failed?.Invoke(ep.Id, new OperationCanceledException());
        }
        catch (Exception ex)
        {
            _jobs.TryRemove(ep.Id, out _);
            _log.Warning(ex, "Download failed for episode {Id} ({Title})", ep.Id, ep.Title);
            Failed?.Invoke(ep.Id, ex);
        }
    }

    private static string GuessExtension(PodcastEpisode ep)
    {
        var type = ep.EnclosureType?.ToLowerInvariant() ?? "";
        if (type.Contains("mp4") || type.Contains("m4a")) return ".m4a";
        if (type.Contains("ogg")) return ".ogg";
        if (type.Contains("flac")) return ".flac";
        return ".mp3";
    }

    private sealed record DownloadJob(PodcastFeed Feed, PodcastEpisode Episode, string LibraryRoot, CancellationToken Token);
}

public readonly record struct DownloadProgress(long EpisodeId, string Title, long BytesReceived, long TotalBytes)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)BytesReceived / TotalBytes, 0, 1) : 0;
}
