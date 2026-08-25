// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using LibVLCSharp.Shared;
using OrgZ.Services.Audiobooks;
using OrgZ.Services.DeviceHelper;
using System.Net.Http;
using Serilog;

namespace OrgZ.ViewModels;

/// <summary>Library lifecycle: initial load, folder scan/analysis, and file-change processing.</summary>
internal partial class MainWindowViewModel
{

    internal async Task LoadAsync()
    {
        UpdateTitle();

        MediaCache.EnsureCreated();
        Services.Podcast.PodcastCache.EnsureCreated();
        Services.Media.AcquisitionStore.EnsureCreated();

        // One-time cleanup: drop any leftover radio rows from the old runtime
        // sync sources. The new world keeps bundled stations in memory only;
        // SQLite is reserved for user-added personal streams.
        try
        {
            var purged = await Task.Run(MediaCache.RemoveLegacyRadioSources);
            if (purged > 0)
            {
                _log.Information("Purged {Purged} legacy radio rows from cache", purged);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Legacy radio purge failed");
        }

        UpdateMainStatus("Loading library...");


        var loadSw = System.Diagnostics.Stopwatch.StartNew();
        _allItems = await Task.Run(() => MediaCache.LoadAll());

        // Append bundled radio stations from the embedded JSON. They live in
        // memory only - never persisted - and are re-loaded fresh every launch.
        // Sorted by genre name (alphabetical) then station name so the grouped
        // DataGrid renders genres alphabetically.
        try
        {
            var bundled = await Task.Run(() => BundledStationsService.LoadAll());

            // Re-apply persisted user state (favorites, plays, renames) over the freshly
            // loaded catalogue - bundled stations have no Media rows to remember it in.
            var radioState = await Task.Run(MediaCache.LoadRadioState);
            foreach (var s in bundled)
            {
                if (radioState.TryGetValue(s.Id, out var st))
                {
                    s.IsFavorite = st.IsFavorite;
                    s.PlayCount = st.PlayCount;
                    s.LastPlayed = st.LastPlayed;
                    if (!string.IsNullOrWhiteSpace(st.TitleOverride))
                    {
                        s.Title = st.TitleOverride;
                    }
                }
            }

            var ordered = bundled
                .OrderBy(s => s.Tags, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _allItems.AddRange(ordered);
            _log.Information("BundledStations: loaded {Count} into memory ({StateCount} with saved state)", ordered.Count, radioState.Count);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "BundledStations: load failed");
        }
        loadSw.Stop();
        _log.Information("MediaCache.LoadAll: {Count} items in {ElapsedMs}ms", _allItems.Count, loadSw.ElapsedMilliseconds);

        // Initialize radio filter options from loaded data
        var radioItems = _allItems.Where(i => i.Kind == MediaKind.Radio).ToList();

        if (radioItems.Count > 0)
        {
            RebuildRadioFilterOptions();
        }

        // Load playlists
        LoadPlaylistSidebarItems();

        // Apply initial filter for the current tab
        ApplyFilter();

        // The header may have been built in the constructor against an empty library
        // (the saved view is restored before this async load runs) - rebuild it now that
        // _allItems is populated so a first-run playlist/Favorites header isn't blank.
        FireAndForget(BuildPlaylistHeaderAsync(SelectedSidebarItem), "playlist header build");

        // Scan and analyze the library folder (music + audiobooks)
        await ScanAndAnalyzeLibraryAsync();

        // Start watching for file changes
        StartFolderWatcher();

        // Start event-driven portable device detection (iPod, Rockbox, Audio CD).
        // CD drive arrival/removal also routes through the same WMI watcher -
        // no separate polling timer required.
        _deviceDetection = new DeviceDetectionService();
        _deviceDetection.DeviceConnected += device => UI(() => FireAndForget(HandleDeviceConnectedAsync(device), "device connect"));
        _deviceDetection.DeviceDisconnected += mountPath => UI(() => HandleDeviceDisconnected(mountPath));
        _deviceDetection.DeviceEjectedByHost += name => UI(() =>
            UpdateMainStatus($"iTunes ejected {name} — replug it to reconnect (enable “disk use” in iTunes to stop the auto-eject)."));
        _deviceDetection.CdDriveEvent += () => UI(() => FireAndForget(ScanForCdAsync(), "CD scan"));
        _deviceDetection.Start();

        // Work the service kept running while we were closed - pick it back up before
        // anything else claims the LCD.
        _ = ReattachToServiceJobsAsync();

        // Share This Library: the service owns the socket but the intent lives in settings -
        // re-assert it at launch. A keep-alive=off exit stopped the share; a service restart
        // may have restored it already, in which case this is a no-op.
        _ = Task.Run(async () =>
        {
            try
            {
                if (!Settings.Get("OrgZ.Services.Sharing.Enabled", false)
                    || !await Services.DeviceHelper.DeviceHelperClient.IsAvailableAsync()
                    || await Services.DeviceHelper.DeviceHelperClient.ShareStatusAsync() is { Sharing: true })
                {
                    return;
                }

                var shareName = Settings.Get("OrgZ.Services.Sharing.Name", $"{Environment.MachineName} Library");
                await Services.DeviceHelper.DeviceHelperClient.StartShareAsync(shareName, Services.Sharing.ShareServiceOps.DefaultPort);
                _log.Information("Share \"{Name}\" re-asserted at launch", shareName);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Share re-assert at launch failed");
            }
        });

        // LAN share discovery: one browse at startup, then every 30 s. Shares come and
        // go with other people's apps, so the sidebar reconciles rather than assuming.
        FireAndForget(ScanForSharesAsync(), "LAN share scan");
        _shareScanTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _shareScanTimer.Tick += (_, _) => FireAndForget(ScanForSharesAsync(), "LAN share scan");
        _shareScanTimer.Start();

        // The podcast check cadence used to be evaluated exactly once, at construction - an
        // hourly interval never re-fired in a running session, and Manual mode never enforced
        // the Keep policy at all. Tick every 5 minutes: due → full refresh; otherwise a daily
        // offline retention-only pass keeps Manual libraries pruned.
        _podcastCheckTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _podcastCheckTimer.Tick += (_, _) =>
        {
            if (Services.Podcast.PodcastSettings.IsDueForCheck)
            {
                FireAndForget(Services.Podcast.PodcastSubscriptionService.Instance.RefreshNowAsync(App.FolderPath), "podcast subscription refresh");
            }
            else if (Services.Podcast.PodcastSettings.IsRetentionDue)
            {
                FireAndForget(Services.Podcast.PodcastSubscriptionService.Instance.ApplyRetentionNowAsync(App.FolderPath), "podcast retention pass");
            }
        };
        _podcastCheckTimer.Start();

        RestoreLastTrack();
    }

    /// <summary>Persists the started item's id (Settings > General > Remember last played track).</summary>
    private static void RememberLastTrack(MediaItem item)
    {
        if (!Settings.Get("OrgZ.RememberLastTrack", false) || string.IsNullOrEmpty(item.Id))
        {
            return;
        }

        Settings.Set("OrgZ.LastTrack.Id", item.Id);
        Settings.SaveDeferred();
    }

    /// <summary>
    /// Startup counterpart of <see cref="RememberLastTrack"/>: cue the remembered item -
    /// selected in the current view and on the LCD, with the play button starting it.
    /// Launching an app must never start audio on its own, so nothing plays.
    /// </summary>
    private void RestoreLastTrack()
    {
        if (!Settings.Get("OrgZ.RememberLastTrack", false))
        {
            return;
        }

        var id = Settings.Get("OrgZ.LastTrack.Id", "");
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        var item = _allItems.FirstOrDefault(i => i.Id == id);
        if (item == null)
        {
            return;
        }

        UI(() =>
        {
            // Something else already owns playback (service reattach, a fast user) -
            // the cue must not stomp a live LCD.
            if (CurrentPlayingItem != null || CurrentStation != null)
            {
                return;
            }

            if (FilteredItems.Contains(item))
            {
                SelectedItem = item;
            }

            CurrentTrackLine1 = item.Title ?? "Unknown Title";
            var artist = item.Artist ?? "Unknown Artist";
            CurrentTrackLine2 = string.IsNullOrWhiteSpace(item.Album) ? artist : $"{artist} — {item.Album}";
        });
    }

    /// <summary>
    /// True for an item that lives in the local library folder - a Music or Audiobook row with a
    /// file path and no Source (device tracks carry "device:{mount}", CD tracks "cdda"). The
    /// library scan reconciles only these: without the Source check, a folder rescan while an
    /// iPod is connected would sweep the device's rows out of _allItems, since device tracks are
    /// also Kind=Music with FilePaths that are never under the library folder.
    /// </summary>
    internal static bool IsLocalLibraryFile(MediaItem item)
        => item.Kind is MediaKind.Music or MediaKind.Audiobook && item.FilePath != null && item.Source == null;

    internal async Task ScanAndAnalyzeLibraryAsync()
    {
        if (string.IsNullOrEmpty(App.FolderPath))
        {
            return;
        }

        UpdateMainStatus("Scanning files...");

        var scan = await FileScanner.ScanDirectoryAsync(App.FolderPath, recursive: true);
        var diskFiles = scan.Items;

        var libraryLookup = _allItems
            .Where(IsLocalLibraryFile)
            .ToDictionary(i => i.FilePath!, StringComparer.OrdinalIgnoreCase);

        var filesToAnalyze = new List<MediaItem>();
        var diskPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var diskFile in diskFiles)
        {
            diskPaths.Add(diskFile.Id);

            if (libraryLookup.TryGetValue(diskFile.Id, out var existing))
            {
                if (existing.LastModified == diskFile.LastModified && existing.FileSize == diskFile.FileSize)
                {
                    continue;
                }

                // The file changed on disk (retag, ReplayGain write, re-encode) - the replacement
                // row must keep the user's rating/plays/options, not reset them to factory.
                diskFile.AdoptUserStateFrom(existing);
                _allItems.Remove(existing);
                _allItems.Add(diskFile);
                filesToAnalyze.Add(diskFile);
            }
            else
            {
                _allItems.Add(diskFile);
                filesToAnalyze.Add(diskFile);
            }
        }

        // Deletions are only reconciled from a COMPLETE walk. A partial scan (locked folder,
        // yanked drive, cancelled) is indistinguishable from a mass delete by content alone -
        // acting on one wiped ratings, play counts, and playlist rows before this guard existed.
        List<MediaItem> deletedItems = [];
        if (scan.Complete)
        {
            deletedItems = _allItems
                .Where(i => IsLocalLibraryFile(i) && !diskPaths.Contains(i.FilePath!))
                .ToList();

            foreach (var item in deletedItems)
            {
                _allItems.Remove(item);
            }
        }
        else
        {
            _log.Warning("Library scan was incomplete; skipping deletion reconciliation this pass");
        }

        ApplyFilter();
        UpdateTitle();

        await AnalyzeAllFilesAsync(filesToAnalyze);

        if (deletedItems.Count > 0)
        {
            await Task.Run(() => MediaCache.RemoveLibraryFiles(deletedItems.Select(i => i.Id)));
        }

        // Fold the scanned audiobook files against the acquisition records: adopt user-dropped
        // books (dropping a file into .audiobooks IS the acquire gesture) and forget user-provided
        // records whose files are gone. Store downloads are left as re-downloadable records here.
        // Same authority rule as above: "forget records whose files are gone" must never run
        // against a partial file list.
        if (scan.Complete)
        {
            var audiobookFiles = diskFiles.Where(f => f.Kind == MediaKind.Audiobook).Select(f => f.Id).ToList();
            await Task.Run(() => Services.Audiobooks.AudiobookLibrary.ReconcileUserFiles(App.FolderPath, audiobookFiles));
            Audiobooks.RefreshOwned();
        }

        // After track reconciliation: a playlist can only resolve tracks already in the library.
        SyncFolderPlaylists(scan.Complete);

        UpdateData();
    }

