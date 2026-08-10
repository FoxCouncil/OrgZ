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

/// <summary>CD audio: disc scan/play, ripping, burning, and the LCD busy page.</summary>
internal partial class MainWindowViewModel
{

    // Drives whose loaded disc was probed and had no audio tracks (blank CD-RW, data
    // disc): skipped on later poll ticks until the media leaves the drive, so a blank
    // disc isn't hit with a READ TOC every 3 seconds.
    private readonly HashSet<string> _cdProbedNoAudio = new(StringComparer.OrdinalIgnoreCase);

    // Last observed drive/media state, so the poll logs transitions instead of heartbeats.
    private string? _lastCdScanSignature;

    private async Task ScanForCdAsync()
    {
        if (_cdScanning)
        {
            _log.Debug("ScanForCdAsync skipped: already scanning");
            return;
        }

        // A rip, burn, or erase owns the drive: the scan's media checks and TOC probes
        // would inject SCSI commands mid-write and stall against the busy drive.
        if (IsBusy || _burnWritePhase)
        {
            return;
        }

        _cdScanning = true;

        try
        {
            // Volume queries against optical drives (IsReady, media checks) block for
            // seconds while a drive is busy - keep every one of them off the UI thread.
            var (all, drives, readiness) = await Task.Run(() =>
            {
                var a = CdAudioService.GetAllCdDrives();
                var w = CdAudioService.GetCdDrivesWithMedia();
                return (a, w, string.Join(", ", a.Select(d => $"{d.Name}[ready={d.IsReady}]")));
            });
            // The 3s media-arrival poll calls this forever, so only a CHANGE is worth a
            // line - the unchanged steady state used to emit ~1200 identical Information
            // entries an hour and bury everything else in the log.
            var signature = $"{all.Count}|{drives.Count}|{readiness}";
            if (signature != _lastCdScanSignature)
            {
                _lastCdScanSignature = signature;
                _log.Information("ScanForCdAsync: AllCdDrives={All} WithMedia={WithMedia} (paths: {Paths})",
                    all.Count, drives.Count, readiness);
            }

            // A drive that lost its media forgets its no-audio memo so the next
            // inserted disc gets probed fresh.
            var mediaIds = drives.Select(d => d.Name.TrimEnd('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _cdProbedNoAudio.RemoveWhere(id => !mediaIds.Contains(id));

            // Probe each drive's write capability once (cached) so the Burn button only
            // shows for real recorders. Probing does SCSI I/O - run it off the UI thread,
            // and skip while a rip/burn holds the drive (uncached → optimistic "writable").
            foreach (var d in all)
            {
                if (!_burnerSupport.ContainsKey(d.Name) && !IsBusy)
                {
                    var probe = d;
                    _burnerSupport[d.Name] = await Task.Run(() => CdAudioService.IsAudioBurner(probe));
                }
            }

            var presentNames = all.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _burnerSupport.Keys.Where(k => !presentNames.Contains(k)).ToList())
            {
                _burnerSupport.Remove(stale);
            }

            // Surface recorder presence so playlist/Favorites views can show Burn.
            IsBurnerPresent = all.Any(d => _burnerSupport.GetValueOrDefault(d.Name, true));

            // Check for ejected discs
            if (_cdTracks.Count > 0)
            {
                var activeDriveIds = drives.Select(d => d.Name.TrimEnd('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var trackedDrives = _cdTracks
                    .Select(t => { var s = t.Id[3..]; return s[..s.LastIndexOf(':')]; })
                    .Distinct()
                    .ToList();

                foreach (var driveId in trackedDrives)
                {
                    if (!activeDriveIds.Contains(driveId))
                    {
                        // Stop playback if playing a CD track from this drive
                        if (CurrentPlayingItem?.Source == "cdda" && CurrentPlayingItem.Id.StartsWith($"cd:{driveId}:"))
                        {
                            ClearPlayback();
                        }

                        _allItems.RemoveAll(i => i.Id.StartsWith($"cd:{driveId}:"));
                        _cdTracks.RemoveAll(t => t.Id.StartsWith($"cd:{driveId}:"));

                        var toRemove = DeviceItems.FirstOrDefault(d => d.Name.Contains(driveId));
                        if (toRemove != null)
                        {
                            DeviceItems.Remove(toRemove);
                        }

                        _cdCoverArt = null;
                        _cdCoverArtBytes = null;
                        CurrentCdInfo = null;
                    }
                }

                if (_cdTracks.Count == 0 && SelectedSidebarItem?.ViewConfigKey == "CdAudio")
                {
                    SelectedSidebarItem = LibraryItems[0];
                    ApplyFilter();
                }
            }

            foreach (var drive in drives)
            {
                var driveId = drive.Name.TrimEnd('\\', '/');

                // Skip if already have tracks from this drive
                if (_cdTracks.Any(t => t.Id.StartsWith($"cd:{driveId}:")))
                {
                    continue;
                }

                if (_cdProbedNoAudio.Contains(driveId))
                {
                    continue;
                }

                var discInfo = await CdAudioService.ReadDiscAsync(_vlc, drive);

                if (discInfo.Tracks.Count == 0)
                {
                    _cdProbedNoAudio.Add(driveId);
                    continue;
                }

                _cdTracks.AddRange(discInfo.Tracks);
                _allItems.AddRange(discInfo.Tracks);

                // Remember the disc's DiscID, and restore green checks for any tracks
                // we've ripped from this exact disc before (this or a past session).
                if (discInfo.DiscId is { Length: > 0 } discId &&
                    DrivePathFromCdTrackId(discInfo.Tracks[0].Id) is { } discKey)
                {
                    _cdDiscIdByDrive[discKey] = discId;

                    // Recognize tracks we've ripped from this exact disc before by the
                    // MUSICBRAINZ_DISCID stamped in the library files' tags - no side DB.
                    var already = _allItems
                        .Where(i => i.Kind == MediaKind.Music && i.DiscId == discId && i.Track is not null)
                        .Select(i => (int)i.Track!.Value)
                        .ToHashSet();
                    foreach (var t in discInfo.Tracks)
                    {
                        if (t.Track is { } n && already.Contains((int)n))
                        {
                            t.RipStatus = RipState.Ripped;
                        }
                    }
                }

                var album = discInfo.Tracks[0].Album;
                var label = string.IsNullOrWhiteSpace(album)
                    ? $"Audio CD ({driveId})"
                    : $"{album} ({driveId})";

                DeviceItems.Add(new SidebarItem
                {
                    Name = label,
                    Icon = "fa-solid fa-compact-disc",
                    Category = "DEVICES",
                    IsEnabled = true,
                    ViewConfigKey = "CdAudio",
                });

                // Store cover art for playback display. Keep the raw bytes too - the
                // macOS Now Playing widget needs them to build an MPMediaItemArtwork.
                _cdCoverArt = discInfo.CoverArtBytes != null ? ArtworkSource.BitmapFromBytes(discInfo.CoverArtBytes) : null;
                _cdCoverArtBytes = discInfo.CoverArtBytes;

                // Surface the disc's details in the CD-view info bar.
                CurrentCdInfo = new CdInfo
                {
                    CoverArt = _cdCoverArt,
                    Album = discInfo.Tracks[0].Album,
                    Artist = discInfo.Tracks[0].Artist,
                    Year = discInfo.Tracks[0].Year,
                    Genre = discInfo.Tracks[0].Genre,
                    TrackCount = discInfo.Tracks.Count,
                    TotalDuration = TimeSpan.FromTicks(discInfo.Tracks.Sum(t => t.Duration?.Ticks ?? 0)),
                    DiscId = discInfo.DiscId,
                    ReleaseMbid = discInfo.ReleaseMbid,
                };

                ApplyFilter();
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "CD scan failed");
        }
        finally
        {
            _cdScanning = false;
        }
    }

    internal void PlayCdTrack(MediaItem track)
    {
        EnsurePlaybackReady();
        if (track.StreamUrl == null)
        {
            return;
        }

        UI(() =>
        {
            _playbackContext?.Release();
            _playbackContext = new PlaybackContext(_cdTracks, track);
            _playbackOriginViewKey = SelectedSidebarItem?.ViewConfigKey;
            OnPropertyChanged(nameof(PlaybackContextUpcoming));
            ExecutePlayCd(track);
        });
    }

    // --- CD Rip / Burn ------------------------------------------------------

    /// <summary>
    /// Extracts the drive path ("D:") from a CD track ID ("cd:D::3").
    /// Returns null if the ID is not a CD track.
    /// </summary>
    private static string? DrivePathFromCdTrackId(string id)
    {
        if (!id.StartsWith("cd:"))
        {
            return null;
        }

        var rest = id[3..];
        var lastColon = rest.LastIndexOf(':');
        if (lastColon < 0)
        {
            return null;
        }

        return rest[..lastColon];
    }

    /// <summary>
    /// Stops playback if the currently-playing track comes from <paramref name="drivePath"/>.
    /// LibVLC's cdda:// driver holds the drive handle while playing; we need it released
    /// before SCSI passthrough can open the drive for rip/burn.
    /// </summary>
    private void EnsureCdDriveFree(string drivePath)
    {
        if (CurrentPlayingItem?.Source == "cdda" && CurrentPlayingItem.Id.StartsWith($"cd:{drivePath}:"))
        {
            ClearPlayback();
        }
    }

    internal async Task RipSelectedCdTrackAsync()
    {
        var track = SelectedItem;
        if (track?.Source != "cdda")
        {
            return;
        }

        var options = await PromptForRipOptionsAsync();
        if (options == null)
        {
            return;
        }

        await RipCdTracksAsync([track], options);
    }

    [RelayCommand]
    internal async Task RipCurrentCdAsync()
    {
        // The user may not have selected a specific CD track - pull the drive from any
        // tracked CD when nothing's selected, so the rip-toolbar button works from the
        // CD sidebar view directly.
        var drivePath = SelectedItem?.Source == "cdda" && DrivePathFromCdTrackId(SelectedItem.Id) is string p
            ? p
            : _cdTracks.Select(t => DrivePathFromCdTrackId(t.Id)).FirstOrDefault(d => d != null);
        if (drivePath == null)
        {
            return;
        }

        var options = await PromptForRipOptionsAsync();
        if (options == null)
        {
            return;
        }

        var tracks = _cdTracks.Where(t => DrivePathFromCdTrackId(t.Id) == drivePath).ToList();
        await RipCdTracksAsync(tracks, options);
    }

    /// <summary>Ejects the optical disc shown in the CD view (the CdInfoBar Eject button).</summary>
    [RelayCommand]
    private void EjectCd()
    {
        var drivePath = _cdTracks.Select(t => DrivePathFromCdTrackId(t.Id)).FirstOrDefault(d => d != null);
        if (drivePath == null)
        {
            return;
        }

        // Stop playback from the disc first, or Windows can't eject it.
        if (CurrentPlayingItem?.Source == "cdda")
        {
            ClearPlayback();
        }

        if (DeviceEjector.Eject(drivePath, out var error))
        {
            _log.Information("Ejected disc at {Drive}", drivePath);
            UpdateMainStatus("Disc ejected.");
        }
        else
        {
            _log.Warning("Eject failed for disc at {Drive}: {Error}", drivePath, error ?? "unknown");
            UpdateMainStatus($"Couldn't eject the disc — {error ?? "it may still be in use"}.");
        }
    }

    public bool IsCdViewActive => SelectedSidebarItem?.ViewConfigKey == "CdAudio";

    private async Task<CdRipOptions?> PromptForRipOptionsAsync()
    {
        var initial = LoadLastRipOptions();
        var dialog = new RipOptionsDialog(initial);
        var result = await dialog.ShowDialog<CdRipOptions?>(_window);
        if (result != null)
        {
            SaveRipOptions(result);
        }

        return result;
    }

    private static CdRipOptions LoadLastRipOptions()
    {
        try
        {
            var json = Settings.Get<string>("OrgZ.Cd.LastRipOptions", "");
            if (string.IsNullOrEmpty(json))
            {
                return CdRipOptions.Default;
            }

            return System.Text.Json.JsonSerializer.Deserialize<CdRipOptions>(json) ?? CdRipOptions.Default;
        }
        catch
        {
            return CdRipOptions.Default;
        }
    }

    private static void SaveRipOptions(CdRipOptions options)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(options);
        Settings.Set("OrgZ.Cd.LastRipOptions", json);
        Settings.Save();
    }

    private async Task RipCdTracksAsync(IReadOnlyList<MediaItem> tracks, CdRipOptions options)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        var drivePath = DrivePathFromCdTrackId(tracks[0].Id);
        if (drivePath == null)
        {
            return;
        }

        // DiscID of the loaded disc - used to remember what we ripped (Part B).
        var ripDiscId = _cdDiscIdByDrive.GetValueOrDefault(drivePath);

        // FoxRedbook on macOS wants a bare BSD name (disk4) / dev path, not the
        // mount point. Same translation we do for TOC reads in CdAudioService.
        var openPath = OperatingSystem.IsMacOS()
            ? CdAudioService.ResolveMacBsdDevice(drivePath) ?? drivePath
            : drivePath;

        var albumRoot = !string.IsNullOrWhiteSpace(App.FolderPath) ? App.FolderPath : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        var artistDir = CdRipService.SanitizeForFileName(tracks[0].Artist) is { Length: > 0 } a ? a : "Unknown Artist";
        var albumDir = CdRipService.SanitizeForFileName(tracks[0].Album) is { Length: > 0 } al ? al : $"Audio CD ({drivePath})";
        var outputDir = Path.Combine(albumRoot, artistDir, albumDir);

        EnsureCdDriveFree(drivePath);

        // Per-track timing for the speed readout. We need a reset each time the
        // track number advances, otherwise the "8.5×" figure averages across the
        // whole disc and stops being informative once a few tracks are done.
        int speedTrackNum = -1;
        var speedClock = System.Diagnostics.Stopwatch.StartNew();
        long speedStartSectors = 0;

        IsBusy = true;
        BusyTitle = $"Importing {tracks.Count} track(s)";
        BusyDetail = string.Empty;
        BusyPercent = 0;

        // Queue indicator: every track about to be ripped shows the grey spinner.
        foreach (var t in tracks)
        {
            t.RipStatus = RipState.Pending;
        }

        var progress = new Progress<RipTrackProgress>(p =>
        {
            // Mark the in-flight track with the spinning (black) indicator.
            var ripping = tracks.FirstOrDefault(t => t.Track == (uint)p.TrackNumber);
            if (ripping is not null && ripping.RipStatus != RipState.Ripped)
            {
                ripping.RipStatus = RipState.Ripping;
            }

            if (p.TrackNumber != speedTrackNum)
            {
                speedTrackNum = p.TrackNumber;
                speedClock.Restart();
                speedStartSectors = p.SectorsDone;
            }

            // CDDA is 75 sectors/second at 1×; speed = (sectors/sec) / 75.
            // Guard against the first tick (zero elapsed) so we don't divide by 0.
            string speedStr;
            string etaStr;
            var elapsed = speedClock.Elapsed.TotalSeconds;
            var sectorsThisTrack = p.SectorsDone - speedStartSectors;
            if (elapsed > 0.5 && sectorsThisTrack > 0)
            {
                var sectorsPerSec = sectorsThisTrack / elapsed;
                var speedX = sectorsPerSec / 75.0;
                speedStr = $"{speedX:0.0}×";
                var sectorsLeft = Math.Max(0, p.SectorsTotal - p.SectorsDone);
                var ts = TimeSpan.FromSeconds(sectorsLeft / sectorsPerSec);
                // "m:ss" by manual format - TimeSpan format strings don't accept
                // literal digits the way numeric format strings do, and the previous
                // attempt ("0\:ss") threw FormatException the moment a real progress
                // tick came in. Manual interpolation sidesteps that entirely.
                etaStr = $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
            }
            else
            {
                speedStr = "—";
                etaStr = "—";
            }

            BusyTitle = $"Importing “{p.TrackTitle}”";
            BusyDetail = $"Track {p.TrackNumber} of {p.TrackCount} — Time remaining: {etaStr} ({speedStr})";
            BusyPercent = p.TrackPercent;
        });

        // Per-track verification feed: each finished track flashes a one-line
        // verdict on the LCD's BusyDetail line while the next track gets going.
        var trackCompleted = new Progress<RipOutcome>(o =>
        {
            // Green check the moment a track is verified. Persistence comes from the
            // MUSICBRAINZ_DISCID tag the encoder writes into the file, not a side ledger.
            var done = tracks.FirstOrDefault(t => t.Track == (uint)o.TrackNumber);
            if (done is not null)
            {
                done.RipStatus = RipState.Ripped;
                done.DiscId ??= ripDiscId;
            }

            string line;
            if (o.Verified)
            {
                line = $"✓ Track {o.TrackNumber:D2} — AR2 {o.AccurateRipV2:X8}";
            }
            else if (o.SkippedSectors > 0)
            {
                line = $"⚠ Track {o.TrackNumber:D2} — {o.SkippedSectors} unverified sector(s) starting at LBA {o.FirstSkippedLba}";
            }
            else
            {
                line = $"⚠ Track {o.TrackNumber:D2} — {o.ReadErrorSectors} read error(s)";
            }
            BusyDetail = line;

            // Surface the finished track in the library now. Relying on the folder
            // watcher races with flac/lame holding the file open for the whole encode,
            // which lagged the view a track behind and dropped the final one. The rip
            // knows its own output path, so add it directly (deduped vs the watcher).
            if (!string.IsNullOrEmpty(o.OutputPath) && File.Exists(o.OutputPath) && !LibraryContainsPath(o.OutputPath))
            {
                var ripped = FileScanner.CreateMediaItemFromPath(o.OutputPath);
                if (ripped != null)
                {
                    _allItems.Add(ripped);
                    ApplyFilter();
                    _ = AnalyzeAllFilesAsync([ripped]);
                }
            }
        });

        string? ripError = null;

        _ripCts = new CancellationTokenSource();
        try
        {
            // CdRipService.RipTracksWithElevationAsync awaits async methods but
            // its inner OpticalDrive.Open + per-sector SCSI reads run synchronously
            // until they actually yield - when called from the UI thread that
            // means a frozen window for the duration of the rip. Task.Run pushes
            // the entire pipeline to the thread pool so the UI keeps animating;
            // Progress<T> already routes its callbacks back to the UI dispatcher
            // via the SynchronizationContext captured at construction.
            var ct = _ripCts.Token;
            var outcomes = await Task.Run(() =>
                CdRipService.RipTracksWithElevationAsync(openPath, tracks, outputDir, options, progress, trackCompleted, _cdCoverArtBytes, ripDiscId, ct), ct);

            var unverified = outcomes.Where(o => !o.Verified).ToList();
            if (unverified.Count == 0)
            {
                _log.Information("Ripped {Count} track(s) from {DrivePath} — all verified — to {OutputDir}", outcomes.Count, drivePath, outputDir);
                UpdateMainStatus($"Ripped {Count(outcomes.Count, "track")} — all verified.");
            }
            else
            {
                var badList = string.Join(", ", unverified.Select(o => o.TrackNumber.ToString("D2")));
                _log.Warning("Ripped {Count} track(s) from {DrivePath}, {Unverified} unverified: {BadList}", outcomes.Count, drivePath, unverified.Count, badList);
                // The per-track ⚠ verdict was a transient LCD line - this survives the LCD reset.
                UpdateMainStatus($"Ripped {Count(outcomes.Count, "track")} — ⚠ {Count(unverified.Count, "track")} unverified: {badList}.");
            }
        }
        catch (OperationCanceledException)
        {
            _log.Information("Rip cancelled by user for {DrivePath}", drivePath);
            UpdateMainStatus("Rip cancelled.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Rip failed for {DrivePath}", drivePath);
            ripError = ex.Message;
        }
        finally
        {
            _ripCts?.Dispose();
            _ripCts = null;
            IsBusy = false;
            BusyTitle = string.Empty;
            BusyDetail = string.Empty;
            BusyPercent = 0;

            // Clear the queued/spinning state for anything not actually ripped (e.g.
            // a cancelled rip), leaving completed tracks' green checks in place.
            foreach (var t in tracks)
            {
                if (t.RipStatus != RipState.Ripped)
                {
                    t.RipStatus = RipState.None;
                }
            }
        }

        // Surface the failure once the LCD is back to normal (mirrors the burn path). The
        // swallowed message was often the deliberately-helpful "encoder not found — install
        // it like this" text, which users never saw - the progress bar just vanished.
        if (ripError != null)
        {
            UpdateMainStatus($"The rip didn't finish: {ripError}");
            var dialog = new ConfirmDialog("Can't Rip CD", $"The rip didn't finish: {ripError}", "OK", showCancel: false);
            await dialog.ShowDialog(_window);
        }
    }

    // -- LCD progress for long device/disc operations --------------------------------
    // These borrow the rip's LCD page (IsBusy + BusyTitle/BusyDetail/BusyPercent) so an
    // import, burn, or device scan reads the same as a rip ("Adding ...", "Scanning ...").
    // Pair Begin/End; all marshal to the UI thread so callers can drive them from a
    // background scan. (Single-slot: a second op started mid-rip would share the page.)

    /// <summary>"1 track", "3 tracks" - regular English plural for user-facing counts.</summary>
    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? string.Empty : "s")}";

    private void BeginLcdBusy(string title, string detail = "")
    {
        UI(() =>
        {
            BusyTitle = title;
            BusyDetail = detail;
            BusyPercent = 0;
            IsBusy = true;
        });
    }

    private void SetLcdBusy(string detail, double? percent = null)
    {
        UI(() =>
        {
            BusyDetail = detail;
            if (percent is { } p)
            {
                BusyPercent = p;
            }
        });
    }

    private void EndLcdBusy()
    {
        UI(() =>
        {
            IsBusy = false;
            BusyTitle = string.Empty;
            BusyDetail = string.Empty;
            BusyPercent = 0;
        });
    }

    // Podcast downloads surfaced on the LCD busy display. Touched only on the UI thread (the
    // download service's background events are marshalled through UI()). _downloadOwnsLcd
    // keeps downloads from clobbering an in-progress rip/import that already owns the display.
    private readonly Dictionary<long, (string Title, double Fraction)> _activeDownloads = new();
    private bool _downloadOwnsLcd;

    private void OnPodcastDownloadStarted(Models.PodcastEpisode ep)
        => UpsertDownload(ep.Id, ep.Title ?? string.Empty, 0);

    private void OnPodcastDownloadProgress(Services.Podcast.DownloadProgress p)
        => UpsertDownload(p.EpisodeId, p.Title, p.Fraction);

    /// <summary>
    /// Records a download's progress and claims the busy LCD on the first one. Started and
    /// Progress were the same twelve lines apart from which tuple they wrote - so the
    /// LCD-ownership rule existed twice and could drift on one path only.
    /// </summary>
    private void UpsertDownload(long episodeId, string title, double fraction)
    {
        var wasIdle = _activeDownloads.Count == 0;
        _activeDownloads[episodeId] = (title, fraction);

        if (wasIdle && !IsBusy)
        {
            _downloadOwnsLcd = true;
            BeginLcdBusy("Downloading");
        }

        if (_downloadOwnsLcd)
        {
            UpdateDownloadLcd();
        }
    }

    private void OnPodcastDownloadFinished(long episodeId)
    {
        _activeDownloads.Remove(episodeId);
        if (_activeDownloads.Count == 0)
        {
            if (_downloadOwnsLcd)
            {
                _downloadOwnsLcd = false;
                EndLcdBusy();
            }
        }
        else if (_downloadOwnsLcd)
        {
            UpdateDownloadLcd();
        }
    }

    private void UpdateDownloadLcd()
    {
        if (_activeDownloads.Count == 0)
        {
            return;
        }
        var avg = _activeDownloads.Values.Average(v => v.Fraction);
        var detail = _activeDownloads.Count == 1
            ? _activeDownloads.Values.First().Title
            : $"{_activeDownloads.Count} episodes";
        SetLcdBusy(detail, avg);
    }

    internal async Task BurnTracksToCdAsync(IReadOnlyList<MediaItem> tracks, string? discTitle = null)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        var sources = tracks.Where(t => !string.IsNullOrEmpty(t.FilePath)).ToList();
        if (sources.Count == 0)
        {
            await ShowBurnErrorAsync("These tracks have no local audio files to burn.");
            return;
        }

        // Recorder discovery, then the dialog straight away: it probes the selected
        // drive live (media label, blank state, capacity) and gates Audio/Data modes
        // and test-write from what's actually in the tray - including empty drives.
        var allDrives = await Task.Run(() => CdAudioService.GetAllCdDrives());
        var burners = allDrives.Where(d => _burnerSupport.GetValueOrDefault(d.Name, false)).ToList();
        if (burners.Count == 0)
        {
            burners = allDrives;
        }

        if (burners.Count == 0)
        {
            _log.Warning("Burn requested with no CD drive present");
            await ShowBurnErrorAsync("No optical drive found to burn to.");
            return;
        }

        var totalLength = TimeSpan.FromTicks(sources.Sum(t => t.Duration?.Ticks ?? 0));
        var totalDataBytes = await Task.Run(() => sources.Sum(t =>
        {
            try { return new FileInfo(t.FilePath!).Length; }
            catch { return 0L; }
        }));

        var dialog = new BurnDiscDialog(
            burners.Select(d => d.Name.TrimEnd('\\', '/')).ToList(),
            sources.Count,
            totalLength,
            totalDataBytes,
            discTitle,
            async path =>
            {
                EnsureCdDriveFree(path);
                return await Task.Run(() => CdBurnService.CheckBurnMedia(path));
            });
        var choice = await dialog.ShowDialog<BurnDiscChoice?>(_window);
        if (choice == null)
        {
            return;
        }

        var drivePath = choice.DrivePath;

        // Re-probe the chosen drive; a used rewritable is one confirmed quick-blank away.
        // No burn ever starts against anything but Ready media.
        var media = await Task.Run(() => CdBurnService.CheckBurnMedia(drivePath));

        if (media.Status == CdBurnService.BurnMediaStatus.NotBlank && media.Erasable)
        {
            var confirm = new ConfirmDialog(
                "Erase Disc",
                $"The disc in {drivePath} isn't blank.\n\nErase everything on it? This cannot be undone.",
                "Erase");
            if (!await confirm.ShowDialog<bool>(_window))
            {
                return;
            }

            BeginLcdBusy($"Erasing {drivePath}", "Quick erase");

            // BusyPercent stays 0 so the LCD runs its barber pole; the detail line
            // ticks elapsed time since the drive reports nothing until it's done.
            var eraseClock = System.Diagnostics.Stopwatch.StartNew();
            var eraseTicker = new System.Threading.Timer(_ => SetLcdBusy($"Quick erase — {(int)eraseClock.Elapsed.TotalMinutes}:{eraseClock.Elapsed.Seconds:D2}"), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            try
            {
                await CdBurnService.EraseWithElevationAsync(drivePath);
                media = await Task.Run(() => CdBurnService.CheckBurnMedia(drivePath));
                _log.Information("Erased disc in {Drive} in {Elapsed:mm\\:ss}; post-erase status: {Status}", drivePath, eraseClock.Elapsed, media.Status);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Erase failed for {Drive}", drivePath);
                await ShowBurnErrorAsync($"The erase didn't finish: {ex.Message}");
                return;
            }
            finally
            {
                await eraseTicker.DisposeAsync();
                EndLcdBusy();
            }
        }

        if (media.Status != CdBurnService.BurnMediaStatus.Ready)
        {
            _log.Information("Burn blocked post-dialog: {Status} on {Drive}", media.Status, drivePath);
            await ShowBurnErrorAsync(media.Status switch
            {
                CdBurnService.BurnMediaStatus.NoMedia     => "Insert a disc to burn to.",
                CdBurnService.BurnMediaStatus.NotBlank    => "The disc in the drive isn't blank.",
                CdBurnService.BurnMediaStatus.NotWritable => "This drive can't write discs.",
                CdBurnService.BurnMediaStatus.Busy        => "The drive is busy finishing a previous operation. Eject and reinsert the disc, then try again.",
                _                                         => "Couldn't read the disc in the drive.",
            });
            return;
        }

        if (choice.Mode is BurnDiscMode.DataCd or BurnDiscMode.DataDvd)
        {
            await BurnDataDiscAsync(drivePath, sources, choice, media);
            return;
        }

        // — Audio CD from here down —

        if (sources.Count > CdBurnService.MaxRedbookTracks)
        {
            await ShowBurnErrorAsync($"Audio CDs hold at most {CdBurnService.MaxRedbookTracks} tracks — this list has {sources.Count}.");
            return;
        }

        // Red Book's 4-second track floor, checked from metadata so the user hears about
        // it before any transcoding; CdBurnService re-checks the exact sector counts.
        var tooShort = sources.Where(t => t.Duration is { TotalSeconds: < 4 }).ToList();
        if (tooShort.Count > 0)
        {
            var names = string.Join(", ", tooShort.Take(3).Select(t => $"“{t.Title}”"));
            await ShowBurnErrorAsync($"{tooShort.Count} track(s) are under the 4-second Red Book minimum: {names}{(tooShort.Count > 3 ? ", …" : "")}.");
            return;
        }

        // The drive only accepts CD-DA WAV (16-bit/44.1k/stereo); library tracks are
        // MP3/AAC/FLAC/ALAC, so each source is transcoded to a sector-aligned WAV first.
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null)
        {
            await ShowBurnErrorAsync("ffmpeg wasn't found — it's needed to convert audio for CD burning.");
            return;
        }

        // CD-TEXT disc performer: the shared artist when every track agrees, else null.
        // (Per-track Title/Performer always go through; this is the album-level line.)
        var distinctArtists = sources
            .Select(t => t.Artist)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var discPerformer = distinctArtists.Count == 1 ? distinctArtists[0] : null;

        var stagingDir = Path.Combine(Path.GetTempPath(), "OrgZ", "burn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);

        _burnCts?.Dispose();
        _burnCts = new CancellationTokenSource();
        var ct = _burnCts.Token;

        string? burnError = null;

        BeginLcdBusy($"Burning to {drivePath}");
        try
        {
            var gapSectors = (int)Math.Round(choice.GapSeconds * 75);   // 75 sectors = 1 s

            var burnTracks = new List<CdBurnTrack>(sources.Count);
            long totalSectors = 150;   // track 1's mandatory 2-second pregap
            for (int i = 0; i < sources.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var t = sources[i];
                SetLcdBusy($"Converting “{t.Title}” ({i + 1}/{sources.Count})", (double)i / sources.Count);
                var wav = Path.Combine(stagingDir, $"{i:D3}.wav");
                await CdAudioTranscoder.ToCdAudioWavAsync(ffmpeg, t.FilePath!, wav, ct);
                totalSectors += (new FileInfo(wav).Length - 44) / 2352;   // canonical 44-byte header, sector-aligned payload
                if (i > 0)
                {
                    totalSectors += gapSectors;   // inter-track gap burns as pregap sectors
                }
                burnTracks.Add(new CdBurnTrack
                {
                    WavFilePath = wav,
                    Title = t.Title,
                    Performer = t.Artist,
                });
            }

            // The exact sector count is only known post-transcode - refuse to elevate
            // into a burn the disc can't hold. (When the drive didn't report capacity,
            // the drive itself rejects the cue sheet before the laser fires.)
            if (media.CapacitySectors is { } capacity && totalSectors > capacity)
            {
                throw new InvalidOperationException($"the disc holds {BurnDiscDialog.FormatCdLength(TimeSpan.FromSeconds(capacity / 75.0))} but this list needs {BurnDiscDialog.FormatCdLength(TimeSpan.FromSeconds(totalSectors / 75.0))}.");
            }

            SetLcdBusy($"Writing {Count(burnTracks.Count, "track")}", 0);
            var progress = new Progress<CdBurnProgress>(p =>
            {
                var title = p.TrackNumber >= 1 && p.TrackNumber <= burnTracks.Count ? burnTracks[p.TrackNumber - 1].Title : null;
                // Live readout: track, percent, and elapsed/total audio time (sectors ÷ 75)
                // so the line ticks continuously instead of only changing per track.
                var pct = (int)Math.Round(p.DiscPercent * 100);
                var done = BurnDiscDialog.FormatCdLength(TimeSpan.FromSeconds(p.TotalSectorsWritten / 75.0));
                var total = BurnDiscDialog.FormatCdLength(TimeSpan.FromSeconds(p.TotalDiscSectors / 75.0));
                SetLcdBusy($"Track {p.TrackNumber}/{p.TrackCount} — {title ?? "…"} · {pct}% · {done}/{total}", p.DiscPercent);
            });

            // A disc that's started writing must run to completion - aborting mid-write
            // is a guaranteed coaster - so from here the Cancel X no longer cancels;
            // transcode-phase cancels above still abort cleanly.
            _burnWritePhase = true;
            _burnCts.Dispose();
            _burnCts = null;

            // CD-Text unchecked: strip all metadata from the burn (no lead-in text packs
            // get built) - burnTracks keeps its titles so the LCD still shows them.
            var sendTracks = choice.WriteCdText ? (IReadOnlyList<CdBurnTrack>)burnTracks : burnTracks.Select(t => t with { Title = null, Performer = null }).ToList();

            var warnings = await CdBurnService.BurnWithElevationAsync(drivePath, sendTracks, progress, choice.WriteCdText ? choice.DiscTitle : null, choice.WriteCdText ? discPerformer : null, choice.TestWrite, choice.WriteSpeedKBps, gapSectors, CancellationToken.None);
            _log.Information("Burned {Count} track(s) to {DrivePath} (title: {Title}, testWrite: {Test}, speed: {Speed})", burnTracks.Count, drivePath, choice.DiscTitle ?? "—", choice.TestWrite, choice.WriteSpeedKBps?.ToString() ?? "max");

            // Eject the finished disc (iTunes-style) so the OS re-reads the new TOC on
            // reinsertion. Un-elevated, same path as the CD view's Eject button; a
            // simulated test write leaves the still-blank disc in the drive.
            if (!choice.TestWrite)
            {
                var (ejected, ejectError) = await Task.Run(() =>
                {
                    var ok = DeviceEjector.Eject(drivePath, out var err);
                    return (ok, err);
                });
                if (!ejected)
                {
                    _log.Warning("Post-burn eject failed for {Drive}: {Error}", drivePath, ejectError ?? "unknown");
                }
            }

            var warningSuffix = warnings.Count > 0 ? $" ({warnings[0]})" : string.Empty;
            UpdateMainStatus(choice.TestWrite
                ? $"Test write finished on {drivePath} — nothing was burned.{warningSuffix}"
                : $"Burned {Count(burnTracks.Count, "track")} to {drivePath}.{warningSuffix}");
        }
        catch (OperationCanceledException)
        {
            _log.Information("Burn cancelled by user for {DrivePath}", drivePath);
            UpdateMainStatus("Burn cancelled.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Burn failed for {DrivePath}", drivePath);
            burnError = ex.Message;
        }
        finally
        {
            _burnWritePhase = false;
            EndLcdBusy();
            _burnCts?.Dispose();
            _burnCts = null;
            try
            {
                Directory.Delete(stagingDir, recursive: true);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to clean burn staging dir {Dir}", stagingDir);
            }
        }

        // Surface a failed burn as a dialog (after the LCD is cleared), not just a status line.
        if (burnError != null)
        {
            await ShowBurnErrorAsync($"The burn didn't finish: {burnError}");
        }
    }

    /// <summary>
    /// Burns the sources' files as an ISO 9660/Joliet/UDF data disc laid out
    /// Artist/Album/file. CD-R/CD-RW burn TAO Mode 1; DVD+RW overwrites in place.
    /// Like the audio path, the write is not cancelable once started.
    /// </summary>
    private async Task BurnDataDiscAsync(string drivePath, IReadOnlyList<MediaItem> sources, BurnDiscChoice choice, CdBurnService.BurnMediaInfo media)
    {
        var files = BuildDataFileList(sources);

        var dataFormat = Settings.Get("OrgZ.Burn.DataFormat", "original");
        string? convertDir = null;

        string? burnError = null;

        _burnWritePhase = true;
        BeginLcdBusy($"Burning to {drivePath}", "Preparing data disc");
        try
        {
            // Settings > Burning > "Convert files to": transcode into a scratch dir first;
            // sources already in the target format copy straight through.
            if (Services.DataDiscTranscoder.ExtensionFor(dataFormat) is { } targetExt)
            {
                convertDir = Path.Combine(Path.GetTempPath(), "orgz-databurn-" + Guid.NewGuid().ToString("N")[..8]);
                files = await ConvertDataDiscFilesAsync(files, dataFormat, targetExt, Settings.Get("OrgZ.Burn.LossyQualityKbps", 256), convertDir);
            }

            // Approximate size gate for CD media (2048 bytes per Mode 1 sector; filesystem
            // overhead comes on top, so the drive still has the final word). DVD+RW doesn't
            // report a capacity - an oversize image is rejected by the drive itself. Runs
            // AFTER conversion so it measures the bytes actually going to disc.
            if (choice.Mode == BurnDiscMode.DataCd && media.CapacitySectors is { } capSectors)
            {
                var capBytes = capSectors * 2048L;
                var totalBytes = await Task.Run(() => files.Sum(f =>
                {
                    try { return new FileInfo(f.SourcePath).Length; }
                    catch { return 0L; }
                }));
                if (totalBytes > capBytes)
                {
                    throw new InvalidOperationException($"The disc holds {BurnDiscDialog.FormatDataSize(capBytes)} but these files need {BurnDiscDialog.FormatDataSize(totalBytes)}.");
                }
            }

            var progress = new Progress<CdBurnProgress>(p => SetLcdBusy($"Writing data disc — {BurnDiscDialog.FormatDataSize(p.TotalSectorsWritten * 2048L)} of {BurnDiscDialog.FormatDataSize(p.TotalDiscSectors * 2048L)}", p.DiscPercent));

            await CdBurnService.DataBurnWithElevationAsync(drivePath, files, choice.DiscTitle, progress, choice.TestWrite, CancellationToken.None);
            _log.Information("Data-burned {Count} file(s) to {DrivePath} (label: {Label}, mode: {Mode}, testWrite: {Test})", files.Count, drivePath, choice.DiscTitle ?? "—", choice.Mode, choice.TestWrite);

            if (!choice.TestWrite)
            {
                var (ejected, ejectError) = await Task.Run(() =>
                {
                    var ok = DeviceEjector.Eject(drivePath, out var err);
                    return (ok, err);
                });
                if (!ejected)
                {
                    _log.Warning("Post-burn eject failed for {Drive}: {Error}", drivePath, ejectError ?? "unknown");
                }
            }

            UpdateMainStatus(choice.TestWrite
                ? $"Test write finished on {drivePath} — nothing was burned."
                : $"Burned {Count(files.Count, "file")} to {drivePath}.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Data burn failed for {DrivePath}", drivePath);
            burnError = ex.Message;
        }
        finally
        {
            _burnWritePhase = false;
            EndLcdBusy();
            if (convertDir != null)
            {
                try
                {
                    Directory.Delete(convertDir, recursive: true);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Failed to clean data-burn convert dir {Dir}", convertDir);
                }
            }
        }

        if (burnError != null)
        {
            await ShowBurnErrorAsync($"The burn didn't finish: {burnError}");
        }
    }

    /// <summary>
    /// Lays the sources out as Artist/Album/filename on the disc, scrubbing path-hostile
    /// characters and suffixing collisions ("song (2).mp3").
    /// </summary>
    internal static List<DataBurnFile> BuildDataFileList(IReadOnlyList<MediaItem> sources)
    {
        static string Clean(string? part, string fallback)
        {
            var s = string.IsNullOrWhiteSpace(part) ? fallback : part.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                s = s.Replace(c, '_');
            }

            return s.Replace('/', '_').Replace('\\', '_');
        }

        var files = new List<DataBurnFile>(sources.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in sources)
        {
            var name = Clean(Path.GetFileName(t.FilePath), "track");
            var dir = $"{Clean(t.Artist, "Unknown Artist")}/{Clean(t.Album, "Unknown Album")}";
            var discPath = $"{dir}/{name}";
            for (int n = 2; !seen.Add(discPath); n++)
            {
                discPath = $"{dir}/{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}";
            }

            files.Add(new DataBurnFile { DiscPath = discPath, SourcePath = t.FilePath! });
        }

        return files;
    }

    /// <summary>
    /// Modal picker for the multi-pressing case: one line per candidate release
    /// ("Album — Artist (1994, DE, 12 tracks)"), double-click or OK to choose.
    /// Null (cancel) lets the caller take the first candidate, the old behavior.
    /// </summary>
    private async Task<DiscLookupResult?> PickCdReleaseAsync(IReadOnlyList<DiscLookupResult> candidates)
    {
        var list = new Avalonia.Controls.ListBox
        {
            ItemsSource = candidates.Select(c => c.DisplayLabel).ToList(),
            SelectedIndex = 0,
            MaxHeight = 320,
        };

        var dialog = new Avalonia.Controls.Window
        {
            Title = "Which pressing is this disc?",
            MinWidth = 480,
            SizeToContent = Avalonia.Controls.SizeToContent.WidthAndHeight,
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        DiscLookupResult? chosen = null;
        var ok = new Avalonia.Controls.Button { Content = "OK", Width = 80 };
        var cancel = new Avalonia.Controls.Button { Content = "Cancel", Width = 80 };
        ok.Click += (_, _) => { chosen = list.SelectedIndex >= 0 ? candidates[list.SelectedIndex] : null; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        list.DoubleTapped += (_, _) => { chosen = list.SelectedIndex >= 0 ? candidates[list.SelectedIndex] : null; dialog.Close(); };

        dialog.Content = new Avalonia.Controls.StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 12,
            Children =
            {
                list,
                new Avalonia.Controls.StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok },
                },
            },
        };

        // Same owner rule as ShowSettings: the main window can be hidden behind the
        // mini-player, and a non-visible owner throws.
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
                     as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                    ?.Windows.FirstOrDefault(w => w.IsVisible) ?? _window;
        await dialog.ShowDialog(owner);
        return chosen;
    }

    /// <summary>Shows an OK-only error dialog for a burn that can't start or didn't finish.</summary>
    private async Task ShowBurnErrorAsync(string message)
    {
        UpdateMainStatus(message);
        var dialog = new ConfirmDialog("Can't Burn Disc", message, "OK", showCancel: false);
        await dialog.ShowDialog(_window);
    }

    /// <summary>
    /// Converts data-disc sources per Settings > Burning: already-target-format files pass
    /// through untouched, everything else transcodes into <paramref name="convertDir"/> and
    /// lands on the disc under the new extension. Conversion can collapse two sources into
    /// one name ("song.flac" and "song.mp3" both become "song.mp3"), so the disc layout is
    /// re-deduped afterwards.
    /// </summary>
    private async Task<List<DataBurnFile>> ConvertDataDiscFilesAsync(List<DataBurnFile> files, string format, string targetExt, int lossyKbps, string convertDir)
    {
        Directory.CreateDirectory(convertDir);

        var converted = new List<DataBurnFile>(files.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];

            string discPath;
            string sourcePath;
            if (Services.DataDiscTranscoder.AlreadyTargetFormat(file.SourcePath, format))
            {
                discPath = file.DiscPath;
                sourcePath = file.SourcePath;
            }
            else
            {
                SetLcdBusy($"Converting {i + 1} of {files.Count} — {Path.GetFileNameWithoutExtension(file.DiscPath)}", (double)i / files.Count);
                sourcePath = Path.Combine(convertDir, $"{i:D4}{targetExt}");
                await Services.DataDiscTranscoder.TranscodeAsync(file.SourcePath, sourcePath, format, lossyKbps);
                discPath = Path.ChangeExtension(file.DiscPath, targetExt);
            }

            var deduped = discPath;
            for (int n = 2; !seen.Add(deduped); n++)
            {
                var dir = Path.GetDirectoryName(discPath)?.Replace('\\', '/');
                var stem = Path.GetFileNameWithoutExtension(discPath);
                var ext = Path.GetExtension(discPath);
                deduped = string.IsNullOrEmpty(dir) ? $"{stem} ({n}){ext}" : $"{dir}/{stem} ({n}){ext}";
            }

            converted.Add(new DataBurnFile { DiscPath = deduped, SourcePath = sourcePath });
        }

        SetLcdBusy("Preparing data disc");
        return converted;
    }

    /// <summary>
    /// Burns the active view's tracks (a user playlist or Favorites) to disc. Bound to
    /// the playlist header's Burn button, which is only visible when <see cref="ShowBurnButton"/>.
    /// </summary>
    [RelayCommand]
    private async Task BurnCurrentViewAsync()
    {
        var tracks = CollectCurrentViewBurnTracks();
        if (tracks.Count == 0)
        {
            UpdateMainStatus("Nothing to burn in this view.");
            return;
        }

        // Playlist / Favorites name becomes the CD-TEXT disc title.
        await BurnTracksToCdAsync(tracks, SelectedSidebarItem?.Name);
    }

    /// <summary>
    /// Gathers burnable audio for the active view: a playlist's full ordered track list,
    /// or every local-file favorite on the Favorites view. Skips items without a local
    /// audio file (radio stations also live in Favorites and have no FilePath).
    /// </summary>
    private List<MediaItem> CollectCurrentViewBurnTracks()
    {
        if (SelectedSidebarItem?.PlaylistId is int playlistId)
        {
            return GetPlaylistMediaItems(playlistId);
        }

        if (SelectedSidebarItem?.IsFavorites == true)
        {
            return FavoriteMusicFiles();
        }

        return [];
    }
}
