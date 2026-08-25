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

internal partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private static readonly ILogger _log = Logging.For<MainWindowViewModel>();

    // Cancelled by Dispose: the lifetime token for background loops (job reattach polling,
    // and anything else that must not outlive the window that started it).
    private readonly CancellationTokenSource _vmCts = new();

    private const string ICON_PLAY = "fa-solid fa-play";

    private readonly Thickness ICON_PLAY_PADDING = new(4, 0, 0, 0);

    private const string ICON_PAUSE = "fa-solid fa-pause";

    private readonly Thickness ICON_PAUSE_PADDING = new(0, 0, 0, 0);

    private readonly MainWindow _window;

    // Null in headless/screenshot mode (InitializePlayback is skipped); never
    // dereferenced there because no playback path runs.
    private LibVLC _vlc = null!;

    private MediaPlayer _player = null!;

    // Audio pipeline:
    //   LibVLC decodes → AudioTap (SetAudioCallbacks) → AudioSinkBus → sinks
    //                                               ↘ AudioAnalyzer (FFT)
    // The sink bus fans PCM out to every user-selected output device (waveOut
    // on Windows, CoreAudio on macOS, PulseAudio on Linux, AirPlay over LAN)
    // with per-device volume control.  The analyzer drives the VU meter.
    internal readonly OrgZ.Services.AudioOutput.AudioOutputManager _audioOutput = new();
    private OrgZ.Services.AudioVisualization.AudioTap _audioTap = null!;

    // Bit-perfect playback for local FLAC: bypasses libvlc (whose amem output
    // is 16-bit only) and decodes at native bit depth / sample rate. Null in
    // headless construction, same as _player.
    private FlacPlaybackEngine? _flacEngine;

    /// <summary>True while the bit-perfect engine owns playback (playing or paused).</summary>
    private bool EngineActive => _flacEngine?.IsActive == true;

#if WINDOWS
    private TaskbarThumbBarService? _thumbBarService;
