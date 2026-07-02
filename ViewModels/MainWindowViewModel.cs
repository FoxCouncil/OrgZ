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
using System.Net.Http;
using Serilog;

namespace OrgZ.ViewModels;

internal partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private static readonly ILogger _log = Logging.For<MainWindowViewModel>();

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

#if WINDOWS
    private SmtcService? _smtcService;
    private TaskbarThumbBarService? _thumbBarService;
#endif

    private MprisService? _mprisService;
    private MacNowPlayingService? _macNowPlaying;

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

    private PlaybackContext? _playbackContext;

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
    [NotifyPropertyChangedFor(nameof(IsCdViewActive), nameof(SearchPlaceholder), nameof(ShowNoSearchResults), nameof(ShowBurnButton), nameof(CanSyncToIPod), nameof(CanSyncPodcasts), nameof(CanSyncToDevice))]
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

    /// <summary>
    public PodcastsViewModel Podcasts { get; private set; } = null!;

    public AudiobooksViewModel Audiobooks { get; private set; } = null!;

    /// <summary>
    /// Rebuilds the LibraryItems list. Called on startup and when settings like "Show Ignored in sidebar" change.
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

        if (Settings.Get("OrgZ.ShowIgnored", true))
        {
            LibraryItems.Add(new() { Name = "Ignored", Icon = "fa-solid fa-eye-slash", Category = "LIBRARY", IsEnabled = true, ViewConfigKey = "Ignored" });
        }

        if (Settings.Get("OrgZ.BadFormat.ShowInSidebar", true))
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

    [ObservableProperty]
    private bool _isButtonPlayPauseEnabled = true;

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
    private string _shuffleIcon = "fa-solid fa-shuffle";

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
    // Transcode aborts immediately; an in-flight elevated burn is left to finish the
    // current disc (cancelling a half-written disc just makes a coaster).
    private CancellationTokenSource? _burnCts;

    [RelayCommand]
    private void CancelRip()
    {
        _ripCts?.Cancel();
        _burnCts?.Cancel();
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
    [NotifyPropertyChangedFor(nameof(ShowNoSearchResults), nameof(NoSearchResultsMessage))]
    // Not persisted across app launches - search is always transient state.
    // Per-view search is stored in _searchTextByView and swapped on sidebar changes.
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSearchResults))]
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
    /// DataGrid-bound view for the active UNGROUPED view (Music, Favorites, Playlists, ...),
    /// wrapping that view's filtered item list. Bound to MainDataGrid. Grouped views use
    /// <see cref="GroupedItemsView"/> instead so the two grids never thrash each other's source.
    /// </summary>
    [ObservableProperty]
    private DataGridCollectionView? _filteredItemsView;

    /// <summary>
    /// DataGrid-bound view for the active GROUPED view (Radio), wrapping its filtered list with
    /// <c>GroupDescriptions</c> for Avalonia's collapsible group headers. Bound to GroupedDataGrid.
    /// Kept separate from <see cref="FilteredItemsView"/> so switching to an ungrouped view never
    /// reassigns the grouped grid's source - that's what lets the grid retain its row-group collapse
    /// state across view switches (no rebuild, no collapse flash).
    /// </summary>
    [ObservableProperty]
    private DataGridCollectionView? _groupedItemsView;

    /// <summary>
    /// DataGrid-bound view for a device Podcasts view - grouped by show, bound to the dedicated
    /// PodcastGroupedDataGrid. Separate from <see cref="GroupedItemsView"/> because that grid carries
    /// Radio's columns and the shared grid can't rebuild columns for a second set (Avalonia
    /// spacer-column crash); the podcast grid builds its own podcast columns once.
    /// </summary>
    [ObservableProperty]
    private DataGridCollectionView? _podcastGroupedItemsView;

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

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isSyncing;

    // -- Computed --

    private IEnumerable<MediaItem> MusicItems => _allItems.Where(i => i.Kind == MediaKind.Music);

    internal bool IsMediaLoaded => _player?.Media != null;

    internal Action? ScrollToSelectedRequested;
    internal Func<double>? GetScrollOffset;
    internal Action<double>? SetScrollOffset;
    internal Action? PlaylistsChanged;

    // -- Change Handlers --

    partial void OnShuffleModeChanged(ShuffleMode value)
    {
        ShuffleOpacity = value == ShuffleMode.On ? 1.0 : 0.4;
        _playbackContext?.SetShuffle(value == ShuffleMode.On);
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

        // The Audiobooks composite has ONE search - this box. ApplyFilter above already filtered
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
    /// Brings the main window back from the mini-player (iTunes-style) hidden
    /// state.  Works whether the mini-player is currently open or not - useful
    /// as a Window-menu fallback if the mini-player was closed while main was
    /// still hidden and the user lost track of the app.
    /// </summary>
    [RelayCommand]
    internal void ShowMainWindow()
    {
        _window.Show();
        _window.Activate();

        if (_miniPlayer != null)
        {
            _miniPlayer.Close();
        }
    }

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

        SidebarItem? target = null;

        // Device tracks → find the matching device sidebar entry
        if (item.Source?.StartsWith("device:") == true)
        {
            var viewKey = $"Device:{item.Source["device:".Length..]}";
            target = DeviceItems.FirstOrDefault(i => i.ViewConfigKey == viewKey);
        }
        // CD tracks → find the CdAudio sidebar entry
        else if (item.Source == "cdda")
        {
            target = DeviceItems.FirstOrDefault(i => i.ViewConfigKey == "CdAudio");
        }
        // Library tracks
        else
        {
            target = item.Kind switch
            {
                MediaKind.Music => LibraryItems.FirstOrDefault(i => i.Kind == MediaKind.Music),
                MediaKind.Radio => LibraryItems.FirstOrDefault(i => i.Kind == MediaKind.Radio),
                _ => null
            };
        }

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
        // Fires BEFORE SelectedSidebarItem is actually updated. SearchText still reflects
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

        _ = BuildPlaylistHeaderAsync(value);

        _activeViewConfig = ListViewConfigs.Get(value?.ViewConfigKey);

        if (!string.IsNullOrEmpty(value?.ViewConfigKey))
        {
            Settings.Set("OrgZ.ActiveView", value.ViewConfigKey);
            Settings.Save();
        }

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
            // same list + DataGridCollectionView. For the grouped grid the ItemsSource instance is
            // unchanged, so BindActiveView is a no-op on the binding and the grid keeps its
            // row-group collapse + scroll state - instant switch, nothing to re-collapse.
            FilteredItems = cached.Items;
            BindActiveView(_activeViewConfig, cached.View);
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

            // Global ignore filter - hide ignored items from every view except the Ignored view itself
            if (!_activeViewConfig.IncludeIgnored)
            {
                items = items.Where(i => !i.IsIgnored);
            }

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
            BindActiveView(_activeViewConfig, view);

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
            BindActiveView(_activeViewConfig, new DataGridCollectionView(FilteredItems));
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

    /// <summary>
    /// Routes a collection view to the grid that renders the active view: grouped views drive
    /// GroupedDataGrid via <see cref="GroupedItemsView"/>, everything else drives MainDataGrid via
    /// <see cref="FilteredItemsView"/>. Re-assigning the grouped grid the same instance it already
    /// holds is a binding no-op - that's exactly why re-entering Radio doesn't rebuild or flash.
    /// </summary>
    private void BindActiveView(ListViewConfig config, DataGridCollectionView view)
    {
        switch (config.Host)
        {
            case ViewHost.PodcastGroupedGrid:
            {
                PodcastGroupedItemsView = view;
            }
            break;

            case ViewHost.GroupedGrid:
            {
                GroupedItemsView = view;
            }
            break;

            default:
            {
                FilteredItemsView = view;
            }
            break;
        }
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
            InitializePlayback();
        }

        ButtonPlayPausePadding = ICON_PLAY_PADDING;

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
        Services.Podcast.PodcastSubscriptionService.Instance.RefreshCompleted += () => UI(() => Podcasts.ReloadSubscriptions());
        if (Services.Podcast.PodcastSettings.IsDueForCheck)
        {
            _ = Services.Podcast.PodcastSubscriptionService.Instance.RefreshNowAsync(App.FolderPath);
        }

        // Surface podcast download progress on the LCD busy display (same as ripping/import).
        // The service's events fire from background threads, so marshal each to the UI thread.
        var podcastDownloads = Services.Podcast.PodcastDownloadService.Instance;
        podcastDownloads.Started += (_, ep) => UI(() => OnPodcastDownloadStarted(ep));
        podcastDownloads.ProgressChanged += p => UI(() => OnPodcastDownloadProgress(p));
        podcastDownloads.Completed += (_, ep) => UI(() => OnPodcastDownloadFinished(ep.Id));
        podcastDownloads.Failed += (epId, _) => UI(() => OnPodcastDownloadFinished(epId));

        // Initialize shuffle/repeat visual state from saved settings
        ShuffleOpacity = ShuffleMode == ShuffleMode.On ? 1.0 : 0.4;
        RepeatIcon = RepeatMode == RepeatMode.One ? "fa-solid fa-arrow-rotate-left" : "fa-solid fa-repeat";
        RepeatOpacity = RepeatMode == RepeatMode.Off ? 0.4 : 1.0;

        RebuildLibraryItems();

        var savedView = Settings.Get("OrgZ.ActiveView", "Music");
        SelectedSidebarItem = PlaylistItems.FirstOrDefault(i => i.ViewConfigKey == savedView) ?? LibraryItems.FirstOrDefault(i => i.ViewConfigKey == savedView) ?? LibraryItems[0];
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
        // with the user's volume slider.  Volume is applied ONLY in the
        // sink bus (MasterVolume) and per-sink, which sit after the tap.
        _player.Volume = 100;

        // Attach the audio tap BEFORE any Play() call - LibVLC only routes
        // samples through SetAudioCallbacks for media that start playing
        // after the callbacks were registered.  Once wired, every track the
        // user plays funnels through the sink bus (audible on selected
        // devices) and the FFT analyzer (VU-meter data).
        _audioTap = new OrgZ.Services.AudioVisualization.AudioTap(_audioOutput.Bus);
        _audioTap.Attach(_player);
        _audioOutput.LoadAndApplyPersistedSelections();
        UpdateMasterVolume();

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

        _player.EndReached += (s, e) => UI(() =>
        {
            // A finished audiobook starts from the top next time - clear its resume point (the
            // throttle only gets within ~5s of the end; this is the authoritative reset).
            if (CurrentPlayingItem is { Kind: MediaKind.Audiobook, Source: null } finishedBook)
            {
                finishedBook.LastPositionMs = 0;
                var finishedId = finishedBook.Id;
                _ = Task.Run(() => MediaCache.UpdatePlaybackPosition(finishedId, 0));
            }

            if (CurrentStation != null)
            {
                ClearPlayback();
                UpdateMainStatus("Stream ended");
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
        });

        _player.Paused += (s, e) => UI(() =>
        {
            ButtonPlayPauseIcon = ICON_PLAY;
            ButtonPlayPausePadding = ICON_PLAY_PADDING;

#if WINDOWS
            _smtcService?.SetPlaybackStatus(MediaPlaybackStatus.Paused);
            _thumbBarService?.SetPlayingState(false);
#endif
            _mprisService?.SetPlaybackStatus("Paused");
            _macNowPlaying?.SetPlaybackStatus("Paused");

            UpdateMainStatus("Paused");
        });

        _player.Playing += (s, e) => UI(() =>
        {
            // Don't clear IsPlaybackLoading here -- libvlc fires Playing the
            // instant it has a Media, which can be well before actual audio
            // reaches the tap. AudioTap.AudioStarted is the precise signal.
            ButtonPlayPauseIcon = ICON_PAUSE;
            ButtonPlayPausePadding = ICON_PAUSE_PADDING;

#if WINDOWS
            _smtcService?.SetPlaybackStatus(MediaPlaybackStatus.Playing);
            _thumbBarService?.SetPlayingState(true);
#endif
            _mprisService?.SetPlaybackStatus("Playing");
            _macNowPlaying?.SetPlaybackStatus("Playing");

            UpdateMainStatus("Playing");
        });

        long _lastMacNowPlayingPushMs = long.MinValue;
        _player.TimeChanged += (s, e) => UI(() =>
        {
            CurrentTrackTime = FormatHelper.FormatDurationCompact(e.Time);
            if (!isSeeking)
            {
                CurrentTrackTimeNumber = e.Time;
            }

            // Persist the podcast resume position, throttled to ~5s of movement (also
            // catches seeks). Runs off the UI thread; UpdateListenPosition opens its own
            // connection, so it's safe.
            if (_currentPodcastStream is { } ps && e.Time > 0 && Math.Abs(e.Time - _lastPodcastSaveMs) >= 5000)
            {
                _lastPodcastSaveMs = e.Time;
                var episodeId = ps.Episode.Id;
                var posMs = e.Time;
                var len = _player.Length;
                var completed = len > 0 && posMs >= len - 15000;
                _ = Task.Run(() => Services.Podcast.PodcastCache.UpdateListenPosition(episodeId, posMs, completed));
            }

            // Audiobook resume position, same ~5s throttle (Math.Abs also catches seeks). Within
            // the last 15s counts as finished - the resume point resets so the next play starts
            // from the top instead of the credits.
            if (CurrentPlayingItem is { Kind: MediaKind.Audiobook, Source: null } book && e.Time > 0 && Math.Abs(e.Time - _lastAudiobookSaveMs) >= 5000)
            {
                _lastAudiobookSaveMs = e.Time;
                var len = _player.Length;
                var pos = len > 0 && e.Time >= len - 15000 ? 0 : e.Time;
                book.LastPositionMs = pos;
                var bookId = book.Id;
                _ = Task.Run(() => MediaCache.UpdatePlaybackPosition(bookId, pos));
            }

            // Push pivots to macOS Now Playing: the very first TimeChanged (so
            // the widget locks onto libvlc's clock instead of extrapolating from
            // 0), every 5 s as a re-sync against any drift, and on a rewind
            // (track change → e.Time resets to 0). The widget extrapolates
            // smoothly between pivots at rate=1, which matches OrgZ's display
            // much better than flooding macOS with 4 Hz updates - the widget
            // appeared to coalesce / lag those, ending up several seconds behind.
            if (_macNowPlaying is not null)
            {
                bool firstPush = _lastMacNowPlayingPushMs == long.MinValue;
                bool rewound = e.Time < _lastMacNowPlayingPushMs;
                bool resyncDue = e.Time - _lastMacNowPlayingPushMs >= 5000;
                if (firstPush || rewound || resyncDue)
                {
                    _lastMacNowPlayingPushMs = e.Time;
                    _macNowPlaying.SetPlaybackPosition(TimeSpan.FromMilliseconds(e.Time), 1.0);
                }
            }

            // Stop time check for per-track options
            var playing = CurrentPlayingItem;
            if (playing is { UseStopTime: true, StopTime: not null })
            {
                if (e.Time >= (long)playing.StopTime.Value.TotalMilliseconds)
                {
                    ButtonNextTrack();
                }
            }
        });

        _player.Stopped += (s, e) => UI(() =>
        {
            // A Stop() we issued to switch tracks - keep the loading/barber-pole state and
            // the now-playing UI we just set for the incoming track.
            if (_suppressStoppedLoadingClear)
            {
                _suppressStoppedLoadingClear = false;
                return;
            }

            IsPlaybackLoading = false;
            ButtonPlayPauseIcon = ICON_PLAY;
            ButtonPlayPausePadding = ICON_PLAY_PADDING;

#if WINDOWS
            _smtcService?.SetPlaybackStatus(MediaPlaybackStatus.Stopped);
            _thumbBarService?.SetPlayingState(false);
#endif
            _mprisService?.SetPlaybackStatus("Stopped");
            _macNowPlaying?.SetPlaybackStatus("Stopped");

            UpdateMainStatus("Stopped");
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
                    _pendingDurationMs = e.Media.Duration > 0 ? e.Media.Duration : null;
                    ApplyPendingDuration();
                }

                var mountPath = CurrentPlayingItem.Source["device:".Length..];
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

            _pendingDurationMs = e.Media.Duration > 0 ? e.Media.Duration : null;
            ApplyPendingDuration();

            CurrentTrackLine1 = CurrentFileItem?.Title ?? "Unknown Title";
            var artist = CurrentFileItem?.Artist ?? "Unknown Artist";
            var album = CurrentFileItem?.Album;
            CurrentTrackLine2 = string.IsNullOrWhiteSpace(album) ? artist : $"{artist} \u2014 {album}";
        });

        // Linux shell integration - GNOME/KDE/XFCE media keys + panel widgets. Failure
        // here is non-fatal: if the session bus isn't reachable the service quietly
        // disables itself and the rest of the app keeps working.
        if (OperatingSystem.IsLinux())
        {
            _mprisService = new MprisService();
            // EVERY MPRIS callback fires from the Tmds.DBus worker thread. VM state
            // touches (player control, property changes) MUST happen on the UI thread
            // or Avalonia bindings will crash with cross-thread access violations.
            _mprisService.PlayRequested     += () => Dispatcher.UIThread.Post(Play);
            _mprisService.PauseRequested    += () => Dispatcher.UIThread.Post(Pause);
            _mprisService.PlayPauseRequested += () => Dispatcher.UIThread.Post(ButtonPlayPause);
            _mprisService.NextRequested     += () => Dispatcher.UIThread.Post(ButtonNextTrack);
            _mprisService.PreviousRequested += () => Dispatcher.UIThread.Post(ButtonPreviousTrack);
            _mprisService.StopRequested     += () => Dispatcher.UIThread.Post(Stop);
            _mprisService.RaiseRequested    += () => Dispatcher.UIThread.Post(() =>
            {
                if (_window.WindowState == Avalonia.Controls.WindowState.Minimized)
                {
                    _window.WindowState = Avalonia.Controls.WindowState.Normal;
                }
                _window.Activate();
            });
            _ = _mprisService.InitializeAsync();
        }

        // macOS Control Center / lock screen / media-key widget. One-way for now
        // (we publish metadata + transport state; remote commands need ObjC block
        // bridging and arrive in a follow-up).
        if (OperatingSystem.IsMacOS())
        {
            _macNowPlaying = new MacNowPlayingService();
            // ObjC dispatches the remote-command callbacks on the main dispatch
            // queue (the AppKit thread we're already on for UI work), but post
            // anyway so a future libdispatch routing change doesn't crash us.
            _macNowPlaying.PlayRequested      += () => Dispatcher.UIThread.Post(Play);
            _macNowPlaying.PauseRequested     += () => Dispatcher.UIThread.Post(Pause);
            _macNowPlaying.PlayPauseRequested += () => Dispatcher.UIThread.Post(ButtonPlayPause);
            _macNowPlaying.NextRequested      += () => Dispatcher.UIThread.Post(ButtonNextTrack);
            _macNowPlaying.PreviousRequested  += () => Dispatcher.UIThread.Post(ButtonPreviousTrack);
            _macNowPlaying.StopRequested      += () => Dispatcher.UIThread.Post(Stop);
        }
    }