    /// <summary>
    /// Reconciles the sidebar's playlists against the .m3u8 files under the music folder.
    /// The file is authoritative for anything it produced; playlists made in OrgZ are untouched.
    ///
    /// <paramref name="scanComplete"/> gates removal only - a partial walk cannot tell a deleted
    /// file from an unreadable folder.
    /// </summary>
    private void SyncFolderPlaylists(bool scanComplete)
    {
        if (string.IsNullOrEmpty(App.FolderPath))
        {
            return;
        }

        // Playlists that predate folder sync have no file yet. Write them out first so every
        // playlist is file-backed and the pass below has a single kind of thing to reconcile.
        foreach (var unbacked in MediaCache.LoadAllPlaylists().Where(p => string.IsNullOrEmpty(p.SourcePath) && p.Source != "Share"))
        {
            ExportPlaylistFile(unbacked.Id);
        }

        var files = PlaylistFolderSync.Discover(App.FolderPath);
        var known = MediaCache.LoadAllPlaylists()
            .Where(p => !string.IsNullOrEmpty(p.SourcePath))
            .ToDictionary(p => p.SourcePath, StringComparer.OrdinalIgnoreCase);

        var byPath = _allItems
            .Where(IsLocalLibraryFile)
            .ToDictionary(i => NormalizePath(i.FilePath!), i => i.Id, StringComparer.OrdinalIgnoreCase);

        var changed = false;

        foreach (var file in files)
        {
            // Per file: a malformed or locked playlist must not abort the ones after it.
            try
            {
                var result = PlaylistImporter.Import(file);

                var mediaIds = result.TrackPaths
                    .Select(NormalizePath)
                    .Where(byPath.ContainsKey)
                    .Select(p => byPath[p])
                    .ToList();

                var name = !string.IsNullOrWhiteSpace(result.Name)
                    ? result.Name
                    : Path.GetFileNameWithoutExtension(file);

                if (known.TryGetValue(file, out var existing))
                {
                    MediaCache.ReplacePlaylistTracks(existing.Id, mediaIds);

                    if (!string.Equals(existing.Name, name, StringComparison.Ordinal))
                    {
                        MediaCache.RenamePlaylist(existing.Id, name);
                    }

                    known.Remove(file);
                }
                else
                {
                    var id = MediaCache.CreatePlaylist(name, "M3U8", file);
                    MediaCache.ReplacePlaylistTracks(id, mediaIds);
                }

                _log.Information("Playlist {Name}: {Matched}/{Total} track(s) matched from {File}",
                    name, mediaIds.Count, result.TrackPaths.Count, file);

                changed = true;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Could not sync the playlist {File}", file);
            }
        }

        // Whatever is left in `known` had a file that is now gone.
        if (scanComplete)
        {
            foreach (var orphan in known.Values)
            {
                MediaCache.DeletePlaylist(orphan.Id);
                changed = true;
                _log.Information("Removed playlist {Name}; {Path} no longer exists", orphan.Name, orphan.SourcePath);
            }
        }

        // Favourites are flags, so nothing else keeps the file current - a track deleted outside
        // OrgZ would otherwise linger in Favorites.m3u8 until the next star was clicked.
        ExportFavoritesFile();

        if (changed)
        {
            LoadPlaylistSidebarItems();
            PlaylistsChanged?.Invoke();
        }
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    /// <summary>True if a local library file (music or audiobook) with this path is already tracked.</summary>
    private bool LibraryContainsPath(string path)
    {
        var full = NormalizePath(path);
        return _allItems.Any(i => IsLocalLibraryFile(i) && NormalizePath(i.FilePath!) == full);
    }

    /// <summary>
    /// Re-evaluates the CD view's green checks against the CURRENT library: a loaded CD
    /// track is Ripped only while a library file with that disc's MUSICBRAINZ_DISCID and
    /// track number still exists. Deleting the ripped folder therefore drops the checks;
    /// re-adding the files restores them. Tracks mid-rip (Pending/Ripping) are left alone
    /// so an active rip's spinner isn't disturbed.
    /// </summary>
    private void RefreshCdRipRecognition()
    {
        if (_cdTracks.Count == 0)
        {
            return;
        }

        foreach (var byDrive in _cdTracks
                     .Where(t => DrivePathFromCdTrackId(t.Id) is not null)
                     .GroupBy(t => DrivePathFromCdTrackId(t.Id)!))
        {
            if (!_cdDiscIdByDrive.TryGetValue(byDrive.Key, out var discId))
            {
                continue;
            }

            var rippedTracks = _allItems
                .Where(i => i.Kind == MediaKind.Music && i.DiscId == discId && i.Track is not null)
                .Select(i => (int)i.Track!.Value)
                .ToHashSet();

            foreach (var t in byDrive)
            {
                if (t.RipStatus is RipState.Pending or RipState.Ripping)
                {
                    continue;   // a rip is in flight for this track - don't touch it
                }
                t.RipStatus = t.Track is { } n && rippedTracks.Contains((int)n) ? RipState.Ripped : RipState.None;
            }
        }
    }

    /// <summary>
    /// True when <paramref name="filePath"/> equals one of the deleted paths or lives
    /// under a deleted directory - so a single folder-delete event clears every track it
    /// contained. The trailing separator stops "Album" from matching "Album 2".
    /// </summary>
    private static bool IsUnderAnyDeletedPath(string filePath, List<string> deletedPaths)
    {
        foreach (var d in deletedPaths)
        {
            if (string.Equals(filePath, d, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            var asDir = d.EndsWith(Path.DirectorySeparatorChar) ? d : d + Path.DirectorySeparatorChar;
            if (filePath.StartsWith(asDir, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private void StartFolderWatcher()
    {
        _folderWatcher?.Stop();

        if (string.IsNullOrEmpty(App.FolderPath))
        {
            return;
        }

        if (_folderWatcher == null)
        {
            _folderWatcher = new MusicFolderWatcher();

            _folderWatcher.ChangesDetected += changeSet =>
            {
                UI(async () => await ProcessFileChangesAsync(changeSet));
            };

            _folderWatcher.FullRescanNeeded += () =>
            {
                UI(async () =>
                {
                    UpdateMainStatus("File watcher buffer overflow, rescanning...");
                    await ScanAndAnalyzeLibraryAsync();
                });
            };
        }

        _folderWatcher.Start(App.FolderPath);
        _log.Information("Folder watcher watching {Path}", App.FolderPath);
    }

    private async Task ProcessFileChangesAsync(WatcherChangeSet changes)
    {
        var filesToAnalyze = new List<MediaItem>();

        // Handle deleted files
        if (changes.Deleted.Count > 0)
        {
            // A deleted path may be a FILE or a DIRECTORY: deleting a folder in Explorer
            // fires a single Deleted for the folder, not one per file. So remove tracked
            // files that equal a deleted path OR live anywhere under a deleted directory.
            // Paths are normalized (GetFullPath) so separator/case drift can't miss.
            var deletedPaths = changes.Deleted.Select(NormalizePath).ToList();
            var deletedItems = _allItems
                .Where(i => IsLocalLibraryFile(i) && IsUnderAnyDeletedPath(NormalizePath(i.FilePath!), deletedPaths))
                .ToList();

            _log.Information("Watcher: {Deleted} deleted path(s) -> matched {Matched} tracked item(s)", changes.Deleted.Count, deletedItems.Count);
            if (deletedItems.Count == 0)
            {
                _log.Debug("Watcher delete matched nothing. Reported: {Paths}", string.Join(" | ", changes.Deleted));
            }

            foreach (var item in deletedItems)
            {
                _allItems.Remove(item);
            }

            if (deletedItems.Count > 0)
            {
                await Task.Run(() => MediaCache.RemoveLibraryFiles(deletedItems.Select(i => i.Id)));
            }
        }

        // Handle created files
        foreach (var path in changes.Created)
        {
            if (await WaitForFileReady(path))
            {
                var item = FileScanner.CreateMediaItemFromPath(path);

                // Dedup: a rip (or any path) may have already added this file directly.
                if (item != null && !LibraryContainsPath(path))
                {
                    _allItems.Add(item);
                    filesToAnalyze.Add(item);
                }
            }
        }

        // Handle changed files (modified in place)
        foreach (var path in changes.Changed)
        {
            if (await WaitForFileReady(path))
            {
                var existing = _allItems.FirstOrDefault(
                    i => IsLocalLibraryFile(i) &&
                    string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase));

                var item = FileScanner.CreateMediaItemFromPath(path);

                if (item != null)
                {
                    if (existing != null)
                    {
                        // Same rule as the full rescan: a changed file keeps its user state.
                        item.AdoptUserStateFrom(existing);
                        _allItems.Remove(existing);
                    }

                    _allItems.Add(item);
                    filesToAnalyze.Add(item);
                }
            }
        }

        if (changes.Deleted.Count > 0 || filesToAnalyze.Count > 0)
        {
            ApplyFilter();
            UpdateTitle();
        }

        if (filesToAnalyze.Count > 0)
        {
            await AnalyzeAllFilesAsync(filesToAnalyze);
            UpdateData();
        }
        else if (changes.Deleted.Count > 0)
        {
            UpdateData();
        }

        // The library changed - re-evaluate the CD view's green checks. Deleting the
        // ripped folder must clear them; newly-analyzed files restore them.
        if (changes.Deleted.Count > 0 || filesToAnalyze.Count > 0)
        {
            RefreshCdRipRecognition();
        }
    }

    private static async Task<bool> WaitForFileReady(string path, int maxAttempts = 10)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (IOException)
            {
                await Task.Delay(300);
            }
        }

        return false;
    }

    private async Task AnalyzeAllFilesAsync(List<MediaItem> filesToAnalyze)
    {
        if (filesToAnalyze.Count == 0)
        {
            UpdateMainStatus("Ready (loaded from cache)");
            return;
        }

        await Task.Run(() =>
        {
            int idx = 0;

            // One transaction per chunk instead of one per FILE: a first scan of a big
            // library used to pay a connection + journal fsync per track. 200 keeps the
            // window a crash could lose to a few seconds of re-analysis.
            const int FlushEvery = 200;
            var pending = new List<MediaItem>(FlushEvery);

            foreach (MediaItem item in filesToAnalyze)
            {
                AudioFileAnalyzer.AnalyzeFile(item);

                pending.Add(item);
                if (pending.Count >= FlushEvery)
                {
                    MediaCache.UpsertMusicBatch(pending);
                    pending.Clear();
                }

                UpdateMainStatus($"Analyzing file {++idx} of {filesToAnalyze.Count}");
            }

            MediaCache.UpsertMusicBatch(pending);

            UpdateMainStatus($"Analyzing file {idx} of {filesToAnalyze.Count} | COMPLETE!");

            UpdateData();
        });
    }

}
