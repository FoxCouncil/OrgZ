// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Collections.Concurrent;
using System.Net.Http;
using OrgZ.Models;
using Serilog;

namespace OrgZ.Services.Audiobooks;

public enum AudiobookDownloadState
{
    NotDownloaded,
    InProgress,
    Downloaded,
}

/// <summary>Whole-book progress: bytes across every file in the set, plus which file is landing.</summary>
public sealed record AudiobookDownloadProgress(string Identifier, string Title, long Received, long Total, int FileIndex, int FileCount);

/// <summary>
/// Pulls a store audiobook to disk under <c>{libraryRoot}/Audiobooks/{Author}/{Title}/</c> - the
/// chaptered m4b parts when the item has them, otherwise the MP3 chapter set (which gets a genre
/// tag of "Audiobook" so the library scan promotes it; m4bs are audiobooks by container). Files
/// stream to a .partial name (an unsupported extension, so the folder watcher and scanner ignore
/// the in-flight bytes) and atomically rename when complete. Same discipline as
/// <see cref="Podcast.PodcastDownloadService"/>: the filesystem is the only download registry.
/// </summary>
public sealed class AudiobookDownloadService
{
    private static readonly ILogger _log = Logging.For("AudiobookDownload");

    private static readonly HttpClient _http = new()
    {
        // Whole books, not episodes - a long m4b on a slow archive.org datanode can legitimately
        // take a while. Cancellation comes from the job token, not the client timeout.
        Timeout = TimeSpan.FromMinutes(60),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36" },
        },
    };

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobs = new(StringComparer.Ordinal);

    public static AudiobookDownloadService Instance { get; } = new();

    public event Action<AudiobookDownloadProgress>? ProgressChanged;
    public event Action<AudiobookListing>? Started;
    public event Action<AudiobookListing>? Completed;
    public event Action<string, Exception>? Failed;

    public bool IsDownloading(string identifier) => _jobs.ContainsKey(identifier);

    /// <summary>
    /// Predictable on-disk home for a book: <c>{library}/.audiobooks/{Author}/{Title}/</c>. The
    /// dot-folder keeps the library tidy in Explorer; the scanner explicitly exempts it from the
    /// hidden-directory skip, and everything under it is an audiobook by location.
    /// </summary>
    public static string TargetDirectoryFor(string libraryRoot, AudiobookListing book)
        => Path.Combine(libraryRoot, ".audiobooks", SanitizeSegment(book.Creator ?? "Unknown Author"), SanitizeSegment(book.Title ?? book.Identifier));

    /// <summary>
    /// Disk + in-flight state. A book is Downloaded when its folder holds at least one completed
    /// audio file and nothing is mid-flight (.partial present = an interrupted set; re-download).
    /// </summary>
    public AudiobookDownloadState GetState(AudiobookListing book, string? libraryRoot)
    {
        if (_jobs.ContainsKey(book.Identifier))
        {
            return AudiobookDownloadState.InProgress;
        }
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            return AudiobookDownloadState.NotDownloaded;
        }

        var dir = TargetDirectoryFor(libraryRoot, book);
        if (!Directory.Exists(dir))
        {
            return AudiobookDownloadState.NotDownloaded;
        }
        if (Directory.EnumerateFiles(dir, "*.partial").Any())
        {
            return AudiobookDownloadState.NotDownloaded;
        }
        return Directory.EnumerateFiles(dir).Any(f => FileScanner.IsSupportedExtension(f))
            ? AudiobookDownloadState.Downloaded
            : AudiobookDownloadState.NotDownloaded;
    }

    public Task EnqueueAsync(AudiobookListing book, string libraryRoot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            _log.Warning("EnqueueAsync skipped for {Identifier}: library root unset", book.Identifier);
            return Task.CompletedTask;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_jobs.TryAdd(book.Identifier, cts))
        {
            cts.Dispose();
            return Task.CompletedTask;   // already in flight
        }

        Started?.Invoke(book);
        return Task.Run(() => RunJob(book, libraryRoot, cts.Token), cts.Token);
    }

    private async Task RunJob(AudiobookListing book, string libraryRoot, CancellationToken ct)
    {
        try
        {
            var item = await ArchiveOrgClient.GetItemAsync(book.Identifier, ct)
                ?? throw new InvalidOperationException("The item's metadata could not be fetched.");
            var files = ArchiveOrgClient.PickDownloadFiles(item.Files);
            if (files.Count == 0)
            {
                throw new InvalidOperationException("The item carries no downloadable audio files.");
            }

            var dir = TargetDirectoryFor(libraryRoot, book);
            Directory.CreateDirectory(dir);

            // The item's real cover (largest non-thumb image file), fetched once and embedded in
            // every audio file below; the image-service thumb is the fallback. Best-effort - a
            // book without art still downloads.
            var coverBytes = await FetchCoverAsync(book, item.Files, ct);

            long totalBytes = files.Sum(f => ParseSize(f.Size));
            long received = 0;

            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = files[i];
                var name = SanitizeFileName(file.Name!);
                var target = Path.Combine(dir, name);

                // Resume-friendly: a completed file from an interrupted set is kept when its
                // size matches what the item reports.
                var expected = ParseSize(file.Size);
                if (File.Exists(target) && expected > 0 && new FileInfo(target).Length == expected)
                {
                    received += expected;
                    ProgressChanged?.Invoke(new AudiobookDownloadProgress(book.Identifier, book.Title ?? "", received, totalBytes, i + 1, files.Count));
                    continue;
                }

                var partial = target + ".partial";
                using (var resp = await _http.GetAsync(ArchiveOrgClient.DownloadUrlFor(book.Identifier, file.Name!), HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    using var src = await resp.Content.ReadAsStreamAsync(ct);
                    using var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.Read);
                    var buf = new byte[64 * 1024];
                    int read;
                    while ((read = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, read), ct);
                        received += read;
                        if (totalBytes > 0)
                        {
                            ProgressChanged?.Invoke(new AudiobookDownloadProgress(book.Identifier, book.Title ?? "", received, totalBytes, i + 1, files.Count));
                        }
                    }
                }

                File.Move(partial, target, overwrite: true);

                // Stamp the feed metadata into the file - LibriVox uploads carry threadbare tags,
                // and we're holding the authoritative catalog data. Stamping after the atomic move
                // is fine: if the folder watcher raced in between, the mtime change makes the
                // delta scan re-analyze.
                StampMetadata(target, book, item.Metadata, coverBytes, trackNumber: i + 1, trackCount: files.Count);
            }

            // Drop the in-flight marker BEFORE signaling, so a Completed handler that re-probes
            // GetState sees Downloaded rather than a racing InProgress.
            RemoveJob(book.Identifier);
            _log.Information("Downloaded audiobook {Identifier} ({Files} file(s)) -> {Dir}", book.Identifier, files.Count, dir);
            Completed?.Invoke(book);
        }
        catch (OperationCanceledException)
        {
            RemoveJob(book.Identifier);
            _log.Information("Audiobook download cancelled: {Identifier}", book.Identifier);
            Failed?.Invoke(book.Identifier, new OperationCanceledException());
        }
        catch (Exception ex)
        {
            RemoveJob(book.Identifier);
            _log.Warning(ex, "Audiobook download failed: {Identifier}", book.Identifier);
            Failed?.Invoke(book.Identifier, ex);
        }
    }

    private void RemoveJob(string identifier)
    {
        if (_jobs.TryRemove(identifier, out var cts))
        {
            cts.Dispose();
        }
    }

    // ── pure pieces (unit-tested) ──────────────────────────────────────────────

    /// <summary>A path segment safe on every filesystem OrgZ writes to; never empty.</summary>
    internal static string SanitizeSegment(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length > 0 ? cleaned : "Unknown";
    }

    /// <summary>archive.org file names keep their extension; everything else gets segment rules.</summary>
    internal static string SanitizeFileName(string name)
    {
        var ext = Path.GetExtension(name);
        var stem = SanitizeSegment(Path.GetFileNameWithoutExtension(name));
        return stem + ext;
    }

    internal static long ParseSize(string? size)
        => long.TryParse(size, out var bytes) && bytes > 0 ? bytes : 0;

    private static async Task<byte[]?> FetchCoverAsync(AudiobookListing book, IReadOnlyList<ArchiveItemFile> files, CancellationToken ct)
    {
        try
        {
            var coverFile = ArchiveOrgClient.PickCoverFile(files);
            var url = coverFile?.Name is { } name
                ? ArchiveOrgClient.DownloadUrlFor(book.Identifier, name)
                : book.CoverUrl;
            return await _http.GetByteArrayAsync(url, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Cover fetch failed for {Identifier} — downloading without art", book.Identifier);
            return null;
        }
    }

    /// <summary>
    /// Writes the catalog's metadata into a downloaded file, so Get Info (and any player, and the
    /// iPod sync later) sees the real book instead of LibriVox's threadbare upload tags. The
    /// catalog is authoritative for author/book/track-order/genre; title, year, comment, and art
    /// only fill in when the file doesn't already carry them. Best-effort: a tagging failure is
    /// logged and the download still succeeds (an untagged MP3 lands as Music until re-kinded -
    /// unless it lives under .audiobooks, where location decides).
    /// </summary>
    internal static void StampMetadata(string path, AudiobookListing book, ArchiveItemMetadataFields? meta, byte[]? coverBytes, int trackNumber, int trackCount)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;

            if (!string.IsNullOrWhiteSpace(book.Creator))
            {
                tag.Performers = [book.Creator];
                tag.AlbumArtists = [book.Creator];
            }
            if (!string.IsNullOrWhiteSpace(book.Title))
            {
                tag.Album = book.Title;
                if (string.IsNullOrWhiteSpace(tag.Title))
                {
                    tag.Title = trackCount > 1 ? $"{book.Title} — Part {trackNumber}" : book.Title;
                }
            }

            tag.Track = (uint)trackNumber;
            tag.TrackCount = (uint)trackCount;

            if (!AudiobookDetector.TagsSayAudiobook(file))
            {
                tag.Genres = (tag.Genres ?? []).Append("Audiobook").ToArray();
            }

            if (tag.Year == 0 && uint.TryParse(meta?.Year, out var year) && year > 0)
            {
                tag.Year = year;
            }

            if (string.IsNullOrWhiteSpace(tag.Comment) && ArchiveOrgClient.StripHtml(meta?.Description) is { } description)
            {
                tag.Comment = description.Length > 2000 ? description[..2000] : description;
            }

            if ((tag.Pictures?.Length ?? 0) == 0 && coverBytes is { Length: > 0 })
            {
                tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(coverBytes)) { Type = TagLib.PictureType.FrontCover }];
            }

            file.Save();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not stamp catalog metadata on {Path}", path);
        }
    }
}