#if WINDOWS
    internal void InitializeSmtc(IntPtr hwnd)
    {
        _smtcService = new SmtcService();
        if (!_smtcService.Initialize(hwnd))
        {
            UpdateMainStatus(_smtcService.InitDiagnostics ?? "SMTC: Init failed (unknown)");
            _smtcService.Dispose();
            _smtcService = null;
            return;
        }

        UpdateMainStatus(_smtcService.InitDiagnostics ?? "SMTC: OK");

        _smtcService.PlayPauseRequested += ButtonPlayPause;
        _smtcService.NextRequested += ButtonNextTrack;
        _smtcService.PreviousRequested += ButtonPreviousTrack;
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
        if (_playbackContext == null || !_playbackContext.HasPrevious)
        {
            return;
        }

        var prev = _playbackContext.MovePrevious()!;
        ExecutePlayItem(prev);
    }

    [RelayCommand]
    public void ButtonPlayPause()
    {
        UI(() =>
        {
            if (_player == null)
            {
                return;
            }

            // Pause / resume / stop ALWAYS acts on what is actually playing - never on the
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
            if (_player.IsPlaying)
            {
                Pause();
                return;
            }

            // Paused or stopped with a track still loaded - resume / restart it.
            if (_player.State == LibVLCSharp.Shared.VLCState.Paused
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
        if (_playbackContext == null || !_playbackContext.HasNext)
        {
            return;
        }

        var next = _playbackContext.MoveNext()!;
        ExecutePlayItem(next);
    }

    [RelayCommand]
    private async Task ChangeLibraryFolder()
    {
        var folders = await _window.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Select OrgZ Folder",
                AllowMultiple = false
            });

        if (folders.Count == 0)
        {
            return;
        }

        Stop();
        ClearPlayback();

        App.FolderPath = folders[0].Path.LocalPath;
        Settings.Set("OrgZ.FolderPath", App.FolderPath);
        Settings.Save();

        _allItems.RemoveAll(i => i.Kind == MediaKind.Music);
        FilteredItems = [];

        _folderWatcher?.Stop();
        await ScanAndAnalyzeLibraryAsync();
        StartFolderWatcher();
    }

    [RelayCommand]
    private void ExitApplication()
    {
        _window.Close();
    }

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

        // Sidebar composition depends on OrgZ.ShowIgnored - refresh in case it was toggled
        RebuildLibraryItems();

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
            _smtcService?.SetPlaybackStatus(MediaPlaybackStatus.Stopped);
            _thumbBarService?.SetPlayingState(false);
#endif

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

#if WINDOWS
        _smtcService?.UpdateMetadata(track.Title, track.Artist, track.Album, null);
#endif
        _mprisService?.SetMetadata(track.Title, track.Artist, track.Album, null);
        _macNowPlaying?.SetMetadata(track.Title, track.Artist, track.Album, track.Duration, _cdCoverArtBytes);

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
        // AFTER BeginPlayback clears the LCD time labels so the total time
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
        _ = _player.Play(_currentMedia);
        DeferDispose(previousMedia, previousHandler);

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
        Settings.Save();
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
        _player.Time = CurrentTrackTimeNumber;
    }

    #endregion

    #region Playback Controls

    internal void PlayMusicItem(MediaItem? file)
    {
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

        if (string.IsNullOrEmpty(file.FilePath))
        {
            return;
        }

        UI(() =>
        {
            // Reuse the existing context only when the current view's filter
            // produces the SAME source list -- so a search that narrows the
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
            _playbackContext = new PlaybackContext(FilteredItems, file, ShuffleMode == ShuffleMode.On) { RepeatMode = RepeatMode };
            OnPropertyChanged(nameof(PlaybackContextUpcoming));
            ExecutePlayMusic(file);
        });
    }

    internal void PlayRadioStation(MediaItem? station)
    {
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

                if (_playbackContext != null
                    && _playbackContext.MatchesSource(FilteredItems)
                    && _playbackContext.JumpTo(station))
                {
                    OnPropertyChanged(nameof(PlaybackContextUpcoming));
                    ExecutePlayRadio(station);
                    return;
                }

                _playbackContext?.Release();
                _playbackContext = new PlaybackContext(FilteredItems, station, ShuffleMode == ShuffleMode.On) { RepeatMode = RepeatMode };
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
    /// iTunes "Sound Check": when enabled, level playback loudness across tracks. Combined model -
    /// if the file already carries a ReplayGain tag, VLC applies that precise, one-time-computed
    /// value (audio-replay-gain-mode=track); if not, VLC normalizes naturally in real time
    /// (normvol) AND a one-shot background pass measures + tags the file so next play is precise.
    /// Radio streams (no MediaItem) always take the normvol path. Set per-media, so it takes effect
    /// from the next track; the filter runs before OrgZ's audio tap, leaving the VU meter and sink
    /// volume untouched.
    /// </summary>
    private void ApplyAudioNormalization(Media media, MediaItem? item)
    {
        if (!Settings.Get("OrgZ.NormalizeVolume", false))
        {
            return;
        }

        if (item?.HasReplayGain == true)
        {
            media.AddOption(":audio-replay-gain-mode=track");
            // Boosting a quiet track toward the reference could push its peaks past 0 dBFS -
            // peak protection caps the gain so a loud transient can't clip.
            media.AddOption(":audio-replay-gain-peak-protection");
            return;
        }

        // No precomputed gain: normalize live now, and tag this file in the background so it gets
        // the precise value for next time. Only worth doing for a real local file.
        media.AddOption(":audio-filter=normvol");
        media.AddOption(":norm-max-level=2.0");

        if (item is { FilePath: { } path } && item.Kind is MediaKind.Music or MediaKind.Audiobook && File.Exists(path) && ResolveFfmpeg() is { } ffmpeg)
        {
            _ = Task.Run(async () =>
            {
                var gain = await ReplayGainService.ComputeAndTagAsync(path, ffmpeg);
                if (gain is { } g)
                {
                    UI(() =>
                    {
                        item.ReplayGainTrackGainDb = g;
                        _ = Task.Run(() => MediaCache.UpdateReplayGain(item.Id, g));
                    });
                }
            });
        }
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
        artBytes ??= ExtractAlbumArtBytes(file.FilePath!);
        CurrentAlbumArt = artBytes != null ? BitmapFromBytes(artBytes) : null;

#if WINDOWS
        _smtcService?.UpdateMetadata(file.Title, file.Artist, file.Album, artBytes);
#endif
        _mprisService?.SetMetadata(file.Title, file.Artist, file.Album, string.IsNullOrEmpty(file.FilePath) ? null : new Uri(file.FilePath).AbsoluteUri);
        _macNowPlaying?.SetMetadata(file.Title, file.Artist, file.Album, file.Duration, artBytes);

        var previousMedia = _currentMedia;
        var previousHandler = _currentMediaMetaHandler;
        _currentMediaMetaHandler = null;
        _currentMedia = new Media(_vlc, file.FilePath!, FromType.FromPath);
        ApplyVisualizerOption(_currentMedia);
        ApplyAudioNormalization(_currentMedia, file);

        // Local file - opens instantly, so skip the barber pole.
        NewPlaybackEpoch();
        BeginPlayback(showLoading: false);

        // Audiobooks resume where they left off - same applied-once-audio-starts machinery as
        // podcast resume. Skip a barely-started position (re-seeking to 0:04 is noise, not resume).
        if (file.Kind == MediaKind.Audiobook && file.Source == null && file.LastPositionMs > 10_000)
        {
            _pendingResumeMs = file.LastPositionMs;
        }
        _lastAudiobookSaveMs = 0;

        _ = _player.Play(_currentMedia);
        DeferDispose(previousMedia, previousHandler);

        ApplyPerTrackOptions(file);

        file.LastPlayed = DateTime.UtcNow;
        file.PlayCount++;
        MediaCache.SetLastPlayed(file.Id, file.LastPlayed.Value);
        MediaCache.IncrementPlayCount(file.Id);

        UpdateNavigationButtons();
    }

    private void ExecutePlayRadio(MediaItem station)
    {
        SelectedItem = station;

        // Don't dispose - Avalonia's ref-counted bitmap lifecycle handles cleanup.
        // Explicit Dispose() while a render pass is in flight causes ObjectDisposedException.
        CurrentAlbumArt = null;

#if WINDOWS
        _smtcService?.UpdateMetadata(station.Title, station.Tags, "Internet Radio", null);
#endif
        _mprisService?.SetMetadata(station.Title, station.Tags, "Internet Radio", station.FaviconUrl);
        _macNowPlaying?.SetMetadata(station.Title, station.Tags, "Internet Radio", null);

        if (!string.IsNullOrWhiteSpace(station.FaviconUrl))
        {
            _ = LoadFaviconAsync(station.FaviconUrl);
        }

        // Atomic swap: capture previous, build new Media, halt libvlc on the old
        // one, hand the new one to the player, then defer the dispose of the old.
        // The lock keeps any concurrent path out of the swap; Stop() forces libvlc's
        // worker thread to settle on the previous Media before we Play() the next,
        // closing the race window where Play() was being called mid-transition.
        lock (_playbackSwitchLock)
        {
            var previousMedia = _currentMedia;
            var previousHandler = _currentMediaMetaHandler;

            _currentMedia = new Media(_vlc, ProcessStreamUrl(station.StreamUrl!), FromType.FromLocation);
            ApplyAudioNormalization(_currentMedia, null);   // radio: real-time normvol, no tag to precompute

            // Radio streams over flaky uplinks need more headroom than libvlc's 1s
            // default network buffer. Without this, the smallest server-side jitter
            // starves the decoder and libvlc cancels the HTTP/2 stream rather than
            // waiting for more data - observed as `local stream 1 error: Cancellation
            // (0x8)` right after the first audio buffer arrives. 3s matches the CD
            // path's :file-caching=3000 and is the conventional value across VLC
            // radio guides.
            _currentMedia.AddOption(":network-caching=3000");
            // Auto-reconnect when the upstream drops the TCP connection. Many shoutcast
            // / icecast servers cycle connections aggressively (especially behind CDNs);
            // without this, a single drop ends playback instead of seamlessly resuming.
            _currentMedia.AddOption(":http-reconnect");
            // Stream the body in chunks instead of trying to fully buffer the response
            // before playback starts. Required for live audio (no Content-Length).
            _currentMedia.AddOption(":http-continuous");
            // Standard browser UA -- libvlc's default sometimes gets blocked
            // by upstream CDNs / firewalls. Same UA we use for the cover-art
            // and podcast image fetchers so server logs see consistent traffic.
            _currentMedia.AddOption(":http-user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

            // Capture THIS specific Media instance. When the user switches stations rapidly,
            // LibVLC can still deliver late MetaChanged events from the previous (disposed)
            // Media object. The ReferenceEquals checks below guard against that; storing
            // the delegate lets DeferDispose detach it before Dispose(), preventing both
            // the latent reentrancy and a closure-per-switch memory leak.
            var thisMedia = _currentMedia;

            EventHandler<MediaMetaChangedEventArgs> handler = (s, e) =>
            {
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
                    return;
                }

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

#if WINDOWS
                    _smtcService?.UpdateMetadata(title, artist, CurrentStation?.Title, null);
#endif
                });
            };
            thisMedia.MetaChanged += handler;
            _currentMediaMetaHandler = handler;

            // Note: don't call _player.Stop() before Play(thisMedia). libvlcsharp's
            // Stop+Play sequence triggers two native transitions back-to-back which
            // is more crash-prone than the single transition Play(newMedia) performs
            // internally. The 120 ms debounce in PlayRadioStation + the lock here +
            // the deferred dispose under the same lock is the safe combination.
            NewPlaybackEpoch();
            BeginPlayback();
            _ = _player.Play(thisMedia);
            DeferDispose(previousMedia, previousHandler);
        }

        ApplyPerTrackOptions(station);

        station.LastPlayed = DateTime.UtcNow;
        station.PlayCount++;
        MediaCache.SetLastPlayed(station.Id, station.LastPlayed.Value);
        MediaCache.IncrementPlayCount(station.Id);

        UpdateNavigationButtons();
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
        _ = ResolveAndStreamPodcastAsync(feed, episode, rawSource, epoch);
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

