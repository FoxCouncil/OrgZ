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

/// <summary>Portable devices (iPod / Rockbox): connect/disconnect and every sync gesture.</summary>
internal partial class MainWindowViewModel
{
    /// <summary>
    /// Whether the active playlist can sync to a connected, writable device - drives the
    /// header's Sync button. One authority: the device tier's own capability claim, the same
    /// gate the sidebar's Sync submenu uses.
    /// </summary>
    public bool CanSyncToIPod =>
        (SelectedSidebarItem?.PlaylistId != null || SelectedSidebarItem?.IsFavorites == true)
        && _connectedDevices.Values.Any(IsSyncTarget);

    /// <summary>A connected device we can sync a playlist's TRACKS to - the playlist itself is
    /// optional garnish (a tier without native playlists still gets the songs).</summary>
    private static bool IsSyncTarget(ConnectedDevice d) => IPodDevice.For(d).SupportsTrackAdd;

    /// <summary>
    /// Syncs the active playlist (or Favorites) to a connected device: Rockbox → copy missing
    /// music + write the M3U; stock iPod → copy missing tracks + write a native playlist (binary
    /// iTunesDB on Hash58 generations, or the Nano 5G SQLite container on Hash72).
    /// </summary>
    [RelayCommand]
    private async Task SyncCurrentPlaylistToIPodAsync()
    {
        if (SelectedSidebarItem is not { } playlistItem || (playlistItem.PlaylistId is null && !playlistItem.IsFavorites))
        {
            return;
        }

        // Never guess between plugged-in iPods: one target syncs directly, several must be chosen
        // from - grabbing the first dictionary entry was a dangerous game.
        var targets = _connectedDevices.Values.Where(IsSyncTarget).ToList();
        if (targets.Count == 0)
        {
            UpdateMainStatus("No connected device can take this playlist.");
            return;
        }
        var device = targets[0];
        if (targets.Count > 1)
        {
            var name0 = string.IsNullOrWhiteSpace(playlistItem.Name) ? "this playlist" : $"“{playlistItem.Name}”";
            var picker = new Views.DevicePickerDialog(targets.Select(t => t.SidebarLabel).ToList(), title: "Sync Playlist", prompt: $"Sync {name0} to:");
            var chosen = await picker.ShowDialog<int?>(_window);
            if (chosen is not { } idx)
            {
                return;
            }
            device = targets[idx];
        }

        var name = string.IsNullOrWhiteSpace(playlistItem.Name) ? "Playlist" : playlistItem.Name;
        await SyncPlaylistToDeviceAsync(name, CollectCurrentViewBurnTracks(), device);
    }