#endif

    // The one OS now-playing surface for this platform (MPRIS / macOS / SMTC), chosen at init.
    private INowPlayingIntegration? _nowPlaying;

    private MusicFolderWatcher? _folderWatcher;

    private Media? _currentMedia;

    // Tracks the MetaChanged delegate attached to _currentMedia (radio path only).
    // Captured so DeferDispose can detach it before Dispose() to avoid leaks and
    // late-event reentrancy onto a disposed native handle.
    private EventHandler<MediaMetaChangedEventArgs>? _currentMediaMetaHandler;

    // Coalesces rapid radio-station clicks. Each click cancels the previous
    // pending switch and schedules a fresh one; only the final click survives
    // the debounce window. Pairs with _playbackSwitchLock for race-safety
    // against libvlc's worker thread mid-transition.
    private CancellationTokenSource? _radioSwitchCts;

    // Serializes the swap of _currentMedia + _player.Play() + DeferDispose so
    // concurrent paths can't interleave the steps and orphan a Media reference
    // or call Play() while libvlc is still transitioning off the previous one.
    private readonly Lock _playbackSwitchLock = new();

    // Radio is single-connection: a StreamSession owns the upstream pull (ICY de-interleave
    // or HLS client), pumps clean audio to VLC through PipeMediaInput, and raises titles off
    // the same bytes - which are injected into the playing Media via SetMeta, firing the
    // same MetaChanged event the radio handler already consumes. VLC never opens a network
    // connection for radio. The handle pairs the session with its MediaInput so teardown
    // can order them around the Media's own deferred dispose.
    private sealed record RadioStreamHandle(StreamSession Session, PipeMediaInput Input) : IDisposable
    {
        public void Dispose()
        {
            Session.Dispose();
            Input.Dispose();
        }
    }

    private RadioStreamHandle? _radioStream;

    /// <summary>Detaches the current radio stream and closes its upstream connection NOW; the returned handle's MediaInput still needs disposal after its Media (via DeferDispose).</summary>
    private RadioStreamHandle? TakeRadioStream()
    {
        var handle = _radioStream;
        _radioStream = null;
        handle?.Session.Dispose();
        return handle;
    }

    private PlaybackContext? _playbackContext;

    // ViewConfigKey of the sidebar view a playback context was started from.
    // NavigateToPlaying prefers it over the kind-based fallback, so "go to
    // current song" returns to the playlist (or Favorites, device, CD view)
    // the song is actually playing from - not just the Music tab.
    private string? _playbackOriginViewKey;

    private readonly List<MediaItem> _cdTracks = [];
    // Drive key (from DrivePathFromCdTrackId) -> MusicBrainz DiscID of the loaded disc,
    // so a rip can record what it ripped and a re-insert can restore green checks.
    private readonly Dictionary<string, string> _cdDiscIdByDrive = [];
    private bool _cdScanning;
    private Bitmap? _cdCoverArt;
    private byte[]? _cdCoverArtBytes;

    /// <summary>Metadata of the inserted audio CD, shown in the CD view's info bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCdInfoBar))]
    private CdInfo? _currentCdInfo;

    /// <summary>The CD info bar shows only while the CD view is active and a disc is loaded.</summary>
    public bool ShowCdInfoBar => SelectedSidebarItem?.ViewConfigKey == "CdAudio" && CurrentCdInfo is not null;

    private DeviceDetectionService? _deviceDetection;
    private readonly Dictionary<string, ConnectedDevice> _connectedDevices = new(StringComparer.OrdinalIgnoreCase);

    // One CTS per in-flight device library scan, keyed by mount path. HandleDeviceDisconnected cancels
    // it so a yanked (or hot-swapped) iPod's ReadLibraryAsync can't keep streaming batches into
    // _allItems after teardown - at a reused drive letter those rows would land in the NEXT iPod's view.
    private readonly Dictionary<string, CancellationTokenSource> _deviceScanCts = new(StringComparer.OrdinalIgnoreCase);

    private bool isSeeking = false;

    private List<MediaItem> _allItems = [];

    private ListViewConfig? _activeViewConfig;

    private MediaItem? CurrentPlayingItem => _playbackContext?.CurrentItem;

    /// <summary>
    /// The playing item when it's a local FILE (any kind PlayMusicItem handles - music, audiobook,
    /// a local podcast file); null for radio/CD/podcast streams. Gating this on Music alone left a
    /// playing audiobook showing "Unknown Title / Unknown Artist" on the LCD while the grid knew
    /// better, and kept the play button from restarting one.
    /// </summary>
    private MediaItem? CurrentFileItem => CurrentPlayingItem?.Kind is MediaKind.Music or MediaKind.Audiobook or MediaKind.Podcast ? CurrentPlayingItem : null;

    private MediaItem? CurrentStation => CurrentPlayingItem?.Kind == MediaKind.Radio ? CurrentPlayingItem : null;

    /// <summary>
    /// Set by <see cref="PlayPodcastEpisodeStream"/> while a podcast stream is
    /// active. Used as the "I'm a podcast right now" signal in MediaChanged
    /// (so it doesn't overwrite the LCD with music metadata) and ButtonPlayPause
    /// (so the user can actually pause / resume). Podcasts don't use the
    /// PlaybackContext system, so this is the source of truth.
    /// </summary>
    private (Models.PodcastFeed Feed, Models.PodcastEpisode Episode)? _currentPodcastStream;

    // One-shot guard: when we Stop() the player to switch tracks (so the old audio cuts
    // immediately), the resulting Stopped event must NOT tear down the loading state - the
    // barber pole should run continuously until the new track's audio starts. Set right
    // before such a Stop(); the Stopped handler consumes it and bails.
    private bool _suppressStoppedLoadingClear;

    // Monotonic playback "epoch". Bumped at the start of every playback (and on stop) so
    // in-flight async work for a superseded playback - chiefly a streamed podcast's redirect
    // resolve - can detect it's stale and bail instead of yanking the user off whatever they
    // started in the meantime. Read/written on the UI thread only.
    private int _playbackEpoch;

    // Podcast resume: where to seek the just-started episode to (set when it begins, applied
    // once audio starts), and a throttle on how often we persist the live position.
    private long? _pendingResumeMs;
    private long _lastPodcastSaveMs;
    // Audiobook resume rides the same _pendingResumeMs; its own save throttle.
    private long _lastAudiobookSaveMs;

    private int NewPlaybackEpoch() => ++_playbackEpoch;

    /// <summary>
    /// Duration captured during MediaChanged (or seeded by API for podcasts)
    /// but held back from the LCD until the first audio buffer reaches the
    /// tap. The AudioStarted handler writes this into
    /// <see cref="CurrentTrackDuration"/> at the moment the loading indicator
    /// clears, so time labels stay blank during the load.
    /// </summary>
    private long? _pendingDurationMs;

    /// <summary>
    /// Promotes <see cref="_pendingDurationMs"/> to the LCD if it's set AND
    /// playback has actually started. Both MediaChanged and AudioStarted
    /// funnel through here -- the race between the two doesn't matter
    /// because whichever lands last (with both signals satisfied) wins.
    /// </summary>
    private void ApplyPendingDuration()
    {
        if (IsPlaybackLoading)
        {
            return;
        }

        if (_pendingDurationMs is not { } d || d <= 0)
        {
            return;
        }

        CurrentTrackDuration = FormatHelper.FormatDurationCompact(d);
        CurrentTrackDurationNumber = d;
        _pendingDurationMs = null;
    }

    [ObservableProperty]
    private StatusBarViewModel _statusBar = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCdViewActive), nameof(SearchPlaceholder), nameof(ShowNoSearchResults), nameof(ShowBurnButton), nameof(CanSyncToIPod), nameof(CanSyncPodcasts), nameof(CanSyncToDevice), nameof(ShowEmptyView), nameof(EmptyViewMessage))]
    private SidebarItem? _selectedSidebarItem;

    // Whether a recorder (writable optical drive) is present. Refreshed by
    // ScanForCdAsync on the CD poll/device-change tick. Write capability is probed
    // un-elevated via CdAudioService.IsAudioBurner (GET CONFIGURATION, same SCSI
    // passthrough as the TOC read) and cached per drive in _burnerSupport - a drive's
    // DAO capability never changes, so each drive is probed once.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBurnButton))]
    private bool _isBurnerPresent;

    private readonly Dictionary<string, bool> _burnerSupport = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// iTunes-style Burn button visibility: an optical drive is present and the
    /// active view is a burnable list (a user playlist or Favorites).
    /// </summary>
    public bool ShowBurnButton =>
        IsBurnerPresent && (SelectedSidebarItem?.PlaylistId != null || SelectedSidebarItem?.IsFavorites == true);

    // Header bar shown above the grid on playlist / Favorites views (mosaic + name +
    // stats + Burn). Rebuilt by BuildPlaylistHeaderAsync on every view switch.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlaylistHeader))]
    private PlaylistHeaderInfo? _currentPlaylistHeader;

    public bool ShowPlaylistHeader => CurrentPlaylistHeader != null;

    /// <summary>
    /// Watermark text for the search box. Mirrors whatever the active
    /// sidebar entry calls itself - "Search Music...", "Search Radio...",
    /// "Search Best of 2024..." for a playlist named "Best of 2024", etc.
    /// Falls back to "Search..." when no sidebar item is selected.
    /// </summary>
    public string SearchPlaceholder =>
        SelectedSidebarItem?.Name is { Length: > 0 } name
            ? $"Search {name}…"
            : "Search…";

    [ObservableProperty]
    private ConnectedDevice? _selectedDevice;

    internal ObservableCollection<SidebarItem> LibraryItems { get; } = [];

    internal ObservableCollection<SidebarItem> DeviceItems { get; } = [];

    /// <summary>Read-only OrgZ libraries discovered on the LAN, listed under DEVICES.</summary>
    internal ObservableCollection<SidebarItem> ShareItems { get; } = [];

    // Share key -> the tracks currently mounted from it, so a vanished share can be
    // withdrawn from the live list without disturbing anything else.
    private readonly Dictionary<string, List<MediaItem>> _shareTracks = new(StringComparer.OrdinalIgnoreCase);

    // When each mounted share's catalogue was last fetched - drives the periodic refresh.
    private readonly Dictionary<string, DateTime> _shareFetchedAt = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ShareRefreshInterval = TimeSpan.FromMinutes(5);

    // Share-playlist view key -> the remote playlist it shows, for the Import verb.
    private readonly Dictionary<string, Services.Sharing.ShareDiscovery.SharePlaylist> _sharePlaylists = new(StringComparer.Ordinal);
    private bool _shareScanning;
    private Avalonia.Threading.DispatcherTimer? _shareScanTimer;

    private Avalonia.Threading.DispatcherTimer? _podcastCheckTimer;

    // Handlers on process-wide singletons, kept so Dispose can detach them (see the ctor).
    private Action? _onSubscriptionsRefreshed;
    private Action<Models.PodcastFeed, Models.PodcastEpisode>? _onDownloadStarted;
    private Action<Services.Podcast.DownloadProgress>? _onDownloadProgress;
    private Action<Models.PodcastFeed, Models.PodcastEpisode>? _onDownloadCompleted;
    private Action<long, Exception>? _onDownloadFailed;

    /// <summary>
    public PodcastsViewModel Podcasts { get; private set; } = null!;

    public AudiobooksViewModel Audiobooks { get; private set; } = null!;

    /// <summary>The audiobook library items (downloaded books' chapter files) the owned-books shelf is built from.</summary>
    internal IEnumerable<MediaItem> AudiobookItems => _allItems.Where(i => i.Kind == MediaKind.Audiobook);

    /// <summary>
    /// Plays a whole book - its chapter files queued in order, resuming at the furthest
    /// chapter the listener reached (the within-chapter seek rides the chapter's own
    /// LastPositionMs in ExecutePlayMusic). A finished or fresh book starts at chapter one.
    /// </summary>
    internal void PlayBook(OwnedBook? book)
    {
        if (book is null || book.Chapters.Count == 0)
        {
            return;
        }

        var chapters = book.Chapters.ToList();
        var start = chapters[Services.Audiobooks.AudiobookLibrary.ResumeChapterIndex(chapters)];
        _playbackContext?.Release();
        _playbackContext = new PlaybackContext(chapters, start) { RepeatMode = RepeatMode };
        _playbackOriginViewKey = SelectedSidebarItem?.ViewConfigKey;
        OnPropertyChanged(nameof(PlaybackContextUpcoming));
        ExecutePlayMusic(start);
    }

    /// <summary>Removes a book everywhere: its files from disk, its library rows, and its acquisition record.</summary>
    internal async Task DeleteOwnedBook(OwnedBook? book)
    {
        if (book is null)
        {
            return;
        }

        // Files + library rows (only when something is actually downloaded).
        if (book.Chapters.Count > 0)
        {
            var deleted = await Task.Run(() => Services.Audiobooks.AudiobookDownloadService.DeleteFromDisk(book.Chapters[0].FilePath!));
            foreach (var item in _allItems.Where(i => i.FilePath is { } p && deleted.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                _allItems.Remove(item);
            }
            await Task.Run(() => MediaCache.RemoveLibraryFiles(deleted));
        }

        // The record itself - a deliberate "remove this book", so it's forgotten even if store-sourced.
        if (book.SourceKey is { } key)
        {
            Services.Media.AcquisitionStore.Release(Models.AcquiredMediaKind.Audiobook, key);
        }

        ApplyFilter();
        Audiobooks.RefreshOwned();
        UpdateData();
    }

    /// <summary>
    /// Rebuilds the LibraryItems list. Called on startup and when sidebar-affecting settings change.
    /// </summary>
    internal void RebuildLibraryItems()
    {
        var selectedKey = SelectedSidebarItem?.ViewConfigKey;
        LibraryItems.Clear();

        LibraryItems.Add(new() { Name = "Music",      Icon = "fa-solid fa-music",           Category = "LIBRARY", IsEnabled = true,  Kind = MediaKind.Music, ViewConfigKey = "Music" });
        LibraryItems.Add(new() { Name = "Radio",      Icon = "fa-solid fa-tower-broadcast", Category = "LIBRARY", IsEnabled = true,  Kind = MediaKind.Radio, ViewConfigKey = "Radio" });
        LibraryItems.Add(new() { Name = "Podcasts",   Icon = "fa-solid fa-podcast",         Category = "LIBRARY", IsEnabled = true,  Kind = MediaKind.Podcast, ViewConfigKey = "Podcasts" });
        // No Kind on purpose: the footer uses the generic item-count stats (the Music footer's
        // song totals don't fit books), keyed by the "Audiobooks" label mapping.
        LibraryItems.Add(new() { Name = "Audiobooks", Icon = "fa-solid fa-headphones",      Category = "LIBRARY", IsEnabled = true,  ViewConfigKey = "Audiobooks" });

        if (Settings.Get("OrgZ.BadFormat.ShowInSidebar", false))
        {
            LibraryItems.Add(new() { Name = "Bad Format", Icon = "fa-solid fa-triangle-exclamation", Category = "LIBRARY", IsEnabled = true, ViewConfigKey = "BadFormat" });
        }

        // Preserve selection if the current view still exists after the rebuild.
        if (selectedKey != null)
        {
            var restore = LibraryItems.FirstOrDefault(i => i.ViewConfigKey == selectedKey);
            if (restore != null)
            {
                SelectedSidebarItem = restore;
            }
        }
    }

    internal ObservableCollection<SidebarItem> PlaylistItems { get; } =
    [
        new() { Name = "Favorites", Icon = "fa-solid fa-star", Category = "PLAYLISTS", IsEnabled = true, IsFavorites = true, ViewConfigKey = "Favorites" },
        new() { Name = "New Playlist...", Icon = "fa-solid fa-plus", Category = "PLAYLISTS", IsEnabled = true, IsNewPlaylistAction = true },
    ];

    // -- Playback State --

    [ObservableProperty]
    private bool _isBackTrackButtonEnabled = false;

    // (No play-button IsEnabled property: the button is always live - with nothing
    // loaded it starts the selection, and with an empty library it no-ops harmlessly.
    // The old bound-but-never-driven flag just froze it permanently enabled anyway.)

    [ObservableProperty]
    private bool _isNextTrackButtonEnabled = false;

    [ObservableProperty]
    private string _buttonPlayPauseIcon = ICON_PLAY;

    [ObservableProperty]
    private Thickness _buttonPlayPausePadding;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTrackDurationDisplay))]
    private long _currentTrackTimeNumber = 0;

    [ObservableProperty]
    private string _currentTrackTime = "00:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLcdIdle), nameof(IsLcdPlaybackIdle), nameof(IsLcdPlaybackActive), nameof(ShowLcdCycleButton))]
    private string _currentTrackLine1 = string.Empty;

    /// <summary>
    /// True when there's no active track on the LCD (fresh boot, after Stop,
    /// between tracks before metadata lands). LcdDisplay shows a centered
    /// BW app icon over the Playback page in this state.
    /// </summary>
    public bool IsLcdIdle => string.IsNullOrEmpty(CurrentTrackLine1);

    /// <summary>
    /// Playback page is active AND there's no track loaded - the LCD body
    /// should show the BW app icon instead of empty text rows.
    /// </summary>
    public bool IsLcdPlaybackIdle => IsLcdPlayback && IsLcdIdle;

    /// <summary>
    /// Playback page is active AND a track is loaded - the standard track
    /// text + seek bar should render.
    /// </summary>
    public bool IsLcdPlaybackActive => IsLcdPlayback && !IsLcdIdle;

    [ObservableProperty]
    private string _currentTrackLine2 = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTrackDurationDisplay))]
    private string _currentTrackDuration = "00:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTrackDurationDisplay))]
    private long _currentTrackDurationNumber = 0;

    /// <summary>
    /// Right-side time label toggles between total duration ("3:45") and
    /// remaining-time countdown ("-1:22") when the user clicks on it. Persists
    /// across tracks within a session - most apps keep this preference sticky.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTrackDurationDisplay))]
    private bool _showRemainingTime = true;

    /// <summary>
    /// Renders the right-side LCD time label. Honours <see cref="ShowRemainingTime"/>
    /// and falls back to the raw duration string when there's no track loaded
    /// (durationNumber == 0) - the "-X:XX" form would be meaningless there.
    /// </summary>
    public string CurrentTrackDurationDisplay
    {
        get
        {
            // Both branches return the duration string with one leading character
            // so toggling between them never changes the rendered width - only
            // the leading glyph swaps between "-" (remaining) and " " (total).
            if (!ShowRemainingTime || CurrentTrackDurationNumber <= 0)
            {
                return " " + CurrentTrackDuration;
            }
            var remainingMs = Math.Max(0, CurrentTrackDurationNumber - CurrentTrackTimeNumber);
            return "-" + FormatHelper.FormatDurationCompact(remainingMs);
        }
    }

    internal void ToggleDurationDisplay() => ShowRemainingTime = !ShowRemainingTime;

    [ObservableProperty]
    private uint _currentVolume = (uint)Settings.Get("OrgZ.Volume", 100);

    private uint _previousVolume;

    [ObservableProperty]
    private Bitmap? _currentAlbumArt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSeekSlider), nameof(ShowBarberPole))]
    private bool _isSeekEnabled = true;

    /// <summary>
    /// True while the player is preparing media -- between the Play() call and
    /// the first PCM buffer reaching <see cref="AudioTap"/>. Covers every kind
    /// of "load": network buffering for radio / podcasts, disk read for music
    /// on slow HDDs, the CD spinning up to deliver the first sector. Drives
    /// the LCD's barber pole at 2x speed as a buffering cue, and flips the
    /// seek-bar slot from the slider to the barber pole regardless of whether
    /// the source supports seeking.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSeekSlider), nameof(ShowBarberPole))]
    private bool _isPlaybackLoading;

    /// <summary>
    /// LCD seek slider is shown when the source supports seeking AND playback
    /// has actually begun. During the load (<see cref="IsPlaybackLoading"/>) it
    /// hides so the barber pole owns the slot.
    /// </summary>
    public bool ShowSeekSlider => IsSeekEnabled && !IsPlaybackLoading;

    /// <summary>
    /// LCD barber-pole indicator: visible during the load AND for live radio
    /// streams (which never expose a duration). One animation, one stripe
    /// pattern, two speeds chosen by the .loading class on the rectangle.
    /// </summary>
    public bool ShowBarberPole => IsPlaybackLoading || !IsSeekEnabled;

    // -- Shuffle / Repeat --

    [ObservableProperty]
    private ShuffleMode _shuffleMode = Settings.Get("OrgZ.ShuffleMode", ShuffleMode.Off);

    [ObservableProperty]
    private RepeatMode _repeatMode = Settings.Get("OrgZ.RepeatMode", RepeatMode.Off);

    [ObservableProperty]
    private double _shuffleOpacity = 0.4;

    [ObservableProperty]
    private string _repeatIcon = "fa-solid fa-repeat";

    [ObservableProperty]
    private double _repeatOpacity = 0.4;

    // -- Queue --

    [ObservableProperty]
    private bool _isQueueVisible;

    public ObservableCollection<MediaItem>? PlaybackContextUpcoming => _playbackContext?.UpcomingItems;

    // Rip-in-progress LCD state. iTunes-style: while a rip is running the
    // now-playing LCD swaps to show "Importing 'Track'", a progress bar, and
    // a "Time remaining: 0:15 (8.5×)" readout. Cleared on completion. Long device
    // operations (import, scan, burn) reuse this same page via BeginLcdBusy/EndLcdBusy.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLcdCycleButton))]
    [NotifyPropertyChangedFor(nameof(BusyIndeterminate))]
    private bool _isBusy;
    [ObservableProperty] private string _busyTitle = string.Empty;
    [ObservableProperty] private string _busyDetail = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BusyIndeterminate))]
    private double _busyPercent;

    /// <summary>
    /// True while busy but with no determinate progress yet - drives the LCD's barber-pole
    /// (indeterminate) animation so there's immediate motion instead of an empty bar. The moment a
    /// real percent lands, the bar switches to a determinate fill.
    /// </summary>
    public bool BusyIndeterminate => IsBusy && BusyPercent <= 0;

    // Active rip's cancellation source. The Cancel X on the LCD's rip page
    // trips this; CdRipService respects the token between sector reads, so the
    // current sector finishes and the loop exits cleanly.
    private CancellationTokenSource? _ripCts;

    // Active burn's cancellation source. The same LCD Cancel X trips this - a burn
    // reuses the rip page (IsBusy), so the one button cancels whichever is running.
    // Only the transcode phase observes it: once sectors are being written the CTS is
    // torn down and _burnWritePhase raised, because cancelling a half-written disc
    // just makes a coaster - the burn always runs to completion (on every platform).
    private CancellationTokenSource? _burnCts;

    // True while the drive is actually writing sectors (or simulating a test write).
    // Lets the Cancel X answer honestly instead of pretending a started disc stopped.
    private bool _burnWritePhase;

    // Active device-sync cancellation source - the same LCD Cancel X trips this. Created by the
    // OUTERMOST sync gesture and reused by nested syncs (see BeginSyncScope), so one press stops
    // the whole gesture: the in-flight transcode/copy aborts, its torn output is deleted, and no
    // half-added track joins the view.
    private CancellationTokenSource? _deviceSyncCts;

    [RelayCommand]
    private void CancelRip()
    {
        if (_burnWritePhase)
        {
            UpdateMainStatus("Burn in progress — a started disc must be finished.");
        }

        _ripCts?.Cancel();
        _burnCts?.Cancel();
        _deviceSyncCts?.Cancel();
    }

    // LCD "pages": the now-playing display has multiple modes the user cycles
    // through with the left-chevron button. Playback (track info + scrubber)
    // and Vu (FFT bars) are always available; Rip joins them only while a rip
    // is in flight. Auto-snap to Rip when one starts so the user sees it
    // immediately; snap back to Playback when it ends.
    public enum LcdPage { Playback, Vu, Busy }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLcdPlayback), nameof(IsLcdVu), nameof(IsLcdBusy), nameof(IsLcdPlaybackIdle), nameof(IsLcdPlaybackActive))]
    private LcdPage _currentLcdPage = LcdPage.Playback;

    public bool IsLcdPlayback => CurrentLcdPage == LcdPage.Playback;
    public bool IsLcdVu => CurrentLcdPage == LcdPage.Vu;
    public bool IsLcdBusy => CurrentLcdPage == LcdPage.Busy;

    private IReadOnlyList<LcdPage> AvailableLcdPages
    {
        get
        {
            var pages = new List<LcdPage> { LcdPage.Playback, LcdPage.Vu };
            if (IsBusy) pages.Add(LcdPage.Busy);
            return pages;
        }
    }

    // Show the cycle arrows whenever there's more than one page to flip between.
    // Idle playback (nothing playing) normally hides them - but an in-progress
    // activity like a rip is itself a cyclable screen, so keep the arrows then.
    public bool ShowLcdCycleButton => AvailableLcdPages.Count > 1 && (!IsLcdIdle || IsBusy);

    [RelayCommand]
    private void CycleLcdPage()
    {
        var pages = AvailableLcdPages;
        int i = 0;
        for (; i < pages.Count; i++)
        {
            if (pages[i] == CurrentLcdPage) break;
        }
        CurrentLcdPage = pages[(i + 1) % pages.Count];
    }

    partial void OnIsBusyChanged(bool value)
    {
        if (value)
        {
            CurrentLcdPage = LcdPage.Busy;
        }
        else if (CurrentLcdPage == LcdPage.Busy)
        {
            CurrentLcdPage = LcdPage.Playback;
        }
    }

    // -- Unified Data --

    [ObservableProperty]
    private MediaItem? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSearchResults), nameof(NoSearchResultsMessage), nameof(ShowEmptyView))]
    // Not persisted across app launches - search is always transient state.
    // Per-view search is stored in _searchTextByView and swapped on sidebar changes.
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSearchResults), nameof(ShowEmptyView))]
    private List<MediaItem> _filteredItems = [];

    // The Podcasts view replaces the data grid with its own panel, which renders
    // its own results + empty-state. Its library item count is always 0, so
    // without this guard the grid's "No search results" overlay would light up
    // (and, as a DockPanel sibling, shove the panel sideways) on every podcast
    // search. Gate it to the grid-backed views only.
    public bool ShowNoSearchResults =>
        FilteredItems.Count == 0
        && !string.IsNullOrWhiteSpace(SearchText)
        && SelectedSidebarItem?.ViewConfigKey != "Podcasts";

    public string NoSearchResultsMessage => $"No search results for \"{SearchText}\".";

    /// <summary>
    /// A view holding nothing with no search active - an empty device node, a fresh
    /// playlist, a share still loading. Distinct from "no search results": there is
    /// nothing to un-filter, so the line explains the emptiness instead.
    /// </summary>
    public bool ShowEmptyView =>
        FilteredItems.Count == 0
        && string.IsNullOrWhiteSpace(SearchText)
        && SelectedSidebarItem is not null
        && SelectedSidebarItem.ViewConfigKey is not ("Podcasts" or "Audiobooks");

    public string EmptyViewMessage => DescribeEmptyView(SelectedSidebarItem);

    /// <summary>
    /// The quiet line an empty view shows. Pure so the wording is tested, and phrased
    /// per view because "nothing here" is unhelpful when the reason differs - an empty
    /// iPod wants a different sentence than an empty playlist.
    /// </summary>
    internal static string DescribeEmptyView(SidebarItem? view)
    {
        if (view is null)
        {
            return "Nothing to show here.";
        }

        var key = view.ViewConfigKey ?? string.Empty;

        if (key.StartsWith("Share:", StringComparison.Ordinal))
        {
            return "This shared library is empty.";
        }

        if (key.EndsWith($":{MediaKind.Podcast}", StringComparison.Ordinal))
        {
            return "No podcasts on this device.";
        }

        if (key.EndsWith($":{MediaKind.Audiobook}", StringComparison.Ordinal))
        {
            return "No audiobooks on this device.";
        }

        if (key.StartsWith("Device:", StringComparison.Ordinal))
        {
            return "No music on this device yet — drag tracks here, or use Sync.";
        }

        if (key.StartsWith("Playlist:", StringComparison.Ordinal))
        {
            return "This playlist is empty — drag songs here to add them.";
        }

        if (view.IsFavorites)
        {
            return "No favorites yet — mark a song with the star to add it.";
        }

        return key switch
        {
            "CdAudio" => "No audio tracks on this disc.",
            "Radio" => "No stations match the current filters.",
            "Music" => "Your library is empty — add a folder in Settings to get started.",
            _ => "Nothing to show here.",
        };
    }

    /// <summary>
    /// The collection view the shared media grid renders, for whichever view is active - flat or
    /// grouped, library or device or radio.
    ///
    /// There were three of these, one per grid, so that switching views never reassigned a grouped
    /// grid's source (a reassignment rebuilds every group expanded, and the saved collapse state was
    /// re-applied a frame later, which is a visible flash). The single grid reassigns on every
    /// switch, and instead applies collapse in the same dispatcher turn as the bind - see
    /// <c>MediaGrid.Bind</c>. The per-view cache below still hands back the same instance for a
    /// re-entered view, so the common case stays a binding no-op regardless.
    /// </summary>
    [ObservableProperty]
    private DataGridCollectionView? _filteredItemsView;

    // Per-view cache of built collection views. A view switch that lands on a key whose cached
    // view is still valid (same library version + same filter signature) reuses it verbatim -
    // no re-filter, no new DataGridCollectionView, and (critically for the grouped grid) no
    // ItemsSource reassignment, so the DataGrid keeps its collapse/scroll state. Invalidated
    // wholesale by bumping _dataVersion on any non-switch ApplyFilter (i.e. anything that
    // actually changed library content, filters, ignored/favorite/playlist membership, sort...).
    private readonly Dictionary<string, CachedFilterView> _viewCache = new(StringComparer.Ordinal);
    private int _dataVersion;

    private sealed record CachedFilterView(List<MediaItem> Items, DataGridCollectionView View, int Version, string Signature);

    // -- Radio Filters --
    //
    // Both collections are seeded with "All" up front. RebuildRadioFilterOptions
    // clears and re-adds it alongside the live entries; pre-seeding matters
    // only at startup, before there's any radio data to rebuild from - without
    // it the ComboBox.SelectedItem binding ("All") has nothing to resolve
    // against and the dropdown renders blank.

    internal ObservableCollection<string> Countries { get; } = ["All"];

    internal ObservableCollection<string> Genres { get; } = ["All"];

    [ObservableProperty]
    private string _selectedCountry = Settings.Get("OrgZ.Radio.Country", "All");

    [ObservableProperty]
    private string _selectedGenre = Settings.Get("OrgZ.Radio.Genre", "All");

    // -- Radio Management --

    internal ObservableCollection<string> Messages { get; } = [];
    // -- Computed --

    private IEnumerable<MediaItem> MusicItems => _allItems.Where(i => i.Kind == MediaKind.Music);


    internal Action? ScrollToSelectedRequested;
    internal Func<MediaItem?>? GetScrollAnchor;
    internal Action<MediaItem?>? RestoreScrollAnchor;
    internal Action? PlaylistsChanged;

    /// <summary>
    /// Rebuilds the window's chrome (host visibility, grid columns, context menu) for the
    /// incoming sidebar view. Invoked from OnSelectedSidebarItemChanged BEFORE ApplyFilter
    /// binds the view's source, so rows realize under the right columns the first time.
    /// </summary>
    internal Action<SidebarItem?>? ApplyViewChrome;

    // -- Change Handlers --

    /// <summary>
    /// Shuffle-by preference (Settings > Playback). Read fresh each use so an OK'd
    /// settings dialog applies without a restart.
    /// </summary>
    private static ShuffleBy ShuffleByPreference => Settings.Get("OrgZ.ShuffleBy", ShuffleBy.Song);

    /// <summary>
    /// Streaming Buffer Size (Settings > Playback) as libvlc caching milliseconds.
    /// Each call site passes its tuned Medium value; the other tiers scale around it,
    /// so the LAN-share and internet-stream baselines keep their ratio.
    /// </summary>
    private static long StreamingBufferMs(long mediumMs)
    {
        var factor = Settings.Get("OrgZ.StreamingBufferSize", "Medium") switch
        {
            "Small" => 0.5,
            "Large" => 2.0,
            "Extra Large" => 4.0,
            _ => 1.0,
        };

        return (long)(mediumMs * factor);
    }

    partial void OnShuffleModeChanged(ShuffleMode value)
    {
        ShuffleOpacity = value == ShuffleMode.On ? 1.0 : 0.4;
        _playbackContext?.SetShuffle(value == ShuffleMode.On, ShuffleByPreference);
        Settings.Set("OrgZ.ShuffleMode", value);
        Settings.Save();
        UpdateNavigationButtons();
    }

    partial void OnRepeatModeChanged(RepeatMode value)
    {
        RepeatIcon = value == RepeatMode.One ? "fa-solid fa-arrow-rotate-left" : "fa-solid fa-repeat";
        RepeatOpacity = value == RepeatMode.Off ? 0.4 : 1.0;

        if (_playbackContext != null)
        {
            _playbackContext.RepeatMode = value;
        }

        Settings.Set("OrgZ.RepeatMode", value);
        Settings.Save();
        UpdateNavigationButtons();
    }

    // Per-view search state: each sidebar view remembers its own search text, so
    // typing "rush" while on Music doesn't leak into the iPod view and vice-versa.
    // Switching away saves the current text under the leaving view's key; switching
    // back restores it. _suppressSearchPersist guards the restore so loading a saved
    // text doesn't cascade back as a "user typed this" save.
    private readonly Dictionary<string, string> _searchTextByView = new(StringComparer.Ordinal);
    private bool _suppressSearchPersist;

    partial void OnSearchTextChanged(string value)
    {
        // During a per-view search restore on a view switch (_suppressSearchPersist == true), skip
        // re-filtering here. OnSelectedSidebarItemChanged calls ApplyFilter(fromViewSwitch: true)
        // immediately after, and filtering here would be a non-switch pass that bumps the cache
        // version - forcing every view (Radio included) to rebuild on the next switch, which is
        // exactly the collapse "flash" the cache is meant to kill. Real searches run normally.
        if (!_suppressSearchPersist)
        {
            ApplyFilter();
        }

        if (!_suppressSearchPersist)
        {
            PerViewSearchState.Save(_searchTextByView, SelectedSidebarItem?.ViewConfigKey, value);
        }

        // The Podcasts panel replaces the data grid with its own surface, so the
        // header search box can't filter a grid there. Route it to a debounced
        // PodcastIndex search that renders into the panel's shared feed-list view.
        //
        // Skip while restoring a per-view saved search on a view switch
        // (_suppressSearchPersist): re-running the podcast search would navigate the panel
        // back to the results and push a duplicate nav-stack entry. On a real switch back
        // the panel keeps whatever view the user left it on.
        if (!_suppressSearchPersist && ListViewConfigs.Get(SelectedSidebarItem?.ViewConfigKey)?.Host == ViewHost.PodcastsPanel && Podcasts is not null)
        {
            Podcasts.ApplyHeaderSearch(value);
        }

        // The Audiobooks composite has one search - this box. ApplyFilter above already filtered
        // the library grid through the normal pipeline; the same text also feeds the store's
        // debounced archive.org search, so the grid and the store react together.
        if (!_suppressSearchPersist && ListViewConfigs.Get(SelectedSidebarItem?.ViewConfigKey)?.Host == ViewHost.AudiobooksPanel && Audiobooks is not null)
        {
            Audiobooks.ApplyHeaderSearch(value);
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
    }

    [RelayCommand]
    private void ToggleShuffle()
    {
        ShuffleMode = ShuffleMode == ShuffleMode.Off ? ShuffleMode.On : ShuffleMode.Off;
    }

    [RelayCommand]
    private void CycleRepeatMode()
    {
        RepeatMode = RepeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            RepeatMode.One => RepeatMode.Off,
            _ => RepeatMode.Off
        };
    }

    [RelayCommand]
    private void ToggleQueue()
    {
        IsQueueVisible = !IsQueueVisible;
    }

    private MiniPlayerWindow? _miniPlayer;

    /// <summary>
    /// Opens the mini-player.  In <see cref="MiniPlayerMode.Replace"/> (iTunes-style)
    /// mode, the main window is hidden; in <see cref="MiniPlayerMode.SideBySide"/>
    /// mode both windows remain visible.  Idempotent - if the mini-player is already
    /// open the call becomes a focus request.
    /// </summary>
    [RelayCommand]
    internal void ToggleMiniPlayer()
    {
        if (_miniPlayer != null)
        {
            _miniPlayer.Activate();
            return;
        }

        var mode = LoadMiniPlayerMode();

        _miniPlayer = new MiniPlayerWindow { DataContext = this };
        _miniPlayer.RestoreMainRequested += () =>
        {
            _window.Show();
            _window.Activate();
        };
        _miniPlayer.Closed += (_, _) =>
        {
            _miniPlayer = null;
            // The mini-player's X button calls Shutdown(), which closes us first.
            // Don't try to re-show the main window if its native handle is already
            // gone - Avalonia throws InvalidOperationException("Cannot re-show a
            // closed window") and the unhandled exception crashes the process.
            if (!_window.IsVisible && _window.PlatformImpl != null)
            {
                _window.Show();
                _window.Activate();
            }
        };

        _miniPlayer.Show();

        if (mode == MiniPlayerMode.Replace)
        {
            _window.Hide();
        }
    }

    internal static MiniPlayerMode LoadMiniPlayerMode()
    {
        var raw = Settings.Get("OrgZ.MiniPlayer.Mode", nameof(MiniPlayerMode.Replace));
        return Enum.TryParse<MiniPlayerMode>(raw, ignoreCase: true, out var mode)
            ? mode
            : MiniPlayerMode.Replace;
    }

    internal static void SaveMiniPlayerMode(MiniPlayerMode mode)
    {
        Settings.Set("OrgZ.MiniPlayer.Mode", mode.ToString());
        Settings.Save();
    }

    [RelayCommand]
    /// <summary>Queues a selection to play next, preserving its order (inserted in
    /// reverse so the first selected track plays first).</summary>
    internal void PlayNext(IReadOnlyList<MediaItem> items)
    {
        for (int i = items.Count - 1; i >= 0; i--)
        {
            PlayNext(items[i]);
        }
    }

    /// <summary>Appends a selection to the queue in order.</summary>
    /// <summary>
    /// Builds a playback context without starting playback, so the queue panel has something
    /// to show. Docs screenshot harness only - a real queue comes from playing something.
    /// </summary>
    internal void SeedQueueForScreenshots(List<MediaItem> source, MediaItem start)
    {
        _playbackContext = new PlaybackContext(source, start) { RepeatMode = RepeatMode };
        OnPropertyChanged(nameof(PlaybackContextUpcoming));
    }

    internal void AddToQueue(IReadOnlyList<MediaItem> items)
    {
        foreach (var item in items)
        {
            AddToQueue(item);
        }
    }

    internal void PlayNext(MediaItem? item)
    {
        if (item == null)
        {
            return;
        }

        if (_playbackContext == null)
        {
            PlayItem(item);
            return;
        }

        _playbackContext.InsertNext(item);
        OnPropertyChanged(nameof(PlaybackContextUpcoming));
    }

    [RelayCommand]
    internal void AddToQueue(MediaItem? item)
    {
        if (item == null)
        {
            return;
        }

        if (_playbackContext == null)
        {
            PlayItem(item);
            return;
        }

        _playbackContext.Append(item);
        OnPropertyChanged(nameof(PlaybackContextUpcoming));
    }

    [RelayCommand]
    internal void RemoveFromQueue(int index)
    {
        _playbackContext?.RemoveFromUpcoming(index);
        OnPropertyChanged(nameof(PlaybackContextUpcoming));
    }

    internal void MoveInQueue(int fromIndex, int toIndex)
    {
        if (_playbackContext == null || fromIndex == toIndex)
        {
            return;
        }

        _playbackContext.MoveInUpcoming(fromIndex, toIndex);
        OnPropertyChanged(nameof(PlaybackContextUpcoming));
    }

    [RelayCommand]
    internal void ClearQueue()
    {
        _playbackContext?.ClearUpcoming();
        OnPropertyChanged(nameof(PlaybackContextUpcoming));
    }

    [RelayCommand]
    internal void NavigateToPlaying()
    {
        var item = CurrentPlayingItem;
        if (item == null)
        {
            return;
        }

        var target = ResolveNavigationTarget(_playbackOriginViewKey, item, LibraryItems, PlaylistItems, DeviceItems);
        if (target == null)
        {
            return;
        }

        // Don't clear SearchText - the per-view swap in OnSelectedSidebarItemChanged
        // restores whatever search was active in the target view (possibly nothing).
        SelectedSidebarItem = target;
        SelectedItem = item;
        ScrollToSelectedRequested?.Invoke();
    }

    /// <summary>
    /// Pure target resolution for "go to current song". The view playback started
    /// from (playlist, Favorites, device, CD) wins - the song in its context, like
    /// iTunes - and the kind-based homes are fallbacks for when that view is gone
    /// (deleted playlist, ejected device). Static so tests drive it directly.
    /// </summary>
    internal static SidebarItem? ResolveNavigationTarget(
        string? originViewKey,
        MediaItem item,
        IReadOnlyList<SidebarItem> libraryItems,
        IReadOnlyList<SidebarItem> playlistItems,
        IReadOnlyList<SidebarItem> deviceItems)
    {
        if (!string.IsNullOrEmpty(originViewKey))
        {
            var origin = libraryItems.FirstOrDefault(i => i.ViewConfigKey == originViewKey)
                ?? playlistItems.FirstOrDefault(i => i.ViewConfigKey == originViewKey)
                ?? deviceItems.FirstOrDefault(i => i.ViewConfigKey == originViewKey);
            if (origin != null)
            {
                return origin;
            }
        }

        if (item.Source?.StartsWith("device:") == true)
        {
            var viewKey = $"Device:{item.Source["device:".Length..]}";
            return deviceItems.FirstOrDefault(i => i.ViewConfigKey == viewKey);
        }

        if (item.Source == "cdda")
        {
            return deviceItems.FirstOrDefault(i => i.ViewConfigKey == "CdAudio");
        }

        return item.Kind switch
        {
            MediaKind.Music => libraryItems.FirstOrDefault(i => i.Kind == MediaKind.Music),
            MediaKind.Radio => libraryItems.FirstOrDefault(i => i.Kind == MediaKind.Radio),
            _ => null,
        };
    }

    /// <summary>
    /// Orders a grid selection by its position in the current view. DataGrid selection
    /// order is CLICK order - burning or queueing a shift-click-upward selection must
    /// not come out backwards. Items absent from the view keep their selection order,
    /// after the in-view ones. Static so tests drive it directly.
    /// </summary>
    internal static List<MediaItem> OrderSelectionByView(IEnumerable<MediaItem> selection, IReadOnlyList<MediaItem> viewOrder)
    {
        var indexByItem = new Dictionary<MediaItem, int>();
        for (int i = 0; i < viewOrder.Count; i++)
        {
            indexByItem.TryAdd(viewOrder[i], i);
        }

        var inView = new List<MediaItem>();
        var strays = new List<MediaItem>();
        foreach (var item in selection)
        {
            if (indexByItem.ContainsKey(item))
            {
                inView.Add(item);
            }
            else
            {
                strays.Add(item);
            }
        }

        inView.Sort((a, b) => indexByItem[a].CompareTo(indexByItem[b]));
        inView.AddRange(strays);
        return inView;
    }

    // Set true during RebuildRadioFilterOptions's bounce-assignment so the
    // intermediate empty value doesn't trip ApplyFilter / Settings.Save.
    private bool _suppressFilterSideEffects;

    partial void OnSelectedCountryChanged(string value)
    {
        if (_suppressFilterSideEffects) return;
        ApplyFilter();
        Settings.Set("OrgZ.Radio.Country", value);
        Settings.Save();
    }

    partial void OnSelectedGenreChanged(string value)
    {
        if (_suppressFilterSideEffects) return;
        ApplyFilter();
        Settings.Set("OrgZ.Radio.Genre", value);
        Settings.Save();
    }

    partial void OnSelectedSidebarItemChanging(SidebarItem? oldValue, SidebarItem? newValue)
    {
        // Fires before SelectedSidebarItem is actually updated. SearchText still reflects
        // the old view, so snapshot it into the per-view dict before the view swap.
        PerViewSearchState.Save(_searchTextByView, oldValue?.ViewConfigKey, SearchText);
    }

    partial void OnSelectedSidebarItemChanged(SidebarItem? value)
    {
        _log.Debug("Sidebar selection changed: ViewKey={ViewKey} Name={Name} _allItems.Count={ItemCount}", value?.ViewConfigKey ?? "<null>", value?.Name ?? "<null>", _allItems.Count);

        // Restore the incoming view's remembered search text. Suppress persistence so
        // this programmatic set doesn't re-save the same value under the NEW key.
        var restored = PerViewSearchState.Restore(_searchTextByView, value?.ViewConfigKey);
        if (restored != SearchText)
        {
            _suppressSearchPersist = true;
            try { SearchText = restored; }
            finally { _suppressSearchPersist = false; }
        }

        StatusBar.ActiveKind = value?.Kind;
        StatusBar.HasGenericStats = value?.Kind == null && value?.ViewConfigKey != null;

        // Resolve the selected device (if this sidebar entry is a portable device view)
        if (value?.ViewConfigKey is { } key && key.StartsWith("Device:"))
        {
            var mountPath = key["Device:".Length..];
            SelectedDevice = _connectedDevices.TryGetValue(mountPath, out var dev) ? dev : null;

            // User actively clicked the device → persist the /.orgz/device identity record.
            // This merges whatever we've detected live with any prior record on the mount,
            // so stock-firmware boots and Rockbox boots accumulate a complete picture over
            // time in a single file that travels with the iPod.
            if (SelectedDevice != null)
            {
                Task.Run(() => DeviceFingerprint.PersistDeviceRecord(SelectedDevice));
            }
        }
        else
        {
            SelectedDevice = null;
        }

        OnPropertyChanged(nameof(ShowCdInfoBar));

        FireAndForget(BuildPlaylistHeaderAsync(value), "playlist header build");

        _activeViewConfig = ListViewConfigs.Get(value?.ViewConfigKey);

        if (!string.IsNullOrEmpty(value?.ViewConfigKey))
        {
            Settings.Set("OrgZ.ActiveView", value.ViewConfigKey);

            // Deferred: a synchronous whole-file write per sidebar click stalls the switch for
            // disk I/O; the in-memory value is what this session reads back.
            Settings.SaveDeferred();
        }

        // The window swaps its chrome now, before the bind below, so the incoming rows realize
        // under the incoming view's columns instead of being laid out twice.
        ApplyViewChrome?.Invoke(value);

        // A view switch changes nothing about content - reuse the target view's cached collection
        // view if it's still valid (same library version + filter signature). This is the path that
        // makes returning to Radio instant and flash-free: the grid's source instance is unchanged,
        // so its row-group collapse state is preserved with no rebuild.
        ApplyFilter(fromViewSwitch: true);

        // Restore selection to the currently playing item if it's in this view
        if (CurrentPlayingItem != null && FilteredItems.Contains(CurrentPlayingItem))
        {
            SelectedItem = CurrentPlayingItem;
        }
        else
        {
            SelectedItem = null;
        }

        UpdateNavigationButtons();

        if (value?.Kind == MediaKind.Radio)
        {
            StatusBar.StationCount = FilteredItems.Count;
        }
    }

    private void ApplyFilter(bool fromViewSwitch = false)
    {
        if (_activeViewConfig == null)
        {
            _log.Debug("ApplyFilter: _activeViewConfig is null — emptying FilteredItems");
            FilteredItems = [];
            UpdateNavigationButtons();
            return;
        }

        // Any NON-switch call means something that affects grid content actually changed:
        // library items, the active filter/search, ignored/favorite/playlist membership, sort.
        // Bump the version so every cached view (including the active one) is now stale and gets
        // rebuilt on next access. A pure view switch changes nothing, so it leaves the version
        // alone and can reuse a still-valid cached view verbatim - the no-rebuild, no-flash path.
        if (!fromViewSwitch)
        {
            _dataVersion++;
        }

        var viewKey = _activeViewConfig.Key;
        var signature = BuildFilterSignature(_activeViewConfig);

        if (_viewCache.TryGetValue(viewKey, out var cached)
            && cached.Version == _dataVersion
            && cached.Signature == signature)
        {
            // Fast path: nothing relevant changed since this view was last built. Reuse the exact
            // same list + DataGridCollectionView. The ItemsSource instance is unchanged, so the
            // assignment is a no-op on the binding and the grid keeps its row-group collapse +
            // scroll state - instant switch, nothing to re-collapse.
            FilteredItems = cached.Items;
            FilteredItemsView = cached.View;
            UpdateViewStats(_activeViewConfig, cached.Items);
            UpdateNavigationButtons();
            _log.Debug("ApplyFilter reuse: ViewKey={ViewKey} Filtered={FilteredCount} Version={Version}", viewKey, cached.Items.Count, _dataVersion);
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var startCount = _allItems.Count;

        try
        {
            // Snapshot _allItems up front so a concurrent mutation (background scan,
            // file watcher, anything that AddRange's during render) can't throw
            // InvalidOperationException("Collection was modified") halfway through the
            // pipeline. _allItems should be UI-thread-only by convention, but the cost
            // of a snapshot is one array allocation - cheap insurance against the kind
            // of all-tabs-go-empty bug we're chasing.
            var snapshot = _allItems.ToArray();
            IEnumerable<MediaItem> items = snapshot.Where(_activeViewConfig.BaseFilter);

            // Unchecked tracks are NOT hidden - iTunes leaves them in place, greyed by their
            // empty tick, so you can see and re-check them. They're skipped by play-through
            // and sync instead (see PlaybackContext and the sync gates).

            // Radio-specific filters
            if (_activeViewConfig.ShowRadioFilterPanel)
            {
                if (SelectedCountry != "All")
                {
                    items = items.Where(s =>
                        (s.Country?.Equals(SelectedCountry, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (s.CountryCode?.Equals(SelectedCountry, StringComparison.OrdinalIgnoreCase) ?? false));
                }

                if (SelectedGenre != "All")
                {
                    items = items.Where(s =>
                        string.Equals(s.Tags, SelectedGenre, StringComparison.OrdinalIgnoreCase));
                }
            }

            // Search text filter
            var searchText = SearchText?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(searchText))
            {
                var search = searchText;
                items = items.Where(item => _activeViewConfig.SearchFilter(item, search));
            }

            // Optional view-defined sort (e.g., playlist track order)
            if (_activeViewConfig.Sorter != null)
            {
                items = _activeViewConfig.Sorter(items);
            }

            var list = items.ToList();
            FilteredItems = list;

            // Build the DataGridCollectionView wrapper. If the view config asks for grouping,
            // wire it up so Avalonia's DataGrid renders collapsible group headers, and add a
            // matching SortDescription so the group headers appear in alphabetical order
            // (otherwise DataGridCollectionView falls back to insertion order, which means
            // the first-seen genre wins the top slot regardless of name).
            var view = new DataGridCollectionView(list);
            if (_activeViewConfig.GroupByPath != null)
            {
                view.GroupDescriptions.Add(new DataGridPathGroupDescription(_activeViewConfig.GroupByPath));
                view.SortDescriptions.Add(DataGridSortDescription.FromPath(
                    _activeViewConfig.GroupByPath,
                    System.ComponentModel.ListSortDirection.Ascending));
            }

            _viewCache[viewKey] = new CachedFilterView(list, view, _dataVersion, signature);
            FilteredItemsView = view;

            UpdateViewStats(_activeViewConfig, list);
            UpdateNavigationButtons();

            sw.Stop();
            _log.Debug("ApplyFilter build: ViewKey={ViewKey} _allItems={AllCount} Filtered={FilteredCount} Version={Version} Elapsed={ElapsedMs}ms", viewKey, startCount, list.Count, _dataVersion, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Don't leave the UI in a broken state. Log loudly, then empty FilteredItems
            // so the user sees a clean (empty) grid instead of stale/garbage rows. The
            // exception is the actual diagnostic - DO NOT swallow without surfacing. Drop any
            // cached entry for this key so the next attempt rebuilds from scratch.
            _log.Error(ex, "ApplyFilter threw: ViewKey={ViewKey} _allItems={AllCount} Elapsed={ElapsedMs}ms", viewKey, startCount, sw.ElapsedMilliseconds);
            _viewCache.Remove(viewKey);
            FilteredItems = [];
            FilteredItemsView = new DataGridCollectionView(FilteredItems);
            UpdateNavigationButtons();
        }
    }

    /// <summary>
    /// The filter inputs that, when unchanged, make a cached view reusable. Search applies to
    /// every view; country/genre only narrow Radio, so they're only part of its signature.
    /// Library content changes are tracked separately via <see cref="_dataVersion"/>.
    /// </summary>
    private string BuildFilterSignature(ListViewConfig config)
    {
        var search = SearchText?.Trim() ?? string.Empty;
        return config.ShowRadioFilterPanel
            ? string.Join("", search, SelectedCountry, SelectedGenre)
            : search;
    }

    /// <summary>Status-bar / footer stats for the active view. Shared by the build and reuse paths.</summary>
    private void UpdateViewStats(ListViewConfig config, List<MediaItem> items)
    {
        // Radio station count in the status bar
        if (config.ShowRadioFilterPanel)
        {
            UI(() => StatusBar.StationCount = items.Count);
        }

        // Generic status bar for non-Music/Radio views
        if (StatusBar.HasGenericStats)
        {
            UpdateGenericStatusBar();
        }

        // Music view: the footer summary reflects the current search/filter, not whole-library
        // totals (UpdateData). With no search active the filtered set is the full library.
        if (StatusBar.ActiveKind == MediaKind.Music)
        {
            var songs = items.Count;
            var duration = TimeSpan.FromTicks(items.Sum(i => i.Duration?.Ticks ?? 0));
            var size = items.Sum(i => i.FileSize ?? 0L);
            UI(() =>
            {
                StatusBar.TotalSongs = songs;
                StatusBar.TotalDuration = duration;
                StatusBar.TotalFileSize = size;
            });
        }
    }

    // -- Headless seeding seam --
    // Generic hooks the docs-screenshot runner uses to drive views. No
    // screenshot-specific data or orchestration lives in the app: UpdateData,
    // DeviceItems/LibraryItems/PlaylistItems, and the playback/LCD
    // properties are already internal/public, so the runner composes scenes from
    // those plus the four primitives below.

    /// <summary>Replaces the backing item list. Pair with <see cref="RefreshView"/>
    /// or a sidebar selection to re-run the filter. Bumps the cache version so a subsequent
    /// same-view switch rebuilds instead of serving the pre-replacement cached view.</summary>
    internal void SetItems(IReadOnlyList<MediaItem> items)
    {
        _allItems = items.ToList();
        _dataVersion++;
    }

    /// <summary>Re-applies the active view's filter.</summary>
    internal void RefreshView() => ApplyFilter();

    /// <summary>The CD-track backing list, for seeding an inserted disc.</summary>
    internal IList<MediaItem> CdTrackList => _cdTracks;

    /// <summary>Sets the transport control to its playing (pause) glyph.</summary>
    internal void ShowPlayingState()
    {
        ButtonPlayPauseIcon = ICON_PAUSE;
        ButtonPlayPausePadding = ICON_PAUSE_PADDING;
    }

    public MainWindowViewModel(MainWindow window) : this(window, headless: false)
    {
    }

    // Headless/screenshot construction skips LibVLC + audio output + OS-shell
    // wiring (MPRIS, macOS Now Playing) so the docs-screenshot harness can render
    // the window with seeded data and no native dependencies. The player fields
    // stay null because no playback path runs in this mode.
    internal MainWindowViewModel(MainWindow window, bool headless)
    {
        _window = window;

        if (!headless)
        {
            // OFF the constructor's critical path. `new LibVLC()` scans libvlc's plugin
            // directory to build its cache, and on a first run - cold disk, every DLL
            // getting virus-scanned as it's touched - that measured TWENTY SECONDS on a
            // clean install, against about five for everything else in startup combined.
            // Doing it here meant nothing was drawn until it finished, so a slow launch was
            // indistinguishable from a hang. The window paints first now; playback comes up
            // behind it, and anything that needs it waits via EnsurePlaybackReady().
            _playbackInit = Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                InitializePlayback();
                _log.Information("startup: playback engine ready after {Ms} ms (background)", sw.ElapsedMilliseconds);
            });
        }

        ButtonPlayPausePadding = ICON_PLAY_PADDING;

        // Multi-pressing CDs ask which release this disc actually is; the answer is
        // cached per DiscID, so it's asked once. Cleared in Dispose - the delegate
        // captures this VM.
        CdAudioService.ChooseRelease = candidates =>
            Dispatcher.UIThread.InvokeAsync(() => PickCdReleaseAsync(candidates));

        Podcasts = new PodcastsViewModel(this);
        Audiobooks = new AudiobooksViewModel(this);
        // Load persisted subscriptions up front so the store's left-column "Subscribed" section and
        // the subscriptions view are populated on startup. Subscriptions no longer live in the
        // sidebar, so a count change doesn't rebuild the library list - the panel binds the set
        // directly and updates reactively.
        Podcasts.ReloadSubscriptions();

        // Apply the global podcast rules on a cadence: on startup, when a check is due,
        // refresh every subscription (auto-download new episodes + prune per the Keep
        // policy). Reload the panel's subscription tiles once a pass finishes.
        // These are PROCESS-WIDE singletons, so the handlers are kept in fields and detached
        // in Dispose. Subscribing with throwaway lambdas leaked a whole ViewModel per
        // subscription - which the docs-screenshot runner (several VMs per process) turned
        // from theory into a real pile of live objects driving a dead window.
        _onSubscriptionsRefreshed = () => UI(() => Podcasts.ReloadSubscriptions());
        Services.Podcast.PodcastSubscriptionService.Instance.RefreshCompleted += _onSubscriptionsRefreshed;
        if (Services.Podcast.PodcastSettings.IsDueForCheck)
        {
            FireAndForget(Services.Podcast.PodcastSubscriptionService.Instance.RefreshNowAsync(App.FolderPath), "podcast subscription refresh");
        }
        else if (Services.Podcast.PodcastSettings.IsRetentionDue)
        {
            // Manual check mode still prunes: the Keep policy is housekeeping, not a fetch.
            FireAndForget(Services.Podcast.PodcastSubscriptionService.Instance.ApplyRetentionNowAsync(App.FolderPath), "podcast retention pass");
        }

        // Surface podcast download progress on the LCD busy display (same as ripping/import).
        // The service's events fire from background threads, so marshal each to the UI thread.
        var podcastDownloads = Services.Podcast.PodcastDownloadService.Instance;
        _onDownloadStarted = (_, ep) => UI(() => OnPodcastDownloadStarted(ep));
        _onDownloadProgress = p => UI(() => OnPodcastDownloadProgress(p));
        _onDownloadCompleted = (_, ep) => UI(() => OnPodcastDownloadFinished(ep.Id));
        _onDownloadFailed = (epId, _) => UI(() => OnPodcastDownloadFinished(epId));
        podcastDownloads.Started += _onDownloadStarted;
        podcastDownloads.ProgressChanged += _onDownloadProgress;
        podcastDownloads.Completed += _onDownloadCompleted;
        podcastDownloads.Failed += _onDownloadFailed;

        // Initialize shuffle/repeat visual state from saved settings
        ShuffleOpacity = ShuffleMode == ShuffleMode.On ? 1.0 : 0.4;
        RepeatIcon = RepeatMode == RepeatMode.One ? "fa-solid fa-arrow-rotate-left" : "fa-solid fa-repeat";
        RepeatOpacity = RepeatMode == RepeatMode.Off ? 0.4 : 1.0;

        RebuildLibraryItems();

        var savedView = Settings.Get("OrgZ.ActiveView", "Music");
        SelectedSidebarItem = PlaylistItems.FirstOrDefault(i => i.ViewConfigKey == savedView) ?? LibraryItems.FirstOrDefault(i => i.ViewConfigKey == savedView) ?? LibraryItems[0];
    }

    /// <summary>Runs while the window is already up; null until it finishes.</summary>
    private Task? _playbackInit;

    /// <summary>
    /// Blocks until the playback engine exists. Every path that touches the player calls
    /// this first, so deferring LibVLC can't turn into a null-reference race.
    ///
    /// In practice it returns instantly: the engine is ready long before a user has found
    /// a track to double-click. It only ever actually waits on a cold first launch, where
    /// the alternative was staring at nothing for twenty seconds anyway.
    /// </summary>
    private void EnsurePlaybackReady()
    {
        try
        {
            _playbackInit?.Wait();
        }
        catch (Exception ex)
        {
            // A failed engine must not take the app with it - playback stays dead, the
            // rest of OrgZ (library, tagging, burning) keeps working.
            _log.Error(ex, "Playback engine failed to initialize");
        }
    }

    private void InitializePlayback()
    {
        _vlc = new();
        _vlc.SetAppId("com.foxcouncil.orgz", App.Version, "Assets/app.ico");
        _vlc.SetUserAgent($"OrgZ {App.Version}", $"orgz{App.Version}/player");

        _player = new(_vlc);
        // LibVLC's own volume is pinned at 100% - the audio tap sits
        // downstream of LibVLC's volume filter, so any attenuation at this
        // level would hit the FFT analyzer and make the VU meter scale
        // with the user's volume slider.  Volume is applied only in the
        // sink bus (MasterVolume) and per-sink, which sit after the tap.
        _player.Volume = 100;

        // Attach the audio tap before any Play() call - LibVLC only routes
        // samples through SetAudioCallbacks for media that start playing
        // after the callbacks were registered.  Once wired, every track the
        // user plays funnels through the sink bus (audible on selected
        // devices) and the FFT analyzer (VU-meter data).
        _audioTap = new OrgZ.Services.AudioVisualization.AudioTap(_audioOutput.Bus);
        _audioTap.Attach(_player);

        // An output that can't open (AirPlay refusing a hi-res track, a receiver that wants
        // pairing) is silence on that device. Say so - the failure used to reach the log only,
        // which from the user's chair is indistinguishable from a broken app.
        _audioOutput.Bus.SinkFailed += (_, failure) => UI(() => HandleSinkFailure(failure));

        // An AirPlay receiver can drive playback back the other way - the Home app's tile has
        // real transport buttons. They land on the same handlers as the lock screen's.
        _audioOutput.Bus.RemoteCommand += (_, command) => UI(() => HandleRemoteCommand(command));

        // The speaker's own volume - the Home app slider, or a touch on the HomePod itself.
        _audioOutput.Bus.RemoteVolume += (_, change) => UI(() => HandleRemoteVolume(change.SinkId, change.Level));

        _audioOutput.LoadAndApplyPersistedSelections();
        UpdateMasterVolume();

        // Ask up front for receivers that ADVERTISE a password we don't have yet. Waiting
        // for the handshake to fail would work, but each wrong-credential attempt counts
        // against the receiver's brute-force lockout, and a HomePod locks pairing for
        // minutes once tripped.
        Helpers.TaskObserver.FireAndForget(PromptForKnownAirPlayPasswordsAsync(), "AirPlay password preflight");

        // Bit-perfect FLAC engine shares the tap (VU/visualizers) and sink bus
        // with the VLC path; its events funnel into the same handlers.
        _flacEngine = new FlacPlaybackEngine(_audioOutput.Bus, _audioTap);
        _flacEngine.TimeChanged += ms => UI(() => HandlePlaybackTime(ms, (long)(CurrentPlayingItem?.Duration?.TotalMilliseconds ?? 0)));
        _flacEngine.EndReached += () => UI(HandlePlaybackEnded);
        _flacEngine.EncounteredError += msg => UI(() =>
        {
            IsPlaybackLoading = false;
            _log.Warning("FlacPlaybackEngine error: {Message}", msg);
            UpdateMainStatus("Couldn't play this — the media source couldn't be opened.");
        });

        // First-audio signal: libvlc fires Playing as soon as it thinks it has
        // a Media to play -- often before any PCM has actually reached the tap.
        // Hook the audio path itself so the loading-state indicator and the
        // empty LCD time labels persist until sound is genuinely flowing.
        _audioTap.AudioStarted += () => UI(() =>
        {
            IsPlaybackLoading = false;
            ApplyPendingDuration();
            // Seek to the saved podcast resume point now that the stream is playable.
            if (_pendingResumeMs is { } resumeMs)
            {
                _pendingResumeMs = null;
                try
                {
                    if (_player.IsSeekable)
                    {
                        _player.Time = resumeMs;
                    }
                }
                catch { /* media not seekable yet - leave at start */ }
            }
        });

        _player.EndReached += (s, e) => UI(HandlePlaybackEnded);

        _player.Paused += (s, e) => UI(UiShowPaused);

        // Don't clear IsPlaybackLoading on Playing -- libvlc fires it the
        // instant it has a Media, which can be well before actual audio
        // reaches the tap. AudioTap.AudioStarted is the precise signal.
        _player.Playing += (s, e) => UI(UiShowPlaying);

        _player.TimeChanged += (s, e) => UI(() => HandlePlaybackTime(e.Time, _player.Length));

        _player.Stopped += (s, e) => UI(() =>
        {
            // A Stop() we issued to switch tracks - keep the loading/barber-pole state and
            // the now-playing UI we just set for the incoming track.
            if (_suppressStoppedLoadingClear)
            {
                _suppressStoppedLoadingClear = false;
                return;
            }

            // The engine stopping VLC as it takes over must not repaint the
            // stopped UI over the track the engine is about to play.
            if (EngineActive)
            {
                return;
            }

            UiShowStopped();
        });

        // libvlc reports open/decode failures asynchronously on this event rather
        // than throwing from Play(), so without a handler a bad source (a dead
        // CDN, an unsupported codec, a redirect chain VLC won't follow) failed
        // silently - the UI just sat there. Surface it.
        _player.EncounteredError += (s, e) => UI(() =>
        {
            IsPlaybackLoading = false;
            _log.Warning("LibVLC EncounteredError — media source could not be opened");
            UpdateMainStatus("Couldn't play this — the media source couldn't be opened.");
        });

        _player.MediaChanged += (s, e) => UI(async () =>
        {
            if (e.Media == null)
            {
                CurrentTrackLine1 = string.Empty;
                CurrentTrackLine2 = string.Empty;

                UpdateMainStatus("Ready");

                return;
            }

            if (CurrentStation != null)
            {
                CurrentTrackDuration = "LIVE";
                CurrentTrackDurationNumber = 0;
                IsSeekEnabled = false;

                CurrentTrackLine1 = CurrentStation.Title ?? "Unknown Station";
                CurrentTrackLine2 = FormatTags(CurrentStation.Tags);

                return;
            }

            // CD tracks set their own display values in ExecutePlayCd - don't overwrite
            if (CurrentPlayingItem?.Source == "cdda")
            {
                if (e.Media != null && e.Media.Duration > 0)
                {
                    _pendingDurationMs = e.Media.Duration;
                    ApplyPendingDuration();
                }
                return;
            }

            // Podcast streams: keep the title/feed we set in PlayPodcastEpisodeStream
            // and pick up the duration libvlc now has. Stop here so the music
            // branch below doesn't overwrite the LCD with "Unknown Title".
            if (_currentPodcastStream is { } ps)
            {
                IsSeekEnabled = true;

                if (e.Media != null && e.Media.ParsedStatus != MediaParsedStatus.Done)
                {
                    _ = await e.Media.Parse();
                }

                // Cache duration -- prefer libvlc's measurement, fall back to
                // the API's reported value for streams libvlc can't measure.
                // AudioStarted writes it to the LCD when audio actually flows.
                long? vlcDurMs = e.Media != null && e.Media.Duration > 0 ? e.Media.Duration : null;
                long apiDurMs = (long)ps.Episode.DurationSec * 1000;
                _pendingDurationMs = vlcDurMs ?? (apiDurMs > 0 ? apiDurMs : null);
                ApplyPendingDuration();

                CurrentTrackLine1 = ps.Episode.Title ?? string.Empty;
                CurrentTrackLine2 = ps.Feed.Title ?? string.Empty;
                return;
            }

            // Device tracks: set metadata from the MediaItem (populated during scan),
            // append the device name to Line2.
            if (CurrentPlayingItem?.Source?.StartsWith("device:") == true)
            {
                IsSeekEnabled = true;

                if (e.Media != null)
                {
                    if (e.Media.ParsedStatus != MediaParsedStatus.Done)
                    {
                        _ = await e.Media.Parse();
                    }
                    _pendingDurationMs = e.Media.Duration > 0
                        ? e.Media.Duration
                        : CurrentPlayingItem?.Duration is { TotalMilliseconds: > 0 } deviceKnown ? (long)deviceKnown.TotalMilliseconds : null;
                    ApplyPendingDuration();
                }

                var mountPath = CurrentPlayingItem!.Source["device:".Length..];
                string deviceLabel = mountPath.TrimEnd('\\', '/');
                if (_connectedDevices.TryGetValue(mountPath, out var dev))
                {
                    deviceLabel = dev.Name;
                }

                CurrentTrackLine1 = CurrentPlayingItem.Title ?? "Unknown Title";
                var devArtist = CurrentPlayingItem.Artist ?? "Unknown Artist";
                var devAlbum = CurrentPlayingItem.Album;
                var devParts = string.IsNullOrWhiteSpace(devAlbum) ? devArtist : $"{devArtist} \u2014 {devAlbum}";
                CurrentTrackLine2 = $"{devParts} ({deviceLabel})";
                return;
            }

            IsSeekEnabled = true;

            if (e.Media.ParsedStatus != MediaParsedStatus.Done)
            {
                _ = await e.Media.Parse();
            }

            // libvlc first, then the item's own known duration - an HTTP share stream
            // often parses with no duration at all, which took the LCD total (and the
            // seek bar with it) even though the catalogue delivered the real length.
            _pendingDurationMs = e.Media.Duration > 0
                ? e.Media.Duration
                : CurrentFileItem?.Duration is { TotalMilliseconds: > 0 } known ? (long)known.TotalMilliseconds : null;
            ApplyPendingDuration();

            CurrentTrackLine1 = CurrentFileItem?.Title ?? "Unknown Title";
            var artist = CurrentFileItem?.Artist ?? "Unknown Artist";
            var album = CurrentFileItem?.Album;
            CurrentTrackLine2 = string.IsNullOrWhiteSpace(album) ? artist : $"{artist} \u2014 {album}";
        });

        // Linux shell integration - GNOME/KDE/XFCE media keys + panel widgets. Failure
        // here is non-fatal: if the session bus isn't reachable the service quietly
        // disables itself and the rest of the app keeps working.
        // Linux shell integration - GNOME/KDE/XFCE media keys + panel widgets. MPRIS's D-Bus
        // connect happens in InitializeAsync; failure there is non-fatal (it disables itself).
        if (OperatingSystem.IsLinux())
        {
            var mpris = new MprisService();
            _nowPlaying = mpris;
            WireNowPlaying(mpris);
            FireAndForget(mpris.InitializeAsync(), "MPRIS init");
        }

        // macOS Control Center / lock screen / media-key widget (MPNowPlayingInfoCenter).
        if (OperatingSystem.IsMacOS())
        {
            var mac = new MacNowPlayingService();
            _nowPlaying = mac;
            WireNowPlaying(mac);
        }
    }

    /// <summary>
    /// Routes an OS now-playing backend's transport events to the player, marshalling each onto
    /// the UI thread - MPRIS fires on the D-Bus worker thread and SMTC on a COM thread, so a
    /// direct call would touch Avalonia bindings cross-thread. A backend that never raises a
    /// given event (e.g. SMTC has no Raise) simply never triggers that handler.
    /// </summary>
    private void WireNowPlaying(INowPlayingIntegration np)
    {
        np.PlayRequested      += () => Dispatcher.UIThread.Post(Play);
        np.PauseRequested     += () => Dispatcher.UIThread.Post(Pause);
        np.PlayPauseRequested += () => Dispatcher.UIThread.Post(ButtonPlayPause);
        np.NextRequested      += () => Dispatcher.UIThread.Post(ButtonNextTrack);
        np.PreviousRequested  += () => Dispatcher.UIThread.Post(ButtonPreviousTrack);
        np.StopRequested      += () => Dispatcher.UIThread.Post(Stop);
        np.RaiseRequested     += () => Dispatcher.UIThread.Post(() =>
        {
            // Show() first: minimize-to-tray Hide()s the window, and a hidden window is
            // unmapped - un-minimizing and activating it does nothing, so a Raise from a
            // second launch (or the desktop's media controls) would leave the user with a
            // running app they cannot get back on screen.
            _window.Show();

            if (_window.WindowState == Avalonia.Controls.WindowState.Minimized)
            {
                _window.WindowState = Avalonia.Controls.WindowState.Normal;
            }
            _window.Activate();
        });
    }

#if WINDOWS
    internal void InitializeSmtc(IntPtr hwnd)
    {
        var smtc = new SmtcNowPlaying();
        // The diagnostics are an acronym plus a raw HRESULT - a log line, not a status bar the
        // user reads. Only the failure gets a plain-language line, and success says nothing.
        if (!smtc.Initialize(hwnd))
        {
            _log.Warning("{Diagnostics}", smtc.Diagnostics ?? "SMTC: Init failed (unknown)");
            UpdateMainStatus("Windows media controls unavailable.");
            smtc.Dispose();
            return;
        }

        _log.Information("{Diagnostics}", smtc.Diagnostics ?? "SMTC: OK");

        // Connecting SMTC as the now-playing surface is what finally feeds it metadata + status,
        // not just the transport buttons WireNowPlaying hooks up.
        WireNowPlaying(smtc);
        _nowPlaying = smtc;
    }

    internal void InitializeThumbBar(IntPtr hwnd)
    {
        _thumbBarService = new TaskbarThumbBarService();
        if (!_thumbBarService.Initialize(hwnd))
        {
            _thumbBarService.Dispose();
            _thumbBarService = null;
            return;
        }

        _thumbBarService.PlayPauseRequested += ButtonPlayPause;
        _thumbBarService.NextRequested += ButtonNextTrack;
        _thumbBarService.PreviousRequested += ButtonPreviousTrack;
    }
#endif

    #region UI Events

    private MediaKind? GetEffectiveKind()
    {
        var kind = SelectedSidebarItem?.Kind;

        if (kind != null)
        {
            return kind;
        }

        // In Favorites or other mixed views, infer from what's playing or selected
        if (CurrentPlayingItem != null && (_player?.IsPlaying == true || _player?.State == LibVLCSharp.Shared.VLCState.Paused))
        {
            return CurrentPlayingItem.Kind;
        }

        return SelectedItem?.Kind;
    }

    [RelayCommand]
    public void ButtonPreviousTrack()
    {
        EnsurePlaybackReady();
        if (_playbackContext == null || !_playbackContext.HasPrevious)
        {
            return;
        }

        _pauseWhenNextTrackStarts = IsPausedNow;
        var prev = _playbackContext.MovePrevious()!;
        ExecutePlayItem(prev);
    }

    /// <summary>
    /// Whether playback is loaded but paused, on either engine. Skipping tracks while paused has to
    /// LAND paused - stepping through a queue looking for something shouldn't start blasting audio.
    /// </summary>
    private bool IsPausedNow => IsPausedState(_flacEngine?.IsPaused == true, EngineActive, _player?.State);

    /// <summary>
    /// Pure form of <see cref="IsPausedNow"/>, so the rule is testable without a live engine.
    /// The FLAC engine's own paused flag wins when it owns playback; libvlc's state is only
    /// meaningful when it does.
    /// </summary>
    internal static bool IsPausedState(bool flacPaused, bool flacEngineActive, LibVLCSharp.Shared.VLCState? vlcState)
    {
        if (flacEngineActive)
        {
            return flacPaused;
        }

        return flacPaused || vlcState == LibVLCSharp.Shared.VLCState.Paused;
    }

    /// <summary>
    /// Set when a track change is initiated from a paused state; consumed by
    /// <see cref="UiShowPlaying"/>, which pauses the newly started track.
    ///
    /// It pauses on the "playing" transition rather than before starting, because neither engine
    /// can load a track without starting it - and that transition fires once libvlc has the media
    /// but before audio reaches the tap, so nothing is actually heard.
    /// </summary>
    private bool _pauseWhenNextTrackStarts;

    [RelayCommand]
    public void ButtonPlayPause()
    {
        EnsurePlaybackReady();
        UI(() =>
        {
            if (_player == null)
            {
                return;
            }

            // An explicit press outranks a pending skip-while-paused. Without this, a track that
            // never reaches "playing" (missing file, dead share) would leave the flag armed and
            // silently pause whatever the user pressed play on next.
            _pauseWhenNextTrackStarts = false;

            // Pause / resume / stop always acts on what is actually playing - never on the
            // active view. A pause request must never be ignored because the user happens to
            // be looking at a different page (e.g. browsing Podcasts while music plays). The
            // view's kind is consulted only to decide what to START when nothing is loaded.

            // Radio can't truly pause a live stream - toggling stops it (and re-plays to
            // resume). Captured to a local so nullable flow holds; guarded so a podcast is
            // never mistaken for radio.
            var station = CurrentStation;
            if (station != null && _currentPodcastStream == null)
            {
                if (_player.IsPlaying)
                {
                    Stop();
                }
                else
                {
                    PlayRadioStation(station);
                }
                return;
            }

            // Anything playing - music, CD, device track, or podcast - pauses. No exceptions,
            // no view checks. This is the line that must never be skipped.
            if (EngineActive ? _flacEngine!.IsPlaying : _player.IsPlaying)
            {
                Pause();
                return;
            }

            // Paused or stopped with a track still loaded - resume / restart it.
            if (_player.State == LibVLCSharp.Shared.VLCState.Paused
                || _flacEngine?.IsPaused == true
                || _currentPodcastStream != null
                || CurrentFileItem != null
                || CurrentPlayingItem != null)
            {
                Play();
                return;
            }

            // Nothing loaded - start something based on the active view / selection.
            var kind = GetEffectiveKind();
            if (kind == MediaKind.Radio)
            {
                if (SelectedItem?.Kind == MediaKind.Radio)
                {
                    PlayRadioStation(SelectedItem);
                }
                else if (FilteredItems.Count > 0)
                {
                    PlayRadioStation(FilteredItems[0]);
                }
                return;
            }

            if (SelectedItem?.Kind == MediaKind.Music)
            {
                PlayMusicItem(SelectedItem);
            }
            else if (FilteredItems.Count > 0)
            {
                PlayMusicItem(FilteredItems[0]);
            }
        });
    }

    [RelayCommand]
    public void ButtonNextTrack()
    {
        EnsurePlaybackReady();
        if (_playbackContext == null || !_playbackContext.HasNext)
        {
            return;
        }

        _pauseWhenNextTrackStarts = IsPausedNow;
        var next = _playbackContext.MoveNext()!;
        ExecutePlayItem(next);
    }

    [RelayCommand]
    private void ExitApplication()
    {
        // Shutdown rather than Close: the minimize-to-tray Closing intercept only
        // swallows plain window closes, and an explicit File > Exit must actually exit.
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
            return;
        }

        _window.Close();
    }

    private const string GitHubUrl = "https://github.com/FoxCouncil/OrgZ";

    /// <summary>The manual for this build. Docs deploy versioned (mike) alongside every release, so
    /// an installed app links to its own version's pages forever - never a newer release's docs.</summary>
    internal static string ManualUrl =>
        typeof(MainWindowViewModel).Assembly.GetName().Version is { } v
            ? $"https://foxcouncil.github.io/OrgZ/{v.Major}.{v.Minor}.{v.Build}/"
            : "https://foxcouncil.github.io/OrgZ/latest/";

    // ── Updates (Help menu) ───────────────────────────────────

    private readonly UpdateService _updates = new();

    /// <summary>
    /// The Help-menu entry's text: "Check for Updates..." until a newer release is known,
    /// then "There are updates...". Nothing downloads or elevates until the user picks it.
    /// </summary>
    [ObservableProperty]
    private string _updateMenuHeader = UpdateService.CheckLabel;

    /// <summary>Quiet startup check - no download, no prompt, just the menu label.</summary>
    internal async Task RefreshUpdateStatusAsync()
    {
        var available = await _updates.CheckAsync();
        UI(() => UpdateMenuHeader = UpdateService.MenuLabel(available));
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        // Picking the menu IS the go-ahead, so a found update installs immediately and
        // Windows' elevation prompt serves as the "install this?" confirmation. What it
        // must never do is nothing: the first version only wrote to the status bar, which
        // on a machine that was already up to date was indistinguishable from a dead menu
        // item - reported as exactly that.
        if (_updates.PendingVersion is null)
        {
            UpdateMainStatus("Checking for updates...");
            var found = await _updates.CheckAsync();
            UpdateMenuHeader = UpdateService.MenuLabel(found);

            if (!found)
            {
                UpdateMainStatus("OrgZ is up to date.");
                await new Views.ConfirmDialog("Updates", $"OrgZ {App.Version} is up to date.", "OK", showCancel: false)
                    .ShowDialog(_window);
                return;
            }
        }

        var version = _updates.PendingVersion;
        UpdateMainStatus($"Downloading OrgZ {version}...");

        if (await _updates.ApplyAsync() is { } error)
        {
            UpdateMainStatus($"Update failed: {error}");
            await new Views.ConfirmDialog("Update failed", error, "OK", showCancel: false).ShowDialog(_window);
        }
    }

    // Receivers we've already prompted for this session, so a failing sink that keeps
    // retrying can't stack password dialogs on top of each other.
    private readonly HashSet<string> _airPlayPrompted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reports an output that stopped working, and offers a password when that's the fix.
    /// </summary>
    private void HandleSinkFailure(Services.AudioOutput.SinkFailure failure)
    {
        UpdateMainStatus(failure.Reason);

        if (failure.NeedsPassword && _airPlayPrompted.Add(failure.SinkId))
        {
            Helpers.TaskObserver.FireAndForget(
                PromptForAirPlayPasswordAsync(failure.SinkId, failure.DisplayName, rejected: true),
                "AirPlay password prompt");
        }
    }

    /// <summary>Asks for the password of every selected receiver that advertises needing one.</summary>
    private async Task PromptForKnownAirPlayPasswordsAsync()
    {
        // Discovery is a background mDNS sweep; without a beat to land, the first pass sees
        // an empty device list and asks for nothing.
        await Task.Delay(TimeSpan.FromSeconds(3));

        foreach (var device in _audioOutput.AirPlayDevicesNeedingPassword())
        {
            if (_airPlayPrompted.Add(device.QualifiedId))
            {
                await PromptForAirPlayPasswordAsync(device.QualifiedId, device.DisplayName, rejected: false);
            }
        }
    }

    /// <summary>
    /// Prompts for a receiver's AirPlay password, stores it if asked, and rebuilds the sink
    /// so the handshake runs again with it.
    /// </summary>
    private async Task PromptForAirPlayPasswordAsync(string qualifiedId, string displayName, bool rejected)
    {
        if (_window is null)
        {
            return;
        }

        var (_, deviceId) = Services.AudioOutput.AudioDeviceInfo.SplitQualified(qualifiedId);
        var dialog = new Views.AirPlayPasswordDialog(displayName, rejected ? null : Services.AudioOutput.AirPlay.AirPlayCredentials.Get(deviceId));
        var password = await dialog.ShowDialog<string?>(_window);

        if (string.IsNullOrEmpty(password))
        {
            UpdateMainStatus($"{displayName} needs an AirPlay password to play.");
            return;
        }

        if (dialog.Remember && Services.AudioOutput.AirPlay.AirPlayCredentials.CanRemember)
        {
            Services.AudioOutput.AirPlay.AirPlayCredentials.Set(deviceId, password);
        }
        else
        {
            // Either the user declined to save it, or this platform has no secret store.
            // Keep it for the session either way - but never claim to have saved it.
            Services.AudioOutput.AirPlay.AirPlayCredentials.SetForSession(deviceId, password);

            if (dialog.Remember)
            {
                UpdateMainStatus($"{displayName}: no secure store on this system, so the password won't be remembered.");
            }
        }

        // The password is baked into the sink at construction, so retrying means rebuilding
        // it. Allow a fresh prompt afterwards in case this password is wrong too.
        _airPlayPrompted.Remove(qualifiedId);

        if (_audioOutput.RecreateSink(qualifiedId))
        {
            UpdateMainStatus($"Reconnecting to {displayName}...");
        }
    }

    /// <summary>
    /// Applies a volume level the OUTPUT DEVICE set - the Home app's slider or the HomePod's
    /// own touch controls.
    ///
    /// This is the DEVICE's own level, which is the per-sink volume - not CurrentVolume, the
    /// app-wide gain applied to samples before they reach any output. Driving the master
    /// slider from it would attenuate the audio a second time on top of the attenuation the
    /// speaker just applied itself, so the sink absorbs it and the app's slider stays put.
    ///
    /// Nothing is echoed back either: a reply would land on the device that just set it and
    /// the two would chase each other for as long as the user kept dragging.
    /// </summary>
    private void HandleRemoteVolume(string sinkId, float level)
    {
        _log.Information("Remote volume from {Sink}: {Percent}%", sinkId, (int)Math.Round(Math.Clamp(level, 0f, 1f) * 100));

        // The sink has already taken the level - it has to, or the next thing the app sends
        // would fight what the user just did on the speaker. What remains is to REMEMBER it,
        // so the output picker opens showing where the speaker actually is and the level
        // survives a restart. Deferred: someone dragging a slider on their phone produces a
        // stream of these, and settings.json does not need rewriting per pixel.
        _audioOutput.SavePersistedSelections(deferred: true);
    }

    /// <summary>
    /// Applies a transport command that came from an output device rather than from this app -
    /// currently an AirPlay receiver's Home-app buttons.
    ///
    /// Unknown codes are logged, not guessed at. Apple's command vocabulary is larger than the
    /// part anyone has documented, and acting on a misread command is worse than ignoring it -
    /// the log is how the set below grows.
    /// </summary>
    private void HandleRemoteCommand(string command)
    {
        switch (command)
        {
            case "play":
            {
                Play();
            }
            break;

            // "paus" is the event channel's spelling, "pause" is DACP's - the receiver uses
            // whichever it likes and both mean the same thing.
            case "paus":
            case "pause":
            {
                Pause();
            }
            break;

            // "plps" is the event channel's toggle, "playpause" is DACP's.
            case "plps":
            case "playpause":
            {
                ButtonPlayPause();
            }
            break;

            case "stop":
            {
                Stop();
            }
            break;

            // Skip has three spellings across the two control channels, and the four-letter
            // ones are not guessable from the others: a receiver sends "nitm"/"pitm" over the
            // event channel and "nextitem"/"previtem" over DACP.
            case "next":
            case "nitm":
            case "nextitem":
            {
                ButtonNextTrack();
            }
            break;

            case "prev":
            case "pitm":
            case "previtem":
            {
                ButtonPreviousTrack();
            }
            break;

            default:
            {
                _log.Information("Remote command not handled: {Command}", command);
            }
            break;
        }
    }

    /// <summary>
    /// Announces the current track to the OS media surface AND to the audio sinks.
    ///
    /// Both need it for the same reason the lock screen does, but a network sink needs one
    /// thing the OS doesn't: the duration. An AirPlay receiver schedules the end of the
    /// stream from the track length, so a speaker that is never told stops pulling audio
    /// partway through every track.
    /// </summary>
    private void PushNowPlaying(NowPlayingMetadata metadata)
    {
        _nowPlaying?.SetMetadata(metadata);
        _audioOutput.Bus.SetTrackInfo(metadata.Title, metadata.Artist, metadata.Album, metadata.Duration, metadata.ArtBytes);
    }

    [RelayCommand]
    private void OpenManual() => HtmlInlinesBuilder.OpenUrl(ManualUrl);

    [RelayCommand]
    private void OpenGitHub() => HtmlInlinesBuilder.OpenUrl(GitHubUrl);

    [RelayCommand]
    private void ReportBug() => HtmlInlinesBuilder.OpenUrl($"{GitHubUrl}/issues/new");

    [RelayCommand]
    internal async Task ShowAbout()
    {
        var logo = new Avalonia.Controls.Image
        {
            Source = new Bitmap(AssetLoader.Open(new Uri("avares://Orgz/Assets/app-icon-1024.png"))),
            Width = 64,
            Height = 64,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var dialog = new Window
        {
            Title = "About OrgZ",
            MinWidth = 300,
            MinHeight = 260,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
            Classes = { "orgzDialog" },
            Content = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    logo,
                    new TextBlock
                    {
                        Text = "OrgZ",
                        FontSize = 24,
                        FontWeight = FontWeight.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 8, 0, 0)
                    },
                    new TextBlock
                    {
                        Text = $"Version {App.Version}",
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 4, 0, 0)
                    },
                    new TextBlock
                    {
                        Text = "Made Because I Love A \ud83d\udc2f!",
                        FontSize = 14,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 12, 0, 0)
                    },
                    new TextBlock
                    {
                        Text = "\u00a9 2026 FoxCouncil",
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 12, 0, 0)
                    },
                    new Button
                    {
                        Content = "github.com/FoxCouncil/OrgZ",
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 8, 0, 0),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Foreground = new SolidColorBrush(Color.Parse("#4A9EFF")),
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                        Padding = new Thickness(0),
                    },
                }
            }
        };

        var ghButton = (Button)((StackPanel)dialog.Content!).Children[^1];
        ghButton.Click += (_, _) =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/FoxCouncil/OrgZ",
                UseShellExecute = true
            });
        };

        await dialog.ShowDialog(_window);
    }

    [RelayCommand]
    internal async Task ShowMediaInfo()
    {
        if (SelectedItem == null)
        {
            return;
        }

        var dialog = new Views.MediaInfoDialog(SelectedItem, FilteredItems);
        var result = await dialog.ShowDialog<bool?>(_window);

        if (result == true && dialog.ItemChanged)
        {
            ApplyFilter();
            UpdateData();
        }
    }

    /// <summary>
    /// Shared entry point for the Get Info dialog when the caller has an
    /// arbitrary MediaItem that isn't part of the active <see cref="FilteredItems"/>
    /// list (the podcast feed-detail view, for example, drives off its own
    /// collection). Same dialog as Music / Radio "Get Info" so the action means
    /// the same thing everywhere.
    /// </summary>
    internal async Task ShowMediaInfoForItemAsync(MediaItem item)
    {
        var dialog = new Views.MediaInfoDialog(item, [item]);
        await dialog.ShowDialog<bool?>(_window);
    }

    [RelayCommand]
    internal async Task ShowSettings()
    {
        var dialog = new Views.SettingsDialog(_allItems);
        // The main window can be hidden when the mini-player is up - Avalonia 12 throws
        // "Cannot show window with non-visible owner" if we use it as the dialog parent.
        // Fall back to whichever visible top-level Avalonia knows about.
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
                     as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                    ?.Windows.FirstOrDefault(w => w.IsVisible) ?? _window;
        var result = await dialog.ShowDialog<bool?>(owner);

        if (result != true)
        {
            return;
        }

        // Sidebar composition depends on settings - refresh in case one was toggled
        RebuildLibraryItems();

        // Sound Check (OrgZ.NormalizeVolume) may have just been toggled - re-apply loudness
        // normalization to the CURRENT track so the difference is audible the moment the dialog
        // closes, not only on the next track.
        SetupNormalization(CurrentPlayingItem);

        // Shuffle by Song/Album may have changed - a live shuffled queue reorders to match.
        if (ShuffleMode == ShuffleMode.On)
        {
            _playbackContext?.SetShuffle(true, ShuffleByPreference);
        }

        if (dialog.SettingsReset)
        {
            Stop();
            _window.Title = $"OrgZ v{App.Version} - [No folder selected]";
            return;
        }

        if (dialog.FolderChanged)
        {
            Stop();
            ClearPlayback();

#if WINDOWS
            _thumbBarService?.SetPlayingState(false);
#endif
            _nowPlaying?.SetPlaybackStatus("Stopped");

            _allItems.RemoveAll(i => i.Kind == MediaKind.Music);
            FilteredItems = [];

            _window.Title = App.FolderPath != string.Empty
                ? $"OrgZ v{App.Version} - {App.FolderPath}"
                : $"OrgZ v{App.Version} - [No folder selected]";

            _folderWatcher?.Stop();

            if (App.FolderPath != string.Empty)
            {
                await ScanAndAnalyzeLibraryAsync();
                StartFolderWatcher();
            }
        }

    }

    internal async Task ShowMessageLog()
    {
        var dialog = new Views.MessageLogDialog(Messages, "Errors");
        await dialog.ShowDialog(_window);
        StatusBar.ErrorCount = Messages.Count;
    }

    private void PlayItem(MediaItem item)
    {
        switch (item.Kind)
        {
            // Podcasts (and audiobooks) on a device are local files - same play path as music.
            // Streamed library podcasts go through PodcastsPanel, not here.
            case MediaKind.Music:
            case MediaKind.Podcast:
            case MediaKind.Audiobook:
            {
                PlayMusicItem(item);
                break;
            }

            case MediaKind.Radio:
            {
                PlayRadioStation(item);
                break;
            }
        }
    }

    private void ExecutePlayItem(MediaItem item)
    {
        // CD tracks are MediaKind.Music but use StreamUrl instead of FilePath
        if (item.Source == "cdda")
        {
            ExecutePlayCd(item);
            return;
        }

        // Device tracks (iPod/Rockbox) are MediaKind.Music with Source="device:{mountPath}"
        if (item.Source?.StartsWith("device:") == true)
        {
            ExecutePlayDeviceTrack(item);
            return;
        }

        switch (item.Kind)
        {
            // Local audiobooks (and any local podcast file) are files like music - without these
            // cases the auto-advance path (EndReached → MoveNext) silently dropped them.
            case MediaKind.Music:
            case MediaKind.Podcast:
            case MediaKind.Audiobook:
            {
                ExecutePlayMusic(item);
                break;
            }

            case MediaKind.Radio:
            {
                ExecutePlayRadio(item);
                break;
            }
        }
    }

    /// <summary>
    /// Plays a track from a connected device (iPod/Rockbox). Delegates to
    /// ExecutePlayMusic for the actual playback - the MediaChanged handler detects
    /// device sources and appends the device label to Line2.
    /// </summary>
    private void ExecutePlayDeviceTrack(MediaItem item)
    {
        ExecutePlayMusic(item);
    }

    private void ExecutePlayCd(MediaItem track)
    {
        // The optical drive is held exclusively by the elevated rip helper during a
        // rip - don't try to play off it, and let the rip status own the play column.
        if (IsBusy)
        {
            UpdateMainStatus("Can't play the CD while it's being imported.");
            return;
        }

        SelectedItem = track;

        CurrentTrackLine1 = track.Title ?? "Unknown Track";
        CurrentTrackLine2 = !string.IsNullOrWhiteSpace(track.Artist)
            ? (string.IsNullOrWhiteSpace(track.Album) ? track.Artist : $"{track.Artist} \u2014 {track.Album}")
            : track.Album ?? "";
        CurrentAlbumArt = _cdCoverArt;

        PushNowPlaying(new NowPlayingMetadata(track.Title, track.Artist, track.Album, Duration: track.Duration, ArtBytes: _cdCoverArtBytes));

        var previousRadio = TakeRadioStream();
        var previousMedia = _currentMedia;
        var previousHandler = _currentMediaMetaHandler;
        _currentMediaMetaHandler = null;
        _currentMedia = new LibVLCSharp.Shared.Media(_vlc, track.StreamUrl!, LibVLCSharp.Shared.FromType.FromLocation);
        if (track.Track.HasValue)
        {
            _currentMedia.AddOption($":cdda-track={track.Track.Value}");
        }
        // CDDA reads from the optical drive at ~1× audio speed (~176 KB/s on a CD),
        // and on macOS we route through cddafs's synthetic AIFFs which add SCSI seek
        // overhead on top. libvlc's default file-caching (~300 ms) isn't enough - the
        // playback stalls between buffer refills. 3 s headroom is comfortable.
        if (track.Source == "cdda")
        {
            _currentMedia.AddOption(":file-caching=3000");
            _currentMedia.AddOption(":disc-caching=3000");
        }

        NewPlaybackEpoch();
        BeginPlayback();
        // CD track duration comes from the TOC, not libvlc - restore it
        // after BeginPlayback clears the LCD time labels so the total time
        // shows up immediately instead of waiting on MediaChanged.
        CurrentTrackDuration = track.Duration?.ToString(@"m\:ss") ?? "--:--";
        CurrentTrackDurationNumber = (long)(track.Duration?.TotalMilliseconds ?? 0);
        // When the total is known up front, seed the elapsed tile at 0:00 too so both
        // LCD labels populate immediately instead of the elapsed staying blank until
        // the first position tick.
        if (track.Duration.HasValue)
        {
            CurrentTrackTime = FormatHelper.FormatDurationCompact(0);
            CurrentTrackTimeNumber = 0;
        }
        if (!_player.Play(_currentMedia))
        {
            // libvlc's Play returns false on a failed native start - a throw-less failure.
            _log.Error("libvlc Play returned false for {Title}", track.Title);
        }
        DeferDispose(previousMedia, previousHandler, previousRadio);

        ButtonPlayPauseIcon = ICON_PAUSE;
        ButtonPlayPausePadding = new Avalonia.Thickness(0);
        IsSeekEnabled = true;
        UpdateNavigationButtons();
    }

    public void DataGridRowDoubleClick()
    {
        if (SelectedItem == null)
        {
            return;
        }

        PlayItem(SelectedItem);
    }

    // Per-track volume adjustment (positive = boost quiet tracks, negative =
    // tame loud ones).  Combined with the global volume into a single
    // MasterVolume on the sink bus; LibVLC stays at 100 so the FFT analyzer
    // always sees the source's real amplitude.
    private double _perTrackMultiplier = 1.0;

    internal void CurrentVolumeChanged()
    {
        UpdateMasterVolume();
        Settings.Set("OrgZ.Volume", (int)CurrentVolume);
        // Deferred: a volume drag calls this per slider tick, and Save is a synchronous
        // whole-file write on the UI thread - dozens of disk writes per drag. The gain
        // change above is immediate; only the persistence trails.
        Settings.SaveDeferred();
    }

    private void UpdateMasterVolume()
    {
        var gain = (CurrentVolume / 100.0) * _perTrackMultiplier;
        _audioOutput.Bus.MasterVolume = (float)Math.Clamp(gain, 0.0, 1.0);
    }

    [RelayCommand]
    internal void MuteVolume()
    {
        if (CurrentVolume > 0)
        {
            _previousVolume = CurrentVolume;
            CurrentVolume = 0;
        }
        else
        {
            CurrentVolume = _previousVolume > 0 ? _previousVolume : 100;
        }

        CurrentVolumeChanged();
    }

    [RelayCommand]
    internal void MaxVolume()
    {
        CurrentVolume = 100;
        CurrentVolumeChanged();
    }

    internal void CurrentTrackTimeNumberPointerPressed()
    {
        isSeeking = true;
    }

    internal void CurrentTrackTimeNumberPointerReleased()
    {
        isSeeking = false;
        if (EngineActive)
        {
            _flacEngine!.SeekMs(CurrentTrackTimeNumber);
            return;
        }
        _player.Time = CurrentTrackTimeNumber;
    }

    #endregion

    #region Playback Controls

    internal void PlayMusicItem(MediaItem? file)
    {
        EnsurePlaybackReady();

        // Accepts Music, downloaded Podcast, and Audiobook files -- all local
        // paths libvlc opens via FromType.FromPath; the only difference is
        // metadata routing handled downstream.
        if (_player == null || file == null || (file.Kind != MediaKind.Music && file.Kind != MediaKind.Podcast && file.Kind != MediaKind.Audiobook))
        {
            return;
        }
        _currentPodcastStream = null;

        // CD tracks use StreamUrl, regular music uses FilePath
        if (file.Source == "cdda")
        {
            PlayCdTrack(file);
            return;
        }

        // A mounted share's tracks have no FilePath at all - they play from the
        // sharing host over HTTP. Everything downstream (queue, LCD, per-track
        // options) is identical to a local file.
        if (PlayableLocation(file) is null)
        {
            return;
        }

        UI(() =>
        {
            // The user picked this track in this view, so this view is where "go to current song"
            // should return them - whether or not the queue below gets rebuilt. Setting it only on
            // the rebuild path left the origin pointing at wherever the queue happened to be built
            // last, so playing from Favorites and clicking the artwork took you to Music.
            _playbackOriginViewKey = SelectedSidebarItem?.ViewConfigKey;

            // Reuse the existing context only when the current view's filter
            // produces the same source list -- so a search that narrows the
            // visible tracks rebuilds the queue against the filtered set
            // (otherwise shuffle would pick from the wider pre-search list).
            if (_playbackContext != null
                && _playbackContext.MatchesSource(FilteredItems)
                && _playbackContext.JumpTo(file))
            {
                OnPropertyChanged(nameof(PlaybackContextUpcoming));
                ExecutePlayMusic(file);
                return;
            }

            _playbackContext?.Release();
            _playbackContext = new PlaybackContext(FilteredItems, file, ShuffleMode == ShuffleMode.On, ShuffleByPreference) { RepeatMode = RepeatMode };
            OnPropertyChanged(nameof(PlaybackContextUpcoming));
            ExecutePlayMusic(file);
        });
    }

    internal void PlayRadioStation(MediaItem? station)
    {
        EnsurePlaybackReady();
        if (_player == null || station == null || station.Kind != MediaKind.Radio || string.IsNullOrEmpty(station.StreamUrl))
        {
            return;
        }
        _currentPodcastStream = null;

        // Debounce rapid clicks: cancel any pending switch, schedule a fresh one.
        // 120 ms is short enough to feel responsive on deliberate clicks, long
        // enough to coalesce double-clicks and mouse-wheel scrubs through the list.
        var freshCts = new CancellationTokenSource();
        var previousCts = Interlocked.Exchange(ref _radioSwitchCts, freshCts);
        previousCts?.Cancel();
        previousCts?.Dispose();
        var token = freshCts.Token;

        _ = Task.Delay(TimeSpan.FromMilliseconds(120), token).ContinueWith(t =>
        {
            if (t.IsCanceled || token.IsCancellationRequested)
            {
                return;
            }

            UI(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                _playbackOriginViewKey = SelectedSidebarItem?.ViewConfigKey;

                if (_playbackContext != null
                    && _playbackContext.MatchesSource(FilteredItems)
                    && _playbackContext.JumpTo(station))
                {
                    OnPropertyChanged(nameof(PlaybackContextUpcoming));
                    ExecutePlayRadio(station);
                    return;
                }

                _playbackContext?.Release();
                _playbackContext = new PlaybackContext(FilteredItems, station, ShuffleMode == ShuffleMode.On, ShuffleByPreference) { RepeatMode = RepeatMode };
                // See PlayMusicItem: the origin is the view the user picked in.
                OnPropertyChanged(nameof(PlaybackContextUpcoming));
                ExecutePlayRadio(station);
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Adds the LibVLC visualizer option to the media when the
    /// <c>OrgZ.Visualizer.Enabled</c> setting is on.  Values match the libvlc
    /// <c>--audio-visual</c> argument: <c>spectrum</c>, <c>scope</c>, <c>vumeter</c>,
    /// <c>spectrometer</c>, <c>goom</c>.  LibVLC opens its own render window when
    /// a visualizer is attached to audio-only media.
    /// </summary>
    private static void ApplyVisualizerOption(Media media)
    {
        if (!Settings.Get("OrgZ.Visualizer.Enabled", false))
        {
            return;
        }

        var name = Settings.Get("OrgZ.Visualizer.Name", "spectrum");
        media.AddOption($":audio-visual={name}");
    }

    /// <summary>
    /// iTunes "Sound Check": level playback loudness across tracks by applying each track's
    /// ReplayGain as a runtime gain on OrgZ's own sink bus. Because it's a live multiplier on the
    /// PCM - not a VLC media option, which OrgZ's audio tap bypasses - it takes effect immediately,
    /// including when the setting is toggled mid-track, and is deterministic. A local track with no
    /// measured gain yet is analyzed in the background; the instant its gain lands it's applied to
    /// the still-playing track and tagged so next play is precise. Radio/podcast streams carry no
    /// gain and play unmodified.
    /// </summary>
    private void SetupNormalization(MediaItem? item)
    {
        var enabled = Settings.Get("OrgZ.NormalizeVolume", false);
        _audioOutput.Bus.NormalizationGain = NormalizationGain(enabled, item?.ReplayGainTrackGainDb);

        if (enabled
            && item is { FilePath: { } path } && item.Kind is MediaKind.Music or MediaKind.Audiobook
            && item.Source?.StartsWith("device:", StringComparison.Ordinal) != true   // never rewrite a file on a synced device - those bytes belong to its own database
            && !item.HasReplayGain && File.Exists(path) && ResolveFfmpeg() is { } ffmpeg)
        {
            _ = Task.Run(async () =>
            {
                var gain = await ReplayGainService.ComputeAndTagAsync(path, ffmpeg);
                if (gain is { } g)
                {
                    UI(() =>
                    {
                        item.ReplayGainTrackGainDb = g;
                        FireAndForget(Task.Run(() => MediaCache.UpdateReplayGain(item.Id, g)), "replay-gain persist");
                        // Still playing this track (and Sound Check still on)? Apply its just-measured
                        // gain without waiting for a replay.
                        if (ReferenceEquals(CurrentPlayingItem, item))
                        {
                            _audioOutput.Bus.NormalizationGain = NormalizationGain(Settings.Get("OrgZ.NormalizeVolume", false), item.ReplayGainTrackGainDb);
                        }
                    });
                }
            });
        }
    }

    /// <summary>
    /// The linear playback-gain multiplier for a track under Sound Check: its ReplayGain converted
    /// from dB, capped at +6 dB of boost so a quiet track can't slam into clipping. Returns 1.0 when
    /// normalization is off or the track carries no measured gain (radio, or a file not yet analyzed).
    /// Pure, so the dB→linear conversion + cap are unit-testable.
    /// </summary>
    internal static float NormalizationGain(bool soundCheckEnabled, double? replayGainDb)
    {
        if (!soundCheckEnabled || replayGainDb is not { } db)
        {
            return 1f;
        }
        return (float)Math.Pow(10.0, Math.Min(db, 6.0) / 20.0);
    }

    private void ExecutePlayMusic(MediaItem file)
    {
        SelectedItem = file;

        // Don't dispose - Avalonia's ref-counted bitmap lifecycle handles cleanup.
        // Explicit Dispose() while a render pass is in flight causes ObjectDisposedException.
        // Device (iPod) tracks keep art in the iPod's ArtworkDB keyed by dbid - read it
        // natively there first, then fall back to any embedded picture in the file.
        byte[]? artBytes = null;
        if (file.Source?.StartsWith("device:") == true && file.Dbid is { } dbid && dbid != 0)
        {
            artBytes = IPodArtworkReader.LoadThumbnail(file.Source["device:".Length..], dbid);
        }

        // One TagLib open serves both the art and the engine's format probe below.
        (int SampleRate, int? BitDepth, int Channels)? probed = null;
        if (file.FilePath is { Length: > 0 } artPath)
        {
            var read = ArtworkSource.ReadArtAndProperties(artPath);
            artBytes ??= read.Art;
            probed = (read.SampleRate, read.BitDepth, read.Channels);
        }

        CurrentAlbumArt = artBytes != null ? ArtworkSource.BitmapFromBytes(artBytes) : null;

        PushNowPlaying(new NowPlayingMetadata(file.Title, file.Artist, file.Album, Duration: file.Duration, ArtUri: string.IsNullOrEmpty(file.FilePath) ? null : new Uri(file.FilePath).AbsoluteUri, ArtBytes: artBytes));

        var previousRadio = TakeRadioStream();
        var previousMedia = _currentMedia;
        var previousHandler = _currentMediaMetaHandler;
        _currentMediaMetaHandler = null;

        SetupNormalization(file);

        // Bit-perfect route: local FLAC decodes at native bit depth / sample
        // rate through the engine. VLC keeps the track when a per-track EQ
        // preset or the VLC visualizer window is in play - both are VLC-side
        // DSP that the bit-exact path by definition bypasses.
        var useEngine = _flacEngine != null
            && FlacPlaybackEngine.CanPlay(file)
            && string.IsNullOrEmpty(file.EqPreset)
            && !Settings.Get("OrgZ.Visualizer.Enabled", false);

        // The engine parses the decoder's raw output with the file's exact
        // bit depth / rate / channel count - guessing corrupts audio. Items
        // scanned before BitDepth existed take the numbers from the single read
        // above; if they can't be established, VLC keeps the track.
        if (useEngine && (file.BitDepth is null or 0 || file.SampleRate is null or 0 || file.AudioChannels is null or 0))
        {
            if (probed is { } p && p.SampleRate > 0)
            {
                file.SampleRate = p.SampleRate;
                file.BitDepth = p.BitDepth;
                file.AudioChannels = p.Channels;
            }
            else
            {
                useEngine = false;
            }
        }
        if (useEngine && (file.BitDepth is not (16 or 24 or 32) || file.SampleRate is null or 0 || file.AudioChannels is not (1 or 2)))
        {
            useEngine = false;
        }

        if (useEngine)
        {
            NewPlaybackEpoch();
            BeginPlayback(showLoading: false);
            _lastAudiobookSaveMs = 0;

            // VLC hands over: stop it quietly so its Stopped event doesn't
            // repaint the UI over the incoming track.
            _currentMedia = null;
            if (_player.State is VLCState.Opening or VLCState.Buffering or VLCState.Playing or VLCState.Paused)
            {
                _suppressStoppedLoadingClear = true;
                _ = ThreadPool.QueueUserWorkItem(_ => _player.Stop());
            }

            long resumeMs = file.Kind == MediaKind.Audiobook && file.Source == null && file.LastPositionMs > 10_000 ? file.LastPositionMs : 0;

            // The MediaChanged handler never fires on this path - set the LCD
            // and duration directly from the item's scanned metadata.
            IsSeekEnabled = true;
            _pendingDurationMs = file.Duration is { } dur && dur.TotalMilliseconds > 0 ? (long)dur.TotalMilliseconds : null;
            ApplyPendingDuration();
            CurrentTrackLine1 = file.Title ?? "Unknown Title";
            var engineArtist = file.Artist ?? "Unknown Artist";
            CurrentTrackLine2 = string.IsNullOrWhiteSpace(file.Album) ? engineArtist : $"{engineArtist} — {file.Album}";

            _flacEngine!.Play(file.FilePath!, file.SampleRate!.Value, file.AudioChannels!.Value, file.BitDepth!.Value, resumeMs);
            UiShowPlaying();
            DeferDispose(previousMedia, previousHandler, previousRadio);
        }
        else
        {
            // A share's tracks live on another machine: open the HTTP location rather
            // than a path, and give libvlc real buffer headroom - a LAN hop is not a
            // disk read, and a busy host or a wifi dip must not stutter the track.
            var fromShare = Services.Sharing.ShareDiscovery.IsShareItem(file);
            if (fromShare)
            {
                _currentMedia = new Media(_vlc, file.StreamUrl!, FromType.FromLocation);
                _currentMedia.AddOption($":network-caching={StreamingBufferMs(1500)}");
            }
            else
            {
                _currentMedia = new Media(_vlc, file.FilePath!, FromType.FromPath);
            }
            ApplyVisualizerOption(_currentMedia);

            // Local file - opens instantly, so skip the barber pole. A share has to
            // reach across the LAN first, so it earns one.
            var epoch = NewPlaybackEpoch();
            BeginPlayback(showLoading: fromShare);

            if (fromShare)
            {
                FireAndForget(LoadShareArtAsync(file, epoch), "share art fetch");
            }

            // Audiobooks resume where they left off - same applied-once-audio-starts machinery as
            // podcast resume. Skip a barely-started position (re-seeking to 0:04 is noise, not resume).
            if (file.Kind == MediaKind.Audiobook && file.Source == null && file.LastPositionMs > 10_000)
            {
                _pendingResumeMs = file.LastPositionMs;
            }
            _lastAudiobookSaveMs = 0;

            if (!_player.Play(_currentMedia))
            {
                _log.Error("libvlc Play returned false for {Title}", file.Title);
            }
            DeferDispose(previousMedia, previousHandler, previousRadio);
        }

        ApplyPerTrackOptions(file);

        file.LastPlayed = DateTime.UtcNow;
        file.PlayCount++;
        MediaCache.SetLastPlayed(file.Id, file.LastPlayed.Value);
        MediaCache.IncrementPlayCount(file.Id);
        RememberLastTrack(file);

        UpdateNavigationButtons();
    }

    /// <summary>
    /// Where a track's audio actually comes from: a local path, or a share's HTTP stream
    /// URL. Null means there's nothing to play - and a share track (no FilePath, StreamUrl
    /// set) reading as unplayable is exactly what made mounted shares silent.
    /// </summary>
    internal static string? PlayableLocation(MediaItem item)
    {
        var location = Services.Sharing.ShareDiscovery.IsShareItem(item) ? item.StreamUrl : item.FilePath;
        return string.IsNullOrEmpty(location) ? null : location;
    }

    /// <summary>
    /// Pulls a shared track's cover from the sharing host and shows it if that track is
    /// still the one playing. A miss is silent - plenty of files have no art, and a
    /// missing cover is not an error worth telling anyone about.
    /// </summary>
    private async Task LoadShareArtAsync(MediaItem file, int epoch)
    {
        if (Services.Sharing.ShareDiscovery.ArtUrlFor(file) is not { } url)
        {
            return;
        }

        var bytes = await Services.Sharing.ShareDiscovery.FetchArtAsync(url);
        if (bytes is null || bytes.Length == 0)
        {
            return;
        }

        UI(() =>
        {
            // The track can have changed while the art was in flight - never repaint
            // the cover of a track that stopped playing.
            if (epoch != _playbackEpoch || !ReferenceEquals(CurrentPlayingItem, file))
            {
                return;
            }

            CurrentAlbumArt = ArtworkSource.BitmapFromBytes(bytes);
            PushNowPlaying(new NowPlayingMetadata(file.Title, file.Artist, file.Album, Duration: file.Duration, ArtBytes: bytes));
        });
    }

    private void ExecutePlayRadio(MediaItem station)
    {
        SelectedItem = station;

        // Don't dispose - Avalonia's ref-counted bitmap lifecycle handles cleanup.
        // Explicit Dispose() while a render pass is in flight causes ObjectDisposedException.
        CurrentAlbumArt = null;
        _stationArtBitmap = null;
        _stationArtBytes = null;
        _currentRadioArtBytes = null;
        _radioTrackArtActive = false;

        PushNowPlaying(new NowPlayingMetadata(station.Title, station.Tags, "Internet Radio", ArtUri: station.FaviconUrl));

        if (!string.IsNullOrWhiteSpace(station.FaviconUrl))
        {
            _ = LoadFaviconAsync(station.FaviconUrl);
        }

        // Stop the outgoing station HERE, not at the swap. Connecting is real network work
        // (redirects, playlist walks, TLS) that can run for seconds, and the previous
        // station's audio used to keep playing across all of it - the user picked a new
        // station and kept hearing the old one until the new one finished loading.
        // Suppressing the Stopped event keeps the barber pole running continuously instead
        // of blinking off, the same way the podcast resolve does it below.
        if (_player is { } outgoing && outgoing.State is VLCState.Opening or VLCState.Buffering or VLCState.Playing or VLCState.Paused)
        {
            _suppressStoppedLoadingClear = true;
            outgoing.Stop();
        }

        // Connecting is real network work (redirects, playlist walks, TLS) done by OUR
        // StreamSession now, not libvlc - so it runs async with the podcast pattern:
        // epoch-stamp the request, show loading immediately, re-check the epoch when the
        // session lands so a superseded connect can't hijack whatever plays by then.
        var epoch = NewPlaybackEpoch();
        BeginPlayback();
        FireAndForget(ConnectRadioAsync(station, epoch), "radio connect");

        ApplyPerTrackOptions(station);

        station.LastPlayed = DateTime.UtcNow;
        station.PlayCount++;
        if (station.Source == "user")
        {
            MediaCache.SetLastPlayed(station.Id, station.LastPlayed.Value);
            MediaCache.IncrementPlayCount(station.Id);
        }
        else
        {
            // Bundled stations have no Media row - the UPDATEs above would hit zero rows
            // and this play would vanish at restart. Their state lives in RadioState.
            MediaCache.BumpRadioPlay(station.Id, station.LastPlayed.Value);
        }
        RememberLastTrack(station);

        UpdateNavigationButtons();
    }

    /// <summary>Connects the single upstream pull for a station, then hands the live session to the swap. Resumes on the UI thread (launched from it).</summary>
    private async Task ConnectRadioAsync(MediaItem station, int epoch)
    {
        StreamSession session;
        try
        {
            session = await StreamSession.ConnectAsync(station.StreamUrl!, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A throwing connect (DNS, TLS, socket teardown) used to fault an unobserved
            // task: the barber pole spun forever and nothing was logged.
            _log.Error(ex, "Radio connect threw for {Url}", station.StreamUrl);
            if (epoch == _playbackEpoch)
            {
                IsPlaybackLoading = false;
                UpdateMainStatus("Couldn't connect to this station.");
            }
            return;
        }

        // Same guard as the podcast resolve: if the user moved on mid-connect, this session
        // must not start playing over whatever superseded it.
        if (epoch != _playbackEpoch)
        {
            _log.Debug("Radio connect superseded (epoch {Epoch} != current {Current}); dropping session for {Url}", epoch, _playbackEpoch, station.StreamUrl);
            session.Dispose();
            return;
        }

        if (!session.IsLive)
        {
            _log.Warning("Radio connect failed for {Url}: {Detail}", station.StreamUrl, session.Facts.Detail);
            session.Dispose();
            IsPlaybackLoading = false;
            UpdateMainStatus($"Station unreachable: {session.Facts.Detail}");
            return;
        }

        StartRadioPlayback(session);
    }

    /// <summary>
    /// Atomic swap onto a live session: VLC reads the session's audio through a
    /// PipeMediaInput instead of opening its own connection. The lock keeps any concurrent
    /// path out of the swap, exactly like the URL-based path this replaces.
    /// </summary>
    private void StartRadioPlayback(StreamSession session)
    {
        lock (_playbackSwitchLock)
        {
            var previousMedia = _currentMedia;
            var previousHandler = _currentMediaMetaHandler;
            var previousRadio = _radioStream;
            previousRadio?.Session.Dispose();   // old station's upstream closes NOW, not when GC gets around to it

            var pipe = session.StartPumping();
            var input = new PipeMediaInput(pipe);
            _radioStream = new RadioStreamHandle(session, input);

            _currentMedia = new Media(_vlc, input);
            SetupNormalization(null);   // radio stream: no ReplayGain tag, plays unmodified

            // Callback media reads through libvlc's imem-style access, so file-caching is
            // the buffering knob (network-caching only governs VLC's own network access,
            // which radio no longer uses - the session is the network client, with its own
            // reconnect logic replacing :http-reconnect). Medium = 3s, matching the CD path.
            _currentMedia.AddOption($":file-caching={StreamingBufferMs(3000)}");

            // Capture this specific Media instance. When the user switches stations rapidly,
            // LibVLC can still deliver late MetaChanged events from the previous (disposed)
            // Media object. The ReferenceEquals checks below guard against that; storing
            // the delegate lets DeferDispose detach it before Dispose(), preventing both
            // the latent reentrancy and a closure-per-switch memory leak.
            var thisMedia = _currentMedia;

            EventHandler<MediaMetaChangedEventArgs> handler = (s, e) =>
            {
                // Per-track artwork: injected by the session (iHeart EXTINF) or set by VLC
                // itself when the stream embeds pictures (ogg/flac → file:// art-cache URL).
                // Empty/absent means this track has none - fall back to the station favicon.
                if (e.MetadataType == MetadataType.ArtworkURL)
                {
                    string? artUrl;
                    lock (_playbackSwitchLock)
                    {
                        if (!ReferenceEquals(_currentMedia, thisMedia))
                        {
                            return;
                        }
                        artUrl = thisMedia.Meta(MetadataType.ArtworkURL);
                    }
                    UI(() =>
                    {
                        if (ReferenceEquals(_currentMedia, thisMedia))
                        {
                            _ = LoadRadioTrackArtAsync(artUrl);
                        }
                    });
                    return;
                }

                if (e.MetadataType != MetadataType.NowPlaying)
                {
                    return;
                }

                string? nowPlaying;

                // Take the playback-swap lock so this libvlc-thread callback can't
                // race with DeferDispose freeing the native Media handle. Without
                // this, ReferenceEquals lets us through but Meta() reads from a
                // disposed pointer when disposal lands between the check and the
                // call - that's the rapid-switch segfault.
                lock (_playbackSwitchLock)
                {
                    if (!ReferenceEquals(_currentMedia, thisMedia))
                    {
                        return;
                    }

                    nowPlaying = thisMedia.Meta(MetadataType.NowPlaying);
                }

                if (string.IsNullOrWhiteSpace(nowPlaying))
                {
                    // VLC clearing its own meta (startup, stream transitions) - breaks no
                    // longer ride this channel (SetMeta rejects empties). Idempotent
                    // branding restore, harmless at tune-in.
                    UI(() =>
                    {
                        if (ReferenceEquals(_currentMedia, thisMedia))
                        {
                            RestoreStationBranding();
                        }
                    });
                    return;
                }

                // iHeart-style streams pad titles with tracking attributes - scrub before display.
                nowPlaying = IcyMetadata.CleanStreamTitle(nowPlaying!);

                UI(() =>
                {
                    // Re-check on the UI thread - a station switch could have landed between
                    // the handler firing and this continuation running.
                    if (!ReferenceEquals(_currentMedia, thisMedia))
                    {
                        return;
                    }

                    UpdateMainStatus($"Playing: {nowPlaying}");

                    string? artist = null;
                    string? title = nowPlaying;

                    var dashIdx = nowPlaying.IndexOf(" - ", StringComparison.Ordinal);
                    if (dashIdx > 0)
                    {
                        artist = nowPlaying[..dashIdx].Trim();
                        title = nowPlaying[(dashIdx + 3)..].Trim();
                    }

                    CurrentTrackLine1 = title ?? nowPlaying;
                    CurrentTrackLine2 = artist ?? string.Empty;

                    // Carry the art that's currently showing (per-track cover or the
                    // station favicon) - SetMetadata rebuilds the whole SMTC entry, so
                    // omitting it drops the thumbnail on every song change.
                    PushNowPlaying(new NowPlayingMetadata(title, artist, CurrentStation?.Title, ArtUri: CurrentStation?.FaviconUrl, ArtBytes: _currentRadioArtBytes ?? _stationArtBytes));
                });
            };
            thisMedia.MetaChanged += handler;
            _currentMediaMetaHandler = handler;

            // No Stop() immediately before this Play(). libvlcsharp's Stop+Play back to back
            // is two native transitions in a row and is more crash-prone than the single
            // transition Play(newMedia) performs internally. ExecutePlayRadio does stop the
            // outgoing station, but at the click - a whole network connect earlier - so the
            // two transitions are never adjacent. The 120 ms debounce in PlayRadioStation +
            // the lock here + the deferred dispose under the same lock is the safe combination.
            if (!_player.Play(thisMedia))
            {
                _log.Error("libvlc Play returned false for radio session");
            }

            // Now-playing parsed off the same connection the audio rides, injected on the
            // UI thread under the swap lock, guarded against station switches - an update
            // that lands after this Media is gone must not stamp its successor (or touch a
            // disposed native handle). Real titles and covers ride SetMeta so the handler
            // above stays the consumer for demuxed AND injected values alike (ArtworkURL is
            // the same slot VLC fills when a stream embeds pictures). CLEAR states are the
            // exception: LibVLCSharp's SetMeta throws ArgumentNullException on null AND
            // empty strings (it killed the curator once), so ad breaks and art-less tracks
            // bypass the meta channel and restore station branding / favicon directly.
            string? lastInjectedArt = null;
            session.NowPlayingChanged += nowPlaying => UI(() =>
            {
                var revertArt = false;
                lock (_playbackSwitchLock)
                {
                    if (!ReferenceEquals(_currentMedia, thisMedia))
                    {
                        return;
                    }
                    if (nowPlaying != null)
                    {
                        if (nowPlaying.ArtUrl != lastInjectedArt)
                        {
                            lastInjectedArt = nowPlaying.ArtUrl;
                            if (nowPlaying.ArtUrl != null)
                            {
                                thisMedia.SetMeta(MetadataType.ArtworkURL, nowPlaying.ArtUrl);
                            }
                            else
                            {
                                revertArt = true;   // track with art → track without: favicon returns
                            }
                        }
                        thisMedia.SetMeta(MetadataType.NowPlaying, nowPlaying.Title);
                    }
                }

                // Outside the lock - these touch UI state and kick off fetches.
                if (nowPlaying == null)
                {
                    lastInjectedArt = null;
                    RestoreStationBranding();
                    _ = LoadRadioTrackArtAsync(null);
                }
                else if (revertArt)
                {
                    _ = LoadRadioTrackArtAsync(null);
                }
            });

            // A fast station can deliver its first now-playing between StartPumping and the
            // subscription above; the session keeps it in Facts, so stamp it now.
            if (session.Facts.LiveTitle is { } earlyTitle)
            {
                if (session.Facts.LiveArtUrl is { } earlyArt)
                {
                    lastInjectedArt = earlyArt;
                    thisMedia.SetMeta(MetadataType.ArtworkURL, earlyArt);
                }
                thisMedia.SetMeta(MetadataType.NowPlaying, earlyTitle);
            }

            DeferDispose(previousMedia, previousHandler, previousRadio);
        }
    }

    /// <summary>
    /// Exposes the audio visualization source to the UI (mini-player VU,
    /// future shader/script visualizers).  The tap is permanently attached
    /// to <see cref="_player"/> so spectrum data flows whenever anything
    /// is playing - consumers just read whenever they need to render.
    /// </summary>
    internal OrgZ.Services.AudioVisualization.IAudioVisualizationSource AudioVisualization => _audioTap;

    /// <summary>
    /// Streams a podcast episode directly from its <c>enclosureUrl</c> without
    /// requiring a download or subscription. Records the play in
    /// <see cref="Services.Podcast.PodcastCache"/> so all listens - streamed or
    /// downloaded - show up in the history.
    /// </summary>
    /// <summary>
    /// Plays a podcast episode. Pass <paramref name="localPath"/> when the
    /// episode is downloaded; libvlc opens it as a local file. Without it,
    /// the episode's <c>EnclosureUrl</c> is streamed. Either way the same LCD
    /// metadata, OS now-playing payload, pause/resume logic, and listen
    /// tracking apply -- this is the single playback path for podcasts.
    /// </summary>
    /// <summary>
    /// Common pre-roll for every Play* path: clear LCD time labels, arm the
    /// fast loading indicator, and reset the audio-start tracker so the next
    /// PCM buffer libvlc delivers cleanly transitions out of the loading state.
    /// Music / Radio / Podcast / CD all call this before handing libvlc new
    /// Media, so the visual experience is identical across kinds.
    /// </summary>
    private void BeginPlayback(bool showLoading = true)
    {
        // Seed the time tiles at 0:00 rather than blanking them - the known total (music,
        // CD) or measured one (radio/podcast via MediaChanged) overwrites the total a beat
        // later, but the tiles never flash empty in between.
        CurrentTrackTime = FormatHelper.FormatDurationCompact(0);
        CurrentTrackDuration = FormatHelper.FormatDurationCompact(0);
        CurrentTrackTimeNumber = 0;
        CurrentTrackDurationNumber = 0;
        // The barber pole is only worth showing for high-latency sources - remote streams
        // (radio, streamed podcasts) and CD spin-up. Local files (library music, iPod tracks
        // over USB, downloaded podcasts) open effectively instantly, so the pole would just
        // flicker; those callers pass showLoading: false.
        IsPlaybackLoading = showLoading;
        // Clear any stale resume target; podcast playback re-sets it right after this.
        _pendingResumeMs = null;
        _audioTap?.ResetAudioStartTracking();
        // Whatever starts next owns the audio path - if the bit-perfect engine
        // was playing, it stops here (its own Play() re-arms it when it's the
        // one taking over).
        _flacEngine?.Stop();
    }

    // Follows redirects ourselves so VLC gets the final URL. Podcast hosts stack
    // tracking/prefix redirects (pdst.fm -> pscrb.fm -> mgln.ai -> CDN); libvlc
    // caps redirects low and aborts with "too many redirections" - silently, in
    // native code - so those episodes never played. HttpClient walks the chain
    // (up to 20 hops) and we hand VLC the resolved URL, which it opens directly.
    private static readonly HttpClient _podcastRedirectResolver = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 20,
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static async Task<string> ResolvePodcastUrlAsync(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Same browser UA the playback path uses - some CDNs vary their
            // redirect target by User-Agent.
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            // ResponseHeadersRead so we don't download the audio body - we only
            // need the final RequestUri after the handler followed the chain.
            using var resp = await _podcastRedirectResolver.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var final = resp.RequestMessage?.RequestUri?.ToString();
            if (!string.IsNullOrWhiteSpace(final) && !string.Equals(final, url, StringComparison.Ordinal))
            {
                // If the chain ends in a per-request *signed* CDN URL (CloudFront/Akamai/
                // Triton token), DON'T hand VLC the one we just fetched - those are often
                // single-use or request-bound, so our resolve GET "spends" it and VLC's
                // open of the same URL is rejected (this is exactly how BBC's
                // open.live.bbc.co.uk -> tritondigital chain behaves). Instead give VLC the
                // original short-redirect URL and let it mint its own fresh signed URL - it
                // follows redirects fine for short chains and uses the same browser UA we
                // set on the media. Long tracking-prefix chains end in plain, reusable CDN
                // URLs that VLC can't walk itself, so those we still hand over resolved.
                if (LooksSigned(final))
                {
                    _log.Information("Podcast resolves to a signed CDN URL; letting VLC follow {Original} itself", url);
                    return url;
                }
                _log.Information("Resolved podcast redirects: {Original} -> {Final}", url, final);
                return final;
            }
            return url;
        }
        catch (Exception ex)
        {
            // Resolution is best-effort - fall back to the original URL and let
            // VLC try directly (it may still work for short chains).
            _log.Warning(ex, "Podcast URL redirect resolve failed; using original {Url}", url);
            return url;
        }
    }

    /// <summary>
    /// True when a URL carries a per-request signature/token (CloudFront, Akamai, Triton) -
    /// re-opening the same URL after we've already fetched it is likely to be rejected.
    /// </summary>
    private static bool LooksSigned(string url) =>
        url.Contains("Signature=", StringComparison.OrdinalIgnoreCase)
        || url.Contains("Key-Pair-Id=", StringComparison.OrdinalIgnoreCase)
        || url.Contains("X-Amz-Signature", StringComparison.OrdinalIgnoreCase)
        || url.Contains("hdnea=", StringComparison.OrdinalIgnoreCase)
        || url.Contains("hdnts=", StringComparison.OrdinalIgnoreCase)
        || url.Contains("__token__", StringComparison.OrdinalIgnoreCase);

    internal void PlayPodcastEpisode(Models.PodcastFeed feed, Models.PodcastEpisode episode, string? localPath = null)
    {
        EnsurePlaybackReady();
        if (_player == null)
        {
            _log.Warning("PlayPodcastEpisode: player not initialized");
            return;
        }

        var rawSource = localPath ?? episode.EnclosureUrl;
        if (string.IsNullOrWhiteSpace(rawSource))
        {
            _log.Warning("PlayPodcastEpisode: episode {Id} has no playable source", episode.Id);
            return;
        }
        bool isLocal = localPath != null && File.Exists(localPath);

        // Bump the playback epoch up front and capture it: if the user starts something else
        // while a streamed episode is still resolving, the resolve sees a newer epoch and
        // bails instead of hijacking whatever's now playing.
        int epoch = NewPlaybackEpoch();

        // Switch the UI to this episode immediately so a double-click registers - even a
        // streamed episode whose redirect chain is still resolving shows its title, feed,
        // art and a loading state right away.
        ShowPodcastSwitching(feed, episode, rawSource, isLocal);

        if (isLocal)
        {
            StartPodcastPlayback(feed, episode, rawSource, isLocal: true, epoch);
            return;
        }

        // Streamed: resolve the redirect chain off the UI thread, then start VLC.
        FireAndForget(ResolveAndStreamPodcastAsync(feed, episode, rawSource, epoch), "podcast stream start");
    }

    /// <summary>
    /// Immediate visual switch to a podcast episode - title, feed, art, now-playing
    /// metadata and a loading state - so a double-click registers before a streamed
    /// episode's redirect chain has resolved. <see cref="StartPodcastPlayback"/> then
    /// hands the (resolved) source to libvlc.
    /// </summary>
    private void ShowPodcastSwitching(Models.PodcastFeed feed, Models.PodcastEpisode episode, string source, bool isLocal)
    {
        UI(() =>
        {
            // Switch like any other media: stop whatever's playing right now and reset the
            // transport (BeginPlayback seeds the 0:00 time tiles + loading state) so the old
            // audio doesn't keep going while a streamed episode's redirect chain resolves.
            // Suppress that Stop()'s Stopped event so the barber pole runs continuously from
            // here until the new episode's audio starts, instead of blinking off.
            if (_player is { } p && p.State is VLCState.Opening or VLCState.Buffering or VLCState.Playing or VLCState.Paused)
            {
                _suppressStoppedLoadingClear = true;
                p.Stop();
            }
            BeginPlayback(showLoading: !isLocal);

            _currentPodcastStream = (feed, episode);
            _playbackContext?.Release();
            _playbackContext = null;

            CurrentAlbumArt = null;
            CurrentTrackLine1 = episode.Title ?? string.Empty;
            CurrentTrackLine2 = feed.Title ?? string.Empty;
            UpdateMainStatus(isLocal ? $"Playing: {episode.Title}" : $"Loading: {episode.Title}");

            SelectedItem = new MediaItem
            {
                Id        = $"podcast:{episode.Id}",
                Kind      = MediaKind.Podcast,
                Source    = "podcast",
                Title     = episode.Title,
                Artist    = feed.Title,
                StreamUrl = isLocal ? null : episode.EnclosureUrl,
                FilePath  = isLocal ? source : null,
            };

            PushNowPlaying(new NowPlayingMetadata(episode.Title, feed.Title, "Podcast", ArtUri: episode.Image ?? feed.DisplayImage));

            var artUrl = !string.IsNullOrWhiteSpace(episode.Image) ? episode.Image : feed.DisplayImage;
            if (!string.IsNullOrWhiteSpace(artUrl))
            {
                _ = LoadPodcastArtAsync(artUrl, episode, feed);
            }
        });
    }

    private async Task ResolveAndStreamPodcastAsync(Models.PodcastFeed feed, Models.PodcastEpisode episode, string url, int epoch)
    {
        var resolved = await ResolvePodcastUrlAsync(url);
        StartPodcastPlayback(feed, episode, resolved, isLocal: false, epoch);
    }

    private void StartPodcastPlayback(Models.PodcastFeed feed, Models.PodcastEpisode episode, string source, bool isLocal, int epoch)
    {
        _log.Information("Playing podcast episode {Id} '{Title}' [{Mode}] from {Source}",
            episode.Id, episode.Title, isLocal ? "local" : "stream", source);

        UI(() =>
        {
            // A newer playback started while this (streamed) episode was resolving - don't
            // hijack whatever the user moved on to. Checked here, in the same UI dispatch as
            // the libvlc Play, so there's no gap with the epoch bump on the new playback.
            if (epoch != _playbackEpoch)
            {
                _log.Debug("Podcast playback superseded (epoch {Epoch} != current {Current}); not starting {Id}",
                    epoch, _playbackEpoch, episode.Id);
                return;
            }

            // The UI already switched to this episode in ShowPodcastSwitching. For a stream
            // we now have the resolved source, so move the status off "Loading...".
            if (!isLocal)
            {
                UpdateMainStatus($"Streaming: {episode.Title}");
            }

            // LCD time labels stay blank until the first PCM buffer reaches the audio tap --
            // BeginPlayback clears them and the MediaChanged podcast branch seeds the API
            // duration once playback is under way (libvlc's measured value taking priority).

            try
            {
                lock (_playbackSwitchLock)
                {
                    var previousRadio = TakeRadioStream();
                    var previousMedia = _currentMedia;
                    var previousHandler = _currentMediaMetaHandler;
                    _currentMediaMetaHandler = null;

                    _currentMedia = isLocal
                        ? new Media(_vlc, source, FromType.FromPath)
                        : new Media(_vlc, source, FromType.FromLocation);
                    SetupNormalization(null);   // podcast episode: no ReplayGain tag, plays unmodified

                    if (!isLocal)
                    {
                        // Streamed only: podcasts have Content-Length so we omit
                        // :http-continuous (a live-stream option). Network caching
                        // and reconnect still help on flaky CDN edges.
                        _currentMedia.AddOption($":network-caching={StreamingBufferMs(3000)}");
                        _currentMedia.AddOption(":http-reconnect");
                        // Force a standard browser UA -- libvlc's default
                        // ("VLC/3.x LibVLC/3.x") gets blocked or fingerprinted
                        // differently by some CDNs (Simplecast / Megaphone),
                        // which can manifest as redirects libvlc won't follow.
                        _currentMedia.AddOption(":http-user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                    }

                    IsSeekEnabled = true;
                    // Streamed episodes hit the network; downloaded ones are local files.
                    BeginPlayback(showLoading: !isLocal);
                    // Resume where the listener left off (set after BeginPlayback so its
                    // reset doesn't clear it; applied once audio actually starts). Skip if
                    // finished or barely started.
                    var savedPos = Services.Podcast.PodcastCache.GetListenPosition(episode.Id);
                    _pendingResumeMs = savedPos is { } sp && !sp.Completed && sp.PositionMs > 10000 ? sp.PositionMs : null;
                    _lastPodcastSaveMs = _pendingResumeMs ?? 0;
                    if (!_player.Play(_currentMedia))
                    {
                        _log.Error("libvlc Play returned false for episode {Id}", episode.Id);
                    }
                    DeferDispose(previousMedia, previousHandler, previousRadio);
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "PlayPodcastEpisode: libvlc Play failed for episode {Id}", episode.Id);
            }

            try
            {
                Services.Podcast.PodcastCache.RecordPlay(feed, episode);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "RecordPlay failed for episode {Id}", episode.Id);
            }
        });
    }

    // Back-compat alias: existing callers may still use the older name.
    internal void PlayPodcastEpisodeStream(Models.PodcastFeed feed, Models.PodcastEpisode episode)
        => PlayPodcastEpisode(feed, episode);

    /// <summary>
    /// Defers disposal of a LibVLC <see cref="Media"/> that's just been replaced
    /// as <see cref="_currentMedia"/>.  The player's native transition from the
    /// old Media to the new one completes on a worker thread after
    /// <see cref="LibVLCSharp.Shared.MediaPlayer.Play(Media)"/> returns; disposing
    /// the old Media inline can race that transition and corrupt native state
    /// (manifests as <c>ExecutionEngineException</c> when the user mashes
    /// Next/Prev faster than the transitions can settle).  Posting the dispose
    /// to the UI dispatcher at Background priority lets the player claim its
    /// new ref and release the old one before we free the native handle.
    /// </summary>
    private void DeferDispose(Media? media, EventHandler<MediaMetaChangedEventArgs>? metaHandler = null, RadioStreamHandle? radio = null)
    {
        if (media == null && radio == null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            // Hold the playback-swap lock for detach + dispose so a concurrent
            // MetaChanged callback on libvlc's worker thread can't read from the
            // native handle while we're freeing it.
            lock (_playbackSwitchLock)
            {
                try
                {
                    if (media != null)
                    {
                        if (metaHandler != null)
                        {
                            media.MetaChanged -= metaHandler;
                        }
                        media.Dispose();
                    }
                }
                catch
                {
                    // Best-effort: the native handle may already be gone if a
                    // previous deferred dispose got there first.
                }
            }
            // The MediaInput outlives its Media: by the time the deferred media dispose has
            // run, VLC's input thread is done with our Read callback and the GCHandle can go.
            radio?.Dispose();
        }, DispatcherPriority.Background);
    }

    private void ClearPlayback()
    {
        // Stopping supersedes any pending async playback (e.g. a podcast resolve or radio
        // connect in flight). Closing the radio session first unblocks VLC's reader (EOF)
        // so the Stop below never waits on a starved callback read.
        var radio = TakeRadioStream();
        NewPlaybackEpoch();
        _flacEngine?.Stop();
        _playbackContext?.Release();
        _playbackContext = null;
        OnPropertyChanged(nameof(PlaybackContextUpcoming));

        // Stop libvlc before releasing the Media object - disposing Media alone
        // leaves the MediaPlayer pointing at a freed source AND keeps the
        // backing file handle open, which prevents EnsureCdDriveFree from
        // actually freeing the CD for ripping.
        _player.Stop();

        if (_currentMedia != null)
        {
            if (_currentMediaMetaHandler != null)
            {
                _currentMedia.MetaChanged -= _currentMediaMetaHandler;
                _currentMediaMetaHandler = null;
            }
            _currentMedia.Dispose();
            _currentMedia = null;
        }
        // After the Media is gone, VLC is done with the callback input.
        radio?.Dispose();

        // Don't dispose - Avalonia's ref-counted bitmap lifecycle handles cleanup.
        // Explicit Dispose() while a render pass is in flight causes ObjectDisposedException.
        CurrentAlbumArt = null;
        _radioTrackArtActive = false;
        CurrentTrackLine1 = string.Empty;
        CurrentTrackLine2 = string.Empty;
        CurrentTrackTime = "";
        CurrentTrackDuration = "";
        CurrentTrackTimeNumber = 0;
        CurrentTrackDurationNumber = 0;

        ButtonPlayPauseIcon = ICON_PLAY;
        ButtonPlayPausePadding = ICON_PLAY_PADDING;

#if WINDOWS
        _thumbBarService?.SetPlayingState(false);
#endif
        _nowPlaying?.SetPlaybackStatus("Stopped");

        UpdateNavigationButtons();
    }

    internal void Play()
    {
        UI(() =>
        {
            if (EngineActive)
            {
                _flacEngine!.Resume();
                UiShowPlaying();
                return;
            }
            _player?.Play();
        });
    }

    internal void Pause()
    {
        UI(() =>
        {
            if (EngineActive)
            {
                _flacEngine!.Pause();
                UiShowPaused();
                return;
            }
            _player?.Pause();
        });
    }

    internal void Stop()
    {
        if (EngineActive)
        {
            _flacEngine!.Stop();
            UI(UiShowStopped);
            return;
        }
        _ = ThreadPool.QueueUserWorkItem(_ => _player?.Stop());
    }

    // -- Shared playback handlers ---------------------------------------
    // One body for both engines: libvlc events and FlacPlaybackEngine events
    // funnel here so end-of-track chaining, resume persistence, Now Playing
    // pivots, and per-track stop times behave identically on either path.

    private long _lastMacNowPlayingPushMs = long.MinValue;

    private void HandlePlaybackEnded()
    {
        // A finished audiobook starts from the top next time - clear its resume point (the
        // throttle only gets within ~5s of the end; this is the authoritative reset).
        if (CurrentPlayingItem is { Kind: MediaKind.Audiobook, Source: null } finishedBook)
        {
            finishedBook.LastPositionMs = 0;
            var finishedId = finishedBook.Id;
            FireAndForget(Task.Run(() => MediaCache.UpdatePlaybackPosition(finishedId, 0)), "audiobook finish reset");
        }

        if (CurrentStation != null)
        {
            ClearPlayback();
            UpdateMainStatus("Stream ended");
            return;
        }

        // Settings > Playback > Auto-advance: off means a finished track ends the
        // session rather than walking the queue.
        if (!Settings.Get("OrgZ.AutoAdvance", true))
        {
            ClearPlayback();
            UpdateMainStatus("Finished");
            return;
        }

        if (_playbackContext != null && _playbackContext.HasNext)
        {
            var next = _playbackContext.MoveNext()!;
            ExecutePlayItem(next);
            return;
        }

        ClearPlayback();
        UpdateMainStatus("Finished");
    }

    private void HandlePlaybackTime(long timeMs, long lengthMs)
    {
        // Show what the LISTENER hears, not what the decoder has read.
        //
        // They are the same thing until the only speaker in use is a distant one: AirPlay
        // hands a receiver its audio a couple of seconds before it plays, so with no local
        // output selected the decoder's clock runs ahead of the room. The bus answers null
        // whenever a local output is in the mix, which is when there is nothing to correct.
        if (_audioOutput.Bus.ListenerPosition is { } audible)
        {
            timeMs = (long)audible.TotalMilliseconds;
        }

        CurrentTrackTime = FormatHelper.FormatDurationCompact(timeMs);
        if (!isSeeking)
        {
            CurrentTrackTimeNumber = timeMs;
        }

        // Persist the podcast resume position, throttled to ~5s of movement (also
        // catches seeks). Runs off the UI thread; UpdateListenPosition opens its own
        // connection, so it's safe.
        if (_currentPodcastStream is { } ps && timeMs > 0 && Math.Abs(timeMs - _lastPodcastSaveMs) >= 5000)
        {
            _lastPodcastSaveMs = timeMs;
            var episodeId = ps.Episode.Id;
            var posMs = timeMs;
            var completed = lengthMs > 0 && posMs >= lengthMs - 15000;
            FireAndForget(Task.Run(() => Services.Podcast.PodcastCache.UpdateListenPosition(episodeId, posMs, completed)), "podcast position persist");
        }

        // Audiobook resume position, same ~5s throttle (Math.Abs also catches seeks). Within
        // the last 15s counts as finished - the resume point resets so the next play starts
        // from the top instead of the credits.
        if (CurrentPlayingItem is { Kind: MediaKind.Audiobook, Source: null } book && timeMs > 0 && Math.Abs(timeMs - _lastAudiobookSaveMs) >= 5000)
        {
            _lastAudiobookSaveMs = timeMs;
            var pos = lengthMs > 0 && timeMs >= lengthMs - 15000 ? 0 : timeMs;
            book.LastPositionMs = pos;
            var bookId = book.Id;
            FireAndForget(Task.Run(() => MediaCache.UpdatePlaybackPosition(bookId, pos)), "audiobook position persist");
        }

        // Push pivots to macOS Now Playing: the very first TimeChanged (so
        // the widget locks onto the playback clock instead of extrapolating
        // from 0), every 5 s as a re-sync against any drift, and on a rewind
        // (track change → time resets to 0). The widget extrapolates
        // smoothly between pivots at rate=1, which matches OrgZ's display
        // much better than flooding macOS with 4 Hz updates - the widget
        // appeared to coalesce / lag those, ending up several seconds behind.
        if (_nowPlaying is not null)
        {
            bool firstPush = _lastMacNowPlayingPushMs == long.MinValue;
            bool rewound = timeMs < _lastMacNowPlayingPushMs;
            bool resyncDue = timeMs - _lastMacNowPlayingPushMs >= 5000;
            if (firstPush || rewound || resyncDue)
            {
                _lastMacNowPlayingPushMs = timeMs;
                _nowPlaying.SetPlaybackPosition(TimeSpan.FromMilliseconds(timeMs), 1.0);
            }
        }

        // Stop time check for per-track options
        var playing = CurrentPlayingItem;
        if (playing is { UseStopTime: true, StopTime: not null })
        {
            if (timeMs >= (long)playing.StopTime.Value.TotalMilliseconds)
            {
                // The stop point ends the track; OrgZ.AutoAdvance decides whether the
                // queue continues, same as a natural end.
                if (Settings.Get("OrgZ.AutoAdvance", true))
                {
                    ButtonNextTrack();
                }
                else
                {
                    Stop();
                    ClearPlayback();
                    UpdateMainStatus("Finished");
                }
            }
        }
    }

    private void UiShowPlaying()
    {
        // A track change that started from a paused state stays paused. Both engines funnel their
        // "now playing" transition through here, so this covers libvlc and the FLAC path alike.
        if (_pauseWhenNextTrackStarts)
        {
            _pauseWhenNextTrackStarts = false;
            Pause();
            return;
        }

        ButtonPlayPauseIcon = ICON_PAUSE;
        ButtonPlayPausePadding = ICON_PAUSE_PADDING;

#if WINDOWS
        _thumbBarService?.SetPlayingState(true);
#endif
        _nowPlaying?.SetPlaybackStatus("Playing");

        UpdateMainStatus("Playing");
    }

    private void UiShowPaused()
    {
        ButtonPlayPauseIcon = ICON_PLAY;
        ButtonPlayPausePadding = ICON_PLAY_PADDING;

#if WINDOWS
        _thumbBarService?.SetPlayingState(false);
#endif
        _nowPlaying?.SetPlaybackStatus("Paused");

        UpdateMainStatus("Paused");
    }

    private void UiShowStopped()
    {
        IsPlaybackLoading = false;
        ButtonPlayPauseIcon = ICON_PLAY;
        ButtonPlayPausePadding = ICON_PLAY_PADDING;

#if WINDOWS
        _thumbBarService?.SetPlayingState(false);
#endif
        _nowPlaying?.SetPlaybackStatus("Stopped");

        UpdateMainStatus("Stopped");
    }

    #endregion

    #region Radio Station Management

    [RelayCommand]
    /// <summary>
    /// Routes a favorite flip to the store that can actually hold it: bundled stations
    /// have no Media row (the UPDATE would hit zero rows and the flag would evaporate
    /// at restart), so theirs goes to the RadioState overlay.
    /// </summary>
    private void PersistFavorite(MediaItem item)
    {
        if (item.Kind == MediaKind.Radio && item.Source != "user")
        {
            MediaCache.SetRadioFavorite(item.Id, item.IsFavorite);
            return;
        }

        MediaCache.SetFavorite(item.Id, item.IsFavorite);
        ExportFavoritesFile();
    }

    internal void ToggleFavorite(MediaItem? station)
    {
        if (station == null)
        {
            return;
        }

        station.IsFavorite = !station.IsFavorite;
        PersistFavorite(station);

        // Same stale-cache hole as playlists: switching views never bumps the version, so the
        // Favorites cache must die now or the next visit serves the pre-toggle list.
        _viewCache.Remove("Favorites");

        // Only rebuild the list when viewing Favorites (item may need to appear/disappear)
        if (SelectedSidebarItem?.IsFavorites == true)
        {
            var scrollAnchor = GetScrollAnchor?.Invoke();
            ApplyFilter();
            RestoreScrollAnchor?.Invoke(scrollAnchor);
        }
    }

    /// <summary>
    /// Adds an item to Favorites (idempotent - never un-favorites). Favorites is a pseudo-playlist,
    /// so it lives in the "Add to Playlist >" submenu; the ADD semantic there must not toggle.
    /// </summary>
    internal void AddToFavorites(MediaItem? item)
    {
        if (item is null || item.IsFavorite)
        {
            return;
        }
        item.IsFavorite = true;
        PersistFavorite(item);
        _viewCache.Remove("Favorites");   // see ToggleFavorite - a view switch alone reuses stale caches
        if (SelectedSidebarItem?.IsFavorites == true)
        {
            ApplyFilter();
        }
    }

    /// <summary>A station the user typed in: three separate fields, never one packed string.</summary>
    public sealed record NewUserStation(string? Name, string Url, string? Genre);

    /// <summary>
    /// Removes a user-added station, with confirmation. Bundled stations aren't removable -
    /// they're the shipped catalogue - so those get a status line instead of a dialog.
    /// </summary>
    internal async Task RemoveUserStationAsync(MediaItem? station)
    {
        if (station is null || station.Kind != MediaKind.Radio)
        {
            return;
        }

        if (station.Source != "user")
        {
            UpdateMainStatus("Only stations you added can be removed — bundled ones are the shipped catalogue.");
            return;
        }

        var dialog = new ConfirmDialog("Remove Station", $"Remove “{station.Title}” from your stations?", "Remove");
        if (await dialog.ShowDialog<bool>(_window) != true)
        {
            return;
        }

        MediaCache.RemoveUserStation(station.Id);
        _allItems.Remove(station);
        _viewCache.Remove("Radio");
        _viewCache.Remove("Favorites");
        ApplyFilter();
        UpdateMainStatus($"Removed “{station.Title}”.");
    }

    [RelayCommand]
    internal void AddUserStation(NewUserStation? input)
    {
        // Was a pipe-delimited "name|url|genre" string, which quietly corrupted any station
        // whose NAME contained a '|' - the split handed the rest of the name to the URL
        // field and the station silently failed to add.
        if (input is null || string.IsNullOrWhiteSpace(input.Url))
        {
            UpdateMainStatus("Station not added — a stream URL is required.");
            return;
        }

        var url = input.Url.Trim();
        var name = string.IsNullOrWhiteSpace(input.Name) ? url : input.Name.Trim();
        var genre = input.Genre?.Trim() ?? string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            UpdateMainStatus($"Station not added — “{url}” isn't a valid URL.");
            return;
        }

        // The genre is whatever the user picked in the dialog - no fuzzy
        // normalization in the app; curation-time mapping lives in the tool.
        var genreEnum = RadioGenres.FromDisplayName(genre);

        var station = new MediaItem
        {
            Id = $"user:{Guid.NewGuid()}",
            Kind = MediaKind.Radio,
            Source = "user",
            Title = name,
            StreamUrl = url,
            Tags = genreEnum == RadioGenre.Unknown ? null : genreEnum.DisplayName(),
        };

        MediaCache.UpsertRadioStations([station]);
        _allItems.Add(station);

        // Rebuild (not just re-filter) so a genre new to the dataset shows up
        // in the Genre dropdown immediately.
        RebuildRadioFilterOptions();
    }

    private void RebuildRadioFilterOptions()
    {
        // Capture intent from Settings, not from SelectedCountry/Genre. The
        // ComboBox binds at startup while Countries/Genres still only contain
        // "All"; Avalonia's ComboBox can't resolve the persisted "Canada"
        // against an empty items list and silently leaves SelectedItem null.
        // Settings is the durable source of truth here.
        var prevCountry = Settings.Get("OrgZ.Radio.Country", "All");
        var prevGenre   = Settings.Get("OrgZ.Radio.Genre",   "All");

        var radioItems = _allItems.Where(i => i.Kind == MediaKind.Radio);

        Countries.Clear();
        Countries.Add("All");
        foreach (var country in radioItems
            .Where(s => !string.IsNullOrWhiteSpace(s.Country))
            .Select(s => s.Country!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order())
        {
            Countries.Add(country);
        }

        Genres.Clear();
        Genres.Add("All");
        // Genre dropdown lists the RadioGenre display names that actually
        // appear in the current dataset, in canonical taxonomy order.
        var activeGenres = radioItems
            .Where(s => !string.IsNullOrWhiteSpace(s.Tags))
            .Select(s => s.Tags!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var genre in RadioGenres.All)
        {
            var name = genre.DisplayName();
            if (activeGenres.Contains(name))
            {
                Genres.Add(name);
            }
        }

        var resolvedCountry = Countries.Contains(prevCountry) ? prevCountry : "All";
        var resolvedGenre   = Genres.Contains(prevGenre)     ? prevGenre   : "All";

        // Bounce through "" before assigning the resolved value. The Avalonia
        // ComboBox bound while Countries/Genres still only contained "All";
        // it silently rendered blank because SelectedItem couldn't resolve
        // against the small items list. Just calling OnPropertyChanged isn't
        // enough - the binding reports the same value and Avalonia's ComboBox
        // doesn't re-pick its SelectedItem. Forcing a real value change makes
        // the ComboBox re-evaluate SelectedItem against the now-populated
        // items collection. ApplyFilter / Settings.Save are suppressed during
        // the bounce; we call ApplyFilter once at the end with the real value.
        _suppressFilterSideEffects = true;
        try
        {
            SelectedCountry = string.Empty;
            SelectedCountry = resolvedCountry;
            SelectedGenre   = string.Empty;
            SelectedGenre   = resolvedGenre;
        }
        finally
        {
            _suppressFilterSideEffects = false;
        }

        ApplyFilter();
    }

    #endregion



    #region UX Updates

    internal void UpdateData()
    {
        // The enumeration lives inside the UI post (same shape as UpdateTitle): _allItems is
        // UI-thread-only, and this method gets called from Task.Run bodies (analysis loop,
        // rip completion). Enumerating on the calling thread here was a live
        // "collection was modified" crash whenever the library changed mid-analysis.
        UI(() =>
        {
            // Whole-library totals are only the truth when nothing is filtering the Music
            // view. With a search active the footer belongs to UpdateViewStats (which sums
            // the filtered set) - both used to write these three, so whichever dispatcher
            // post landed last won and a searched footer got silently stomped with
            // library-wide numbers.
            if (StatusBar.ActiveKind == MediaKind.Music && !string.IsNullOrWhiteSpace(SearchText))
            {
                return;
            }

            StatusBar.TotalSongs = MusicItems.Count();
            StatusBar.TotalDuration = TimeSpan.FromTicks(MusicItems.Sum(x => x.Duration?.Ticks ?? 0));
            StatusBar.TotalFileSize = MusicItems.Sum(x => x.FileSize ?? 0L);
        });
    }

    internal void UpdateTitle()
    {
        UI(() =>
        {
            string sep = " - ";

            List<string> parts = [];

            parts.Add($"OrgZ v{App.Version}");
            parts.Add(App.FolderPath);

            var musicCount = MusicItems.Count();
            if (musicCount > 0)
            {
                parts.Add($"({musicCount} files)");
            }

            _window.Title = string.Join(sep, parts);
        });
    }

    internal void UpdateNavigationButtons()
    {
        if (_playbackContext == null)
        {
            IsBackTrackButtonEnabled = false;
            IsNextTrackButtonEnabled = false;
#if WINDOWS
            _thumbBarService?.SetNavigationEnabled(false, false);
#endif
            _nowPlaying?.SetNavigationEnabled(false, false);
            return;
        }

        IsBackTrackButtonEnabled = _playbackContext.HasPrevious;
        IsNextTrackButtonEnabled = _playbackContext.HasNext;
#if WINDOWS
        _thumbBarService?.SetNavigationEnabled(IsBackTrackButtonEnabled, IsNextTrackButtonEnabled);
#endif
        _nowPlaying?.SetNavigationEnabled(IsBackTrackButtonEnabled, IsNextTrackButtonEnabled);
    }

    internal void UpdateMainStatus(string status)
    {
        UI(() =>
        {
            StatusBar.MainStatus = status;
        });
    }

    private void UpdateGenericStatusBar()
    {
        var count = FilteredItems.Count;
        var viewKey = SelectedSidebarItem?.ViewConfigKey ?? "";

        var label = viewKey switch
        {
            "Favorites" => "songs",
            "BadFormat" => "issues",
            "CdAudio" => "tracks",
            "Audiobooks" => "audiobooks",
            _ when viewKey.StartsWith("Playlist:") => "tracks",
            _ when viewKey.StartsWith("Device:") => "tracks",
            _ => "items"
        };

        var duration = TimeSpan.FromTicks(FilteredItems.Where(i => i.Duration.HasValue).Sum(i => i.Duration!.Value.Ticks));
        var fileSize = FilteredItems.Sum(i => i.FileSize ?? 0L);

        UI(() =>
        {
            StatusBar.ItemCount = count;
            StatusBar.ItemLabel = label;
            StatusBar.ItemDuration = duration;
            StatusBar.ItemFileSize = fileSize;
        });
    }

    #endregion

    #region Utils



    private async Task LoadPodcastArtAsync(string url, Models.PodcastEpisode episode, Models.PodcastFeed feed)
    {
        try
        {
            var bytes = Helpers.ImageDecoder.EnsureRasterBytes(await ArtworkSource.Http.GetByteArrayAsync(url));
            var bitmap = ArtworkSource.BitmapFromBytes(bytes);
            if (bitmap == null) return;
            UI(() =>
            {
                // Only assign if this episode is still the one playing -- the
                // user may have hopped to a different episode while we were
                // downloading the artwork.
                if (_currentPodcastStream is { } ps && ps.Episode.Id == episode.Id)
                {
                    CurrentAlbumArt = bitmap;
                    _nowPlaying?.SetArtwork(ArtworkSource.ToOsArtworkBytes(bitmap, bytes));
                }
            });
        }
        catch
        {
            // Best-effort -- if the image host is down or the URL is bad, the
            // LCD just stays with the music-note placeholder.
        }
    }

    // The tuned station's favicon, kept for the life of the station: it's the art floor
    // radio falls back to whenever the current track carries no artwork of its own.
    private Bitmap? _stationArtBitmap;
    private byte[]? _stationArtBytes;

    // The art bytes CURRENTLY on the OS now-playing surface for radio - station favicon
    // or a per-track cover, whichever is showing. An ICY title update rebuilds the whole
    // SMTC entry, and without carrying this the thumbnail is dropped every time the song
    // changes: the art flashed in on tune-in, then vanished on the first "now playing".
    private byte[]? _currentRadioArtBytes;

    private async Task LoadFaviconAsync(string url)
    {
        try
        {
            // SVG station logos become PNG bytes here, so the bitmap decode below AND the
            // OS now-playing surfaces (SMTC/macOS) all receive something they can render.
            var bytes = Helpers.ImageDecoder.EnsureRasterBytes(await ArtworkSource.Http.GetByteArrayAsync(url));
            var bitmap = ArtworkSource.BitmapFromBytes(bytes);
            if (bitmap != null)
            {
                // PNG-normalized for the OS surface; the raw favicon can be a format WIC
                // won't decode even though Skia just did (that's why the app showed it).
                var osBytes = ArtworkSource.ToOsArtworkBytes(bitmap, bytes);
                UI(() =>
                {
                    _stationArtBitmap = bitmap;
                    _stationArtBytes = osBytes;
                    // A per-track cover may have landed before the favicon finished
                    // downloading - never stomp real track art with the station logo.
                    if (_radioTrackArtActive)
                    {
                        return;
                    }
                    // Don't dispose - Avalonia's ref-counted bitmap lifecycle handles cleanup.
                    // Explicit Dispose() while a render pass is in flight causes ObjectDisposedException.
                    CurrentAlbumArt = bitmap;
                    // The now-playing widgets only learn the artwork once the favicon
                    // download finishes - push it to the current track's cover.
                    _currentRadioArtBytes = osBytes;
                    _nowPlaying?.SetArtwork(osBytes);
                });
            }
        }
        catch
        {
            // Favicon unavailable, keep default icon
        }
    }

    // True while the art slot shows a per-track cover instead of the station favicon -
    // lets a late-finishing favicon download know not to stomp real track art.
    private bool _radioTrackArtActive;

    /// <summary>Radio LCD back to station identity (the tune-in look): name + tags, station art pushed to SMTC. UI thread only; art slot reverts separately via <see cref="LoadRadioTrackArtAsync"/>(null).</summary>
    private void RestoreStationBranding()
    {
        if (CurrentStation is not { } station)
        {
            return;
        }
        CurrentTrackLine1 = station.Title ?? "Unknown Station";
        CurrentTrackLine2 = FormatTags(station.Tags);
        _currentRadioArtBytes = _stationArtBytes;
        PushNowPlaying(new NowPlayingMetadata(station.Title, station.Tags, "Internet Radio", ArtUri: station.FaviconUrl, ArtBytes: _stationArtBytes));
    }

    /// <summary>
    /// Per-track radio artwork from the stream's metadata channel (iHeart EXTINF art URL,
    /// or VLC's own file:// art-cache path for streams with embedded pictures). An empty
    /// or missing URL means the current track has none - revert to the station favicon,
    /// which is always the fallback. Art is decoration: every failure lands on the favicon.
    /// </summary>
    private async Task LoadRadioTrackArtAsync(string? url)
    {
        var epoch = _playbackEpoch;

        if (string.IsNullOrWhiteSpace(url))
        {
            _radioTrackArtActive = false;
            CurrentAlbumArt = _stationArtBitmap;
            _currentRadioArtBytes = _stationArtBytes;
            PushNowPlaying(new NowPlayingMetadata(CurrentTrackLine1, CurrentTrackLine2, CurrentStation?.Title, ArtUri: CurrentStation?.FaviconUrl, ArtBytes: _stationArtBytes));
            return;
        }

        try
        {
            // iHeart's catalog URLs arrive with a small fit() baked in (typically 200×200),
            // but the ops parameter is server-side resizable - ask for a size worthy of the
            // art slot instead of upscaling a thumbnail.
            if (url.Contains("i.iheart.com/", StringComparison.OrdinalIgnoreCase))
            {
                url = System.Text.RegularExpressions.Regex.Replace(url, @"fit\(\d+,\d+\)", "fit(600,600)");
            }

            // VLC's art cache hands us file:// URLs; the session's injected URLs are http(s).
            var raw = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? await System.IO.File.ReadAllBytesAsync(new Uri(url).LocalPath)
                : await ArtworkSource.Http.GetByteArrayAsync(url);
            var bytes = Helpers.ImageDecoder.EnsureRasterBytes(raw);
            var bitmap = ArtworkSource.BitmapFromBytes(bytes);
            if (bitmap == null)
            {
                return;
            }
            UI(() =>
            {
                // The fetch raced a station switch - this cover belongs to the old epoch.
                if (epoch != _playbackEpoch)
                {
                    return;
                }
                _radioTrackArtActive = true;
                // Don't dispose - Avalonia's ref-counted bitmap lifecycle handles cleanup.
                CurrentAlbumArt = bitmap;
                var osBytes = ArtworkSource.ToOsArtworkBytes(bitmap, bytes);
                _currentRadioArtBytes = osBytes;
                PushNowPlaying(new NowPlayingMetadata(CurrentTrackLine1, CurrentTrackLine2, CurrentStation?.Title, ArtUri: CurrentStation?.FaviconUrl, ArtBytes: osBytes));
            });
        }
        catch
        {
            // Track art unavailable - the favicon (or whatever is showing) stands.
        }
    }

    private void ApplyPerTrackOptions(MediaItem item)
    {
        // Per-track volume adjustment goes into the sink-bus master volume,
        // not LibVLC - keeping LibVLC at 100 means the FFT analyzer always
        // sees the source track's real amplitude regardless of playback gain.
        _perTrackMultiplier = 1.0 + (item.VolumeAdjustment / 100.0);
        UpdateMasterVolume();

        // Equalizer preset
        if (!string.IsNullOrEmpty(item.EqPreset))
        {
            try
            {
                using var tempEq = new Equalizer();
                var count = tempEq.PresetCount;
                for (uint i = 0; i < count; i++)
                {
                    if (tempEq.PresetName(i) == item.EqPreset)
                    {
                        _player.SetEqualizer(new Equalizer(i));
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                // Get Info still says "EQ: Rock" - the user must at least be able to grep why it's flat.
                _log.Warning(ex, "Per-track EQ preset '{Preset}' failed to apply", item.EqPreset);
            }
        }
        else
        {
            try
            {
                _player.UnsetEqualizer();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to clear the equalizer");
            }
        }

        // Start time: seek after a brief delay to let playback begin
        if (item.UseStartTime && item.StartTime.HasValue)
        {
            // Snapshot the engine: it can be torn down during the 100 ms delay, and the old
            // null-forgiving deref was an NRE inside a discarded task.
            var engine = _flacEngine;
            FireAndForget(Task.Run(async () =>
            {
                await Task.Delay(100);
                if (EngineActive)
                {
                    engine?.SeekMs((long)item.StartTime.Value.TotalMilliseconds);
                }
                else if (_player.IsPlaying)
                {
                    _player.Time = (long)item.StartTime.Value.TotalMilliseconds;
                }
            }), "start-time seek");
        }
    }

    private static string FormatTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return string.Empty;
        }

        return string.Join(" \u00B7 ", tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static void UI(Action action)
    {
        Dispatcher.UIThread.Post(action);
    }

    /// <summary>
    /// Async overload: an <c>async</c> lambda handed to the Action overload becomes
    /// async-void - its exceptions bypass every handler and take the process down (or
    /// vanish, depending on the runtime's mood). This overload awaits the task and turns
    /// a fault into a log line instead.
    /// </summary>
    private static void UI(Func<Task> action)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Async UI task faulted");
            }
        });
    }

    /// <summary>Shim over <see cref="TaskObserver.FireAndForget"/> - kept so the VM's many call sites stay short.</summary>
    private static void FireAndForget(Task task, string what) => TaskObserver.FireAndForget(task, what);






    #endregion



    public void Dispose()
    {
        _vmCts.Cancel();   // stop background loops (job reattach polling) before teardown

        // The release-picker delegate captures this VM - a stale hook would drive a dead window.
        CdAudioService.ChooseRelease = null;

        // Detach from the process-wide singletons first: they outlive this ViewModel, and a
        // handler left attached keeps the whole VM (and its window) alive and reachable.
        if (_onSubscriptionsRefreshed is not null)
        {
            Services.Podcast.PodcastSubscriptionService.Instance.RefreshCompleted -= _onSubscriptionsRefreshed;
        }
        var downloads = Services.Podcast.PodcastDownloadService.Instance;
        if (_onDownloadStarted is not null) { downloads.Started -= _onDownloadStarted; }
        if (_onDownloadProgress is not null) { downloads.ProgressChanged -= _onDownloadProgress; }
        if (_onDownloadCompleted is not null) { downloads.Completed -= _onDownloadCompleted; }
        if (_onDownloadFailed is not null) { downloads.Failed -= _onDownloadFailed; }

        // The share browse ticks every 30s against MediaCache and the sidebar; left running
        // it kept scanning through a disposed VM.
        _shareScanTimer?.Stop();
        _shareScanTimer = null;
        _podcastCheckTimer?.Stop();
        _podcastCheckTimer = null;

        // Keep Running After OrgZ Closes > Library sharing: unchecked means the share dies
        // with the app (LoadAsync re-asserts it next launch while the setting stays on).
        // Bounded wait - shutdown must not hang on a dead service socket.
        if (Settings.Get("OrgZ.Services.Sharing.Enabled", false) && !Settings.Get("OrgZ.Services.KeepAlive.Sharing", false))
        {
            try
            {
                Services.DeviceHelper.DeviceHelperClient.StopShareAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Stopping the share on exit failed");
            }
        }

        // In-flight jobs stop with the window that started them.
        _ripCts?.Cancel();
        _burnCts?.Cancel();
        _deviceSyncCts?.Cancel();

        // The bit-perfect engine owns a decoder process and a pump thread.
        _flacEngine?.Dispose();

        _folderWatcher?.Dispose();
        _deviceDetection?.Dispose();
        foreach (var scanCts in _deviceScanCts.Values)
        {
            scanCts.Cancel();   // disposal happens in the scan's own finally
        }
#if WINDOWS
        _thumbBarService?.Dispose();
#endif
        _nowPlaying?.Dispose();
        _audioOutput.SavePersistedSelections();
        _audioTap?.Dispose();
        _audioOutput.Dispose();

        // Say goodbye on the way out, in this order.
        //
        // The sessions go first (above), then the services they advertised, then the responder
        // that carries the announcements - a responder disposed first would have no socket left
        // to send the goodbyes through. Without this the records simply stop being refreshed,
        // and every receiver on the network keeps a cached iTunes_Ctrl_ entry pointing at a port
        // that died with the process. A receiver that resolves a control endpoint it cannot
        // reach is a receiver that greys out its buttons for the next session.
        Services.AudioOutput.AirPlay.DacpControlServer.Shutdown();
        Services.AudioOutput.AirPlay.PtpClock.Shutdown();
        Services.Sharing.MdnsAdvertiser.Shutdown();
        var pendingCts = Interlocked.Exchange(ref _radioSwitchCts, null);
        pendingCts?.Cancel();
        pendingCts?.Dispose();
        _radioStream?.Session.Dispose();
        if (_currentMedia != null && _currentMediaMetaHandler != null)
        {
            _currentMedia.MetaChanged -= _currentMediaMetaHandler;
            _currentMediaMetaHandler = null;
        }
        _currentMedia?.Dispose();
        _radioStream?.Input.Dispose();
        _radioStream = null;
        _player?.Dispose();
        _vlc?.Dispose();
    }
}