#if WINDOWS
            _smtcService?.UpdateMetadata(episode.Title, feed.Title, "Podcast", null);
#endif
            _mprisService?.SetMetadata(episode.Title, feed.Title, "Podcast", episode.Image ?? feed.DisplayImage);
            _macNowPlaying?.SetMetadata(episode.Title, feed.Title, "Podcast", null);

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
                    var previousMedia = _currentMedia;
                    var previousHandler = _currentMediaMetaHandler;
                    _currentMediaMetaHandler = null;

                    _currentMedia = isLocal
                        ? new Media(_vlc, source, FromType.FromPath)
                        : new Media(_vlc, source, FromType.FromLocation);
                    ApplyAudioNormalization(_currentMedia, null);   // podcast episode: real-time normvol

                    if (!isLocal)
                    {
                        // Streamed only: podcasts have Content-Length so we omit
                        // :http-continuous (a live-stream option). Network caching
                        // and reconnect still help on flaky CDN edges.
                        _currentMedia.AddOption(":network-caching=3000");
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
                    _ = _player.Play(_currentMedia);
                    DeferDispose(previousMedia, previousHandler);
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
    private void DeferDispose(Media? media, EventHandler<MediaMetaChangedEventArgs>? metaHandler = null)
    {
        if (media == null)
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
                    if (metaHandler != null)
                    {
                        media.MetaChanged -= metaHandler;
                    }
                    media.Dispose();
                }
                catch
                {
                    // Best-effort: the native handle may already be gone if a
                    // previous deferred dispose got there first.
                }
            }
        }, DispatcherPriority.Background);
    }

    private void ClearPlayback()
    {
        // Stopping supersedes any pending async playback (e.g. a podcast resolve in flight).
        NewPlaybackEpoch();
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

        // Don't dispose - Avalonia's ref-counted bitmap lifecycle handles cleanup.
        // Explicit Dispose() while a render pass is in flight causes ObjectDisposedException.
        CurrentAlbumArt = null;
        CurrentTrackLine1 = string.Empty;
        CurrentTrackLine2 = string.Empty;
        CurrentTrackTime = "";
        CurrentTrackDuration = "";
        CurrentTrackTimeNumber = 0;
        CurrentTrackDurationNumber = 0;

        ButtonPlayPauseIcon = ICON_PLAY;
        ButtonPlayPausePadding = ICON_PLAY_PADDING;

#if WINDOWS
        _smtcService?.SetPlaybackStatus(MediaPlaybackStatus.Stopped);
        _thumbBarService?.SetPlayingState(false);
#endif

        UpdateNavigationButtons();
    }

    private string ProcessStreamUrl(string streamUrl)
    {
        if (!streamUrl.Contains("yp.shoutcast.com", StringComparison.OrdinalIgnoreCase))
        {
            return streamUrl;
        }

        try
        {
            var content = _faviconHttp.GetStringAsync(streamUrl).GetAwaiter().GetResult();

            // M3U format: non-# non-empty lines are stream URLs
            foreach (var line in content.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith('#'))
                {
                    return trimmed;
                }
            }
        }
        catch
        {
            // Playlist fetch failed, fall through to original URL
        }

        return streamUrl;
    }

    internal void Play()
    {
        UI(() => _player?.Play());
    }

    internal void Pause()
    {
        UI(() => _player?.Pause());
    }

    internal void Stop()
    {
        _ = ThreadPool.QueueUserWorkItem(_ => _player?.Stop());
    }

    #endregion

    #region Radio Station Management

    [RelayCommand]
    internal void ToggleFavorite(MediaItem? station)
    {
        if (station == null)
        {
            return;
        }

        station.IsFavorite = !station.IsFavorite;
        MediaCache.SetFavorite(station.Id, station.IsFavorite);

        // Only rebuild the list when viewing Favorites (item may need to appear/disappear)
        if (SelectedSidebarItem?.IsFavorites == true)
        {
            var scroll = GetScrollOffset?.Invoke() ?? 0;
            ApplyFilter();
            SetScrollOffset?.Invoke(scroll);
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
        MediaCache.SetFavorite(item.Id, true);
        if (SelectedSidebarItem?.IsFavorites == true)
        {
            ApplyFilter();
        }
    }

    [RelayCommand]
    internal void AddUserStation(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        string name;
        string url;
        string genre = string.Empty;

        var parts = input.Split('|', 3);
        if (parts.Length >= 2)
        {
            name = parts[0].Trim();
            url = parts[1].Trim();
            if (parts.Length == 3)
            {
                genre = parts[2].Trim();
            }
        }
        else
        {
            url = parts[0].Trim();
            name = url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
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

    #region Playlist Management

    private void LoadPlaylistSidebarItems()
    {
        // Remove existing playlist sidebar items (keep Favorites and New Playlist action)
        var toRemove = PlaylistItems.Where(i => i.PlaylistId != null).ToList();
        foreach (var item in toRemove)
        {
            PlaylistItems.Remove(item);
        }

        var playlists = MediaCache.LoadAllPlaylists();

        foreach (var playlist in playlists)
        {
            var key = $"Playlist:{playlist.Id}";
            var trackIds = MediaCache.GetPlaylistTrackIds(playlist.Id);
            ListViewConfigs.Register(key, ListViewConfigs.BuildPlaylistConfig(playlist.Id, trackIds));

            // Insert before the "New Playlist..." item
            var insertIndex = PlaylistItems.Count - 1;
            if (insertIndex < 0)
            {
                insertIndex = 0;
            }

            PlaylistItems.Insert(insertIndex, new SidebarItem
            {
                Name = playlist.Name,
                Icon = "fa-solid fa-list-ul",
                Category = "PLAYLISTS",
                IsEnabled = true,
                ViewConfigKey = key,
                PlaylistId = playlist.Id,
            });
        }
    }

    [RelayCommand]
    internal async Task CreatePlaylist()
    {
        var dialog = new Views.PlaylistNameDialog();
        var result = await dialog.ShowDialog<string?>(_window);

        if (string.IsNullOrWhiteSpace(result))
        {
            return;
        }

        var id = MediaCache.CreatePlaylist(result.Trim());
        LoadPlaylistSidebarItems();
        PlaylistsChanged?.Invoke();

        // Navigate to the new playlist
        var newItem = PlaylistItems.FirstOrDefault(i => i.PlaylistId == id);
        if (newItem != null)
        {
            SelectedSidebarItem = newItem;
        }
    }

    [RelayCommand]
    internal async Task RenamePlaylist(SidebarItem? item)
    {
        if (item?.PlaylistId == null)
        {
            return;
        }

        var dialog = new Views.PlaylistNameDialog(item.Name);
        var result = await dialog.ShowDialog<string?>(_window);

        if (string.IsNullOrWhiteSpace(result))
        {
            return;
        }

        MediaCache.RenamePlaylist(item.PlaylistId.Value, result.Trim());
        LoadPlaylistSidebarItems();
        PlaylistsChanged?.Invoke();
    }

    /// <summary>
    /// "Import Audiobooks..." (the Audiobooks sidebar entry's context menu): picks files the user
    /// already owns and copies them into {library}/.audiobooks/{Author}/{Book}/ - where LOCATION
    /// makes them audiobooks regardless of tagging - then folds them in with a delta scan.
    /// </summary>
    internal async Task ImportAudiobooksAsync()
    {
        if (string.IsNullOrWhiteSpace(App.FolderPath))
        {
            UpdateMainStatus("Set a library folder first.");
            return;
        }

        var files = await _window.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Import Audiobooks",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("Audiobook Files") { Patterns = ["*.m4b", "*.m4a", "*.mp3", "*.aac"] },
                new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = ["*"] }
            ]
        });
        if (files.Count == 0)
        {
            return;
        }

        int copied = 0, skipped = 0;
        await Task.Run(() =>
        {
            foreach (var picked in files)
            {
                var source = picked.Path.LocalPath;
                if (!FileScanner.IsSupportedExtension(source))
                {
                    skipped++;
                    continue;
                }
                try
                {
                    var dest = AudiobookDownloadService.ImportDestinationFor(App.FolderPath, source);
                    if (File.Exists(dest))
                    {
                        skipped++;   // already imported - importing twice shouldn't duplicate
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(source, dest);
                    copied++;
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Audiobook import failed for {Source}", source);
                    skipped++;
                }
            }
        });

        _log.Information("Audiobook import: {Copied} copied, {Skipped} skipped", copied, skipped);
        UpdateMainStatus(skipped == 0
            ? $"Imported {copied} audiobook file(s)."
            : $"Imported {copied} audiobook file(s), skipped {skipped}.");
        if (copied > 0)
        {
            await ScanAndAnalyzeLibraryAsync();
        }
    }

    [RelayCommand]
    internal async Task ImportPlaylist()
    {
        var files = await _window.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Import Playlist",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("Playlist Files") { Patterns = ["*.m3u", "*.m3u8", "*.pls", "*.xspf"] },
                new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = ["*"] }
            ]
        });

        if (files.Count == 0)
        {
            return;
        }

        var filePath = files[0].Path.LocalPath;
        var result = PlaylistImporter.Import(filePath);
        if (result.TrackPaths.Count == 0)
        {
            return;
        }

        // Match tracks to library by file path
        var libraryLookup = _allItems
            .Where(i => i.FilePath != null)
            .ToDictionary(i => i.FilePath!, StringComparer.OrdinalIgnoreCase);

        var matched = new List<MediaItem>();
        var unmatched = new List<string>();

        foreach (var path in result.TrackPaths)
        {
            if (libraryLookup.TryGetValue(path, out var item))
            {
                matched.Add(item);
            }
            else if (File.Exists(path) && FileScanner.IsSupportedExtension(path))
            {
                unmatched.Add(path);
            }
        }

        // If there are unmatched tracks that exist on disk, offer to copy them
        if (unmatched.Count > 0)
        {
            var copyDialog = new Views.ConfirmDialog(
                "Copy to Library",
                $"{unmatched.Count} track(s) are not in your library but exist on disk.\n\nCopy them to your music folder?",
                "Copy");
            var doCopy = await copyDialog.ShowDialog<bool>(_window);

            if (doCopy)
            {
                int copied = 0;

                foreach (var sourcePath in unmatched)
                {
                    copied++;
                    var destPath = Path.Combine(App.FolderPath, Path.GetFileName(sourcePath));

                    try
                    {
                        if (!File.Exists(destPath))
                        {
                            File.Copy(sourcePath, destPath);
                        }

                        var newItem = FileScanner.CreateMediaItemFromPath(destPath);
                        if (newItem != null)
                        {
                            AudioFileAnalyzer.AnalyzeFile(newItem);
                            MediaCache.UpsertMusic(newItem);
                            _allItems.Add(newItem);
                            matched.Add(newItem);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "Failed to copy {Source} into the library", sourcePath);
                    }
                }

                _log.Information("Copied {Count} track(s) into the library", copied);
            }
        }

        if (matched.Count == 0)
        {
            return;
        }

        // Ask for playlist name
        var name = !string.IsNullOrWhiteSpace(result.Name) ? result.Name : Path.GetFileNameWithoutExtension(filePath);
        var nameDialog = new Views.PlaylistNameDialog(name);
        var chosenName = await nameDialog.ShowDialog<string?>(_window);
        if (string.IsNullOrWhiteSpace(chosenName))
        {
            return;
        }

        var importSource = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".m3u8" => "M3U8",
            ".m3u"  => "M3U",
            ".pls"  => "PLS",
            ".xspf" => "XSPF",
            _       => "Imported",
        };
        var playlistId = MediaCache.CreatePlaylist(chosenName.Trim(), importSource);
        foreach (var track in matched)
        {
            MediaCache.AddTrackToPlaylist(playlistId, track.Id);
        }

        LoadPlaylistSidebarItems();
        PlaylistsChanged?.Invoke();

        var newPlaylistItem = PlaylistItems.FirstOrDefault(i => i.PlaylistId == playlistId);
        if (newPlaylistItem != null)
        {
            SelectedSidebarItem = newPlaylistItem;
        }
    }

    internal List<MediaItem> GetPlaylistMediaItems(int playlistId)
    {
        var trackIds = MediaCache.GetPlaylistTrackIds(playlistId);
        var lookup = _allItems.Where(i => i.FilePath != null).ToDictionary(i => i.Id);
        return trackIds.Where(lookup.ContainsKey).Select(id => lookup[id]).ToList();
    }

    /// <summary>
    /// Snapshot of currently connected devices - safe to enumerate from the view layer
    /// without holding a reference to the live _connectedDevices dictionary.
    /// </summary>
    internal IReadOnlyList<ConnectedDevice> ConnectedDevicesSnapshot()
        => _connectedDevices.Values.ToList();

    /// <summary>
    /// Resolves the connected device a "Device:{mount}" sidebar node points at.
    /// </summary>
    private ConnectedDevice? DeviceForSidebarItem(SidebarItem? item)
    {
        var mount = ResolveDeviceMountPath(item?.ViewConfigKey, _connectedDevices.Keys);
        return mount is not null && _connectedDevices.TryGetValue(mount, out var dev) ? dev : null;
    }

    /// <summary>
    /// Resolves a sidebar <c>ViewConfigKey</c> to the mount path of the device it belongs to, or null
    /// when it isn't a device view. The device's Podcasts/Audiobooks child views suffix the mount path
    /// with the media kind (<c>"Device:E:\:Podcast"</c>), so a bare exact lookup misses - this matches
    /// against the known mount paths (longest prefix wins) instead. Pulled out and made pure so the
    /// resolution is unit-tested: a miss here silently kills "Remove from iPod" and podcast sync from
    /// those sub-views (dev resolves null → the CRUD call no-ops), which is exactly how it regressed.
    /// </summary>
    internal static string? ResolveDeviceMountPath(string? viewConfigKey, IEnumerable<string> knownMountPaths)
    {
        if (viewConfigKey is not { } key || !key.StartsWith("Device:", StringComparison.Ordinal))
        {
            return null;
        }
        var rest = key["Device:".Length..];
        string? best = null;
        foreach (var mount in knownMountPaths)
        {
            if (string.Equals(rest, mount, StringComparison.OrdinalIgnoreCase))
            {
                return mount;   // the device's own root node ("Device:{mount}")
            }
            if (rest.StartsWith(mount, StringComparison.OrdinalIgnoreCase) && (best is null || mount.Length > best.Length))
            {
                best = mount;   // a "{mount}:Podcast" / ":Audiobook" child view - longest match wins
            }
        }
        return best;
    }

    /// <summary>
    /// Whether a view/cache key belongs to the device at <paramref name="mountPath"/> - its root view
    /// ("Device:{mount}") or any sub-view ("Device:{mount}:Podcast", ":Audiobook", ":Playlist:{id}").
    /// Boundary-aware on purpose: unlike a bare prefix match, "Device:/media/ipod" does NOT claim
    /// "Device:/media/ipod-red"'s keys, so tearing one device down can't take a sibling's views with it.
    /// Drives the disconnect teardown (view-cache eviction + selection fallback).
    /// </summary>
    internal static bool IsDeviceViewKeyFor(string? viewConfigKey, string mountPath)
    {
        if (viewConfigKey is null)
        {
            return false;
        }
        var root = $"Device:{mountPath}";
        return string.Equals(viewConfigKey, root, StringComparison.OrdinalIgnoreCase)
            || viewConfigKey.StartsWith($"{root}:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether dragging <paramref name="item"/> onto the given device node is allowed: a Music
    /// item on any writable stock iPod, or an Audiobook when the tier carries audiobooks NATIVELY
    /// (media_type/media_kind 8) - a device without the concept refuses rather than mislabeling a
    /// book as a song. Other media kinds / device types reject so the drop cursor shows "no".
    /// </summary>
    internal bool CanAcceptMediaDrop(SidebarItem? deviceItem, MediaItem? item)
    {
        if (item is null || item.Kind is not (MediaKind.Music or MediaKind.Audiobook))
        {
            return false;
        }
        var dev = DeviceForSidebarItem(deviceItem);
        if (dev is not { DeviceType: DeviceType.StockIPod } || !IPodCapabilities.SupportsDatabaseWrite(dev.IpodGeneration))
        {
            return false;
        }
        return item.Kind != MediaKind.Audiobook || IPodDevice.For(dev).SupportsAudiobooks;
    }

    /// <summary>
    /// Whether the media right-click > Sync submenu should offer <paramref name="device"/> for
    /// <paramref name="item"/>: a local music/audiobook file (not one already on a device) whose
    /// tier can write it - any playlist-capable tier for music, an audiobook-capable tier for books.
    /// </summary>
    internal bool CanSyncItemToDevice(MediaItem item, ConnectedDevice device)
    {
        if (item.Kind is not (MediaKind.Music or MediaKind.Audiobook)
            || string.IsNullOrEmpty(item.FilePath)
            || item.Source?.StartsWith("device:", StringComparison.Ordinal) == true)
        {
            return false;
        }
        var ipod = IPodDevice.For(device);
        return item.Kind == MediaKind.Audiobook ? ipod.SupportsAudiobooks : ipod.SupportsPlaylists;
    }

    /// <summary>
    /// Whether the item is already on the device (by artist+title) - the Sync submenu greys out
    /// such a device, the same "already there, adding is a no-op" cue the Add-to-Playlist submenu
    /// gives for the current playlist.
    /// </summary>
    internal bool IsItemAlreadyOnDevice(MediaItem item, ConnectedDevice device)
    {
        var key = NormalizeMatchKey(item.Artist, item.Title);
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }
        var source = $"device:{device.MountPath}";
        return _allItems.Any(i => i.Source == source && NormalizeMatchKey(i.Artist, i.Title) == key);
    }

    /// <summary>
    /// Media right-click > Sync > (device): imports one track onto the device through its tier
    /// backend (media_type auto-detected for audiobooks). Skips a track already there by
    /// artist+title, and never creates a playlist - the single item just joins the device library.
    /// </summary>
    internal async Task SyncItemToDeviceAsync(MediaItem item, ConnectedDevice device)
    {
        if (!CanSyncItemToDevice(item, device) || !File.Exists(item.FilePath!))
        {
            return;
        }

        var ffmpeg = ResolveFfmpeg();
        if (device.DeviceType == DeviceType.StockIPod && ffmpeg is null)
        {
            UpdateMainStatus("ffmpeg wasn't found — needed to transcode for the iPod.");
            return;
        }

        var deviceSource = $"device:{device.MountPath}";
        var already = _allItems
            .Where(i => i.Source == deviceSource && !string.IsNullOrEmpty(i.FilePath))
            .Select(i => NormalizeMatchKey(i.Artist, i.Title))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (already.Contains(NormalizeMatchKey(item.Artist, item.Title)))
        {
            UpdateMainStatus($"“{item.Title}” is already on {device.Name}.");
            return;
        }

        var ipod = IPodDevice.For(device);
        using var batch = ipod.BeginBatchWrite();
        BeginLcdBusy($"Syncing to {device.Name}");
        try
        {
            var deviceItem = await ipod.AddTrackAsync(item, ffmpeg ?? "ffmpeg");
            _allItems.Add(deviceItem);
            IPodArtworkReader.Invalidate(device.MountPath);
            device.SetSpaceFrom(_allItems.Where(i => i.Source == deviceSource));
            ApplyFilter();
            _log.Information("Synced “{Title}” to {Device}", item.Title, device.MountPath);
            UpdateMainStatus($"Synced “{item.Title}” to {device.Name}.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to sync {Track} to {Device}", item.FilePath, device.MountPath);
            UpdateMainStatus($"Couldn't sync “{item.Title}” to {device.Name} — {ex.Message}");
        }
        finally
        {
            EndLcdBusy();
        }
    }

    /// <summary>Right-click "Remove from iPod": deletes the item - any media kind - from the connected
    /// device via the per-tier backend (Nano 5G SQLite, binary iTunesDB, or Rockbox filesystem): its
    /// database rows and its audio file, then drops it from the live list. Reports loudly when a tier
    /// has no remove backend.</summary>
    internal async Task RemoveFromDeviceAsync(MediaItem? track)
    {
        if (track is null)
        {
            return;
        }
        var dev = DeviceForSidebarItem(SelectedSidebarItem);
        if (dev is null)
        {
            return;
        }

        try
        {
            await IPodDevice.For(dev).RemoveTrackAsync(track);

            _allItems.Remove(track);
            if (track.FileSize is { } removedSize && removedSize > 0)
            {
                dev.AdjustSpaceFor(track, -removedSize);
            }
            IPodArtworkReader.Invalidate(dev.MountPath);
            ApplyFilter();
            UpdateMainStatus($"Removed “{track.Title}” from {dev.Name}.");
        }
        catch (NotImplementedException)
        {
            UpdateMainStatus($"Removing isn't supported on {dev.Name}.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to remove {Track} from {Device}", track.FilePath, dev.MountPath);
            UpdateMainStatus($"Couldn't remove “{track.Title}”: {ex.Message}");
        }
    }

    /// <summary>
    /// Imports a dragged library track onto the dropped-on iPod (transcode + iTunesDB +
    /// artwork + checksum). Progress shows on the LCD; errors go to the log.
    /// </summary>
    internal async Task ImportMediaToDeviceAsync(SidebarItem? deviceItem, MediaItem? track)
    {
        if (!CanAcceptMediaDrop(deviceItem, track) || track is null)
        {
            return;
        }
        var dev = DeviceForSidebarItem(deviceItem)!;

        if (string.IsNullOrEmpty(track.FilePath) || !File.Exists(track.FilePath))
        {
            UpdateMainStatus($"Can't add “{track.Title}” — source file is missing.");
            return;
        }

        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null)
        {
            UpdateMainStatus("ffmpeg not found on PATH — needed to transcode for the iPod.");
            return;
        }

        // Progress shows on the LCD (the rip/progress page), not the retired activity feed.
        var title = track.Title ?? Path.GetFileName(track.FilePath);
        IsBusy = true;
        BusyTitle = $"Adding “{title}”";
        BusyDetail = $"Transcoding for {dev.Name}…";
        BusyPercent = 0;
        try
        {
            // Import via the device tier, then reflect it in the live list right away (no re-scan).
            var imported = await IPodDevice.For(dev).AddTrackAsync(track, ffmpeg);
            _allItems.Add(imported);
            IPodArtworkReader.Invalidate(dev.MountPath);
            if (imported.FileSize is { } sz && sz > 0)
            {
                dev.AdjustSpaceFor(imported, sz);
            }
            ApplyFilter();

            BusyTitle = $"Added “{title}”";
            BusyDetail = $"On {dev.Name}";
            BusyPercent = 1;
            await Task.Delay(1800);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to add {Track} to {Device}", track.FilePath, dev.MountPath);
            BusyTitle = "Couldn't add to iPod";
            BusyDetail = ex.Message;
            await Task.Delay(3500);
        }
        finally
        {
            IsBusy = false;
            BusyTitle = string.Empty;
            BusyDetail = string.Empty;
            BusyPercent = 0;
        }
    }



    /// <summary>Locates ffmpeg on PATH, then a bundled copy next to the app.</summary>
    private static string? ResolveFfmpeg()
    {
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            var sep = OperatingSystem.IsWindows() ? ';' : ':';
            foreach (var dir in pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch { /* malformed PATH entry */ }
            }
        }
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", name);
        return File.Exists(bundled) ? bundled : null;
    }

    /// <summary>
    /// Sends a specific playlist (or Favorites) to a connected device from the sidebar's "send to device"
    /// entry. Resolves the tracks, then hands off to the tier-agnostic <see cref="SyncPlaylistToDeviceAsync"/>
    /// (copy/transcode + native playlist) - one path for stock iPods and Rockbox alike.
    /// </summary>
    internal async Task SendPlaylistToDevice(SidebarItem playlistItem, ConnectedDevice device)
    {
        List<MediaItem> tracks;
        if (playlistItem.PlaylistId is int pid)
        {
            tracks = await Task.Run(() => GetPlaylistMediaItems(pid));
        }
        else if (playlistItem.IsFavorites)
        {
            tracks = _allItems
                .Where(i => i.IsFavorite && i.Kind == MediaKind.Music && !string.IsNullOrEmpty(i.FilePath))
                .ToList();
        }
        else
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(playlistItem.Name) ? "Playlist" : playlistItem.Name;
        await SyncPlaylistToDeviceAsync(name, tracks, device);
    }

    internal static string NormalizeMatchKey(string? artist, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }
        return $"{(artist ?? "").Trim()}|{title.Trim()}";
    }

    internal static string ToDeviceRelativePath(string absoluteDevicePath, string mountPath)
    {
        // Strip the mount prefix so the M3U uses Rockbox-style absolute-to-device paths
        // ("/Music/Rush/Signals/01.mp3") rather than host-specific ones ("/run/media/...").
        if (absoluteDevicePath.StartsWith(mountPath, StringComparison.OrdinalIgnoreCase))
        {
            var rel = absoluteDevicePath[mountPath.Length..].Replace('\\', '/');
            return rel.StartsWith('/') ? rel : '/' + rel;
        }
        return absoluteDevicePath.Replace('\\', '/');
    }

    internal static string ToMountAbsolute(string deviceRelativePath, string mountPath)
    {
        var rel = deviceRelativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(mountPath, rel));
    }

    internal static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        return sb.ToString().Trim();
    }

    internal async Task ExportPlaylist(SidebarItem item, string format)
    {
        if (!item.PlaylistId.HasValue)
        {
            return;
        }

        var tracks = GetPlaylistMediaItems(item.PlaylistId.Value);
        if (tracks.Count == 0)
        {
            return;
        }

        var extension = format switch
        {
            "M3U8" => "m3u8",
            "PLS" => "pls",
            "XSPF" => "xspf",
            _ => "m3u8"
        };

        var file = await _window.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = $"Export Playlist — {item.Name}",
            SuggestedFileName = $"{item.Name}.{extension}",
            FileTypeChoices =
            [
                new Avalonia.Platform.Storage.FilePickerFileType(format) { Patterns = [$"*.{extension}"] }
            ]
        });

        if (file == null)
        {
            return;
        }

        var path = file.Path.LocalPath;

        switch (format)
        {
            case "M3U8":
            {
                PlaylistExporter.ExportM3U8(path, item.Name, tracks);
                break;
            }

            case "PLS":
            {
                PlaylistExporter.ExportPLS(path, item.Name, tracks);
                break;
            }

            case "XSPF":
            {
                PlaylistExporter.ExportXSPF(path, item.Name, tracks);
                break;
            }
        }
    }

    /// <summary>
    /// File > Export Library - writes the whole local music library out as a playlist file the user
    /// names (format from the chosen extension). Was a disabled placeholder; PlaylistExporter already
    /// does the work, so it's a real feature now. Audiobooks/podcasts/device/CD tracks are excluded -
    /// this is the music library, matching the Music view.
    /// </summary>
    [RelayCommand]
    internal async Task ExportLibrary()
    {
        var tracks = _allItems
            .Where(i => i.Kind == MediaKind.Music && i.Source == null && !string.IsNullOrEmpty(i.FilePath))
            .ToList();
        if (tracks.Count == 0)
        {
            UpdateMainStatus("Your library has no music to export.");
            return;
        }

        var file = await _window.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Export Library",
            SuggestedFileName = "OrgZ Library.m3u8",
            FileTypeChoices =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("M3U8") { Patterns = ["*.m3u8"] },
                new Avalonia.Platform.Storage.FilePickerFileType("PLS") { Patterns = ["*.pls"] },
                new Avalonia.Platform.Storage.FilePickerFileType("XSPF") { Patterns = ["*.xspf"] },
            ],
        });
        if (file is null)
        {
            return;
        }

        var path = file.Path.LocalPath;
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".pls": PlaylistExporter.ExportPLS(path, "OrgZ Library", tracks); break;
            case ".xspf": PlaylistExporter.ExportXSPF(path, "OrgZ Library", tracks); break;
            default: PlaylistExporter.ExportM3U8(path, "OrgZ Library", tracks); break;
        }

        _log.Information("Exported library: {Count} tracks -> {Path}", tracks.Count, path);
        UpdateMainStatus($"Exported {tracks.Count} tracks to {Path.GetFileName(path)}.");
    }

    [RelayCommand]
    internal async Task DeletePlaylist(SidebarItem? item)
    {
        if (item?.PlaylistId == null)
        {
            return;
        }

        var dialog = new Views.ConfirmDialog("Delete Playlist", $"Delete playlist \"{item.Name}\"?\n\nThis cannot be undone.", "Delete");
        var ok = await dialog.ShowDialog<bool>(_window);
        if (!ok)
        {
            return;
        }

        var key = item.ViewConfigKey;
        MediaCache.DeletePlaylist(item.PlaylistId.Value);
        ListViewConfigs.Remove(key);

        // Navigate away if we're viewing the deleted playlist
        if (SelectedSidebarItem == item)
        {
            SelectedSidebarItem = PlaylistItems.FirstOrDefault(i => i.IsFavorites) ?? LibraryItems[0];
        }

        PlaylistItems.Remove(item);
        PlaylistsChanged?.Invoke();
    }

    [RelayCommand]
    internal void AddToPlaylist(int playlistId)
    {
        if (SelectedItem == null)
        {
            return;
        }

        AddTrackToPlaylist(playlistId, SelectedItem);
    }

    internal void AddTrackToPlaylist(int playlistId, MediaItem item)
    {
        MediaCache.AddTrackToPlaylist(playlistId, item.Id);

        // Refresh the playlist's config with updated track IDs
        var key = $"Playlist:{playlistId}";
        var trackIds = MediaCache.GetPlaylistTrackIds(playlistId);
        ListViewConfigs.Register(key, ListViewConfigs.BuildPlaylistConfig(playlistId, trackIds));

        // Refresh view if currently viewing this playlist
        if (SelectedSidebarItem?.ViewConfigKey == key)
        {
            _activeViewConfig = ListViewConfigs.Get(key);
            ApplyFilter();
        }
    }

    internal void SetRating(MediaItem item, int? rating)
    {
        if (item.Kind != MediaKind.Music)
        {
            return;
        }

        item.Rating = rating;
        MediaCache.SetRating(item.Id, rating);
    }

    /// <summary>
    /// Prompts the user to confirm, then marks the item as ignored.
    /// The file is never touched. The item is also removed from any playlists it belongs to.
    /// Shows a restore-capable "Ignored" view in the sidebar (if enabled in Settings).
    /// </summary>
    /// <summary>
    /// Deletes an audiobook from disk, with confirmation. A book in the managed
    /// .audiobooks/{Author}/{Title}/ layout deletes as a whole - every chapter/part file, however
    /// many rows it spans - because that's the unit the user thinks in; a loose audiobook file
    /// anywhere else deletes alone. The store's Downloaded state reads from disk, so the book
    /// flips back to Download on its next detail open.
    /// </summary>
    internal async Task DeleteAudiobookFromDiskAsync(MediaItem item)
    {
        if (item.Kind != MediaKind.Audiobook || string.IsNullOrEmpty(item.FilePath))
        {
            return;
        }

        var bookDir = AudiobookDetector.BookFolderFor(item.FilePath);
        var bookName = !string.IsNullOrWhiteSpace(item.Album) ? item.Album : (item.Title ?? item.FileName ?? "this audiobook");
        var fileCount = bookDir is not null && Directory.Exists(bookDir)
            ? Directory.EnumerateFiles(bookDir, "*.*", SearchOption.AllDirectories).Count(FileScanner.IsSupportedExtension)
            : 1;

        var dialog = new Views.ConfirmDialog(
            "Remove from Library",
            $"Remove “{bookName}” from your library?\n\nThis permanently deletes {fileCount} file(s) from disk. It cannot be undone.",
            "Delete");
        if (!await dialog.ShowDialog<bool>(_window))
        {
            return;
        }

        List<string> deleted;
        try
        {
            deleted = AudiobookDownloadService.DeleteFromDisk(item.FilePath);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Audiobook delete failed for {Path}", item.FilePath);
            UpdateMainStatus($"Couldn't delete {bookName} — {ex.Message}");
            return;
        }

        // Drop the matching rows immediately (the folder watcher would also catch up, but the
        // grid shouldn't show ghost rows for however long that takes) and their cache entries.
        var deletedPaths = new HashSet<string>(deleted, StringComparer.OrdinalIgnoreCase);
        var removedItems = _allItems.Where(i => IsLocalLibraryFile(i) && deletedPaths.Contains(i.FilePath!)).ToList();
        foreach (var removed in removedItems)
        {
            _allItems.Remove(removed);
        }
        await Task.Run(() => MediaCache.RemoveLibraryFiles(removedItems.Select(i => i.Id)));
        RefreshAllPlaylistConfigs();
        ApplyFilter();
        UpdateMainStatus($"Deleted {bookName} — {deleted.Count} file(s) removed.");
    }

    /// <summary>
    /// Removes a local library file for good: confirm, delete from disk, drop the rows. One
    /// gesture, one meaning across every kind (Fox's spec) - an audiobook scopes to its whole
    /// book folder, music to the single file. Only local library files route through here;
    /// device tracks have Remove from iPod and CD tracks never carry the command. The old
    /// ignore-based soft remove is retired (the Ignored view still shows and restores anything
    /// ignored before the change).
    /// </summary>
    internal async Task RemoveFromLibraryAsync(MediaItem item)
    {
        if (!IsLocalLibraryFile(item))
        {
            return;
        }

        if (item.Kind == MediaKind.Audiobook)
        {
            await DeleteAudiobookFromDiskAsync(item);
            return;
        }

        var title = !string.IsNullOrWhiteSpace(item.Title) ? item.Title : item.FileName ?? "this track";
        var dialog = new Views.ConfirmDialog(
            "Remove from Library",
            $"Remove “{title}” from your library?\n\nThis permanently deletes the file from disk. It cannot be undone.",
            "Delete");
        if (!await dialog.ShowDialog<bool>(_window))
        {
            return;
        }

        try
        {
            File.Delete(item.FilePath!);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Library file delete failed for {Path}", item.FilePath);
            UpdateMainStatus($"Couldn't delete {title} — {ex.Message}");
            return;
        }

        _allItems.Remove(item);
        await Task.Run(() => MediaCache.RemoveLibraryFiles([item.Id]));
        RefreshAllPlaylistConfigs();
        ApplyFilter();
        UpdateMainStatus($"Deleted {title}.");
    }

    /// <summary>
    /// Clears the ignored flag on the item. It re-appears in its natural view (Music, Favorites, etc.).
    /// Playlist memberships are NOT restored - they were deleted at ignore time.
    /// </summary>
    internal void RestoreFromIgnored(MediaItem item)
    {
        MediaCache.RestoreMedia(item.Id);
        item.IsIgnored = false;
        ApplyFilter();
    }

    /// <summary>
    /// Re-reads every playlist's track set from the DB and rebuilds its ListViewConfig entry.
    /// Call this after operations that mutate playlist membership outside of direct playlist APIs.
    /// </summary>
    private void RefreshAllPlaylistConfigs()
    {
        var playlists = MediaCache.LoadAllPlaylists();
        foreach (var p in playlists)
        {
            var key = $"Playlist:{p.Id}";
            var trackIds = MediaCache.GetPlaylistTrackIds(p.Id);
            ListViewConfigs.Register(key, ListViewConfigs.BuildPlaylistConfig(p.Id, trackIds));
        }

        // If currently viewing a playlist, swap in the refreshed config so ApplyFilter uses it
        if (_activeViewConfig?.PlaylistId != null)
        {
            _activeViewConfig = ListViewConfigs.Get(_activeViewConfig.Key);
        }
    }

    [RelayCommand]
    internal void RemoveFromPlaylist()
    {
        if (SelectedItem == null || SelectedSidebarItem?.PlaylistId == null)
        {
            return;
        }

        var playlistId = SelectedSidebarItem.PlaylistId.Value;
        var scroll = GetScrollOffset?.Invoke() ?? 0;

        MediaCache.RemoveTrackFromPlaylist(playlistId, SelectedItem.Id);

        var key = $"Playlist:{playlistId}";
        var trackIds = MediaCache.GetPlaylistTrackIds(playlistId);
        ListViewConfigs.Register(key, ListViewConfigs.BuildPlaylistConfig(playlistId, trackIds));
        _activeViewConfig = ListViewConfigs.Get(key);
        ApplyFilter();
        SetScrollOffset?.Invoke(scroll);
    }

    /// <summary>
    /// Returns the active playlist ID if the current view is a playlist; null otherwise.
    /// Used by the view to enable drag-to-reorder.
    /// </summary>
    internal int? ActivePlaylistId => _activeViewConfig?.PlaylistId;

    /// <summary>
    /// Reorders a track within the currently-active playlist.
    /// fromIndex/toIndex are positions within the current FilteredItems list.
    /// </summary>
    internal void ReorderPlaylistTrack(int fromIndex, int toIndex)
    {
        if (_activeViewConfig?.PlaylistId == null)
        {
            return;
        }

        if (fromIndex < 0 || fromIndex >= FilteredItems.Count || toIndex < 0 || toIndex >= FilteredItems.Count || fromIndex == toIndex)
        {
            return;
        }

        var playlistId = _activeViewConfig.PlaylistId.Value;
        var scroll = GetScrollOffset?.Invoke() ?? 0;

        // Move within current order then push the whole list back to DB.
        // Use the full DB order (not just filtered) so search-filtered reorders don't lose hidden tracks.
        var fullOrder = MediaCache.GetPlaylistTrackIds(playlistId);
        var movedItem = FilteredItems[fromIndex];
        var targetItem = FilteredItems[toIndex];

        var fromDbIdx = fullOrder.IndexOf(movedItem.Id);
        var toDbIdx = fullOrder.IndexOf(targetItem.Id);
        if (fromDbIdx < 0 || toDbIdx < 0)
        {
            return;
        }

        fullOrder.RemoveAt(fromDbIdx);
        fullOrder.Insert(toDbIdx, movedItem.Id);

        MediaCache.ReorderPlaylistTracks(playlistId, fullOrder);

        var key = $"Playlist:{playlistId}";
        ListViewConfigs.Register(key, ListViewConfigs.BuildPlaylistConfig(playlistId, fullOrder));
        _activeViewConfig = ListViewConfigs.Get(key);
        ApplyFilter();
        SetScrollOffset?.Invoke(scroll);
    }

    #endregion

    #region Loading and Analyzing Files

    internal async Task LoadAsync()
    {
        UpdateTitle();

        MediaCache.EnsureCreated();
        Services.Podcast.PodcastCache.EnsureCreated();

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
            var ordered = bundled
                .OrderBy(s => s.Tags, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _allItems.AddRange(ordered);
            _log.Information("BundledStations: loaded {Count} into memory", ordered.Count);
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
        _ = BuildPlaylistHeaderAsync(SelectedSidebarItem);

        // Scan and analyze the library folder (music + audiobooks)
        await ScanAndAnalyzeLibraryAsync();

        // Start watching for file changes
        StartFolderWatcher();

        // Start event-driven portable device detection (iPod, Rockbox, Audio CD).
        // CD drive arrival/removal also routes through the same WMI watcher -
        // no separate polling timer required.
        _deviceDetection = new DeviceDetectionService();
        _deviceDetection.DeviceConnected += device => UI(() => _ = HandleDeviceConnectedAsync(device));
        _deviceDetection.DeviceDisconnected += mountPath => UI(() => HandleDeviceDisconnected(mountPath));
        _deviceDetection.CdDriveEvent += () => UI(() => _ = ScanForCdAsync());
        _deviceDetection.Start();
    }

    /// <summary>
    /// True for an item that lives in the local library folder - a Music or Audiobook row with a
    /// file path and no Source (device tracks carry "device:{mount}", CD tracks "cdda"). The
    /// library scan reconciles ONLY these: without the Source check, a folder rescan while an
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

        var diskFiles = await FileScanner.ScanDirectoryAsync(App.FolderPath, recursive: true);

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

        var deletedItems = _allItems
            .Where(i => IsLocalLibraryFile(i) && !diskPaths.Contains(i.FilePath!))
            .ToList();

        foreach (var item in deletedItems)
        {
            _allItems.Remove(item);
        }

        ApplyFilter();
        UpdateTitle();

        await AnalyzeAllFilesAsync(filesToAnalyze);

        if (deletedItems.Count > 0)
        {
            await Task.Run(() => MediaCache.RemoveLibraryFiles(deletedItems.Select(i => i.Id)));
        }

        UpdateData();
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

            foreach (MediaItem item in filesToAnalyze)
            {
                AudioFileAnalyzer.AnalyzeFile(item);

                MediaCache.UpsertMusic(item);

                UpdateMainStatus($"Analyzing file {++idx} of {filesToAnalyze.Count}");
            }

            UpdateMainStatus($"Analyzing file {idx} of {filesToAnalyze.Count} | COMPLETE!");

            UpdateData();
        });
    }

    #endregion

    #region UX Updates

    internal void UpdateData()
    {
        int totalSongs = MusicItems.Count();
        TimeSpan totalDuration = TimeSpan.FromTicks(MusicItems.Sum(x => x.Duration?.Ticks ?? 0));
        long totalFileSize = MusicItems.Sum(x => x.FileSize ?? 0L);

        UI(() =>
        {
            StatusBar.TotalSongs = totalSongs;
            StatusBar.TotalDuration = totalDuration;
            StatusBar.TotalFileSize = totalFileSize;
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
            _smtcService?.SetNavigationEnabled(false, false);
            _thumbBarService?.SetNavigationEnabled(false, false);
#endif
            return;
        }

        IsBackTrackButtonEnabled = _playbackContext.HasPrevious;
        IsNextTrackButtonEnabled = _playbackContext.HasNext;
#if WINDOWS
        _smtcService?.SetNavigationEnabled(IsBackTrackButtonEnabled, IsNextTrackButtonEnabled);
        _thumbBarService?.SetNavigationEnabled(IsBackTrackButtonEnabled, IsNextTrackButtonEnabled);
#endif
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
            "Ignored" => "ignored",
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

    private static readonly HttpClient _faviconHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
        DefaultRequestHeaders = { { "User-Agent", $"OrgZ/{App.Version}" } }
    };

    // Dedicated client for fetching podcast show art to embed on a device. Standard browser UA - the
    // art sits on third-party CDNs that can reject odd agents, and request logs stay anonymous.
    private static readonly HttpClient _artHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" } }
    };

    /// <summary>Downloads a podcast show's cover (URL) to a temp file so it can be rendered into the
    /// iPod ArtworkDB. Returns the local path, or null when there's no URL / the fetch fails.</summary>
    private static string? TryDownloadShowArt(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }
        try
        {
            var bytes = _artHttp.GetByteArrayAsync(url).GetAwaiter().GetResult();
            if (bytes.Length == 0)
            {
                return null;
            }
            var path = Path.Combine(Path.GetTempPath(), "orgz_pcart_" + Guid.NewGuid().ToString("N")[..8] + ".img");
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch
        {
            return null;   // no art / fetch failed - the episode just imports without a cover
        }
    }

    private async Task LoadPodcastArtAsync(string url, Models.PodcastEpisode episode, Models.PodcastFeed feed)
    {
        try
        {
            var bytes = await _faviconHttp.GetByteArrayAsync(url);
            var bitmap = BitmapFromBytes(bytes);
            if (bitmap == null) return;
            UI(() =>
            {
                // Only assign if this episode is still the one playing -- the
                // user may have hopped to a different episode while we were
                // downloading the artwork.
                if (_currentPodcastStream is { } ps && ps.Episode.Id == episode.Id)
                {
                    CurrentAlbumArt = bitmap;
#if WINDOWS
                    _smtcService?.UpdateMetadata(episode.Title, feed.Title, "Podcast", bytes);
#endif
                    _macNowPlaying?.SetMetadata(episode.Title, feed.Title, "Podcast", null, bytes);
                }
            });
        }
        catch
        {
            // Best-effort -- if the image host is down or the URL is bad, the
            // LCD just stays with the music-note placeholder.
        }
    }

    private async Task LoadFaviconAsync(string url)
    {
        try
        {
            var bytes = await _faviconHttp.GetByteArrayAsync(url);
            var bitmap = BitmapFromBytes(bytes);
            if (bitmap != null)
            {
                UI(() =>
                {
                    // Don't dispose - Avalonia's ref-counted bitmap lifecycle handles cleanup.
                    // Explicit Dispose() while a render pass is in flight causes ObjectDisposedException.
                    CurrentAlbumArt = bitmap;
#if WINDOWS
                    _smtcService?.UpdateMetadata(CurrentStation?.Title, CurrentStation?.Tags, "Internet Radio", bytes);
#endif
                    // macOS Now Playing only learns about the artwork after the
                    // favicon download finishes - push an updated metadata frame.
                    _macNowPlaying?.SetMetadata(CurrentStation?.Title, CurrentStation?.Tags, "Internet Radio", null, bytes);
                });
            }
        }
        catch
        {
            // Favicon unavailable, keep default icon
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
            catch { }
        }
        else
        {
            try { _player.UnsetEqualizer(); } catch { }
        }

        // Start time: seek after a brief delay to let playback begin
        if (item.UseStartTime && item.StartTime.HasValue)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                if (_player.IsPlaying)
                {
                    _player.Time = (long)item.StartTime.Value.TotalMilliseconds;
                }
            });
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

    private static byte[]? ExtractAlbumArtBytes(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            if (file.Tag.Pictures?.Length > 0)
            {
                return file.Tag.Pictures[0].Data.Data;
            }
        }
        catch { }

        return null;
    }

    private static Bitmap? BitmapFromBytes(byte[] bytes)
    {
        try
        {
            var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch { }

        return null;
    }


    #endregion

    #region CD Audio

    private async Task ScanForCdAsync()
    {
        if (_cdScanning)
        {
            _log.Debug("ScanForCdAsync skipped: already scanning");
            return;
        }

        _cdScanning = true;

        try
        {
            var drives = CdAudioService.GetCdDrivesWithMedia();
            var all = CdAudioService.GetAllCdDrives();
            _log.Information("ScanForCdAsync: AllCdDrives={All} WithMedia={WithMedia} (paths: {Paths})",
                all.Count, drives.Count, string.Join(", ", all.Select(d => $"{d.Name}[ready={d.IsReady}]")));

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

                var discInfo = await CdAudioService.ReadDiscAsync(_vlc, drive);

                if (discInfo.Tracks.Count == 0)
                {
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
                _cdCoverArt = discInfo.CoverArtBytes != null ? BitmapFromBytes(discInfo.CoverArtBytes) : null;
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
        if (track.StreamUrl == null)
        {
            return;
        }

        UI(() =>
        {
            _playbackContext?.Release();
            _playbackContext = new PlaybackContext(_cdTracks, track);
            OnPropertyChanged(nameof(PlaybackContextUpcoming));
            ExecutePlayCd(track);
        });
    }

    // --- CD Rip / Burn ------------------------------------------------------

    private static readonly char[] _cdIdDriveSep = [':'];

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
            }
            else
            {
                var badList = string.Join(", ", unverified.Select(o => o.TrackNumber.ToString("D2")));
                _log.Warning("Ripped {Count} track(s) from {DrivePath}, {Unverified} unverified: {BadList}", outcomes.Count, drivePath, unverified.Count, badList);
            }
        }
        catch (OperationCanceledException)
        {
            _log.Information("Rip cancelled by user for {DrivePath}", drivePath);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Rip failed for {DrivePath}", drivePath);
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
    }

    // -- LCD progress for long device/disc operations --------------------------------
    // These borrow the rip's LCD page (IsBusy + BusyTitle/BusyDetail/BusyPercent) so an
    // import, burn, or device scan reads the same as a rip ("Adding ...", "Scanning ...").
    // Pair Begin/End; all marshal to the UI thread so callers can drive them from a
    // background scan. (Single-slot: a second op started mid-rip would share the page.)

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
    {
        var wasIdle = _activeDownloads.Count == 0;
        _activeDownloads[ep.Id] = (ep.Title ?? string.Empty, 0);
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

    private void OnPodcastDownloadProgress(Services.Podcast.DownloadProgress p)
    {
        var wasIdle = _activeDownloads.Count == 0;
        _activeDownloads[p.EpisodeId] = (p.Title, p.Fraction);
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

        // Prefer a known recorder when several optical drives are present (e.g. a virtual
        // CD-ROM alongside a real burner); fall back to the first drive if none is cached yet.
        var drives = CdAudioService.GetAllCdDrives();
        var drive = drives.FirstOrDefault(d => _burnerSupport.GetValueOrDefault(d.Name, false))
                    ?? drives.FirstOrDefault();
        if (drive == null)
        {
            _log.Warning("Burn requested with no CD drive present");
            await ShowBurnErrorAsync("No optical drive found to burn to.");
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

        var drivePath = drive.Name.TrimEnd('\\', '/');
        EnsureCdDriveFree(drivePath);

        // Fail fast (no transcode, no UAC) if there isn't a blank, writable disc loaded.
        // The probe is un-elevated - same SCSI passthrough as the recorder detection.
        var media = await Task.Run(() => CdBurnService.CheckBurnMedia(drivePath));
        if (media != CdBurnService.BurnMediaStatus.Ready)
        {
            _log.Information("Burn pre-flight blocked: {Status} on {Drive}", media, drivePath);
            await ShowBurnErrorAsync(media switch
            {
                CdBurnService.BurnMediaStatus.NoMedia     => "Insert a blank CD-R or CD-RW disc to burn to.",
                CdBurnService.BurnMediaStatus.NotBlank    => "The disc in the drive isn't blank. Insert a blank disc, or erase a rewritable one first.",
                CdBurnService.BurnMediaStatus.NotWritable => "This drive can't write discs.",
                _                                         => "Couldn't read the disc in the drive.",
            });
            return;
        }

        // ~80 min is the practical CD-R ceiling. Warn but still attempt - over-burn
        // discs exist and the drive itself has the final say on capacity.
        var totalMinutes = sources.Sum(t => t.Duration?.TotalMinutes ?? 0);
        if (totalMinutes > 80)
        {
            _log.Warning("Burn list is {Minutes:F1} min — may exceed CD-R capacity", totalMinutes);
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

        BeginLcdBusy($"Preparing {sources.Count} track(s)");
        try
        {
            var burnTracks = new List<CdBurnTrack>(sources.Count);
            for (int i = 0; i < sources.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var t = sources[i];
                SetLcdBusy($"Converting “{t.Title}” ({i + 1}/{sources.Count})", (double)i / sources.Count);
                var wav = Path.Combine(stagingDir, $"{i:D3}.wav");
                await CdAudioTranscoder.ToCdAudioWavAsync(ffmpeg, t.FilePath!, wav, ct);
                burnTracks.Add(new CdBurnTrack
                {
                    WavFilePath = wav,
                    Title = t.Title,
                    Performer = t.Artist,
                });
            }

            SetLcdBusy($"Burning {burnTracks.Count} track(s)", 0);
            var progress = new Progress<CdBurnProgress>(p =>
                SetLcdBusy($"Track {p.TrackNumber} of {p.TrackCount}", p.DiscPercent));

            await CdBurnService.BurnWithElevationAsync(drivePath, burnTracks, progress, discTitle, discPerformer, cancellationToken: ct);
            _log.Information("Burned {Count} track(s) to {DrivePath} (title: {Title})", burnTracks.Count, drivePath, discTitle ?? "—");
            UpdateMainStatus($"Burned {burnTracks.Count} track(s) to {drivePath}.");
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

    /// <summary>Shows an OK-only error dialog for a burn that can't start or didn't finish.</summary>
    private async Task ShowBurnErrorAsync(string message)
    {
        UpdateMainStatus(message);
        var dialog = new ConfirmDialog("Can't Burn Disc", message, "OK", showCancel: false);
        await dialog.ShowDialog(_window);
    }

    /// <summary>
    /// Burns the active view's tracks (a user playlist or Favorites) to disc. Bound to
    /// the footer Burn button, which is only visible when <see cref="ShowBurnButton"/>.
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
    /// Whether the active playlist can sync to a connected, writable device - drives the
    /// header's Sync button. One authority: the device tier's own capability claim, the same
    /// gate the sidebar's Sync submenu uses.
    /// </summary>
    public bool CanSyncToIPod =>
        (SelectedSidebarItem?.PlaylistId != null || SelectedSidebarItem?.IsFavorites == true)
        && _connectedDevices.Values.Any(IsSyncTarget);

    /// <summary>A connected device we can sync a playlist to - whatever tier claims playlists
    /// (filesystem players, binary iTunesDB iPods, the Nano 5G SQLite stack, Shuffles).</summary>
    private static bool IsSyncTarget(ConnectedDevice d) => IPodDevice.For(d).SupportsPlaylists;

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

        var device = _connectedDevices.Values.FirstOrDefault(IsSyncTarget);
        if (device == null)
        {
            return;
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
    private async Task SyncPlaylistToDeviceAsync(string name, IReadOnlyList<MediaItem> tracks, ConnectedDevice device)
    {
        if (tracks.Count == 0)
        {
            UpdateMainStatus($"“{name}” is empty — nothing to sync.");
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
        // One batch scope around the whole sync: tiers with deferrable per-write work (the Nano 5G's
        // full-CDB regeneration) rebuild once at the end instead of once per track.
        using var batch = ipod.BeginBatchWrite();
        var deviceSource = $"device:{device.MountPath}";
        var deviceByAT = _allItems
            .Where(i => i.Source == deviceSource && !string.IsNullOrEmpty(i.FilePath))
            .GroupBy(i => NormalizeMatchKey(i.Artist, i.Title), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var playlistItems = new List<MediaItem>(tracks.Count);   // matched-or-imported device items, in order
        int matched = 0, added = 0, failed = 0;

        BeginLcdBusy($"Syncing to {device.Name}");
        try
        {
            for (int i = 0; i < tracks.Count; i++)
            {
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
                    SetLcdBusy($"Adding “{t.Title}” ({i + 1}/{tracks.Count})", (double)i / tracks.Count);
                    var deviceItem = await ipod.AddTrackAsync(t, ffmpeg ?? "ffmpeg");
                    _allItems.Add(deviceItem);
                    playlistItems.Add(deviceItem);
                    added++;
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

            SetLcdBusy($"Writing playlist “{name}”", 1);
            await ipod.CreatePlaylistAsync(name, playlistItems);

            // Reflect the new playlist + freshly-imported audio in OrgZ's device tree right away.
            IPodArtworkReader.Invalidate(device.MountPath);
            var pl = new DevicePlaylist { Name = name, Key = SanitizeFileName(name), TrackIds = playlistItems.Select(x => x.Id).ToList() };
            PublishDevicePlaylists(device, device.Playlists.Where(e => e.Key != pl.Key).Append(pl).ToList());
            device.SetSpaceFrom(_allItems.Where(i => i.Source == deviceSource));
            ApplyFilter();

            _log.Information("Synced playlist {Name} to {Device}: matched={Matched} added={Added} failed={Failed} total={Total}", name, device.MountPath, matched, added, failed, playlistItems.Count);
            UpdateMainStatus($"Synced “{name}” to {device.Name} — {playlistItems.Count} track(s), {added} new.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Sync to {Device} failed", device.Name);
            UpdateMainStatus($"Sync failed: {ex.Message}");
        }
        finally
        {
            EndLcdBusy();
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

    /// <summary>Whether the shown device can take ANY sync - the header Sync button's gate.</summary>
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
    /// Runs a device's whole saved plan under ONE batch scope, so a Nano 5G regenerates its
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

        var ipod = IPodDevice.For(dev);
        using var batch = ipod.BeginBatchWrite();

        if (plan.Podcasts && ipod.SupportsPodcasts)
        {
            await SyncPodcastsToDeviceAsync(dev, ipod);
        }

        if (plan.Audiobooks && ipod.SupportsAudiobooks)
        {
            await SyncAudiobooksToDeviceAsync(dev, ipod);
        }

        if (plan.Favorites && ipod.SupportsPlaylists)
        {
            var favorites = _allItems
                .Where(i => i.IsFavorite && i.Kind == MediaKind.Music && !string.IsNullOrEmpty(i.FilePath))
                .ToList();
            if (favorites.Count > 0)
            {
                await SyncPlaylistToDeviceAsync("Favorites", favorites, dev);
            }
        }

        if (plan.PlaylistIds.Count > 0 && ipod.SupportsPlaylists)
        {
            var nameById = await Task.Run(() => MediaCache.LoadAllPlaylists().ToDictionary(p => p.Id, p => p.Name));
            foreach (var pid in plan.PlaylistIds)
            {
                var tracks = await Task.Run(() => GetPlaylistMediaItems(pid));
                if (tracks.Count > 0)
                {
                    await SyncPlaylistToDeviceAsync(nameById.GetValueOrDefault(pid, "Playlist"), tracks, dev);
                }
            }
        }

        UpdateMainStatus($"Sync to {dev.Name} complete.");
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
        int added = 0, skipped = 0, failed = 0;
        try
        {
            for (int i = 0; i < books.Count; i++)
            {
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
                    SetLcdBusy($"Adding “{b.Title}” ({i + 1}/{books.Count})", (double)i / books.Count);
                    var deviceItem = await ipod.AddTrackAsync(b, ffmpeg ?? "ffmpeg");
                    _allItems.Add(deviceItem);
                    added++;
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
        finally
        {
            EndLcdBusy();
        }
    }

    // First-sync cap: only the most recent N downloaded episodes get pushed, so a real library
    // (e.g. 1800+ downloads) doesn't dump everything onto the iPod before we've verified podcasts
    // even show up. Raised/made per-show + unplayed-aware once the format is confirmed on hardware.
    private const int PodcastSyncCap = 5;

    /// <summary>
    /// Syncs DOWNLOADED podcast episodes to a connected iPod through the <see cref="IPodDevice"/>
    /// abstraction, which picks the right format + database for the model (binary iTunesDB, Nano 5G
    /// SQLite, or Rockbox filesystem). Gathers files under {library}/.podcasts/{feedId}/, newest
    /// first, capped at <see cref="PodcastSyncCap"/>. Show grouping + resume are follow-ups.
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
        try
        {
            // Gather episodes (newest first, capped) off the UI thread.
            var episodes = await Task.Run(() =>
            {
                var candidates = new List<(string File, DateTime Mtime, long FeedId)>();
                foreach (var feedDir in Directory.EnumerateDirectories(podcastsDir))
                {
                    if (!long.TryParse(Path.GetFileName(feedDir), out var feedId))
                    {
                        continue;
                    }
                    foreach (var file in Directory.EnumerateFiles(feedDir))
                    {
                        if (!file.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                        {
                            candidates.Add((file, File.GetLastWriteTimeUtc(file), feedId));
                        }
                    }
                }

                var picked = candidates.OrderByDescending(c => c.Mtime).Take(PodcastSyncCap).ToList();
                SetLcdBusy($"Reading {picked.Count} episode(s)…", 0.3);
                var list = new List<PodcastPush>(picked.Count);
                var artByFeed = new Dictionary<long, string?>();   // show cover, fetched once per feed
                var pubByFeed = new Dictionary<long, Dictionary<long, DateTime>>();   // feedId -> episodeId -> RSS publish date
                var titleByFeed = new Dictionary<long, Dictionary<long, string>>();   // feedId -> episodeId -> RSS episode title
                foreach (var (file, mtime, feedId) in picked)
                {
                    subs.TryGetValue(feedId, out var sub);

                    // Downloaded files are named {episodeId}; map back to the local RSS feed cache
                    // (offline) once per feed for the real publish date + episode title.
                    if (!pubByFeed.TryGetValue(feedId, out var pubMap))
                    {
                        pubMap = new Dictionary<long, DateTime>();
                        var titles = new Dictionary<long, string>();
                        foreach (var ep in Services.Podcast.PodcastIndexClient.GetCachedEpisodesByFeedId(feedId) ?? [])
                        {
                            if (ep.DatePublishedEpoch > 0) { pubMap[ep.Id] = DateTimeOffset.FromUnixTimeSeconds(ep.DatePublishedEpoch).UtcDateTime; }
                            if (!string.IsNullOrWhiteSpace(ep.Title)) { titles[ep.Id] = ep.Title!; }
                        }
                        pubByFeed[feedId] = pubMap;
                        titleByFeed[feedId] = titles;
                    }
                    var haveEpId = long.TryParse(Path.GetFileNameWithoutExtension(file), out var epId);

                    // Title precedence: the file's own ID3 tag, then the RSS episode title (so tagless
                    // MP3s don't surface as their bare numeric episode id), then the filename.
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
                    if (haveEpId && title == epId.ToString()
                        && titleByFeed[feedId].TryGetValue(epId, out var rssTitle))
                    {
                        title = rssTitle;
                    }

                    if (!artByFeed.TryGetValue(feedId, out var coverPath))
                    {
                        coverPath = TryDownloadShowArt(sub?.ImageUrl);
                        artByFeed[feedId] = coverPath;
                    }

                    var pubDate = haveEpId && pubMap.TryGetValue(epId, out var pd) ? pd : mtime;

                    list.Add(new PodcastPush(file, title, sub?.Title ?? "Podcast", desc, sub?.FeedUrl, pubDate, lengthMs, coverPath));
                }
                return list;
            });

            if (episodes.Count == 0)
            {
                UpdateMainStatus("No downloaded podcasts to sync.");
                return;
            }

            added = await ipod.AddPodcastsAsync(episodes, ffmpeg ?? "ffmpeg",
                (done, total) => SetLcdBusy($"Copying episode {done} of {total} to {dev.Name}…", total == 0 ? 0.6 : (double)done / total));
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

        _log.Information("Synced podcasts to {Device}: added={Added}", dev.MountPath, added);
        UpdateMainStatus(added > 0 ? $"Synced {added} podcast episode(s) to {dev.Name}." : "No downloaded podcasts to sync.");
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
            return _allItems
                .Where(i => i.IsFavorite && i.Kind == MediaKind.Music && !string.IsNullOrEmpty(i.FilePath))
                .ToList();
        }

        return [];
    }

    /// <summary>
    /// Builds the playlist/Favorites header (name, source, stats, up to four mosaic covers)
    /// for the given view, or clears it for any other view. Covers are decoded off the UI
    /// thread; a fast re-selection is guarded so a stale build can't overwrite a newer view.
    /// </summary>
    private async Task BuildPlaylistHeaderAsync(SidebarItem? item)
    {
        List<MediaItem> tracks;
        string name;
        string source;

        if (item?.PlaylistId is int playlistId)
        {
            tracks = GetPlaylistMediaItems(playlistId);
            name = item.Name;
            source = await Task.Run(() => MediaCache.GetPlaylistSource(playlistId));
        }
        else if (item?.IsFavorites == true)
        {
            tracks = _allItems
                .Where(i => i.IsFavorite && i.Kind == MediaKind.Music && !string.IsNullOrEmpty(i.FilePath))
                .ToList();
            name = item.Name;
            source = "Favorites";
        }
        else
        {
            CurrentPlaylistHeader = null;
            return;
        }

        var count = tracks.Count;
        var totalDuration = TimeSpan.FromTicks(tracks.Sum(t => t.Duration?.Ticks ?? 0));
        var totalSize = tracks.Sum(t => t.FileSize ?? 0);
        var summary = $"{count:N0} {(count == 1 ? "song" : "songs")} · {FormatPlaylistDuration(totalDuration)} · {FormatHelper.FormatFileSize(totalSize)}";

        // ONE TILE PER SONG - the first four tracks in playlist order, each showing its OWN album
        // art or a no-art placeholder (null) when it has none. Duplicates are intentional: two
        // songs from the same album give two identical tiles. Cells past the song count stay null,
        // so a short playlist pads with placeholders.
        var first4 = tracks.Take(4).ToList();
        var covers = await Task.Run(() =>
        {
            var loaded = new List<Bitmap?>(4);
            foreach (var t in first4)
            {
                Bitmap? bmp = null;
                if (t.HasAlbumArt == true && !string.IsNullOrEmpty(t.FilePath))
                {
                    try
                    {
                        var bytes = ExtractAlbumArtBytes(t.FilePath!);
                        if (bytes is { Length: > 0 })
                        {
                            bmp = BitmapFromBytes(bytes);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Debug(ex, "Playlist header cover load failed for {Path}", t.FilePath);
                    }
                }
                loaded.Add(bmp);   // null → the tile renders the no-art placeholder
            }
            return loaded;
        });

        // The user may have switched views while covers decoded - don't clobber the new view.
        if (SelectedSidebarItem != item)
        {
            return;
        }

        CurrentPlaylistHeader = new PlaylistHeaderInfo
        {
            Name = name,
            SourceLabel = source,
            Summary = summary,
            Cover1 = covers.ElementAtOrDefault(0),
            Cover2 = covers.ElementAtOrDefault(1),
            Cover3 = covers.ElementAtOrDefault(2),
            Cover4 = covers.ElementAtOrDefault(3),
        };
    }

    private static string FormatPlaylistDuration(TimeSpan d)
        => d.TotalHours >= 1
            ? $"{(int)d.TotalHours}:{d.Minutes:D2}:{d.Seconds:D2}"
            : $"{d.Minutes}:{d.Seconds:D2}";

    #endregion

    #region Portable Devices (iPod / Rockbox)

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

        // Cancellation scope for THIS device's library scan. Disconnect (or a swap re-using the drive
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
        // capability (via IPodDevice). They list the device's content of that kind once its library
        // is read kind-aware (today the reader tags everything Music, so they're empty for now).
        var ipod = IPodDevice.For(device);
        ListViewConfigs.Register($"{viewKey}:{MediaKind.Podcast}", ListViewConfigs.BuildDeviceKindConfig(device.MountPath, MediaKind.Podcast));
        ListViewConfigs.Register($"{viewKey}:{MediaKind.Audiobook}", ListViewConfigs.BuildDeviceKindConfig(device.MountPath, MediaKind.Audiobook));

        var podcastsChild = new SidebarItem
        {
            Name = "Podcasts",
            Icon = "fa-solid fa-podcast",
            Category = "DEVICE",
            IsEnabled = ipod.SupportsPodcasts,
            ViewConfigKey = $"{viewKey}:{MediaKind.Podcast}",
        };

        var audiobooksChild = new SidebarItem
        {
            Name = "Audiobooks",
            Icon = "fa-solid fa-headphones",
            Category = "DEVICE",
            IsEnabled = ipod.SupportsAudiobooks,
            ViewConfigKey = $"{viewKey}:{MediaKind.Audiobook}",
        };

        var sidebarItem = new SidebarItem
        {
            Name = device.SidebarLabel,
            Icon = device.Icon,
            IconBitmap = device.GenerationImage,
            Category = "DEVICES",
            IsEnabled = true,
            ViewConfigKey = viewKey,
            Children = { podcastsChild, audiobooksChild },
        };
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

        // Cancel the in-flight library scan FIRST, so batches it already queued on the dispatcher void
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
        // family MUST go: a different iPod arriving at the reused drive letter inherits the exact same
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

    #endregion

    public void Dispose()
    {
        _folderWatcher?.Dispose();
        _deviceDetection?.Dispose();
        foreach (var scanCts in _deviceScanCts.Values)
        {
            scanCts.Cancel();   // disposal happens in the scan's own finally
        }
#if WINDOWS
        _thumbBarService?.Dispose();
        _smtcService?.Dispose();
#endif
        _mprisService?.Dispose();
        _macNowPlaying?.Dispose();
        _audioOutput.SavePersistedSelections();
        _audioTap?.Dispose();
        _audioOutput.Dispose();
        var pendingCts = Interlocked.Exchange(ref _radioSwitchCts, null);
        pendingCts?.Cancel();
        pendingCts?.Dispose();
        if (_currentMedia != null && _currentMediaMetaHandler != null)
        {
            _currentMedia.MetaChanged -= _currentMediaMetaHandler;
            _currentMediaMetaHandler = null;
        }
        _currentMedia?.Dispose();
        _player?.Dispose();
        _vlc?.Dispose();
    }
}
