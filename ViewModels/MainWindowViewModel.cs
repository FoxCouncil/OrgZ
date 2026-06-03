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
    private bool _cdScanning;
    private Bitmap? _cdCoverArt;
    private byte[]? _cdCoverArtBytes;

    private DeviceDetectionService? _deviceDetection;
    private readonly Dictionary<string, ConnectedDevice> _connectedDevices = new(StringComparer.OrdinalIgnoreCase);

    private bool isSeeking = false;

    private List<MediaItem> _allItems = [];

    private ListViewConfig? _activeViewConfig;

    private MediaItem? CurrentPlayingItem => _playbackContext?.CurrentItem;

    private MediaItem? CurrentMusicItem => CurrentPlayingItem?.Kind == MediaKind.Music ? CurrentPlayingItem : null;

    private MediaItem? CurrentStation => CurrentPlayingItem?.Kind == MediaKind.Radio ? CurrentPlayingItem : null;

    [ObservableProperty]
    private StatusBarViewModel _statusBar = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCdViewActive))]
    private SidebarItem? _selectedSidebarItem;

    [ObservableProperty]
    private ConnectedDevice? _selectedDevice;

    internal ObservableCollection<SidebarItem> LibraryItems { get; } = [];

    internal ObservableCollection<SidebarItem> DeviceItems { get; } = [];

    /// <summary>
    /// Rebuilds the LibraryItems list. Called on startup and when settings like "Show Ignored in sidebar" change.
    /// </summary>
    internal void RebuildLibraryItems()
    {
        var selectedKey = SelectedSidebarItem?.ViewConfigKey;
        LibraryItems.Clear();

        LibraryItems.Add(new() { Name = "Music",      Icon = "fa-solid fa-music",           Category = "LIBRARY", IsEnabled = true,  Kind = MediaKind.Music, ViewConfigKey = "Music" });
        LibraryItems.Add(new() { Name = "Radio",      Icon = "fa-solid fa-tower-broadcast", Category = "LIBRARY", IsEnabled = true,  Kind = MediaKind.Radio, ViewConfigKey = "Radio" });
        LibraryItems.Add(new() { Name = "Podcasts",   Icon = "fa-solid fa-podcast",         Category = "LIBRARY", IsEnabled = false });
        LibraryItems.Add(new() { Name = "Audiobooks", Icon = "fa-solid fa-headphones",      Category = "LIBRARY", IsEnabled = false });

        if (Settings.Get("OrgZ.ShowIgnored", true))
        {
            LibraryItems.Add(new() { Name = "Ignored", Icon = "fa-solid fa-eye-slash", Category = "LIBRARY", IsEnabled = true, ViewConfigKey = "Ignored" });
        }

        if (Settings.Get("OrgZ.BadFormat.ShowInSidebar", true))
        {
            LibraryItems.Add(new() { Name = "Bad Format", Icon = "fa-solid fa-triangle-exclamation", Category = "LIBRARY", IsEnabled = true, ViewConfigKey = "BadFormat" });
        }

        // Preserve selection if the current view still exists after the rebuild
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
    /// Playback page is active AND there's no track loaded — the LCD body
    /// should show the BW app icon instead of empty text rows.
    /// </summary>
    public bool IsLcdPlaybackIdle => IsLcdPlayback && IsLcdIdle;

    /// <summary>
    /// Playback page is active AND a track is loaded — the standard track
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
    /// across tracks within a session — most apps keep this preference sticky.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTrackDurationDisplay))]
    private bool _showRemainingTime = true;

    /// <summary>
    /// Renders the right-side LCD time label. Honours <see cref="ShowRemainingTime"/>
    /// and falls back to the raw duration string when there's no track loaded
    /// (durationNumber == 0) — the "-X:XX" form would be meaningless there.
    /// </summary>
    public string CurrentTrackDurationDisplay
    {
        get
        {
            // Both branches return the duration string with one leading character
            // so toggling between them never changes the rendered width — only
            // the leading glyph swaps between "-" (remaining) and " " (total).
            if (!ShowRemainingTime || CurrentTrackDurationNumber <= 0)
            {
                return " " + CurrentTrackDuration;
            }
            var remainingMs = Math.Max(0, CurrentTrackDurationNumber - CurrentTrackTimeNumber);
            return "-" + TimeSpan.FromMilliseconds(remainingMs).ToString(@"m\:ss");
        }
    }

    internal void ToggleDurationDisplay() => ShowRemainingTime = !ShowRemainingTime;

    [ObservableProperty]
    private uint _currentVolume = (uint)Settings.Get("OrgZ.Volume", 100);

    private uint _previousVolume;

    [ObservableProperty]
    private Bitmap? _currentAlbumArt;

    [ObservableProperty]
    private bool _isSeekEnabled = true;

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

    // -- Drill-Down --

    [ObservableProperty]
    private DrillDownState? _drillDownState;

    [ObservableProperty]
    private List<DrillDownEntry> _drillDownEntries = [];

    public bool IsDrillDownActive => DrillDownState != null;

    // -- Queue --

    [ObservableProperty]
    private bool _isQueueVisible;

    public ObservableCollection<MediaItem>? PlaybackContextUpcoming => _playbackContext?.UpcomingItems;

    // -- Activity --

    [ObservableProperty]
    private bool _isActivityPanelVisible;

    // Rip-in-progress LCD state. iTunes-style: while a rip is running the
    // now-playing LCD swaps to show "Importing 'Track'", a progress bar, and
    // a "Time remaining: 0:15 (8.5×)" readout. Cleared on rip completion or
    // failure. The activity panel still tracks the same data for history.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLcdCycleButton))]
    private bool _isRipping;
    [ObservableProperty] private string _ripTitle = string.Empty;
    [ObservableProperty] private string _ripDetail = string.Empty;
    [ObservableProperty] private double _ripPercent;

    // Active rip's cancellation source. The Cancel X on the LCD's rip page
    // trips this; CdRipService respects the token between sector reads, so the
    // current sector finishes and the loop exits cleanly.
    private CancellationTokenSource? _ripCts;

    [RelayCommand]
    private void CancelRip()
    {
        _ripCts?.Cancel();
    }

    // LCD "pages": the now-playing display has multiple modes the user cycles
    // through with the left-chevron button. Playback (track info + scrubber)
    // and Vu (FFT bars) are always available; Rip joins them only while a rip
    // is in flight. Auto-snap to Rip when one starts so the user sees it
    // immediately; snap back to Playback when it ends.
    public enum LcdPage { Playback, Vu, Rip }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLcdPlayback), nameof(IsLcdVu), nameof(IsLcdRip), nameof(IsLcdPlaybackIdle), nameof(IsLcdPlaybackActive))]
    private LcdPage _currentLcdPage = LcdPage.Playback;

    public bool IsLcdPlayback => CurrentLcdPage == LcdPage.Playback;
    public bool IsLcdVu => CurrentLcdPage == LcdPage.Vu;
    public bool IsLcdRip => CurrentLcdPage == LcdPage.Rip;

    private IReadOnlyList<LcdPage> AvailableLcdPages
    {
        get
        {
            var pages = new List<LcdPage> { LcdPage.Playback, LcdPage.Vu };
            if (IsRipping) pages.Add(LcdPage.Rip);
            return pages;
        }
    }

    public bool ShowLcdCycleButton => AvailableLcdPages.Count > 1 && !IsLcdIdle;

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

    partial void OnIsRippingChanged(bool value)
    {
        if (value)
        {
            CurrentLcdPage = LcdPage.Rip;
        }
        else if (CurrentLcdPage == LcdPage.Rip)
        {
            CurrentLcdPage = LcdPage.Playback;
        }
    }

    internal ObservableCollection<ActivityItem> Activities { get; } = [];

    public int ActivityBadgeCount => Activities.Count(a => a.Status is ActivityStatus.Pending or ActivityStatus.Running);

    public bool HasActiveActivities => ActivityBadgeCount > 0;

    internal ActivityItem AddActivity(string title)
    {
        var item = new ActivityItem { Title = title, Status = ActivityStatus.Running };
        UI(() =>
        {
            Activities.Add(item);
            OnPropertyChanged(nameof(ActivityBadgeCount));
            OnPropertyChanged(nameof(HasActiveActivities));
        });
        return item;
    }

    internal void UpdateActivityBadge()
    {
        UI(() =>
        {
            OnPropertyChanged(nameof(ActivityBadgeCount));
            OnPropertyChanged(nameof(HasActiveActivities));
        });
    }

    [RelayCommand]
    private void ClearCompletedActivities()
    {
        var toRemove = Activities.Where(a => a.Status is ActivityStatus.Completed or ActivityStatus.Failed).ToList();
        foreach (var item in toRemove)
        {
            Activities.Remove(item);
        }
        OnPropertyChanged(nameof(ActivityBadgeCount));
        OnPropertyChanged(nameof(HasActiveActivities));
    }

    [RelayCommand]
    private void ToggleActivityPanel()
    {
        IsActivityPanelVisible = !IsActivityPanelVisible;
    }

    // -- Unified Data --

    [ObservableProperty]
    private MediaItem? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSearchResults), nameof(NoSearchResultsMessage))]
    // Not persisted across app launches — search is always transient state.
    // Per-view search is stored in _searchTextByView and swapped on sidebar changes.
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSearchResults))]
    private List<MediaItem> _filteredItems = [];

    public bool ShowNoSearchResults => FilteredItems.Count == 0 && !string.IsNullOrWhiteSpace(SearchText);

    public string NoSearchResultsMessage => $"No search results for \"{SearchText}\".";

    /// <summary>
    /// DataGrid-bound view wrapping <see cref="FilteredItems"/>. When the active view config
    /// sets <c>GroupByPath</c>, this view's <c>GroupDescriptions</c> enables Avalonia's built-in
    /// collapsible group headers. Always non-null after the first ApplyFilter call.
    /// </summary>
    [ObservableProperty]
    private DataGridCollectionView? _filteredItemsView;

    // -- Radio Filters --

    internal ObservableCollection<string> Countries { get; } = [];

    internal ObservableCollection<string> Genres { get; } = [];

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
        ApplyFilter();

        if (!_suppressSearchPersist)
        {
            PerViewSearchState.Save(_searchTextByView, SelectedSidebarItem?.ViewConfigKey, value);
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
    private NowPlayingFullScreenWindow? _fullScreen;

    /// <summary>
    /// Brings the main window back from the mini-player (iTunes-style) hidden
    /// state.  Works whether the mini-player is currently open or not — useful
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
    /// mode both windows remain visible.  Idempotent — if the mini-player is already
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
        _miniPlayer.FullScreenRequested += ShowNowPlayingFullScreen;
        _miniPlayer.Closed += (_, _) =>
        {
            _miniPlayer = null;
            if (!_window.IsVisible)
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

    [RelayCommand]
    internal void ShowNowPlayingFullScreen()
    {
        if (_fullScreen != null)
        {
            _fullScreen.Activate();
            return;
        }

        _fullScreen = new NowPlayingFullScreenWindow { DataContext = this };
        _fullScreen.Closed += (_, _) => _fullScreen = null;
        _fullScreen.Show();
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

        // Don't clear SearchText — the per-view swap in OnSelectedSidebarItemChanged
        // restores whatever search was active in the target view (possibly nothing).
        SelectedSidebarItem = target;
        SelectedItem = item;
        ScrollToSelectedRequested?.Invoke();
    }

    partial void OnSelectedCountryChanged(string value)
    {
        ApplyFilter();
        Settings.Set("OrgZ.Radio.Country", value);
        Settings.Save();
    }

    partial void OnSelectedGenreChanged(string value)
    {
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

        _activeViewConfig = ListViewConfigs.Get(value?.ViewConfigKey);

        if (!string.IsNullOrEmpty(value?.ViewConfigKey))
        {
            Settings.Set("OrgZ.ActiveView", value.ViewConfigKey);
            Settings.Save();
        }

        ApplyFilter();

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
            StatusBar.StationCount = FilteredItems.Count.ToString();
        }
    }

    private void ApplyFilter()
    {
        if (_activeViewConfig == null)
        {
            _log.Debug("ApplyFilter: _activeViewConfig is null — emptying FilteredItems");
            FilteredItems = [];
            UpdateNavigationButtons();
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var viewKey = _activeViewConfig.Key;
        var startCount = _allItems.Count;

        try
        {
            // Snapshot _allItems up front so a concurrent mutation (background scan,
            // file watcher, anything that AddRange's during render) can't throw
            // InvalidOperationException("Collection was modified") halfway through the
            // pipeline. _allItems should be UI-thread-only by convention, but the cost
            // of a snapshot is one array allocation — cheap insurance against the kind
            // of all-tabs-go-empty bug we're chasing.
            var snapshot = _allItems.ToArray();
            IEnumerable<MediaItem> items = snapshot.Where(_activeViewConfig.BaseFilter);

            // Global ignore filter — hide ignored items from every view except the Ignored view itself
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
                    items = items.Where(s => s.NormalizedGenre == SelectedGenre);
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

            FilteredItems = items.ToList();

            // Build the DataGridCollectionView wrapper. If the view config asks for grouping,
            // wire it up so Avalonia's DataGrid renders collapsible group headers.
            var view = new DataGridCollectionView(FilteredItems);
            if (_activeViewConfig.GroupByPath != null)
            {
                view.GroupDescriptions.Add(new DataGridPathGroupDescription(_activeViewConfig.GroupByPath));
            }
            FilteredItemsView = view;

            // Update radio station count in status bar
            if (_activeViewConfig.ShowRadioFilterPanel)
            {
                UI(() => StatusBar.StationCount = FilteredItems.Count.ToString());
            }

            // Update generic status bar for non-Music/Radio views
            if (StatusBar.HasGenericStats)
            {
                UpdateGenericStatusBar();
            }

            UpdateNavigationButtons();

            sw.Stop();
            _log.Debug("ApplyFilter ok: ViewKey={ViewKey} _allItems={AllCount} Filtered={FilteredCount} Elapsed={ElapsedMs}ms", viewKey, startCount, FilteredItems.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Don't leave the UI in a broken state. Log loudly, then empty FilteredItems
            // so the user sees a clean (empty) grid instead of stale/garbage rows. The
            // exception is the actual diagnostic — DO NOT swallow without surfacing.
            _log.Error(ex, "ApplyFilter threw: ViewKey={ViewKey} _allItems={AllCount} Elapsed={ElapsedMs}ms", viewKey, startCount, sw.ElapsedMilliseconds);
            FilteredItems = [];
            FilteredItemsView = new DataGridCollectionView(FilteredItems);
            UpdateNavigationButtons();
        }
    }

    // -- Headless seeding seam --
    // Generic hooks the docs-screenshot runner uses to drive views. No
    // screenshot-specific data or orchestration lives in the app: UpdateData,
    // AddActivity, DeviceItems/LibraryItems/PlaylistItems, and the playback/LCD
    // properties are already internal/public, so the runner composes scenes from
    // those plus the four primitives below.

    /// <summary>Replaces the backing item list. Pair with <see cref="RefreshView"/>
    /// or a sidebar selection to re-run the filter.</summary>
    internal void SetItems(IReadOnlyList<MediaItem> items) => _allItems = items.ToList();

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
        // LibVLC's own volume is pinned at 100% — the audio tap sits
        // downstream of LibVLC's volume filter, so any attenuation at this
        // level would hit the FFT analyzer and make the VU meter scale
        // with the user's volume slider.  Volume is applied ONLY in the
        // sink bus (MasterVolume) and per-sink, which sit after the tap.
        _player.Volume = 100;

        // Attach the audio tap BEFORE any Play() call — LibVLC only routes
        // samples through SetAudioCallbacks for media that start playing
        // after the callbacks were registered.  Once wired, every track the
        // user plays funnels through the sink bus (audible on selected
        // devices) and the FFT analyzer (VU-meter data).
        _audioTap = new OrgZ.Services.AudioVisualization.AudioTap(_audioOutput.Bus);
        _audioTap.Attach(_player);
        _audioOutput.LoadAndApplyPersistedSelections();
        UpdateMasterVolume();

        _player.EndReached += (s, e) => UI(() =>
        {
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
            CurrentTrackTime = TimeSpan.FromMilliseconds(e.Time).ToString("m\\:ss");
            if (!isSeeking)
            {
                CurrentTrackTimeNumber = e.Time;
            }

            // Push pivots to macOS Now Playing: the very first TimeChanged (so
            // the widget locks onto libvlc's clock instead of extrapolating from
            // 0), every 5 s as a re-sync against any drift, and on a rewind
            // (track change → e.Time resets to 0). The widget extrapolates
            // smoothly between pivots at rate=1, which matches OrgZ's display
            // much better than flooding macOS with 4 Hz updates — the widget
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

            // CD tracks set their own display values in ExecutePlayCd — don't overwrite
            if (CurrentPlayingItem?.Source == "cdda")
            {
                if (e.Media != null && e.Media.Duration > 0)
                {
                    CurrentTrackDuration = TimeSpan.FromMilliseconds(e.Media.Duration).ToString(@"m\:ss");
                    CurrentTrackDurationNumber = e.Media.Duration;
                }
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
                    CurrentTrackDuration = TimeSpan.FromMilliseconds(e.Media.Duration).ToString(@"m\:ss");
                    CurrentTrackDurationNumber = e.Media.Duration;
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

            CurrentTrackDuration = TimeSpan.FromMilliseconds(e.Media.Duration).ToString("m\\:ss");
            CurrentTrackDurationNumber = e.Media.Duration;

            CurrentTrackLine1 = CurrentMusicItem?.Title ?? "Unknown Title";
            var artist = CurrentMusicItem?.Artist ?? "Unknown Artist";
            var album = CurrentMusicItem?.Album;
            CurrentTrackLine2 = string.IsNullOrWhiteSpace(album) ? artist : $"{artist} \u2014 {album}";
        });

        // Linux shell integration — GNOME/KDE/XFCE media keys + panel widgets. Failure
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

            var kind = GetEffectiveKind();

            if (kind == MediaKind.Radio)
            {
                if (CurrentStation != null && _player.IsPlaying)
                {
                    Stop();
                    return;
                }

                if (CurrentStation != null)
                {
                    PlayRadioStation(CurrentStation);
                    return;
                }

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

            if (CurrentMusicItem == null)
            {
                if (SelectedItem?.Kind == MediaKind.Music)
                {
                    PlayMusicItem(SelectedItem);
                }
                else if (FilteredItems.Count > 0)
                {
                    PlayMusicItem(FilteredItems[0]);
                }

                return;
            }

            if (_player.IsPlaying)
            {
                Pause();
            }
            else
            {
                Play();
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
        await ScanAndAnalyzeMusicAsync();
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

    [RelayCommand]
    internal async Task ShowSettings()
    {
        var dialog = new Views.SettingsDialog(_allItems, _audioOutput);
        // The main window can be hidden when the mini-player is up — Avalonia 12 throws
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

        // Sidebar composition depends on OrgZ.ShowIgnored — refresh in case it was toggled
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
                await ScanAndAnalyzeMusicAsync();
                StartFolderWatcher();
            }
        }

        if (dialog.RadioCacheCleared)
        {
            Services.MediaCache.RemoveRadioBySource("radiobrowser");

            _allItems.RemoveAll(i => i.Kind == MediaKind.Radio && !i.IsFavorite);

            if (SelectedSidebarItem?.Kind == MediaKind.Radio)
            {
                ApplyFilter();
            }
        }
    }

    internal async Task LaunchRadioSync()
    {
        if (IsSyncing)
        {
            return;
        }

        var dialog = new Views.SyncProgressDialog();
        var syncTask = RunSyncWithDialog(dialog);
        await dialog.ShowDialog(_window);
        await syncTask;
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
            case MediaKind.Music:
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
            case MediaKind.Music:
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
    /// ExecutePlayMusic for the actual playback — the MediaChanged handler detects
    /// device sources and appends the device label to Line2.
    /// </summary>
    private void ExecutePlayDeviceTrack(MediaItem item)
    {
        ExecutePlayMusic(item);
    }

    private void ExecutePlayCd(MediaItem track)
    {
        SelectedItem = track;

        CurrentTrackLine1 = track.Title ?? "Unknown Track";
        CurrentTrackLine2 = !string.IsNullOrWhiteSpace(track.Artist)
            ? (string.IsNullOrWhiteSpace(track.Album) ? track.Artist : $"{track.Artist} \u2014 {track.Album}")
            : track.Album ?? "";
        CurrentTrackDuration = track.Duration?.ToString(@"m\:ss") ?? "--:--";
        CurrentTrackDurationNumber = (long)(track.Duration?.TotalMilliseconds ?? 0);

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
        // overhead on top. libvlc's default file-caching (~300 ms) isn't enough — the
        // playback stalls between buffer refills. 3 s headroom is comfortable.
        if (track.Source == "cdda")
        {
            _currentMedia.AddOption(":file-caching=3000");
            _currentMedia.AddOption(":disc-caching=3000");
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
        if (_player == null || file == null || file.Kind != MediaKind.Music)
        {
            return;
        }

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
            // If the item is already in the current context, just jump to it
            if (_playbackContext != null && _playbackContext.JumpTo(file))
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

                if (_playbackContext != null && _playbackContext.JumpTo(station))
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

    private void ExecutePlayMusic(MediaItem file)
    {
        SelectedItem = file;

        // Don't dispose — Avalonia's ref-counted bitmap lifecycle handles cleanup.
        // Explicit Dispose() while a render pass is in flight causes ObjectDisposedException.
        var artBytes = ExtractAlbumArtBytes(file.FilePath!);
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

        // Don't dispose — Avalonia's ref-counted bitmap lifecycle handles cleanup.
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

            // Radio streams over flaky uplinks need more headroom than libvlc's 1s
            // default network buffer. Without this, the smallest server-side jitter
            // starves the decoder and libvlc cancels the HTTP/2 stream rather than
            // waiting for more data — observed as `local stream 1 error: Cancellation
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
                // call — that's the rapid-switch segfault.
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
                    // Re-check on the UI thread — a station switch could have landed between
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
    /// is playing — consumers just read whenever they need to render.
    /// </summary>
    internal OrgZ.Services.AudioVisualization.IAudioVisualizationSource AudioVisualization => _audioTap;

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
        _playbackContext?.Release();
        _playbackContext = null;
        OnPropertyChanged(nameof(PlaybackContextUpcoming));

        // Stop libvlc before releasing the Media object — disposing Media alone
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

        // Don't dispose — Avalonia's ref-counted bitmap lifecycle handles cleanup.
        // Explicit Dispose() while a render pass is in flight causes ObjectDisposedException.
        CurrentAlbumArt = null;
        CurrentTrackLine1 = string.Empty;
        CurrentTrackLine2 = string.Empty;
        CurrentTrackTime = "0:00";
        CurrentTrackDuration = "0:00";
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
    internal async Task FetchPopularStationsAsync()
    {
        var radioItems = _allItems.Where(i => i.Kind == MediaKind.Radio);
        if (radioItems.Any() || IsLoading)
        {
            return;
        }

        IsLoading = true;

        try
        {
            UpdateMainStatus("Fetching popular stations...");

            try
            {
                var stations = await RadioBrowserService.GetTopStationsAsync(250);
                if (stations.Count > 0)
                {
                    await Task.Run(() => MediaCache.UpsertRadioStations(stations));
                    _allItems.AddRange(stations);
                }
            }
            catch (Exception ex)
            {
                Messages.Add($"Fetch failed: {ex.Message}");
            }

            RebuildRadioFilterOptions();
            ApplyFilter();

            StatusBar.ErrorCount = Messages.Count;
            StatusBar.SyncStatus = "Sync for full library (~95k)";
        }
        catch (Exception ex)
        {
            Messages.Add($"Load error: {ex.Message}");
            StatusBar.ErrorCount = Messages.Count;
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal async Task RunSyncWithDialog(Views.SyncProgressDialog dialog)
    {
        if (IsSyncing)
        {
            return;
        }

        IsSyncing = true;
        var ct = dialog.CancellationToken;
        int totalSynced = 0;

        try
        {
            dialog.UpdateSource("Syncing radio stations...");
            dialog.SetIndeterminate(true);

            await foreach (var batch in RadioBrowserService.GetAllStationsAsync(ct))
            {
                await Task.Run(() => MediaCache.UpsertRadioStations(batch), ct);
                totalSynced += batch.Count;
                dialog.UpdateProgress(totalSynced, $"{totalSynced:N0} stations");
            }

            MediaCache.RecordSync("radiobrowser", totalSynced, dialog.ElapsedMs);

            // Reload radio items from DB
            var freshRadio = await Task.Run(() => MediaCache.LoadAllRadio());
            _allItems.RemoveAll(i => i.Kind == MediaKind.Radio);
            _allItems.AddRange(freshRadio);

            RebuildRadioFilterOptions();
            ApplyFilter();

            dialog.Finish($"Done! {totalSynced:N0} stations synced");
            UI(() => StatusBar.SyncStatus = "Last synced: just now");
        }
        catch (OperationCanceledException)
        {
            var freshRadio = await Task.Run(() => MediaCache.LoadAllRadio());
            _allItems.RemoveAll(i => i.Kind == MediaKind.Radio);
            _allItems.AddRange(freshRadio);

            RebuildRadioFilterOptions();
            ApplyFilter();

            dialog.Finish($"Cancelled ({totalSynced:N0} stations synced)");
            Messages.Add($"Sync cancelled after {totalSynced:N0} stations");
            StatusBar.ErrorCount = Messages.Count;
        }
        catch (Exception ex)
        {
            Messages.Add($"Sync error: {ex.Message}");
            StatusBar.ErrorCount = Messages.Count;
            dialog.Finish($"Error: {ex.Message}");
        }
        finally
        {
            IsSyncing = false;
        }
    }

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

    [RelayCommand]
    internal void AddUserStation(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        string name;
        string url;

        var parts = input.Split('|', 2);
        if (parts.Length == 2)
        {
            name = parts[0].Trim();
            url = parts[1].Trim();
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

        var station = new MediaItem
        {
            Id = $"user:{Guid.NewGuid()}",
            Kind = MediaKind.Radio,
            Source = "user",
            Title = name,
            StreamUrl = url,
        };

        MediaCache.UpsertRadioStations([station]);
        _allItems.Add(station);
        ApplyFilter();
    }

    private void RebuildRadioFilterOptions()
    {
        var prevCountry = SelectedCountry;
        var prevGenre = SelectedGenre;

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
        // Only show canonical genres that actually appear in the current dataset.
        var activeGenres = radioItems
            .Select(s => s.NormalizedGenre)
            .Distinct()
            .ToHashSet();
        foreach (var canonical in GenreNormalizer.AllCanonical)
        {
            if (activeGenres.Contains(canonical))
            {
                Genres.Add(canonical);
            }
        }

        SelectedCountry = Countries.Contains(prevCountry) ? prevCountry : "All";
        SelectedGenre = Genres.Contains(prevGenre) ? prevGenre : "All";
    }

    #endregion

    #region Drill-Down Navigation

    [RelayCommand]
    internal void ToggleDrillDown()
    {
        if (DrillDownState != null)
        {
            ExitDrillDown();
        }
        else if (_activeViewConfig?.SupportsDrillDown == true)
        {
            DrillDownState = new DrillDownState { Level = DrillDownLevel.Artists };
            BuildDrillDownEntries();
            OnPropertyChanged(nameof(IsDrillDownActive));
        }
    }

    internal void DrillInto(DrillDownEntry entry)
    {
        if (DrillDownState == null)
        {
            return;
        }

        switch (DrillDownState.Level)
        {
            case DrillDownLevel.Artists:
            {
                DrillDownState.SelectedArtist = entry.GroupKey;
                DrillDownState.Level = DrillDownLevel.Albums;
                BuildDrillDownEntries();
                break;
            }

            case DrillDownLevel.Albums:
            {
                DrillDownState.SelectedAlbum = entry.GroupKey;
                DrillDownState.Level = DrillDownLevel.Songs;
                // At songs level, filter the normal list and exit drill-down grid mode
                ApplyDrillDownSongsFilter();
                break;
            }
        }
    }

    internal void DrillUpToRoot()
    {
        ExitDrillDown();
    }

    internal void DrillUpToArtist()
    {
        if (DrillDownState == null)
        {
            return;
        }

        DrillDownState.SelectedAlbum = null;
        DrillDownState.Level = DrillDownLevel.Albums;
        BuildDrillDownEntries();
    }

    private void ExitDrillDown()
    {
        DrillDownState = null;
        DrillDownEntries = [];
        OnPropertyChanged(nameof(IsDrillDownActive));
        ApplyFilter();
    }

    private void BuildDrillDownEntries()
    {
        var musicItems = _allItems.Where(i => i.Kind == MediaKind.Music);

        if (DrillDownState!.Level == DrillDownLevel.Artists)
        {
            DrillDownEntries = musicItems
                .Where(i => !string.IsNullOrEmpty(i.Artist))
                .GroupBy(i => i.Artist!)
                .Select(g => new DrillDownEntry
                {
                    GroupKey = g.Key,
                    ItemCount = g.Count(),
                    TotalDuration = TimeSpan.FromTicks(g.Sum(x => x.Duration?.Ticks ?? 0)),
                    SecondaryInfo = $"{g.Select(x => x.Album).Where(a => !string.IsNullOrEmpty(a)).Distinct().Count()} albums, {g.Count()} songs",
                })
                .OrderBy(e => e.GroupKey)
                .ToList();
        }
        else if (DrillDownState.Level == DrillDownLevel.Albums)
        {
            DrillDownEntries = musicItems
                .Where(i => string.Equals(i.Artist, DrillDownState.SelectedArtist, StringComparison.OrdinalIgnoreCase))
                .Where(i => !string.IsNullOrEmpty(i.Album))
                .GroupBy(i => i.Album!)
                .Select(g => new DrillDownEntry
                {
                    GroupKey = g.Key,
                    ItemCount = g.Count(),
                    TotalDuration = TimeSpan.FromTicks(g.Sum(x => x.Duration?.Ticks ?? 0)),
                    SecondaryInfo = $"{g.FirstOrDefault()?.Year?.ToString() ?? "?"} \u2014 {g.Count()} songs",
                })
                .OrderBy(e => e.GroupKey)
                .ToList();
        }
    }

    private void ApplyDrillDownSongsFilter()
    {
        if (DrillDownState == null)
        {
            return;
        }

        var artist = DrillDownState.SelectedArtist;
        var album = DrillDownState.SelectedAlbum;

        var tracksInAlbum = _allItems
            .Where(i => i.Kind == MediaKind.Music)
            .Where(i => string.Equals(i.Artist, artist, StringComparison.OrdinalIgnoreCase))
            .Where(i => string.Equals(i.Album, album, StringComparison.OrdinalIgnoreCase));
        FilteredItems = AlbumTrackSort.Order(tracksInAlbum).ToList();

        // Search within this scope
        var searchText = SearchText?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(searchText) && _activeViewConfig != null)
        {
            FilteredItems = FilteredItems.Where(i => _activeViewConfig.SearchFilter(i, searchText)).ToList();
        }
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
                var activity = AddActivity($"Copying {unmatched.Count} track(s) to library");
                IsActivityPanelVisible = true;
                int copied = 0;

                foreach (var sourcePath in unmatched)
                {
                    copied++;
                    UI(() =>
                    {
                        activity.Detail = $"Copying {copied} of {unmatched.Count}: {Path.GetFileName(sourcePath)}";
                        activity.Progress = (double)copied / unmatched.Count;
                    });

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
                        UI(() => activity.Detail = $"Failed: {Path.GetFileName(sourcePath)} — {ex.Message}");
                    }
                }

                UI(() =>
                {
                    activity.Status = ActivityStatus.Completed;
                    activity.Detail = $"{copied} track(s) copied";
                    activity.Progress = 1.0;
                    UpdateActivityBadge();
                });
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

        var playlistId = MediaCache.CreatePlaylist(chosenName.Trim());
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
    /// Snapshot of currently connected devices — safe to enumerate from the view layer
    /// without holding a reference to the live _connectedDevices dictionary.
    /// </summary>
    internal IReadOnlyList<ConnectedDevice> ConnectedDevicesSnapshot()
        => _connectedDevices.Values.ToList();

    /// <summary>
    /// Copies a library playlist onto a connected writable device as an M3U file under
    /// <c>{mount}/Playlists/</c>. Tracks are matched against the device's own library by
    /// case-insensitive Artist + Title; unmatched library tracks are reported in the
    /// activity detail but don't abort the export. The resulting M3U uses Rockbox-style
    /// absolute paths ("/Music/...") so the device can resolve them regardless of where
    /// its filesystem is mounted on a host.
    /// </summary>
    internal async Task SendPlaylistToDevice(SidebarItem playlistItem, ConnectedDevice device)
    {
        if (!playlistItem.PlaylistId.HasValue || device.IsReadOnly)
        {
            return;
        }

        var activity = AddActivity($"Sending \"{playlistItem.Name}\" to {device.Name}");

        try
        {
            var libraryTracks = await Task.Run(() => GetPlaylistMediaItems(playlistItem.PlaylistId.Value));
            if (libraryTracks.Count == 0)
            {
                activity.Status = ActivityStatus.Completed;
                activity.Detail = "Playlist is empty — nothing to send";
                UpdateActivityBadge();
                return;
            }

            var deviceSource = $"device:{device.MountPath}";
            var deviceTracksByAT = _allItems
                .Where(i => i.Source == deviceSource && !string.IsNullOrEmpty(i.FilePath))
                .GroupBy(i => NormalizeMatchKey(i.Artist, i.Title))
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var matchedPaths = new List<string>(libraryTracks.Count);
            int matched = 0, missed = 0;

            foreach (var libTrack in libraryTracks)
            {
                var key = NormalizeMatchKey(libTrack.Artist, libTrack.Title);
                if (!string.IsNullOrEmpty(key) && deviceTracksByAT.TryGetValue(key, out var deviceTrack))
                {
                    matchedPaths.Add(ToDeviceRelativePath(deviceTrack.FilePath!, device.MountPath));
                    matched++;
                }
                else
                {
                    missed++;
                }
            }

            // Write the M3U file next to the device's existing playlists
            var playlistsDir = Path.Combine(device.MountPath, "Playlists");
            Directory.CreateDirectory(playlistsDir);
            var safeName = SanitizeFileName(playlistItem.Name);
            var targetPath = Path.Combine(playlistsDir, safeName + ".m3u");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("#EXTM3U");
            sb.Append("#PLAYLIST:").AppendLine(playlistItem.Name);
            foreach (var p in matchedPaths)
            {
                sb.AppendLine(p);
            }
            await File.WriteAllTextAsync(targetPath, sb.ToString());

            activity.Status = matched > 0 ? ActivityStatus.Completed : ActivityStatus.Failed;
            activity.Detail = missed == 0
                ? $"{matched} tracks written"
                : $"{matched} written, {missed} not found on device";
            if (matched == 0)
            {
                activity.Error = "None of the library tracks were found on the device";
            }
            UpdateActivityBadge();

            // Publish the newly-added playlist into the device's sidebar tree so the user
            // can navigate to it immediately without waiting for a reconnect.
            var pl = new DevicePlaylist
            {
                Name = playlistItem.Name,
                Key = safeName,
                TrackIds = matchedPaths
                    .Select(p => ToMountAbsolute(p, device.MountPath))
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList(),
            };
            // Merge into the existing list (don't clobber other device playlists) —
            // find any with the same Key and replace; otherwise append.
            var merged = device.Playlists
                .Where(existing => existing.Key != pl.Key)
                .Append(pl)
                .ToList();
            PublishDevicePlaylists(device, merged);

            _log.Information("Playlist sent to device: Playlist={Name} Device={MountPath} Matched={Matched} Missed={Missed}",
                playlistItem.Name, device.MountPath, matched, missed);
        }
        catch (Exception ex)
        {
            activity.Status = ActivityStatus.Failed;
            activity.Error = ex.Message;
            activity.Detail = "Send failed";
            UpdateActivityBadge();
            _log.Error(ex, "Failed to send playlist {Name} to {MountPath}", playlistItem.Name, device.MountPath);
        }
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
    internal async Task RemoveFromLibraryAsync(MediaItem item)
    {
        if (item.Kind != MediaKind.Music)
        {
            return;
        }

        var title = !string.IsNullOrWhiteSpace(item.Title) ? item.Title : item.FileName ?? "this track";
        var dialog = new Views.ConfirmDialog(
            "Remove from Library",
            $"Remove \"{title}\" from your library?\n\nThe file will not be deleted. You can restore it later from the Ignored view.",
            "Remove");

        var ok = await dialog.ShowDialog<bool>(_window);
        if (!ok)
        {
            return;
        }

        MediaCache.IgnoreMedia(item.Id);
        item.IsIgnored = true;

        // If the item was in any playlists, those playlist configs now have stale trackIds.
        // Rebuild them so playlist views stay accurate.
        RefreshAllPlaylistConfigs();

        ApplyFilter();
    }

    /// <summary>
    /// Clears the ignored flag on the item. It re-appears in its natural view (Music, Favorites, etc.).
    /// Playlist memberships are NOT restored — they were deleted at ignore time.
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

        UpdateMainStatus("Loading library...");

        var loadSw = System.Diagnostics.Stopwatch.StartNew();
        _allItems = await Task.Run(() => MediaCache.LoadAll());
        loadSw.Stop();
        _log.Information("MediaCache.LoadAll: {Count} items in {ElapsedMs}ms", _allItems.Count, loadSw.ElapsedMilliseconds);

        // Initialize radio filter options from loaded data
        var radioItems = _allItems.Where(i => i.Kind == MediaKind.Radio).ToList();

        if (radioItems.Count > 0)
        {
            RebuildRadioFilterOptions();

            var rbSync = MediaCache.GetLastSync("radiobrowser");
            var scSync = MediaCache.GetLastSync("shoutcast");
            var staleDays = 7;

            if (rbSync == null || scSync == null ||
                (DateTime.UtcNow - rbSync.Value.LastSync).TotalDays > staleDays ||
                (DateTime.UtcNow - scSync.Value.LastSync).TotalDays > staleDays)
            {
                StatusBar.SyncStatus = "Stations may be stale";
            }
            else
            {
                var ago = DateTime.UtcNow - new[] { rbSync.Value.LastSync, scSync.Value.LastSync }.Min();
                StatusBar.SyncStatus = $"Last synced: {FormatHelper.FormatTimeAgo(ago)}";
            }
        }
        else
        {
            StatusBar.SyncStatus = "Sync for full library (~95k)";
        }

        // Load playlists
        LoadPlaylistSidebarItems();

        // Apply initial filter for the current tab
        ApplyFilter();

        // Scan and analyze music files
        await ScanAndAnalyzeMusicAsync();

        // Start watching for file changes
        StartFolderWatcher();

        // Start event-driven portable device detection (iPod, Rockbox, Audio CD).
        // CD drive arrival/removal also routes through the same WMI watcher —
        // no separate polling timer required.
        _deviceDetection = new DeviceDetectionService();
        _deviceDetection.DeviceConnected += device => UI(() => _ = HandleDeviceConnectedAsync(device));
        _deviceDetection.DeviceDisconnected += mountPath => UI(() => HandleDeviceDisconnected(mountPath));
        _deviceDetection.CdDriveEvent += () => UI(() => _ = ScanForCdAsync());
        _deviceDetection.Start();
    }

    internal async Task ScanAndAnalyzeMusicAsync()
    {
        if (string.IsNullOrEmpty(App.FolderPath))
        {
            return;
        }

        UpdateMainStatus("Scanning files...");

        var diskFiles = await FileScanner.ScanDirectoryAsync(App.FolderPath, recursive: true);

        var musicLookup = _allItems
            .Where(i => i.Kind == MediaKind.Music && i.FilePath != null)
            .ToDictionary(i => i.FilePath!, StringComparer.OrdinalIgnoreCase);

        var filesToAnalyze = new List<MediaItem>();
        var diskPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var diskFile in diskFiles)
        {
            diskPaths.Add(diskFile.Id);

            if (musicLookup.TryGetValue(diskFile.Id, out var existing))
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
            .Where(i => i.Kind == MediaKind.Music && i.FilePath != null && !diskPaths.Contains(i.FilePath))
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
            await Task.Run(() => MediaCache.RemoveMusic(deletedItems.Select(i => i.Id)));
        }

        UpdateData();
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
                    await ScanAndAnalyzeMusicAsync();
                });
            };
        }

        _folderWatcher.Start(App.FolderPath);
    }

    private async Task ProcessFileChangesAsync(WatcherChangeSet changes)
    {
        var filesToAnalyze = new List<MediaItem>();

        // Handle deleted files
        if (changes.Deleted.Count > 0)
        {
            var deletedPaths = new HashSet<string>(changes.Deleted, StringComparer.OrdinalIgnoreCase);
            var deletedItems = _allItems
                .Where(i => i.Kind == MediaKind.Music && i.FilePath != null && deletedPaths.Contains(i.FilePath))
                .ToList();

            foreach (var item in deletedItems)
            {
                _allItems.Remove(item);
            }

            if (deletedItems.Count > 0)
            {
                await Task.Run(() => MediaCache.RemoveMusic(deletedItems.Select(i => i.Id)));
            }
        }

        // Handle created files
        foreach (var path in changes.Created)
        {
            if (await WaitForFileReady(path))
            {
                var item = FileScanner.CreateMediaItemFromPath(path);

                if (item != null)
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
                    i => i.Kind == MediaKind.Music && i.FilePath != null &&
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
        int totalArtists = MusicItems.Where(f => !string.IsNullOrEmpty(f.Artist)).Select(f => f.Artist).Distinct().Count();
        int totalAlbums = MusicItems.Where(f => !string.IsNullOrEmpty(f.Album)).Select(f => f.Album).Distinct().Count();

        int totalSongs = MusicItems.Count();

        TimeSpan totalDuration = TimeSpan.FromTicks(MusicItems.Sum(x => x.Duration?.Ticks ?? 0));

        UI(() =>
        {
            StatusBar.TotalArtists = totalArtists.ToString();
            StatusBar.TotalAlbums = totalAlbums.ToString();
            StatusBar.TotalSongs = totalSongs.ToString();
            StatusBar.TotalDuration = totalDuration.ToString(@"dd\:hh\:mm\:ss");
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
            "Favorites" => "favorites",
            "Ignored" => "ignored",
            "BadFormat" => "issues",
            _ when viewKey.StartsWith("Playlist:") => "tracks",
            _ => "items"
        };

        var duration = TimeSpan.FromTicks(FilteredItems.Where(i => i.Duration.HasValue).Sum(i => i.Duration!.Value.Ticks));
        var durationStr = duration.TotalSeconds > 0 ? duration.ToString(@"d\:hh\:mm\:ss") : "";

        UI(() =>
        {
            StatusBar.ItemCount = count.ToString();
            StatusBar.ItemLabel = label;
            StatusBar.ItemDuration = durationStr;
        });
    }

    #endregion

    #region Utils

    private static readonly HttpClient _faviconHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
        DefaultRequestHeaders = { { "User-Agent", $"OrgZ/{App.Version}" } }
    };

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
                    // Don't dispose — Avalonia's ref-counted bitmap lifecycle handles cleanup.
                    // Explicit Dispose() while a render pass is in flight causes ObjectDisposedException.
                    CurrentAlbumArt = bitmap;
#if WINDOWS
                    _smtcService?.UpdateMetadata(CurrentStation?.Title, CurrentStation?.Tags, "Internet Radio", bytes);
#endif
                    // macOS Now Playing only learns about the artwork after the
                    // favicon download finishes — push an updated metadata frame.
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
        // not LibVLC — keeping LibVLC at 100 means the FFT analyzer always
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

                var label = discInfo.Tracks[0].Album ?? $"Audio CD ({driveId})";

                DeviceItems.Add(new SidebarItem
                {
                    Name = label,
                    Icon = "fa-solid fa-compact-disc",
                    Category = "DEVICES",
                    IsEnabled = true,
                    ViewConfigKey = "CdAudio",
                });

                // Store cover art for playback display. Keep the raw bytes too — the
                // macOS Now Playing widget needs them to build an MPMediaItemArtwork.
                if (discInfo.CoverArtBytes != null)
                {
                    _cdCoverArt = BitmapFromBytes(discInfo.CoverArtBytes);
                    _cdCoverArtBytes = discInfo.CoverArtBytes;
                }

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
        // The user may not have selected a specific CD track — pull the drive from any
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

        // FoxRedbook on macOS wants a bare BSD name (disk4) / dev path, not the
        // mount point. Same translation we do for TOC reads in CdAudioService.
        var openPath = OperatingSystem.IsMacOS()
            ? CdAudioService.ResolveMacBsdDevice(drivePath) ?? drivePath
            : drivePath;

        var albumRoot = !string.IsNullOrWhiteSpace(App.FolderPath) ? App.FolderPath : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        var artistDir = CdRipService.SanitizeForFileName(tracks[0].Artist) is { Length: > 0 } a ? a : "Unknown Artist";
        var albumDir = CdRipService.SanitizeForFileName(tracks[0].Album) is { Length: > 0 } al ? al : $"Audio CD ({drivePath})";
        var outputDir = Path.Combine(albumRoot, artistDir, albumDir);

        var activity = AddActivity($"Ripping {tracks.Count} track(s) from {drivePath} — {options.ShortLabel}");

        EnsureCdDriveFree(drivePath);

        // Per-track timing for the speed readout. We need a reset each time the
        // track number advances, otherwise the "8.5×" figure averages across the
        // whole disc and stops being informative once a few tracks are done.
        int speedTrackNum = -1;
        var speedClock = System.Diagnostics.Stopwatch.StartNew();
        long speedStartSectors = 0;

        IsRipping = true;
        RipTitle = $"Importing {tracks.Count} track(s)";
        RipDetail = string.Empty;
        RipPercent = 0;

        var progress = new Progress<RipTrackProgress>(p =>
        {
            activity.Detail = $"Track {p.TrackNumber} of {p.TrackCount}: {p.TrackTitle} ({p.TrackPercent:P0})";
            activity.Progress = p.TrackPercent;

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
                // "m:ss" by manual format — TimeSpan format strings don't accept
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

            RipTitle = $"Importing “{p.TrackTitle}”";
            RipDetail = $"Track {p.TrackNumber} of {p.TrackCount} — Time remaining: {etaStr} ({speedStr})";
            RipPercent = p.TrackPercent;
        });

        // Per-track verification feed: each finished track appends a one-line
        // verdict to the activity's Detail so the user can see, in order,
        // which tracks ripped cleanly and which had skipped sectors. The same
        // information is also flashed through the LCD's RipDetail line while
        // the next track gets going.
        var verificationLines = new List<string>(tracks.Count);
        var trackCompleted = new Progress<RipOutcome>(o =>
        {
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
            verificationLines.Add(line);
            RipDetail = line;
            activity.Detail = string.Join("  •  ", verificationLines.TakeLast(3));
        });

        _ripCts = new CancellationTokenSource();
        try
        {
            // CdRipService.RipTracksWithElevationAsync awaits async methods but
            // its inner OpticalDrive.Open + per-sector SCSI reads run synchronously
            // until they actually yield — when called from the UI thread that
            // means a frozen window for the duration of the rip. Task.Run pushes
            // the entire pipeline to the thread pool so the UI keeps animating;
            // Progress<T> already routes its callbacks back to the UI dispatcher
            // via the SynchronizationContext captured at construction.
            var ct = _ripCts.Token;
            var outcomes = await Task.Run(() =>
                CdRipService.RipTracksWithElevationAsync(openPath, tracks, outputDir, options, progress, trackCompleted, _cdCoverArtBytes, ct), ct);

            var unverified = outcomes.Where(o => !o.Verified).ToList();
            if (unverified.Count == 0)
            {
                activity.Detail = $"✓ Ripped {outcomes.Count} track(s) — all verified — to {outputDir}";
                activity.Status = ActivityStatus.Completed;
            }
            else
            {
                var badList = string.Join(", ", unverified.Select(o => o.TrackNumber.ToString("D2")));
                activity.Detail = $"⚠ Ripped {outcomes.Count} track(s), {unverified.Count} unverified: {badList}. Re-rip with higher paranoia to recover.";
                activity.Status = ActivityStatus.Completed;
            }
            activity.Progress = 1.0;
            UpdateActivityBadge();
        }
        catch (OperationCanceledException)
        {
            _log.Information("Rip cancelled by user for {DrivePath}", drivePath);
            activity.Detail = "Rip cancelled";
            activity.Status = ActivityStatus.Failed;
            UpdateActivityBadge();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Rip failed for {DrivePath}", drivePath);
            activity.Error = ex.Message;
            activity.Status = ActivityStatus.Failed;
            UpdateActivityBadge();
        }
        finally
        {
            _ripCts?.Dispose();
            _ripCts = null;
            IsRipping = false;
            RipTitle = string.Empty;
            RipDetail = string.Empty;
            RipPercent = 0;
        }
    }

    internal async Task BurnTracksToCdAsync(IReadOnlyList<MediaItem> tracks)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        var drive = CdAudioService.GetAllCdDrives().FirstOrDefault();
        if (drive == null)
        {
            _log.Warning("Burn requested with no CD drive present");
            return;
        }

        var drivePath = drive.Name.TrimEnd('\\', '/');
        var burnTracks = new List<CdBurnTrack>(tracks.Count);
        foreach (var t in tracks)
        {
            if (string.IsNullOrEmpty(t.FilePath))
            {
                _log.Warning("Burn: track {Id} has no FilePath; skipping", t.Id);
                continue;
            }

            burnTracks.Add(new CdBurnTrack
            {
                WavFilePath = t.FilePath,
                Title = t.Title,
                Performer = t.Artist,
            });
        }

        if (burnTracks.Count == 0)
        {
            return;
        }

        EnsureCdDriveFree(drivePath);

        var activity = AddActivity($"Burning {burnTracks.Count} track(s) to {drivePath}");
        var progress = new Progress<CdBurnProgress>(p =>
        {
            activity.Detail = $"Track {p.TrackNumber} of {p.TrackCount} ({p.DiscPercent:P0})";
            activity.Progress = p.DiscPercent;
        });

        try
        {
            await CdBurnService.BurnWithElevationAsync(drivePath, burnTracks, progress);
            activity.Detail = $"Burned {burnTracks.Count} track(s) to {drivePath}";
            activity.Progress = 1.0;
            activity.Status = ActivityStatus.Completed;
            UpdateActivityBadge();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Burn failed for {DrivePath}", drivePath);
            activity.Error = ex.Message;
            activity.Status = ActivityStatus.Failed;
            UpdateActivityBadge();
        }
    }

    #endregion

    #region Portable Devices (iPod / Rockbox)

    private async Task HandleDeviceConnectedAsync(ConnectedDevice device)
    {
        if (_connectedDevices.ContainsKey(device.MountPath))
        {
            _log.Debug("HandleDeviceConnectedAsync ignored — {MountPath} already connected", device.MountPath);
            return;
        }

        _connectedDevices[device.MountPath] = device;

        var viewKey = $"Device:{device.MountPath}";
        var playlistsKey = $"Device:{device.MountPath}:Playlists";
        ListViewConfigs.Register(viewKey, ListViewConfigs.BuildDeviceConfig(device.MountPath, device.DeviceType));
        ListViewConfigs.Register(playlistsKey, ListViewConfigs.BuildDevicePlaylistsConfig(device.MountPath));

        // The device row itself IS the music view (its ViewConfigKey = "Device:{mount}").
        // Children under it are secondary views — currently just Playlists, expandable to
        // future sub-views (Browse, Settings, Sync).
        var playlistsChild = new SidebarItem
        {
            Name = "Playlists",
            Icon = "fa-solid fa-list",
            Category = "DEVICE",
            IsEnabled = true,
            ViewConfigKey = playlistsKey,
        };

        var sidebarItem = new SidebarItem
        {
            Name = device.SidebarLabel,
            Icon = device.Icon,
            IconBitmap = device.GenerationImage,
            Category = "DEVICES",
            IsEnabled = true,
            ViewConfigKey = viewKey,
            Children = { playlistsChild },
        };
        DeviceItems.Add(sidebarItem);

        var activity = AddActivity($"Scanning {device.Name}");
        _log.Information("Device scan starting: MountPath={MountPath} Type={DeviceType} Name={Name}", device.MountPath, device.DeviceType, device.Name);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            List<MediaItem> scanned;

            var beforeCount = _allItems.Count;

            // Stream scanned items into _allItems in small batches so the grid fills in
            // as the scan runs, instead of staying empty until the walk completes. Every
            // batch is marshalled to the UI thread and triggers a filter re-apply when
            // the device's sidebar entry is currently selected.
            void FlushBatch(IReadOnlyList<MediaItem> batch)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _allItems.AddRange(batch);
                    if (SelectedSidebarItem == sidebarItem)
                    {
                        ApplyFilter();
                    }
                });
            }

            void PublishPlaylists(IReadOnlyList<DevicePlaylist> playlists)
            {
                PublishDevicePlaylists(device, playlists);
            }

            if (device.DeviceType == DeviceType.StockIPod)
            {
                scanned = await Task.Run(() => ScanStockIPod(device, activity, FlushBatch, PublishPlaylists));
            }
            else
            {
                scanned = await Task.Run(() => ScanRockboxDevice(device, activity, FlushBatch, PublishPlaylists));
            }

            sw.Stop();
            var afterCount = _allItems.Count;
            device.AudioSpace = scanned.Sum(i => i.FileSize ?? 0);
            device.RefreshSpace();

            activity.Status = ActivityStatus.Completed;
            activity.Detail = $"Found {scanned.Count} tracks";
            activity.Progress = 1.0;
            UpdateActivityBadge();

            _log.Information("Device scan complete: MountPath={MountPath} Tracks={Tracks} ScanMs={ScanMs} _allItems {Before}->{After}", device.MountPath, scanned.Count, sw.ElapsedMilliseconds, beforeCount, afterCount);

            if (SelectedSidebarItem == sidebarItem)
            {
                _log.Debug("Selected sidebar is the just-scanned device; re-applying filter");
                ApplyFilter();
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            activity.Status = ActivityStatus.Failed;
            activity.Error = ex.Message;
            activity.Detail = "Scan failed";
            UpdateActivityBadge();
            _log.Error(ex, "Device scan failed: MountPath={MountPath} ElapsedMs={ElapsedMs}", device.MountPath, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Re-runs device fingerprinting for the selected device without requiring a
    /// reconnect. Useful after the user has edited /.orgz/device or wants to pick up
    /// new metadata from a freshly-booted firmware mode. Activity panel reports progress.
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

        var activity = AddActivity($"Refreshing {oldDevice.Name}");

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

                activity.Status = ActivityStatus.Completed;
                activity.Detail = $"{oldDevice.Model}";
            }
            else
            {
                activity.Status = ActivityStatus.Failed;
                activity.Error = "device no longer recognized";
            }
        }
        catch (Exception ex)
        {
            activity.Status = ActivityStatus.Failed;
            activity.Error = ex.Message;
            activity.Detail = "Refresh failed";
        }
        UpdateActivityBadge();
    }

    internal void EjectDevice(SidebarItem item)
    {
        if (item.ViewConfigKey?.StartsWith("Device:") != true)
        {
            return;
        }

        var mountPath = item.ViewConfigKey["Device:".Length..];
        var activity = AddActivity($"Ejecting {item.Name}");

        if (DeviceEjector.Eject(mountPath, out var error))
        {
            activity.Status = ActivityStatus.Completed;
            activity.Detail = "Safely removed";
            // The WMI removal event will fire shortly and HandleDeviceDisconnected will
            // tear down the sidebar entry, view config, and items.
        }
        else
        {
            activity.Status = ActivityStatus.Failed;
            activity.Error = error ?? "unknown error";
            activity.Detail = $"Eject failed: {error}";
        }
        UpdateActivityBadge();
    }

    private void HandleDeviceDisconnected(string mountPath)
    {
        if (!_connectedDevices.Remove(mountPath))
        {
            return;
        }

        var source = $"device:{mountPath}";
        var viewKey = $"Device:{mountPath}";
        var playlistsKey = $"Device:{mountPath}:Playlists";

        // Stop playback if we're playing from this device
        if (CurrentPlayingItem?.Source == source)
        {
            ClearPlayback();
        }

        _allItems.RemoveAll(i => i.Source == source);

        // The device entry is a tree parent — removing it drops the Music + Playlists
        // children along with it, since they're just Children of the parent SidebarItem.
        var sidebarItem = DeviceItems.FirstOrDefault(d => d.ViewConfigKey == viewKey);
        if (sidebarItem != null)
        {
            DeviceItems.Remove(sidebarItem);
        }

        ListViewConfigs.Remove(viewKey);
        ListViewConfigs.Remove(playlistsKey);

        // If the user was viewing any part of this device tree, fall back to the library
        var selectedKey = SelectedSidebarItem?.ViewConfigKey;
        if (selectedKey == viewKey || selectedKey == playlistsKey)
        {
            SelectedSidebarItem = LibraryItems.FirstOrDefault() ?? null;
        }
    }

    /// <summary>
    /// Reads an iTunesDB from a stock iPod and converts tracks to MediaItems. The iTunesDB
    /// is authoritative — FilePaths point to scrambled names like F23/ABCD.mp3 but all
    /// metadata (title/artist/album/play counts/ratings) comes from the DB. When the DB is
    /// missing or unreadable, falls back to a raw filesystem walk so the grid still fills
    /// with whatever audio we can find on the device.
    ///
    /// Stock iPods are treated as READ-ONLY — we never write to the device, not even a
    /// cache file under <c>/.orgz/</c>. The iTunesDB parse runs every connect; it's fast
    /// enough (~1s for a 14k-track library) that shaving it isn't worth the policy break.
    /// </summary>
    private static List<MediaItem> ScanStockIPod(ConnectedDevice device, ActivityItem activity, Action<IReadOnlyList<MediaItem>>? flushBatch = null, Action<IReadOnlyList<DevicePlaylist>>? publishPlaylists = null)
    {
        var dbPath = Path.Combine(device.MountPath, "iPod_Control", "iTunes", "iTunesDB");
        if (!File.Exists(dbPath))
        {
            activity.Detail = "iTunesDB missing — walking filesystem";
            _log.Information("iTunesDB not found at {DbPath} — falling back to filesystem walk", dbPath);
            return ScanRockboxDevice(device, activity, flushBatch);
        }

        var source = $"device:{device.MountPath}";
        activity.Detail = "Parsing iTunesDB...";
        ITunesDbReader.ReadAll(dbPath, device.MountPath, out var tracks, out var itunesPlaylists);

        // Convert iTunesDB playlists into DevicePlaylist — their TrackIds are MediaItem Ids
        // ("device:{mount}:{trackId}") so the per-playlist view config can filter by
        // set membership directly.
        var devicePlaylists = new List<DevicePlaylist>();
        foreach (var pl in itunesPlaylists)
        {
            if (string.IsNullOrWhiteSpace(pl.Name))
            {
                continue;
            }
            devicePlaylists.Add(new DevicePlaylist
            {
                Name = pl.Name!,
                Key = $"MHYP:{pl.PlaylistId}",
                TrackIds = pl.TrackIds.Select(tid => $"device:{device.MountPath}:{tid}").ToList(),
            });
        }
        publishPlaylists?.Invoke(devicePlaylists);

        var items = new List<MediaItem>(tracks.Count);
        var pending = new List<MediaItem>(capacity: 64);
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            var ext = !string.IsNullOrEmpty(t.FilePath) ? Path.GetExtension(t.FilePath) : null;

            var mediaItem = new MediaItem
            {
                Id = $"device:{device.MountPath}:{t.TrackId}",
                Kind = MediaKind.Music,
                Title = t.Title,
                Artist = t.Artist,
                Album = t.Album,
                Genre = t.Genre,
                Composer = t.Composer,
                Year = t.Year > 0 ? (uint)t.Year : null,
                Track = t.TrackNumber > 0 ? (uint)t.TrackNumber : null,
                TotalTracks = t.TotalTracks > 0 ? (uint)t.TotalTracks : null,
                Disc = t.DiscNumber > 0 ? (uint)t.DiscNumber : null,
                TotalDiscs = t.TotalDiscs > 0 ? (uint)t.TotalDiscs : null,
                Duration = t.DurationMs > 0 ? TimeSpan.FromMilliseconds(t.DurationMs) : null,
                FilePath = t.FilePath,
                FileName = !string.IsNullOrEmpty(t.FilePath) ? Path.GetFileName(t.FilePath) : null,
                Extension = ext,
                FileSize = t.FileSize,
                AudioBitrate = t.Bitrate > 0 ? t.Bitrate : null,
                SampleRate = t.SampleRate > 0 ? t.SampleRate : null,
                PlayCount = t.PlayCount,
                Rating = t.Rating > 0 ? t.Rating / 20 : null,
                LastPlayed = t.LastPlayed,
                DateAdded = t.DateAdded ?? DateTime.UtcNow,
                IsAnalyzed = true,
                Source = source,
                StreamUrl = t.FilePath,
            };
            items.Add(mediaItem);
            pending.Add(mediaItem);

            if ((i & 0xFF) == 0)
            {
                activity.Detail = $"Read {i + 1} of {tracks.Count} tracks";
                activity.Progress = tracks.Count > 0 ? (double)(i + 1) / tracks.Count : 1.0;

                if (pending.Count > 0 && flushBatch != null)
                {
                    flushBatch(pending.ToArray());
                    pending.Clear();
                }
            }
        }

        if (pending.Count > 0 && flushBatch != null)
        {
            flushBatch(pending.ToArray());
        }

        return items;
    }

    /// <summary>
    /// Walks a Rockbox device filesystem, analyzing every supported audio file with TagLib.
    /// Slower than iTunesDB parsing but necessary because Rockbox has no central database.
    /// Updates <see cref="ConnectedDevice.AudioSpace"/> progressively so the capacity bar
    /// in the DeviceInfoBar fills in as the scan runs. When <paramref name="flushBatch"/>
    /// is non-null, items are pushed in batches of ~32 so the grid populates incrementally
    /// instead of remaining empty until the full walk completes.
    ///
    /// Uses <see cref="DeviceLibraryCache"/> to persist results to <c>{mount}/.orgz/library.db</c>
    /// and only re-analyze files whose size+mtime differ from the cached entry. First scan
    /// is full; every subsequent connect deltas in ~milliseconds unless the music changed.
    /// Stock iPods do NOT use this — their iTunesDB is authoritative and fast to parse, and
    /// we treat stock iPods as read-only (don't write anything to the device).
    /// </summary>
    private static List<MediaItem> ScanRockboxDevice(ConnectedDevice device, ActivityItem activity, Action<IReadOnlyList<MediaItem>>? flushBatch = null, Action<IReadOnlyList<DevicePlaylist>>? publishPlaylists = null)
    {
        var source = $"device:{device.MountPath}";

        // Load cache first — even if the filesystem walk is slow, we can flush cached
        // items to the grid immediately and only pay for analysis on actually-new files.
        activity.Detail = "Loading device cache...";
        var cached = DeviceLibraryCache.TryLoad(device.MountPath, source);
        var cacheByPath = cached
            .Where(i => !string.IsNullOrEmpty(i.FilePath))
            .ToDictionary(i => i.FilePath!, StringComparer.OrdinalIgnoreCase);

        activity.Detail = "Walking filesystem...";
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
            // Skip directories we can't enter — best effort
        }

        var items = new List<MediaItem>(files.Count);
        var pending = new List<MediaItem>(capacity: 32);
        var newlyAnalyzed = new List<MediaItem>(capacity: 32);
        long audioBytes = 0;
        int reused = 0;
        int analyzed = 0;

        for (int i = 0; i < files.Count; i++)
        {
            var path = files[i];
            MediaItem? item;
            try
            {
                var info = new FileInfo(path);

                // Delta: if the cached entry matches size + mtime, reuse it verbatim and
                // skip TagLib analysis entirely. On a steady library the vast majority of
                // files hit this path, and the whole "scan" becomes a directory enumeration.
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
            audioBytes += item.FileSize ?? 0;

            if ((i & 0x1F) == 0)
            {
                activity.Detail = $"Analyzing {i + 1} of {files.Count}";
                activity.Progress = files.Count > 0 ? (double)(i + 1) / files.Count : 1.0;

                // Push live audio-bytes back to the device model so the capacity bar
                // fills in as we go. Marshalled to UI thread because the bar bindings
                // listen to property-changed events.
                var snapshot = audioBytes;
                Dispatcher.UIThread.Post(() => device.AudioSpace = snapshot);

                if (pending.Count > 0 && flushBatch != null)
                {
                    flushBatch(pending.ToArray());
                    pending.Clear();
                }

                // Persist newly-analyzed items immediately so an interrupted scan resumes
                // from here on the next connect instead of replaying all the TagLib work.
                if (newlyAnalyzed.Count > 0)
                {
                    DeviceLibraryCache.Upsert(device.MountPath, newlyAnalyzed);
                    newlyAnalyzed.Clear();
                }
            }
        }

        // Final sync of the accumulated total + any tail items
        var finalTotal = audioBytes;
        Dispatcher.UIThread.Post(() => device.AudioSpace = finalTotal);

        if (pending.Count > 0 && flushBatch != null)
        {
            flushBatch(pending.ToArray());
        }

        if (newlyAnalyzed.Count > 0)
        {
            DeviceLibraryCache.Upsert(device.MountPath, newlyAnalyzed);
        }

        _log.Information("Rockbox scan: total={Total} cached={Reused} analyzed={Analyzed}", files.Count, reused, analyzed);

        // Scan completed to the end — prune cache rows for files that have been removed
        // from the device since the last complete scan. Skipped on interrupt (this line
        // is never reached) so a partial run doesn't erase otherwise-valid rows.
        DeviceLibraryCache.PruneMissing(device.MountPath, items.Select(i => i.FilePath!).Where(p => !string.IsNullOrEmpty(p)));

        // Read Rockbox-format M3U playlists from /Playlists/ — their TrackIds are
        // absolute file paths, which match MediaItem.Id for Rockbox tracks (also the
        // full path). Missing /Playlists/ folder is the common case; returns empty.
        var playlists = M3UPlaylistReader.Read(device.MountPath);
        publishPlaylists?.Invoke(playlists);

        return items;
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

            // Swap the device's playlist collection atomically — callers can bind to it
            // if we ever want a device-level playlist header.
            device.Playlists.Clear();
            foreach (var pl in playlists)
            {
                device.Playlists.Add(pl);
            }

            // Find the sidebar's "Playlists" child under the device parent. If the user
            // disconnected the device between scan completion and this dispatch (unlikely
            // but possible), the device item won't be there — just bail.
            var deviceViewKey = $"Device:{mountPath}";
            var playlistsViewKey = $"Device:{mountPath}:Playlists";
            var deviceParent = DeviceItems.FirstOrDefault(d => d.ViewConfigKey == deviceViewKey);
            var playlistsNode = deviceParent?.Children.FirstOrDefault(c => c.ViewConfigKey == playlistsViewKey);
            if (playlistsNode == null)
            {
                return;
            }

            // Remove any previously-registered per-playlist view configs for this device
            // so a rescan replaces them cleanly instead of accumulating duplicates.
            foreach (var stale in playlistsNode.Children.ToList())
            {
                if (stale.ViewConfigKey != null)
                {
                    ListViewConfigs.Remove(stale.ViewConfigKey);
                }
            }
            playlistsNode.Children.Clear();

            foreach (var pl in playlists)
            {
                var viewKey = $"Device:{mountPath}:Playlist:{pl.Key}";
                ListViewConfigs.Register(viewKey, ListViewConfigs.BuildDevicePlaylistConfig(viewKey, pl.TrackIds));

                playlistsNode.Children.Add(new SidebarItem
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
