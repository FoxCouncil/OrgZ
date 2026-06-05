// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Collections.Concurrent;
using System.Net.Http;
using OrgZ.Models;
using Serilog;

namespace OrgZ.Services.Podcast;

/// <summary>
/// Pulls podcast episode enclosures to disk under
/// <c>{libraryRoot}/.podcasts/{feedId}/{episodeId}.mp3</c>. Each download is a
/// background task; callers can subscribe to <see cref="ProgressChanged"/> for
/// UI updates and to <see cref="Completed"/> to be notified when the file is on
/// disk and registered in <see cref="PodcastCache"/>.
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
    public event Action<PodcastDownload>?  Completed;
    public event Action<long, Exception>?  Failed;

    public bool IsDownloading(long episodeId) => _jobs.ContainsKey(episodeId);

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

            var record = new PodcastDownload
            {
                EpisodeId          = ep.Id,
                FeedId             = job.Feed.Id,
                Title              = ep.Title,
                Description        = ep.Description,
                EnclosureUrl       = ep.EnclosureUrl,
                EnclosureBytes     = ep.EnclosureLength,
                DurationSec        = ep.DurationSec,
                DatePublishedEpoch = ep.DatePublishedEpoch,
                LocalPath          = null,
                AddedAt            = DateTime.UtcNow,
            };
            PodcastCache.UpsertDownload(record);

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
                        ProgressChanged?.Invoke(new DownloadProgress(ep.Id, received, totalBytes));
                    }
                }
            }

            // Atomic rename: only replace target when fully written.
            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(partialPath, targetPath);

            var completed = record with
            {
                LocalPath    = targetPath,
                CompletedAt  = DateTime.UtcNow,
            };
            PodcastCache.UpsertDownload(completed);
            Completed?.Invoke(completed);
            _log.Information("Downloaded {Title} ({Bytes} bytes) -> {Path}", ep.Title, new FileInfo(targetPath).Length, targetPath);
        }
        catch (OperationCanceledException)
        {
            _log.Information("Download cancelled for episode {Id}", ep.Id);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Download failed for episode {Id} ({Title})", ep.Id, ep.Title);
            Failed?.Invoke(ep.Id, ex);
        }
        finally
        {
            _jobs.TryRemove(ep.Id, out _);
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

public readonly record struct DownloadProgress(long EpisodeId, long BytesReceived, long TotalBytes)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)BytesReceived / TotalBytes, 0, 1) : 0;
}
