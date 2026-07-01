// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;
using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Reads a player's library straight off the filesystem - for Rockbox (no central database) and as the
/// fallback for a stock iPod whose iTunesDB is missing. Walks every supported audio file with TagLib,
/// backed by <see cref="DeviceLibraryCache"/> so only files whose size+mtime changed are re-analyzed
/// (steady libraries scan in milliseconds), and reads Rockbox-format <c>.m3u8</c> playlists.
///
/// UI-free by design: it streams tracks through <paramref name="onBatch"/> and reports text via
/// <paramref name="onProgress"/>; the caller owns any capacity-bar / thread-marshalling concerns.
/// </summary>
public static class FilesystemLibraryScanner
{
    private static readonly ILogger _log = Logging.For("FilesystemScan");

    public static DeviceLibrary Scan(ConnectedDevice device, Action<IReadOnlyList<MediaItem>>? onBatch = null, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var source = $"device:{device.MountPath}";

        // Load cache first - even if the walk is slow, we can flush cached items immediately and only
        // pay for TagLib analysis on actually-new files.
        onProgress?.Invoke("Loading device cache...");
        var cached = DeviceLibraryCache.TryLoad(device.MountPath, source);
        var cacheByPath = cached
            .Where(i => !string.IsNullOrEmpty(i.FilePath))
            .ToDictionary(i => i.FilePath!, StringComparer.OrdinalIgnoreCase);

        onProgress?.Invoke("Walking filesystem...");
        var files = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(device.MountPath, "*.*", SearchOption.AllDirectories))
            {
                if (FileScanner.IsSupportedExtension(path))
                {
                    files.Add(path);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't enter - best effort
        }

        var items = new List<MediaItem>(files.Count);
        var pending = new List<MediaItem>(capacity: 32);
        var newlyAnalyzed = new List<MediaItem>(capacity: 32);
        int reused = 0;
        int analyzed = 0;

        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = files[i];
            MediaItem? item;
            try
            {
                var info = new FileInfo(path);

                // Delta: if the cached entry matches size + mtime, reuse it verbatim and skip TagLib
                // analysis entirely. On a steady library the whole "scan" becomes a directory enumeration.
                if (cacheByPath.TryGetValue(path, out var cachedItem)
                    && cachedItem.FileSize == info.Length
                    && cachedItem.LastModified == info.LastWriteTimeUtc)
                {
                    item = cachedItem;
                    reused++;
                }
                else
                {
                    item = new MediaItem
                    {
                        Id = path,
                        Kind = MediaKind.Music,
                        FilePath = path,
                        FileName = info.Name,
                        Extension = info.Extension,
                        FileSize = info.Length,
                        LastModified = info.LastWriteTimeUtc,
                        Source = source,
                        StreamUrl = path,
                    };
                    AudioFileAnalyzer.AnalyzeFile(item);
                    analyzed++;
                    newlyAnalyzed.Add(item);
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Rockbox scan failed on file {Path}", path);
                continue;
            }

            items.Add(item);
            pending.Add(item);

            if ((i & 0x1F) == 0)
            {
                onProgress?.Invoke($"Analyzing {i + 1} of {files.Count}");

                if (pending.Count > 0 && onBatch != null)
                {
                    onBatch(pending.ToArray());
                    pending.Clear();
                }

                // Persist newly-analyzed items immediately so an interrupted scan resumes from here on
                // the next connect instead of replaying all the TagLib work.
                if (newlyAnalyzed.Count > 0)
                {
                    DeviceLibraryCache.Upsert(device.MountPath, newlyAnalyzed);
                    newlyAnalyzed.Clear();
                }
            }
        }

        if (pending.Count > 0 && onBatch != null)
        {
            onBatch(pending.ToArray());
        }

        if (newlyAnalyzed.Count > 0)
        {
            DeviceLibraryCache.Upsert(device.MountPath, newlyAnalyzed);
        }

        _log.Information("Rockbox scan: total={Total} cached={Reused} analyzed={Analyzed}", files.Count, reused, analyzed);

        // Scan completed to the end - prune cache rows for files removed since the last complete scan.
        // Skipped on cancel (that throws above) so a partial run doesn't erase otherwise-valid rows.
        DeviceLibraryCache.PruneMissing(device.MountPath, items.Select(i => i.FilePath!).Where(p => !string.IsNullOrEmpty(p)));

        // Rockbox-format M3U playlists from /Playlists/ - TrackIds are absolute file paths, which match
        // MediaItem.Id for Rockbox tracks (also the full path). Missing folder is common; returns empty.
        var playlists = M3UPlaylistReader.Read(device.MountPath);

        return new DeviceLibrary(items, playlists);
    }
}