    /// <summary>
    /// Syncs a playlist's tracks to any writable device through the <see cref="IPodDevice"/> abstraction:
    /// copies each track not already present (transcoding as the tier needs), then writes a native user
    /// playlist referencing all of them. Tier-agnostic - the Nano 5G SQLite, binary iTunesDB, and Rockbox
    /// filesystem paths all live behind <see cref="IPodDevice.AddTrackAsync"/> / <see cref="IPodDevice.CreatePlaylistAsync"/>.
    /// </summary>
    /// <summary>A null <paramref name="name"/> syncs the tracks with NO native playlist - the
    /// entire-library leg, which only fills the device's master list.</summary>
    private async Task SyncPlaylistToDeviceAsync(string? name, IReadOnlyList<MediaItem> tracks, ConnectedDevice device)
    {
        var label = name ?? "Music Library";
        if (tracks.Count == 0)
        {
            UpdateMainStatus($"“{label}” is empty — nothing to sync.");
            return;
        }

        // Only the stock-iPod tiers transcode; Rockbox copies files as-is, so don't gate it on ffmpeg.
        var ffmpeg = ResolveFfmpeg();
        if (device.DeviceType == DeviceType.StockIPod && ffmpeg is null)
        {
            UpdateMainStatus("ffmpeg wasn't found — needed to transcode for the iPod.");
            return;
        }

        var ipod = IPodDevice.For(device);
        var deviceSource = $"device:{device.MountPath}";
        var deviceByAT = _allItems
            .Where(i => i.Source == deviceSource && !string.IsNullOrEmpty(i.FilePath))
            .GroupBy(i => NormalizeMatchKey(i.Artist, i.Title), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Opt-in (Settings > Services > Keep Running After OrgZ Closes): offer the tracks the
        // device is missing to the background service, which owns the copy from there - it
        // keeps running if OrgZ closes. Tracks only: a native playlist (on tiers that have
        // them) is written by the next in-app sync, which matches the already-copied tracks
        // and skips straight to the playlist write. Nested sub-syncs of a full device plan
        // (_deviceSyncCts held) never hand off - the plan owns the whole gesture.
        if (_deviceSyncCts == null && Settings.Get("OrgZ.Services.KeepAlive.IPodSync", false))
        {
            var missing = tracks.Where(t =>
            {
                var key = NormalizeMatchKey(t.Artist, t.Title);
                return string.IsNullOrEmpty(key) || !deviceByAT.ContainsKey(key);
            }).ToList();

            var progressPath = Path.Combine(Path.GetTempPath(), $"orgz-sync-{Guid.NewGuid():N}.jsonl");
            if (await TryHandOffSyncToServiceAsync(device.MountPath, missing,
                    (mount, ids) => Services.DeviceHelper.DeviceHelperClient.RunSyncAsync(mount, progressPath, ids),
                    keepAliveEnabled: true))
            {
                _log.Information("Sync of {Name} to {Device} handed to the background service ({Count} track(s))", label, device.MountPath, missing.Count);
                UpdateMainStatus($"Syncing “{label}” to {device.Name} in the background — it keeps going even if OrgZ closes.");
                await ReattachToServiceJobsAsync();
                return;
            }
        }

        // One batch scope around the whole sync: tiers with deferrable per-write work (the Nano 5G's
        // full-CDB regeneration) rebuild once at the end instead of once per track.
        using var batch = ipod.BeginBatchWrite();
        var (ct, owns) = BeginSyncScope();

        var playlistItems = new List<MediaItem>(tracks.Count);   // matched-or-imported device items, in order
        int matched = 0, added = 0, failed = 0;

        // The pipeline: the USB copy is the only genuinely serial resource, so the host-side
        // work (ReplayGain analysis + transcode) runs a bounded window of workers AHEAD of the
        // device writer. On an all-FLAC library this turns per-track time from
        // transcode+copy into just copy.
        using var prepareGate = new SemaphoreSlim(SyncPrepareWorkers);
        var prepareTasks = new Task<string?>?[tracks.Count];
        int nextPrepare = 0;

        bool NeedsAdd(MediaItem t)
        {
            var k = NormalizeMatchKey(t.Artist, t.Title);
            return (string.IsNullOrEmpty(k) || !deviceByAT.ContainsKey(k))
                && !string.IsNullOrEmpty(t.FilePath) && File.Exists(t.FilePath);
        }

        void StartPreparesThrough(int upTo)
        {
            for (; nextPrepare < tracks.Count && nextPrepare <= upTo; nextPrepare++)
            {
                var t = tracks[nextPrepare];
                prepareTasks[nextPrepare] = NeedsAdd(t)
                    ? PrepareTrackForDeviceAsync(ipod, t, ffmpeg ?? "ffmpeg", prepareGate, ct)
                    : null;
            }
        }

        BeginLcdBusy($"Syncing to {device.Name}");
        try
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                StartPreparesThrough(i + SyncPrepareWorkers * 2);

                var t = tracks[i];
                var key = NormalizeMatchKey(t.Artist, t.Title);
                if (!string.IsNullOrEmpty(key) && deviceByAT.TryGetValue(key, out var existing))
                {
                    playlistItems.Add(existing);
                    matched++;
                    continue;
                }
                if (string.IsNullOrEmpty(t.FilePath) || !File.Exists(t.FilePath))
                {
                    failed++;
                    continue;
                }
                try
                {
                    var preparedFile = prepareTasks[i] is { } prep ? await prep : null;
                    var deviceItem = await AddTrackToDeviceCoreAsync(ipod, t, ffmpeg ?? "ffmpeg", ct, i + 1, tracks.Count, preparedFile);
                    playlistItems.Add(deviceItem);
                    added++;
                }
                catch (Services.Nano5gNotSeededException)
                {
                    throw;   // no track will ever write - surface it once, don't tally 100 "failed"s
                }
                catch (OperationCanceledException)
                {
                    throw;   // cancellation stops the gesture - never counts as a per-track failure
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Sync: failed to add {Track} to {Device}", t.FilePath, device.MountPath);
                    failed++;
                }
            }

            if (playlistItems.Count == 0)
            {
                UpdateMainStatus($"Couldn't sync any tracks to {device.Name}.");
                return;
            }

            // The playlist write is optional garnish: a tier without native playlists still got the
            // songs above, it just has nothing to hang the name on.
            if (name is not null && ipod.SupportsPlaylists)
            {
                SetLcdBusy($"Writing playlist “{name}”", 1);
                await ipod.CreatePlaylistAsync(name, playlistItems);

                // Reflect the new playlist in OrgZ's device tree right away.
                var pl = new DevicePlaylist { Name = name, Key = SanitizeFileName(name), TrackIds = playlistItems.Select(x => x.Id).ToList() };
                PublishDevicePlaylists(device, device.Playlists.Where(e => e.Key != pl.Key).Append(pl).ToList());
            }

            IPodArtworkReader.Invalidate(device.MountPath);
            device.SetSpaceFrom(_allItems.Where(i => i.Source == deviceSource));
            ApplyFilter();

            _log.Information("Synced playlist {Name} to {Device}: matched={Matched} added={Added} failed={Failed} total={Total} nativePlaylist={Native}", label, device.MountPath, matched, added, failed, playlistItems.Count, name is not null && ipod.SupportsPlaylists);
            UpdateMainStatus(name is null
                ? $"Synced your library to {device.Name} — {playlistItems.Count} track(s), {added} new."
                : ipod.SupportsPlaylists
                ? $"Synced “{name}” to {device.Name} — {playlistItems.Count} track(s), {added} new."
                : $"Synced “{name}”'s tracks to {device.Name} — {added} new (no playlists on this device).");
        }
        catch (Services.Nano5gNotSeededException ex)
        {
            _log.Warning("Sync to {Device} skipped: {Reason}", device.Name, ex.Message);
            UpdateMainStatus(ex.Message);
        }
        catch (OperationCanceledException)
        {
            if (!owns)
            {
                throw;   // a full device sync owns this gesture - let it stop everything
            }
            _log.Information("Sync of {Name} to {Device} cancelled after {Added} added", label, device.MountPath, added);
            UpdateMainStatus($"Sync cancelled — {added} track(s) made it to {device.Name}.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Sync to {Device} failed", device.Name);
            UpdateMainStatus($"Sync failed: {ex.Message}");
        }
        finally
        {
            // Prepared-but-unconsumed temps (cancel or failure mid-run) are ours to delete -
            // consumed ones were deleted by the import. Still-running prepares clean up via a
            // continuation once their ffmpeg exits (they share this sync's token).
            foreach (var task in prepareTasks)
            {
                if (task is null)
                {
                    continue;
                }
                if (task.IsCompleted)
                {
                    if (task.Status == TaskStatus.RanToCompletion && task.Result is { } temp)
                    {
                        try { File.Delete(temp); } catch { /* best-effort temp cleanup */ }
                    }
                }
                else
                {
                    _ = task.ContinueWith(static t =>
                    {
                        _ = t.Exception;   // observe, so a faulted prepare never trips the unobserved handler
                        if (t.Status == TaskStatus.RanToCompletion && t.Result is { } temp)
                        {
                            try { File.Delete(temp); } catch { /* best-effort temp cleanup */ }
                        }
                    }, TaskScheduler.Default);
                }
            }

            EndSyncScope(owns);
            EndLcdBusy();
        }
    }

    /// <summary>Workers for the host-side prepare stage of a sync. ffmpeg saturates cores
    /// quickly, and past the USB copy rate more workers only pile up temp files.</summary>
    internal static int SyncPrepareWorkers { get; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    /// <summary>
    /// One pipeline worker: ReplayGain-analyze the LIBRARY file if it never was (permanent, same
    /// as the serial path did), then the tier's host-side transcode. Gated so at most
    /// <see cref="SyncPrepareWorkers"/> ffmpeg processes run at once.
    /// </summary>
    private async Task<string?> PrepareTrackForDeviceAsync(IPodDevice ipod, MediaItem track, string ffmpeg, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (track.ReplayGainTrackGainDb is null && !string.IsNullOrEmpty(track.FilePath) && File.Exists(track.FilePath)
                && track.Source?.StartsWith("device:", StringComparison.Ordinal) != true)
            {
                var gain = await ReplayGainService.ComputeAndTagAsync(track.FilePath, ffmpeg, ct);
                if (gain is { } g)
                {
                    track.ReplayGainTrackGainDb = g;
                    FireAndForget(Task.Run(() => MediaCache.UpdateReplayGain(track.Id, g), CancellationToken.None), "replay-gain persist");
                }
            }

            return await ipod.PrepareTrackAsync(track, ffmpeg, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Whether the device shown in the info bar can take a podcast sync - the tier's own claim.</summary>
    public bool CanSyncPodcasts
    {
        get
        {
            var dev = DeviceForSidebarItem(SelectedSidebarItem);
            return dev is not null && IPodDevice.For(dev).SupportsPodcasts;
        }
    }

    /// <summary>Whether the shown device can take any sync - the header Sync button's gate.</summary>
    public bool CanSyncToDevice
    {
        get
        {
            var dev = DeviceForSidebarItem(SelectedSidebarItem);
            if (dev is null)
            {
                return false;
            }
            var ipod = IPodDevice.For(dev);
            return ipod.SupportsPodcasts || ipod.SupportsAudiobooks || ipod.SupportsPlaylists;
        }
    }

    /// <summary>Header Sync button: the unified sync for the device shown in the info bar.</summary>
    [RelayCommand]
    private async Task SyncSelectedDevice() => await SyncDeviceAsync(SelectedSidebarItem);

    /// <summary>Header Sync Settings button: opens the plan without waiting for a first sync.</summary>
    [RelayCommand]
    private async Task SyncSelectedDeviceSettings() => await SyncDeviceAsync(SelectedSidebarItem, forceSettings: true);

    /// <summary>
    /// The one Sync gesture (device right-click > Sync). First run - or any device with no saved
    /// plan - opens the settings dialog; after that, runs the saved plan straight. Passing
    /// <paramref name="forceSettings"/> always opens the dialog (the "Sync Settings..." entry).
    /// </summary>
    internal async Task SyncDeviceAsync(SidebarItem? item, bool forceSettings = false)
    {
        var dev = DeviceForSidebarItem(item);
        if (dev is null)
        {
            // A limbo device (ejected by iTunes/AMDS but still on the USB tree) lands here -
            // saying nothing made the button look broken.
            UpdateMainStatus("No device selected to sync — if it was just ejected, replug it.");
            return;
        }

        var plan = SyncPlanStore.Load(dev);
        if (plan is null || forceSettings)
        {
            plan = await EditSyncPlanAsync(dev);
            if (plan is null)
            {
                return;   // cancelled
            }
        }

        await RunSyncPlanAsync(dev, plan);
    }

    /// <summary>Opens the sync-settings dialog for a device, persisting the result on Save. Null on cancel.</summary>
    private async Task<SyncPlan?> EditSyncPlanAsync(ConnectedDevice dev)
    {
        var ipod = IPodDevice.For(dev);
        var playlists = await Task.Run(() => MediaCache.LoadAllPlaylists().Select(p => (p.Id, p.Name)).ToList());
        var current = SyncPlanStore.Load(dev) ?? new SyncPlan();

        var dialog = new Views.SyncSettingsDialog(
            dev.Name, ipod.SupportsPodcasts, ipod.SupportsAudiobooks, ipod.SupportsPlaylists, playlists, current);
        var result = await dialog.ShowDialog<SyncPlan?>(_window);
        if (result is null)
        {
            return null;
        }

        SyncPlanStore.Save(dev, result);
        return result;
    }

    /// <summary>
    /// Runs a device's whole saved plan under one batch scope, so a Nano 5G regenerates its
    /// compressed CDB a single time across podcasts + audiobooks + every playlist, not once each.
    /// Each component honors the tier's own capability claim, so a stale plan can't push a kind the
    /// device can't carry.
    /// </summary>
    private async Task RunSyncPlanAsync(ConnectedDevice dev, SyncPlan plan)
    {
        if (!plan.SyncsAnything)
        {
            UpdateMainStatus($"Nothing selected to sync to {dev.Name} — open Sync Settings to choose.");
            return;
        }

        if (!DeviceStillConnected(dev))
        {
            UpdateMainStatus($"{dev.Name} was disconnected — sync cancelled.");
            return;
        }

        var ipod = IPodDevice.For(dev);
        var (ct, owns) = BeginSyncScope();   // one Cancel X press stops the WHOLE plan, not one sub-sync

        try
        {
            // Refresh the device's library from disk first, so add-dedup and the mirror pass match against
            // what's actually on the device - a stale in-memory view was writing duplicate copies of tracks
            // (random on-device filenames + always-insert), and mirror needs the true device set to prune.
            await ReloadStockIPodLibraryAsync(dev);

            // Snapshot the UI-bound _allItems on this (UI) thread before any Task.Run below reads it.
            var itemById = BuildItemLookup();

            // Preflight: measure the device against its limits before writing a byte. Problems that are
            // already there (a file at the FAT32 ceiling, rows with no media type) are reported now
            // rather than discovered after hours of copying; the repair pass and the write paths
            // below deal with the ones they can, and the post-sync check says what's left.
            var preflight = await Task.Run(() => Services.DeviceLimits.DeviceVerifier.Preflight(dev, 0, 0), ct);
            if (preflight.Worst != Services.DeviceLimits.FindingLevel.Ok)
            {
                UpdateMainStatus($"Before sync, {dev.Name}: {preflight.Summary()}");
            }

            // Block-scoped using (not `using var`): the batch's Dispose - which flushes/regenerates
            // the CDB - runs inside the try, so if it throws on a dead mount the catch below owns it.
            using (var batch = ipod.BeginBatchWrite())
            {
                // Heal rows written by older OrgZ builds before adding anything - it rides this
                // batch's single commit, so an already-full iPod is fixed without re-copying.
                var healed = await ipod.RepairLibraryAsync(ct);
                if (healed > 0)
                {
                    UpdateMainStatus($"Repaired {healed} track(s) already on {dev.Name} so they show in its menus.");
                }

                ct.ThrowIfCancellationRequested();
                if (plan.Podcasts && ipod.SupportsPodcasts)
                {
                    await SyncPodcastsToDeviceAsync(dev, ipod);
                }

                ct.ThrowIfCancellationRequested();
                if (plan.Audiobooks && ipod.SupportsAudiobooks)
                {
                    await SyncAudiobooksToDeviceAsync(dev, ipod);
                }

                ct.ThrowIfCancellationRequested();
                if (plan.EntireLibrary && ipod.SupportsTrackAdd)
                {
                    var music = _allItems.Where(i => IsLocalLibraryFile(i) && i.Kind == MediaKind.Music).ToList();
                    _log.Information("Entire-library sync leg for {Device}: {Count} local music track(s)", dev.MountPath, music.Count);
                    if (music.Count == 0)
                    {
                        UpdateMainStatus("No local music in the library to sync.");
                    }
                    else if (EntireLibraryFitsOrExplain(dev, music))
                    {
                        // No playlist name: the library sync fills the device's master list only;
                        // native playlists come from the selections below.
                        await SyncPlaylistToDeviceAsync(null, music, dev);
                    }
                }

                ct.ThrowIfCancellationRequested();
                if (plan.Favorites && ipod.SupportsTrackAdd)
                {
                    var favorites = FavoriteMusicFiles();
                    if (favorites.Count > 0)
                    {
                        await SyncPlaylistToDeviceAsync("Favorites", favorites, dev);
                    }
                }

                if (plan.PlaylistIds.Count > 0 && ipod.SupportsTrackAdd)
                {
                    var nameById = await Task.Run(() => MediaCache.LoadAllPlaylists().ToDictionary(p => p.Id, p => p.Name));
                    foreach (var pid in plan.PlaylistIds)
                    {
                        var tracks = await Task.Run(() => GetPlaylistMediaItems(pid, itemById));
                        if (tracks.Count > 0)
                        {
                            await SyncPlaylistToDeviceAsync(nameById.GetValueOrDefault(pid, "Playlist"), tracks, dev);
                        }
                    }
                }

                // Auto-sync (mirror): make the device match the plan by pruning music that's no longer
                // selected. Runs last, inside the same batch, so removals join the single CDB regen.
                if (plan.Automatic)
                {
                    await MirrorRemoveAsync(dev, ipod, plan);
                }
            }

            // The batch has committed: read the device back and check what a per-track view can't see -
            // every music row typed, every art claim backed, every playlist entry resolving, no file at
            // the FAT32 ceiling, no folder at the entry limit, the database within what the model loads.
            var verified = await Task.Run(() => Services.DeviceLimits.DeviceVerifier.Verify(dev), CancellationToken.None);
            UpdateMainStatus(verified.Worst switch
            {
                Services.DeviceLimits.FindingLevel.Failed => $"Sync to {dev.Name} finished, but a check failed: {verified.Summary()}",
                Services.DeviceLimits.FindingLevel.Warning => $"Sync to {dev.Name} complete. {verified.Summary()}",
                _ => $"Sync to {dev.Name} complete - device checks passed.",
            });
        }
        catch (OperationCanceledException)
        {
            _log.Information("Sync plan for {Device} cancelled", dev.MountPath);
            UpdateMainStatus($"Sync to {dev.Name} cancelled.");
        }
        catch (Exception ex) when (!DeviceStillConnected(dev))
        {
            // The cable came out mid-sync - the writes now hit a dead mount and throw. Stop
            // rather than crash or grind against a volume that no longer exists.
            _log.Warning(ex, "Sync aborted — {Device} disconnected mid-sync", dev.MountPath);
            UpdateMainStatus($"{dev.Name} was disconnected — sync stopped.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Sync to {Device} failed", dev.MountPath);
            UpdateMainStatus($"Sync to {dev.Name} failed: {ex.Message}");
        }
        finally
        {
            EndSyncScope(owns);
        }
    }

    /// <summary>
    /// Cheap liveness check for a mounted device: false once its volume has vanished (the cable
    /// was pulled). Used to bail a sync early and to classify a mid-sync exception as an unplug
    /// rather than a real failure.
    /// </summary>
    private static bool DeviceStillConnected(ConnectedDevice dev)
        => string.IsNullOrEmpty(dev.MountPath) || Directory.Exists(dev.MountPath);

    /// <summary>
    /// The auto-sync (mirror) removal pass: makes the device MATCH the plan by pruning what's no
    /// longer selected. The keep-set is everything the plan puts on the device - Favorites, each
    /// selected playlist, and (when selected) the library's audiobooks - keyed by artist+title.
    /// Device music and audiobooks not in it are removed, plus all device podcasts when podcasts are
    /// deselected. Untagged tracks (no match key) are always left alone. Removal is confirmed first
    /// (it can't be undone short of re-syncing) and runs inside the caller's batch scope, so a Nano 5G
    /// regenerates its CDB once for the whole add+remove pass.
    /// </summary>
    /// <summary>
    /// The device entries a mirror sync should remove: those whose artist+title match key is NOT in
    /// the keep-set. Untagged tracks (empty key) are never removed - we can't prove they were
    /// deselected. Pure and testable; callers pass the already-kind-filtered device tracks.
    /// </summary>
    /// <summary>Headroom the preflight refuses to eat into: the device needs working space for
    /// its own databases and the copy-then-rename staging writes.</summary>
    internal const long SyncFreeSpaceMarginBytes = 200L * 1024 * 1024;

    /// <summary>
    /// Pure capacity check: do <paramref name="bytesToAdd"/> more bytes fit in
    /// <paramref name="freeBytes"/> with the margin intact? Transcoded sizes aren't knowable up
    /// front, so the source file size stands in - close for copies, conservative for FLAC-&gt;ALAC.
    /// A non-positive freeBytes means the free space is unknown; the sync proceeds rather than
    /// refusing on missing data.
    /// </summary>
    internal static bool FitsOnDevice(long freeBytes, long bytesToAdd)
        => freeBytes <= 0 || bytesToAdd <= freeBytes - SyncFreeSpaceMarginBytes;

    /// <summary>Sums the on-disk size of the tracks not already on the device (matched by
    /// artist/title, the same key every sync path dedups with).</summary>
    internal static long BytesMissingFromDevice(IEnumerable<MediaItem> tracks, HashSet<string> deviceKeys)
    {
        long total = 0;
        foreach (var t in tracks)
        {
            var key = NormalizeMatchKey(t.Artist, t.Title);
            if (!string.IsNullOrEmpty(key) && deviceKeys.Contains(key))
            {
                continue;
            }
            total += t.FileSize ?? 0;
        }
        return total;
    }

    /// <summary>True when the whole-library sync fits on the device; otherwise reports how far
    /// short it falls and skips the leg (podcasts/audiobooks/playlists still run).</summary>
    private bool EntireLibraryFitsOrExplain(ConnectedDevice dev, IReadOnlyList<MediaItem> music)
    {
        var deviceSource = $"device:{dev.MountPath}";
        var deviceKeys = _allItems
            .Where(i => i.Source == deviceSource)
            .Select(i => NormalizeMatchKey(i.Artist, i.Title))
            .Where(k => !string.IsNullOrEmpty(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var needed = BytesMissingFromDevice(music, deviceKeys);
        if (FitsOnDevice(dev.FreeSpace, needed))
        {
            return true;
        }

        var (needSize, needUnit, _) = Helpers.FormatHelper.ReduceBytes(needed);
        _log.Warning("Entire-library sync to {Device} skipped: needs ~{Needed} bytes, {Free} free", dev.MountPath, needed, dev.FreeSpace);
        UpdateMainStatus($"Your library needs ~{needSize:0.#} {needUnit} but {dev.Name} only has {dev.FreeSpaceLabel} free — entire-library sync skipped.");
        return false;
    }

    internal static List<MediaItem> MirrorRemovals(IEnumerable<MediaItem> deviceTracks, HashSet<string> keep)
        => deviceTracks.Where(i =>
        {
            var k = NormalizeMatchKey(i.Artist, i.Title);
            return !string.IsNullOrEmpty(k) && !keep.Contains(k);
        }).ToList();

    private async Task MirrorRemoveAsync(ConnectedDevice dev, IPodDevice ipod, SyncPlan plan)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Note(MediaItem t)
        {
            var k = NormalizeMatchKey(t.Artist, t.Title);
            if (!string.IsNullOrEmpty(k)) { keep.Add(k); }
        }

        if (plan.EntireLibrary)
        {
            foreach (var m in _allItems.Where(i => IsLocalLibraryFile(i) && i.Kind == MediaKind.Music))
            {
                Note(m);
            }
        }
        if (plan.Favorites)
        {
            foreach (var f in FavoriteMusicFiles())
            {
                Note(f);
            }
        }
        var keepLookup = BuildItemLookup();
        foreach (var pid in plan.PlaylistIds)
        {
            foreach (var t in await Task.Run(() => GetPlaylistMediaItems(pid, keepLookup)))
            {
                Note(t);
            }
        }
        if (plan.Audiobooks)
        {
            foreach (var a in _allItems.Where(i => IsLocalLibraryFile(i) && i.Kind == MediaKind.Audiobook))
            {
                Note(a);
            }
        }

        // What the mirror prunes: always music + audiobooks (keep-sets come from the library), and
        // podcasts only when they're deselected entirely - pruning stale episodes WHILE subscribed
        // needs the downloaded-episode enumeration and is a follow-up.
        var deviceSource = $"device:{dev.MountPath}";
        var prune = _allItems.Where(i =>
            i.Source == deviceSource &&
            (i.Kind == MediaKind.Music || i.Kind == MediaKind.Audiobook || (i.Kind == MediaKind.Podcast && !plan.Podcasts)));
        var toRemove = MirrorRemovals(prune, keep);

        // Orphaned playlists: device playlists the plan no longer names. Favorites (when selected) and
        // the Podcasts container are protected here; the master/Library list is protected by the tier.
        var keepNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Podcasts" };
        if (plan.Favorites)
        {
            keepNames.Add("Favorites");
        }
        if (plan.PlaylistIds.Count > 0)
        {
            var nameById = await Task.Run(() => MediaCache.LoadAllPlaylists().ToDictionary(p => p.Id, p => p.Name));
            foreach (var pid in plan.PlaylistIds)
            {
                if (nameById.TryGetValue(pid, out var n)) { keepNames.Add(n); }
            }
        }
        var orphanPlaylists = dev.Playlists.Where(p => !keepNames.Contains(p.Name)).ToList();

        if (toRemove.Count == 0 && orphanPlaylists.Count == 0)
        {
            return;
        }

        var parts = new List<string>();
        if (toRemove.Count > 0)       { parts.Add($"{toRemove.Count} track(s)"); }
        if (orphanPlaylists.Count > 0) { parts.Add($"{orphanPlaylists.Count} playlist(s)"); }
        var confirm = new Views.ConfirmDialog(
            "Auto-sync",
            $"Remove {string.Join(" and ", parts)} from {dev.Name} that are no longer selected?",
            "Remove");
        if (!await confirm.ShowDialog<bool>(_window))
        {
            return;   // kept - this sync stays additive
        }

        int removed = 0;
        foreach (var item in toRemove)
        {
            try
            {
                await ipod.RemoveTrackAsync(item);
                _allItems.Remove(item);
                removed++;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Mirror sync: failed to remove {Track} from {Device}", item.Title, dev.MountPath);
            }
        }
        foreach (var pl in orphanPlaylists)
        {
            try
            {
                await ipod.RemovePlaylistAsync(pl.Name);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Mirror sync: failed to remove playlist {Name} from {Device}", pl.Name, dev.MountPath);
            }
        }

        if (removed > 0 || orphanPlaylists.Count > 0)
        {
            dev.SetSpaceFrom(_allItems.Where(i => i.Source == deviceSource));
            if (orphanPlaylists.Count > 0)
            {
                PublishDevicePlaylists(dev, dev.Playlists.Where(p => keepNames.Contains(p.Name)).ToList());
            }
            ApplyFilter();
            _log.Information("Mirror sync removed {Tracks} track(s) and {Playlists} playlist(s) from {Device}", removed, orphanPlaylists.Count, dev.MountPath);
        }
    }

    /// <summary>
    /// Syncs the library's audiobooks to a device as AUDIOBOOKS. Each import auto-detects the kind
    /// (media_type/media_kind 8) inside the importer; already-present books are skipped by
    /// artist+title match. No playlist - books stand on their own in the device's Audiobooks menu.
    /// </summary>
    private async Task SyncAudiobooksToDeviceAsync(ConnectedDevice dev, IPodDevice ipod)
    {
        var books = _allItems
            .Where(i => IsLocalLibraryFile(i) && i.Kind == MediaKind.Audiobook)
            .ToList();
        if (books.Count == 0)
        {
            UpdateMainStatus("No audiobooks in your library to sync.");
            return;
        }

        var ffmpeg = ResolveFfmpeg();
        if (dev.DeviceType == DeviceType.StockIPod && ffmpeg is null)
        {
            UpdateMainStatus("ffmpeg wasn't found — needed to import audiobooks onto the iPod.");
            return;
        }

        var deviceSource = $"device:{dev.MountPath}";
        var present = _allItems
            .Where(i => i.Source == deviceSource && !string.IsNullOrEmpty(i.FilePath))
            .Select(i => NormalizeMatchKey(i.Artist, i.Title))
            .Where(k => !string.IsNullOrEmpty(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        BeginLcdBusy($"Syncing audiobooks to {dev.Name}");
        var (ct, owns) = BeginSyncScope();
        int added = 0, skipped = 0, failed = 0;
        try
        {
            for (int i = 0; i < books.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var b = books[i];
                if (present.Contains(NormalizeMatchKey(b.Artist, b.Title)))
                {
                    skipped++;
                    continue;
                }
                if (string.IsNullOrEmpty(b.FilePath) || !File.Exists(b.FilePath))
                {
                    failed++;
                    continue;
                }
                try
                {
                    await AddTrackToDeviceCoreAsync(ipod, b, ffmpeg ?? "ffmpeg", ct, i + 1, books.Count);
                    added++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Audiobook sync: failed to add {Book} to {Device}", b.FilePath, dev.MountPath);
                    failed++;
                }
            }

            IPodArtworkReader.Invalidate(dev.MountPath);
            dev.SetSpaceFrom(_allItems.Where(i => i.Source == deviceSource));
            ApplyFilter();
            _log.Information("Synced audiobooks to {Device}: added={Added} skipped={Skipped} failed={Failed}", dev.MountPath, added, skipped, failed);
            UpdateMainStatus($"Synced audiobooks to {dev.Name} — {added} new, {skipped} already there.");
        }
        catch (OperationCanceledException)
        {
            if (!owns)
            {
                throw;
            }
            UpdateMainStatus($"Audiobook sync cancelled — {added} made it to {dev.Name}.");
        }
        finally
        {
            EndSyncScope(owns);
            EndLcdBusy();
        }
    }

    // Per-show sync window: the newest N unplayed downloads of EACH show. (The old global
    // newest-5 cap predated on-hardware verification of the podcast format; per-show keeps
    // a chatty daily show from starving everything else, and finished episodes never ride.)
    private const int PodcastSyncPerShowCap = 10;

    /// <summary>
    /// Syncs DOWNLOADED podcast episodes to a connected iPod through the <see cref="IPodDevice"/>
    /// abstraction, which picks the right format + database for the model (binary iTunesDB, Nano 5G
    /// SQLite, or Rockbox filesystem). Selection is per show: the newest
    /// <see cref="PodcastSyncPerShowCap"/> unplayed downloads of each feed under
    /// {library}/.podcasts/{feedId}/. Afterwards, episodes of SUBSCRIBED shows that are no longer
    /// downloaded locally (retention pruned them, or they finished) are removed from the device;
    /// shows OrgZ isn't subscribed to - an iTunes-managed feed, say - are never touched.
    /// </summary>
    private async Task SyncPodcastsToDeviceAsync(ConnectedDevice dev, IPodDevice ipod)
    {
        var root = App.FolderPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            UpdateMainStatus("No library folder set.");
            return;
        }

        var podcastsDir = Path.Combine(root, ".podcasts");
        if (!Directory.Exists(podcastsDir))
        {
            UpdateMainStatus("No downloaded podcasts to sync.");
            return;
        }

        var subs = Services.Podcast.PodcastCache.GetSubscriptions().ToDictionary(s => s.FeedId);
        var ffmpeg = ResolveFfmpeg();   // only used for non-MP3/AAC episodes; most pass straight through

        BeginLcdBusy($"Syncing podcasts to {dev.Name}");
        int added = 0;
        int pruned = 0;
        List<PodcastPush> episodes;
        HashSet<string> localKeys;
        try
        {
            // Gather per-show selections off the UI thread. localKeys carries EVERY downloaded
            // episode's "{show}\n{title}" - the prune step below matches device rows against it.
            (episodes, localKeys) = await Task.Run(() =>
            {
                var pushes = new List<PodcastPush>();
                var allKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var artByFeed = new Dictionary<long, string?>();   // show cover, fetched once per feed

                foreach (var feedDir in Directory.EnumerateDirectories(podcastsDir))
                {
                    if (!long.TryParse(Path.GetFileName(feedDir), out var feedId))
                    {
                        continue;
                    }
                    subs.TryGetValue(feedId, out var sub);
                    var show = sub?.Title ?? "Podcast";

                    // Downloaded files are named {episodeId}; map back to the local RSS feed cache
                    // (offline) once per feed for the real publish date + episode title.
                    var pubMap = new Dictionary<long, DateTime>();
                    var rssTitles = new Dictionary<long, string>();
                    foreach (var ep in Services.Podcast.PodcastIndexClient.GetCachedEpisodesByFeedId(feedId) ?? [])
                    {
                        if (ep.DatePublishedEpoch > 0) { pubMap[ep.Id] = DateTimeOffset.FromUnixTimeSeconds(ep.DatePublishedEpoch).UtcDateTime; }
                        if (!string.IsNullOrWhiteSpace(ep.Title)) { rssTitles[ep.Id] = ep.Title!; }
                    }

                    var perFeed = new List<(PodcastPush Push, DateTime Pub, bool Played)>();
                    foreach (var file in Directory.EnumerateFiles(feedDir))
                    {
                        if (file.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var haveEpId = long.TryParse(Path.GetFileNameWithoutExtension(file), out var epId);

                        // Title precedence: the file's own ID3 tag, then the RSS episode title (so
                        // tagless MP3s don't surface as their bare numeric id), then the filename.
                        var title = Path.GetFileNameWithoutExtension(file);
                        var lengthMs = 0;
                        string? desc = null;
                        try
                        {
                            using var tf = TagLib.File.Create(file);
                            if (!string.IsNullOrWhiteSpace(tf.Tag.Title)) { title = tf.Tag.Title; }
                            lengthMs = (int)tf.Properties.Duration.TotalMilliseconds;
                            desc = tf.Tag.Comment;
                        }
                        catch
                        {
                            // tagless file - fall back to the RSS title / filename + zero duration
                        }
                        if (haveEpId && title == epId.ToString() && rssTitles.TryGetValue(epId, out var rssTitle))
                        {
                            title = rssTitle;
                        }

                        allKeys.Add($"{show}\n{title}");

                        if (!artByFeed.TryGetValue(feedId, out var coverPath))
                        {
                            coverPath = ArtworkSource.DownloadShowArtToTempFile(sub?.ImageUrl);
                            artByFeed[feedId] = coverPath;
                        }

                        var mtime = File.GetLastWriteTimeUtc(file);
                        var pubDate = haveEpId && pubMap.TryGetValue(epId, out var pd) ? pd : mtime;
                        var played = haveEpId
                            && Services.Podcast.PodcastCache.GetListenPosition(epId) is { Completed: true };

                        perFeed.Add((new PodcastPush(file, title, show, desc, sub?.FeedUrl, pubDate, lengthMs, coverPath), pubDate, played));
                    }

                    pushes.AddRange(perFeed
                        .Where(e => !e.Played)
                        .OrderByDescending(e => e.Pub)
                        .Take(PodcastSyncPerShowCap)
                        .Select(e => e.Push));
                }

                return (pushes, allKeys);
            });

            if (episodes.Count > 0)
            {
                added = await ipod.AddPodcastsAsync(episodes, ffmpeg ?? "ffmpeg",
                    (done, total) => SetLcdBusy($"Copying episode {done} of {total} to {dev.Name}…", total == 0 ? 0.6 : (double)done / total));
            }

            // Mirror pruning within SUBSCRIBED shows: an episode retention deleted locally (or
            // one that finished and fell out of the window) leaves the device too, so the iPod
            // tracks the subscription window instead of accreting forever.
            var deviceSource = $"device:{dev.MountPath}";
            var subscribedShows = new HashSet<string>(
                subs.Values.Select(s => s.Title).Where(t => !string.IsNullOrEmpty(t))!,
                StringComparer.OrdinalIgnoreCase);
            var stale = _allItems.Where(i => i.Source == deviceSource
                                             && i.Kind == MediaKind.Podcast
                                             && i.Album is { } show && subscribedShows.Contains(show)
                                             && !localKeys.Contains($"{show}\n{i.Title}"))
                                 .ToList();
            foreach (var staleEp in stale)
            {
                try
                {
                    SetLcdBusy($"Removing “{staleEp.Title}”…");
                    await ipod.RemoveTrackAsync(staleEp);
                    _allItems.Remove(staleEp);
                    pruned++;
                    _log.Information("Pruned stale device episode {Show} / {Title}", staleEp.Album, staleEp.Title);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Couldn't prune device episode {Title}", staleEp.Title);
                }
            }
            if (pruned > 0)
            {
                ApplyFilter();
            }

            if (episodes.Count == 0 && pruned == 0)
            {
                UpdateMainStatus("No downloaded podcasts to sync.");
                return;
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Podcast sync to {Device} failed", dev.MountPath);
            UpdateMainStatus($"Podcast sync failed: {ex.Message}");
            return;
        }
        finally
        {
            EndLcdBusy();
        }

        // The batch podcast write only touched the on-device database - unlike the music/playlist
        // sync it built no device MediaItems, so re-read the library to surface the new episodes in
        // the iPod's Podcasts view without waiting for a reconnect.
        if (added > 0)
        {
            await ReloadStockIPodLibraryAsync(dev);
        }

        _log.Information("Synced podcasts to {Device}: added={Added} pruned={Pruned}", dev.MountPath, added, pruned);
        UpdateMainStatus((added, pruned) switch
        {
            (> 0, > 0) => $"Synced {Count(added, "episode")} to {dev.Name}, removed {Count(pruned, "stale one")}.",
            (> 0, _)   => $"Synced {Count(added, "episode")} to {dev.Name}.",
            (_, > 0)   => $"Removed {Count(pruned, "stale episode")} from {dev.Name}.",
            _          => "Podcasts are already in sync.",
        });
    }

    /// <summary>
    /// Re-reads a stock iPod's on-device library and swaps the result into <see cref="_allItems"/>,
    /// refreshing the capacity split and the active view. The music/playlist sync updates items in
    /// place, but the batch podcast sync writes only the database, so it calls this to surface the
    /// freshly-added episodes without a reconnect. No-op for non-stock devices.
    /// </summary>
    private async Task ReloadStockIPodLibraryAsync(ConnectedDevice device)
    {
        if (device.DeviceType != DeviceType.StockIPod)
        {
            return;
        }

        var library = await IPodDevice.For(device).ReadLibraryAsync();
        PublishDevicePlaylists(device, library.Playlists);

        var source = $"device:{device.MountPath}";
        _allItems.RemoveAll(i => i.Source == source);
        _allItems.AddRange(library.Tracks);

        device.SetSpaceFrom(library.Tracks);

        ApplyFilter();
    }


    private async Task HandleDeviceConnectedAsync(ConnectedDevice device)
    {
        if (_connectedDevices.TryGetValue(device.MountPath, out var existing))
        {
            // Same physical device re-announced (a duplicate arrival event) - ignore.
            if (DeviceDetectionService.IsSameConnectedDevice(existing, device))
            {
                _log.Debug("HandleDeviceConnectedAsync ignored — {MountPath} already connected (same device)", device.MountPath);
                return;
            }

            // A DIFFERENT iPod now occupies this mount - the previous one's removal was
            // missed/late. Tear it down (drops its tracks + sidebar entry) before the new one
            // connects, so the grid doesn't keep showing the old iPod's library at the reused
            // drive letter.
            _log.Information("HandleDeviceConnectedAsync: {MountPath} now holds a different device — replacing \"{Old}\" with \"{New}\"", device.MountPath, existing.Name, device.Name);
            HandleDeviceDisconnected(device.MountPath);
        }

        _connectedDevices[device.MountPath] = device;

        // Cancellation scope for this device's library scan. Disconnect (or a swap re-using the drive
        // letter) cancels it, which both aborts the read and voids every batch still queued on the
        // dispatcher - see FlushBatch below.
        var scanCts = new CancellationTokenSource();
        _deviceScanCts[device.MountPath] = scanCts;
        var scanToken = scanCts.Token;

        OnPropertyChanged(nameof(CanSyncToIPod));

        var viewKey = $"Device:{device.MountPath}";
        ListViewConfigs.Register(viewKey, ListViewConfigs.BuildDeviceConfig(device.MountPath));

        // The device row itself IS the music view (its ViewConfigKey = "Device:{mount}"). The
        // Podcasts / Audiobooks children are device-scoped sub-views, enabled per the model's
        // capability (via IPodDevice) - and skipped entirely for one-list devices (Shuffles), where
        // pushed episodes fold into the single track list and the sub-views could only ever be empty.
        var ipod = IPodDevice.For(device);
        device.HasKindSubViews = ipod.HasKindSubViews;

        var sidebarItem = new SidebarItem
        {
            Name = device.SidebarLabel,
            Icon = device.Icon,
            IconBitmap = device.GenerationImage,
            Category = "DEVICES",
            IsEnabled = true,
            ViewConfigKey = viewKey,
        };

        if (ipod.HasKindSubViews)
        {
            ListViewConfigs.Register($"{viewKey}:{MediaKind.Podcast}", ListViewConfigs.BuildDeviceKindConfig(device.MountPath, MediaKind.Podcast));
            ListViewConfigs.Register($"{viewKey}:{MediaKind.Audiobook}", ListViewConfigs.BuildDeviceKindConfig(device.MountPath, MediaKind.Audiobook));

            sidebarItem.Children.Add(new SidebarItem
            {
                Name = "Podcasts",
                Icon = "fa-solid fa-podcast",
                Category = "DEVICE",
                IsEnabled = ipod.SupportsPodcasts,
                ViewConfigKey = $"{viewKey}:{MediaKind.Podcast}",
            });

            sidebarItem.Children.Add(new SidebarItem
            {
                Name = "Audiobooks",
                Icon = "fa-solid fa-headphones",
                Category = "DEVICE",
                IsEnabled = ipod.SupportsAudiobooks,
                ViewConfigKey = $"{viewKey}:{MediaKind.Audiobook}",
            });
        }

        DeviceItems.Add(sidebarItem);

        BeginLcdBusy($"Scanning {device.Name}");
        _log.Information("Device scan starting: MountPath={MountPath} Type={DeviceType} Name={Name}", device.MountPath, device.DeviceType, device.Name);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var beforeCount = _allItems.Count;

            // Stream scanned items into _allItems in small batches so the grid fills in as the scan runs,
            // instead of staying empty until it completes. Each batch is marshalled to the UI thread,
            // grows the capacity bar, and re-applies the filter when this device is the selected view.
            long audioBytes = 0;
            void FlushBatch(IReadOnlyList<MediaItem> batch)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    // The device left while this batch sat in the dispatcher queue - dropping it here is
                    // what keeps a departed iPod's rows from re-populating _allItems after teardown.
                    if (scanToken.IsCancellationRequested)
                    {
                        return;
                    }
                    _allItems.AddRange(batch);
                    // Approximate progressive fill while the scan streams; SetSpaceFrom below is the authority.
                    audioBytes += batch.Where(i => i.Kind != MediaKind.Podcast).Sum(i => i.FileSize ?? 0);
                    device.AudioSpace = audioBytes;
                    if (SelectedSidebarItem == sidebarItem)
                    {
                        ApplyFilter();
                    }
                });
            }

            // One polymorphic read path - the tier (SQLite .itlp, binary iTunesDB, or filesystem walk) is
            // chosen inside IPodDevice; playlists come back with the library rather than via a callback.
            var library = await IPodDevice.For(device).ReadLibraryAsync(FlushBatch, d => SetLcdBusy(d), scanToken);
            scanToken.ThrowIfCancellationRequested();   // disconnected between the last batch and completion
            PublishDevicePlaylists(device, library.Playlists);

            sw.Stop();
            var afterCount = _allItems.Count;
            device.SetSpaceFrom(library.Tracks);

            _log.Information("Device scan complete: MountPath={MountPath} Tracks={Tracks} ScanMs={ScanMs} _allItems {Before}->{After}", device.MountPath, library.Tracks.Count, sw.ElapsedMilliseconds, beforeCount, afterCount);

            if (SelectedSidebarItem == sidebarItem)
            {
                _log.Debug("Selected sidebar is the just-scanned device; re-applying filter");
                ApplyFilter();
            }
        }
        catch (OperationCanceledException) when (scanToken.IsCancellationRequested)
        {
            sw.Stop();
            _log.Information("Device scan cancelled: MountPath={MountPath} disconnected mid-scan after {ElapsedMs}ms", device.MountPath, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.Error(ex, "Device scan failed: MountPath={MountPath} ElapsedMs={ElapsedMs}", device.MountPath, sw.ElapsedMilliseconds);
        }
        finally
        {
            EndLcdBusy();
            // Only clear the registration if it's still OURS - a swap may already have installed the
            // replacement device's CTS under this mount path.
            if (_deviceScanCts.TryGetValue(device.MountPath, out var current) && ReferenceEquals(current, scanCts))
            {
                _deviceScanCts.Remove(device.MountPath);
            }
            scanCts.Dispose();
        }

        await MaybeAutoReadIdentityAsync(device);
    }

    // iPods whose privileged identity read we've already attempted this session, keyed by the
    // most stable id - so we prompt at most once per device even across folder-watcher rescans
    // or a quick unplug/replug, and never nag after the user declines.
    private readonly HashSet<string> _identityReadAttempted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Automatic identity read on connect - but only when the privileged helper service is
    /// installed and can do it silently (no UAC / auth dialog). Without the service we do
    /// NOT auto-fire a prompt; the manual info-bar affordance stays as the (deliberately
    /// unlovely) fallback. So installing the service is what turns "click to read" into
    /// "it's just there", with no per-connect permission prompt on any OS.
    /// </summary>
    private async Task MaybeAutoReadIdentityAsync(ConnectedDevice device)
    {
        if (!device.NeedsPrivilegedIdentity)
        {
            return;
        }

        if (!await DeviceHelperClient.IsAvailableAsync())
        {
            return;
        }

        var key = device.FireWireGuid ?? device.Serial ?? device.MountPath;
        if (!_identityReadAttempted.Add(key))
        {
            return;
        }

        await ReadDeviceIdentityAsync(device);
    }

    /// <summary>
    /// Reads the privileged iPod identity - serial + Apple OS version - that only a raw
    /// disk read can recover (UAC on Windows, authopen on macOS), persisting anything new
    /// to <c>/.orgz/device</c>. Returns true if a field was learned. The single source for
    /// both the automatic read-on-connect and the manual info-bar retry.
    /// </summary>
    internal async Task<bool> ReadDeviceIdentityAsync(ConnectedDevice device)
    {
        if (device.DeviceType != DeviceType.StockIPod)
        {
            return false;
        }

        try
        {
            // Prefer the privileged helper service - it does the raw read as root/LocalSystem
            // with NO prompt. Only if it isn't installed do we fall back to the per-operation
            // elevation (UAC / authopen), which is what the manual click triggers.
            var viaService = await DeviceHelperClient.ReadIdentityAsync(device.MountPath, device.IpodGeneration);
            if (viaService is { } svc && ApplyIdentity(device, svc.Serial, svc.FirmwareVersion, svc.ModelNumber))
            {
                _log.Information("Read iPod identity via helper service for {MountPath}: Version={Version} Serial={Serial}", device.MountPath, device.AppleFirmwareVersion, device.Serial);
                return true;
            }

            var learned = false;
            if (OperatingSystem.IsMacOS())
            {
                var mac = await Task.Run(() => IPodFirmwarePartition.ReadIdentityMacOS(device.MountPath, device.IpodGeneration));
                _log.Debug("macOS firmware read diagnostic for {MountPath}:\n{Diagnostic}", device.MountPath, mac.Diagnostic);
                learned = ApplyIdentity(device, mac.Serial, mac.Version, mac.ModelNumber);
            }
            else if (OperatingSystem.IsWindows())
            {
                // Windows already has the serial from WMI; the elevated read fills the OS version.
                var result = await IPodFirmwareElevation.ReadAsync(device.MountPath, device.IpodGeneration);
                if (result.UserDeclined)
                {
                    _log.Information("User declined elevation for iPod identity read on {MountPath}", device.MountPath);
                }
                else if (string.IsNullOrWhiteSpace(result.Version))
                {
                    _log.Warning("iPod identity read returned no version for {MountPath}: {Diagnostic}", device.MountPath, result.Diagnostic);
                }
                learned = ApplyIdentity(device, serial: null, version: result.Version, modelNumber: null);
            }

            if (learned)
            {
                _log.Information("Read iPod identity for {MountPath}: Version={Version} Serial={Serial} ModelNumber={ModelNumber}", device.MountPath, device.AppleFirmwareVersion, device.Serial, device.AppleModelNumber);
            }
            return learned;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Device identity read failed for {MountPath}", device.MountPath);
            return false;
        }
    }

    /// <summary>
    /// Merges freshly-read identity fields into a live device (never overwriting a value we
    /// already have), decoding the model from a recovered serial, and persisting to
    /// <c>/.orgz/device</c> when anything changed. Shared by the service and fallback paths.
    /// </summary>
    private bool ApplyIdentity(ConnectedDevice device, string? serial, string? version, string? modelNumber)
    {
        var learned = false;
        var gotExactModel = false;
        if (!string.IsNullOrWhiteSpace(serial) && string.IsNullOrWhiteSpace(device.Serial))
        {
            device.Serial = serial;
            learned = true;
            if (IPodModelDatabase.LookupBySerial(serial) is { } info)
            {
                device.Model = info.DisplayNameForActualCapacity(device.TotalSpace);
                device.IpodGeneration = info.Generation;
                device.Color = info.Color;
                device.IsGenerationProvisional = false;
                gotExactModel = true;
            }
        }
        if (!string.IsNullOrWhiteSpace(modelNumber) && string.IsNullOrWhiteSpace(device.AppleModelNumber))
        {
            device.AppleModelNumber = modelNumber;
            learned = true;
            // The model number decodes to the exact model/colour/capacity too - use it when the
            // serial didn't already give us one (or its suffix wasn't in the table).
            if (!gotExactModel && IPodModelDatabase.LookupByModelNumber(modelNumber) is { } minfo)
            {
                device.Model = minfo.DisplayNameForActualCapacity(device.TotalSpace);
                device.IpodGeneration = minfo.Generation;
                device.Color = minfo.Color;
                device.IsGenerationProvisional = false;
            }
        }
        if (!string.IsNullOrWhiteSpace(version) && string.IsNullOrWhiteSpace(device.AppleFirmwareVersion))
        {
            device.AppleFirmwareVersion = version;
            learned = true;
        }

        if (learned)
        {
            DeviceFingerprint.PersistDeviceRecord(device);
        }
        return learned;
    }

    /// <summary>
    /// Re-runs device fingerprinting for the selected device without requiring a
    /// reconnect. Useful after the user has edited /.orgz/device or wants to pick up
    /// new metadata from a freshly-booted firmware mode.
    /// </summary>
    internal void RefreshDeviceInfo(SidebarItem item)
    {
        if (item.ViewConfigKey?.StartsWith("Device:") != true)
        {
            return;
        }

        var mountPath = item.ViewConfigKey["Device:".Length..];
        if (!_connectedDevices.TryGetValue(mountPath, out var oldDevice))
        {
            return;
        }

        try
        {
            var drive = new DriveInfo(mountPath);
            var refreshed = DeviceFingerprint.Identify(drive);
            if (refreshed != null)
            {
                // Copy the fresh values back into the live device so existing bindings update
                oldDevice.Name                 = refreshed.Name;
                oldDevice.Model                = refreshed.Model;
                oldDevice.HardwareModel        = refreshed.HardwareModel;
                oldDevice.Serial               = refreshed.Serial;
                oldDevice.FireWireGuid         = refreshed.FireWireGuid;
                oldDevice.AppleModelNumber     = refreshed.AppleModelNumber;
                oldDevice.IpodGeneration       = refreshed.IpodGeneration;
                oldDevice.FirmwareVersion      = refreshed.FirmwareVersion;
                oldDevice.AppleFirmwareVersion = refreshed.AppleFirmwareVersion;
                oldDevice.Format               = refreshed.Format;
                oldDevice.RefreshSpace();

                // The sidebar row's label is a snapshot from connect time - push the fresh one
                // (renames land here via RenameDeviceAsync -> RefreshDeviceInfo).
                item.Name = oldDevice.SidebarLabel;

                DeviceFingerprint.PersistDeviceRecord(oldDevice);
                _log.Information("Refreshed device info: {Model} at {MountPath}", oldDevice.Model, mountPath);
            }
            else
            {
                _log.Warning("Refresh: device at {MountPath} no longer recognized", mountPath);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Refresh failed for {MountPath}", mountPath);
        }
    }

    internal void EjectDevice(SidebarItem item)
    {
        if (item.ViewConfigKey?.StartsWith("Device:") != true)
        {
            return;
        }
        EjectByMount(item.ViewConfigKey["Device:".Length..], item.Name);
    }

    /// <summary>Ejects the device currently shown in the view - the iPod-view header button.</summary>
    [RelayCommand]
    private void EjectSelectedDevice()
    {
        if (SelectedDevice is { } dev)
        {
            EjectByMount(dev.MountPath, dev.Name);
        }
    }

    /// <summary>
    /// Erases a connected iPod: deletes all music/artwork and empties its library (via
    /// <see cref="IPodDevice.EraseAsync"/>), then ejects so the wipe flushes to the removable drive
    /// - reconnecting shows a clean, empty iPod ready to load. For second-hand iPods. Invoked from
    /// the device right-click menu (Settings > Erase iPod).
    /// </summary>
    internal async Task EraseDeviceAsync(SidebarItem? item)
    {
        var dev = DeviceForSidebarItem(item);
        if (dev is null)
        {
            return;
        }

        var confirm = new Views.ConfirmDialog(
            "Erase iPod",
            $"Erase EVERYTHING on “{dev.Name}”?\n\nThis permanently deletes all music, playlists, and artwork on the device, leaving it empty. It cannot be undone.",
            "Erase");
        if (!await confirm.ShowDialog<bool>(_window))
        {
            return;
        }

        var ipod = IPodDevice.For(dev);
        BeginLcdBusy($"Erasing {dev.Name}");
        int removed = 0;
        try
        {
            removed = await ipod.EraseAsync();

            // Privacy pass: an erased iPod stops testifying. The iTunesPrefs machine-name slots
            // become "{user}'s Computer", and OrgZ's own .orgzbak ghosts (pre-erase databases with
            // their track lists and old device name) go with them.
            if (dev.DeviceType == DeviceType.StockIPod)
            {
                await Task.Run(() =>
                {
                    IPodHostPrefs.ScrubHosts(dev.MountPath);
                    IPodHostPrefs.PurgeBackups(dev.MountPath);
                });
                var hosts = IPodHostPrefs.ReadHosts(dev.MountPath);
                dev.HostUserName = hosts.UserName;
                dev.HostComputer = hosts.Computer;
                dev.SetHostRecords(hosts.LegacySlots.Select(s => s.Value).ToList(), hosts.Computer);
            }
        }
        catch (NotImplementedException)
        {
            UpdateMainStatus($"Erase isn't supported on {dev.Name} yet.");
            return;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Erase failed for {Device}", dev.MountPath);
            UpdateMainStatus($"Erase failed: {ex.Message}");
            return;
        }
        finally
        {
            EndLcdBusy();
        }

        _log.Information("Erased {Device}: removed {Count} file(s)", dev.MountPath, removed);

        // Refresh IN PLACE - drop the erased device's now-stale items, clear its playlists, and zero
        // its audio usage so the view + capacity bar show the empty library immediately. Critically
        // we do NOT pull the device off the sidebar (the old teardown+re-add read as an eject). The
        // iPod stays mounted and selected. (FAT32 quick-removal flushes the wipe on close.)
        _allItems.RemoveAll(i => i.Source == $"device:{dev.MountPath}");
        PublishDevicePlaylists(dev, Array.Empty<DevicePlaylist>());
        dev.SetSpaceFrom([]);
        ApplyFilter();
        UpdateMainStatus($"Erased {dev.Name} — {removed} file(s) removed. The library is now empty.");
    }

    private void EjectByMount(string mountPath, string? name)
    {
        // Let go of everything we hold on the device first, or Windows can't eject it.
        ReleaseDeviceHandles(mountPath);

        if (DeviceEjector.Eject(mountPath, out var error))
        {
            _log.Information("Ejected {Name} at {MountPath}", name, mountPath);
            UpdateMainStatus($"Ejected {name}.");
            // The WMI removal event will fire shortly and HandleDeviceDisconnected will
            // tear down the sidebar entry, view config, and items.
        }
        else
        {
            _log.Warning("Eject failed for {Name} at {MountPath}: {Error}", name, mountPath, error ?? "unknown error");
            UpdateMainStatus($"Couldn't eject {name} — {error ?? "it may still be in use"}.");
        }
    }

    /// <summary>
    /// Releases everything OrgZ holds on a device so the OS can eject it cleanly: stops playback when
    /// the current track lives on it (which frees the backing file handle), drops pooled SQLite
    /// connections (the device's <c>/.orgz/library.db</c>), and clears the cached ArtworkDB reader.
    /// </summary>
    private void ReleaseDeviceHandles(string mountPath)
    {
        if (CurrentPlayingItem?.Source == $"device:{mountPath}")
        {
            _log.Information("Stopping playback from {MountPath} ahead of eject", mountPath);
            ClearPlayback();
        }

        // The /.orgz cache uses pooled per-op connections; clear the pool so its handle releases.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        IPodArtworkReader.Invalidate(mountPath);
    }

    private void HandleDeviceDisconnected(string mountPath)
    {
        if (!_connectedDevices.Remove(mountPath))
        {
            return;
        }

        // Cancel the in-flight library scan first, so batches it already queued on the dispatcher void
        // themselves (FlushBatch checks the token) instead of re-populating _allItems after the
        // RemoveAll below.
        if (_deviceScanCts.Remove(mountPath, out var scanCts))
        {
            scanCts.Cancel();
        }

        // Release the mount-path-keyed handles (pooled SQLite connections + the cached ArtworkDB reader)
        // and stop playback from this device. Without this, a DIFFERENT iPod arriving on the same reused
        // drive letter would reuse the departed iPod's pooled DB handle + artwork cache and show its
        // library - the eject path already did this, but WMI-removal and hot-swap disconnects did not.
        ReleaseDeviceHandles(mountPath);

        OnPropertyChanged(nameof(CanSyncToIPod));

        var source = $"device:{mountPath}";
        var viewKey = $"Device:{mountPath}";

        _allItems.RemoveAll(i => i.Source == source);

        // Evict every cached view that can still show the departed iPod's rows. The device's own view
        // family must go: a different iPod arriving at the reused drive letter inherits the exact same
        // view key, and with _dataVersion untouched by any of the removals above, ApplyFilter's reuse
        // path would serve the OLD iPod's cached list verbatim - the swapped-in device showing its
        // predecessor's library. The content scan catches device rows cached under non-device keys
        // (a favorited device track sitting in the Favorites view's cache).
        var evicted = _viewCache.Keys.Where(k => IsDeviceViewKeyFor(k, mountPath) || _viewCache[k].Items.Any(i => i.Source == source)).ToList();
        foreach (var key in evicted)
        {
            _viewCache.Remove(key);
        }

        // The device entry is a tree parent - removing it drops the Music/Podcasts/Audiobooks/playlist
        // children along with it, since they're just Children of the parent SidebarItem.
        var sidebarItem = DeviceItems.FirstOrDefault(d => d.ViewConfigKey == viewKey);
        if (sidebarItem != null)
        {
            DeviceItems.Remove(sidebarItem);
        }

        // The whole view-config family: the root view plus the Podcast/Audiobook/per-playlist sub-views
        // (removing only the root leaked the rest on every swap).
        ListViewConfigs.RemoveWithSubViews(viewKey);

        // If the user was viewing any part of this device tree - including a Podcasts/Audiobooks/playlist
        // child - fall back to the library. Otherwise, if the on-screen view's cache was evicted above,
        // it is showing rows the device took with it: rebuild it in place (fromViewSwitch keeps
        // _dataVersion untouched so unaffected views keep their cached state).
        if (IsDeviceViewKeyFor(SelectedSidebarItem?.ViewConfigKey, mountPath))
        {
            SelectedSidebarItem = LibraryItems.FirstOrDefault() ?? null;
        }
        else if (_activeViewConfig != null && evicted.Contains(_activeViewConfig.Key))
        {
            ApplyFilter(fromViewSwitch: true);
        }
    }

    /// <summary>
    /// Marshals a batch of device-side playlists back to the UI thread, replaces the
    /// device's current playlist list, and rebuilds the sidebar tree children under the
    /// "Playlists" node. Also registers/unregisters the per-playlist view configs so
    /// selecting a playlist in the sidebar filters the grid correctly.
    /// </summary>
    private void PublishDevicePlaylists(ConnectedDevice device, IReadOnlyList<DevicePlaylist> playlists)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var mountPath = device.MountPath;

            // Swap the device's playlist collection atomically - callers can bind to it
            // if we ever want a device-level playlist header.
            device.Playlists.Clear();
            foreach (var pl in playlists)
            {
                device.Playlists.Add(pl);
            }

            // Playlists hang directly off the device node (no "Playlists" grouping node).
            // If the user disconnected between scan completion and this dispatch, the device
            // item won't be there - just bail.
            var deviceViewKey = $"Device:{mountPath}";
            var deviceParent = DeviceItems.FirstOrDefault(d => d.ViewConfigKey == deviceViewKey);
            if (deviceParent == null)
            {
                return;
            }

            // Drop previously-published playlist rows (+ their view configs) so a rescan
            // replaces them cleanly. Identified by the per-playlist view-key prefix, so the
            // Podcasts / Audiobooks placeholders are left in place.
            var playlistPrefix = $"Device:{mountPath}:Playlist:";
            foreach (var stale in deviceParent.Children.Where(c => c.ViewConfigKey?.StartsWith(playlistPrefix, StringComparison.Ordinal) == true).ToList())
            {
                if (stale.ViewConfigKey != null)
                {
                    ListViewConfigs.Remove(stale.ViewConfigKey);
                }
                deviceParent.Children.Remove(stale);
            }

            // Append the playlists below the Podcasts / Audiobooks nodes - same level, not nested.
            foreach (var pl in playlists)
            {
                var viewKey = $"Device:{mountPath}:Playlist:{pl.Key}";
                ListViewConfigs.Register(viewKey, ListViewConfigs.BuildDevicePlaylistConfig(viewKey, pl.TrackIds));

                deviceParent.Children.Add(new SidebarItem
                {
                    Name = pl.Name,
                    Icon = "fa-solid fa-list-ul",
                    Category = "DEVICE",
                    IsEnabled = true,
                    ViewConfigKey = viewKey,
                });
            }

            _log.Information("Device playlists published: MountPath={MountPath} Count={Count}", mountPath, playlists.Count);
        });
    }

}
