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

    private readonly LibVLC _vlc;

    private readonly MediaPlayer _player;

#if WINDOWS
    private SmtcService? _smtcService;
    private TaskbarThumbBarService? _thumbBarService;
#endif

    private MusicFolderWatcher? _folderWatcher;

    private Media? _currentMedia;

    private PlaybackContext? _playbackContext;

    private readonly List<MediaItem> _cdTracks = [];
    private bool _cdScanning;
    private Bitmap? _cdCoverArt;

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
    private long _currentTrackTimeNumber = 0;

    [ObservableProperty]
    private string _currentTrackTime = "00:00";

    [ObservableProperty]
    private string _currentTrackLine1 = string.Empty;

    [ObservableProperty]
    private string _currentTrackLine2 = string.Empty;

    [ObservableProperty]
    private string _currentTrackDuration = "00:00";

    [ObservableProperty]
    private long _currentTrackDurationNumber = 0;

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
    private string _searchText = Settings.Get("OrgZ.SearchText", string.Empty);

    [ObservableProperty]
    private List<MediaItem> _filteredItems = [];

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

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        Settings.Set("OrgZ.SearchText", value);
        Settings.Save();
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

        SearchText = string.Empty;
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

    partial void OnSelectedSidebarItemChanged(SidebarItem? value)
    {
        _log.Debug("Sidebar selection changed: ViewKey={ViewKey} Name={Name} _allItems.Count={ItemCount}", value?.ViewConfigKey ?? "<null>", value?.Name ?? "<null>", _allItems.Count);

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

    public MainWindowViewModel(MainWindow window)
    {
        _window = window;

        _vlc = new();
        _vlc.SetAppId("com.foxcouncil.orgz", App.Version, "Assets/app.ico");
        _vlc.SetUserAgent($"OrgZ {App.Version}", $"orgz{App.Version}/player");

        _player = new(_vlc);
        _player.Volume = (int)CurrentVolume;

        ButtonPlayPausePadding = ICON_PLAY_PADDING;

        // Initialize shuffle/repeat visual state from saved settings
        ShuffleOpacity = ShuffleMode == ShuffleMode.On ? 1.0 : 0.4;
        RepeatIcon = RepeatMode == RepeatMode.One ? "fa-solid fa-arrow-rotate-left" : "fa-solid fa-repeat";
        RepeatOpacity = RepeatMode == RepeatMode.Off ? 0.4 : 1.0;

        RebuildLibraryItems();

        var savedView = Settings.Get("OrgZ.ActiveView", "Music");
        SelectedSidebarItem = PlaylistItems.FirstOrDefault(i => i.ViewConfigKey == savedView) ?? LibraryItems.FirstOrDefault(i => i.ViewConfigKey == savedView) ?? LibraryItems[0];

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

            UpdateMainStatus("Playing");
        });

        _player.TimeChanged += (s, e) => UI(() =>
        {
            CurrentTrackTime = TimeSpan.FromMilliseconds(e.Time).ToString("mm\\:ss");
            if (!isSeeking)
            {
                CurrentTrackTimeNumber = e.Time;
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
                    CurrentTrackDuration = TimeSpan.FromMilliseconds(e.Media.Duration).ToString(@"mm\:ss");
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
                    CurrentTrackDuration = TimeSpan.FromMilliseconds(e.Media.Duration).ToString(@"mm\:ss");
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

            CurrentTrackDuration = TimeSpan.FromMilliseconds(e.Media.Duration).ToString("mm\\:ss");
            CurrentTrackDurationNumber = e.Media.Duration;

            CurrentTrackLine1 = CurrentMusicItem?.Title ?? "Unknown Title";
            var artist = CurrentMusicItem?.Artist ?? "Unknown Artist";
            var album = CurrentMusicItem?.Album;
            CurrentTrackLine2 = string.IsNullOrWhiteSpace(album) ? artist : $"{artist} \u2014 {album}";
        });
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
            Source = new Bitmap(AssetLoader.Open(new Uri("avares://Orgz/Assets/app.ico"))),
            Width = 64,
            Height = 64,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var dialog = new Window
        {
            Title = "About OrgZ",
            Width = 300,
            Height = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
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
        var dialog = new Views.SettingsDialog(_allItems);
        var result = await dialog.ShowDialog<bool?>(_window);

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
        CurrentTrackDuration = track.Duration?.ToString(@"mm\:ss") ?? "--:--";
        CurrentTrackDurationNumber = (long)(track.Duration?.TotalMilliseconds ?? 0);

        CurrentAlbumArt = _cdCoverArt;

#if WINDOWS
        _smtcService?.UpdateMetadata(track.Title, track.Artist, track.Album, null);
#endif

        _currentMedia?.Dispose();
        _currentMedia = new LibVLCSharp.Shared.Media(_vlc, track.StreamUrl!, LibVLCSharp.Shared.FromType.FromLocation);
        if (track.Track.HasValue)
        {
            _currentMedia.AddOption($":cdda-track={track.Track.Value}");
        }
        _ = _player.Play(_currentMedia);

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

    internal void CurrentVolumeChanged()
    {
        _player.Volume = (int)CurrentVolume;
        Settings.Set("OrgZ.Volume", (int)CurrentVolume);
        Settings.Save();
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

        UI(() =>
        {
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

        _currentMedia?.Dispose();
        _currentMedia = new Media(_vlc, file.FilePath!, FromType.FromPath);

        _ = _player.Play(_currentMedia);

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

        if (!string.IsNullOrWhiteSpace(station.FaviconUrl))
        {
            _ = LoadFaviconAsync(station.FaviconUrl);
        }

        _currentMedia?.Dispose();
        _currentMedia = new Media(_vlc, ProcessStreamUrl(station.StreamUrl!), FromType.FromLocation);

        _currentMedia.MetaChanged += (s, e) =>
        {
            if (e.MetadataType != MetadataType.NowPlaying || _currentMedia == null)
            {
                return;
            }

            var nowPlaying = _currentMedia.Meta(MetadataType.NowPlaying);
            if (string.IsNullOrWhiteSpace(nowPlaying))
            {
                return;
            }

            UI(() =>
            {
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

        _ = _player.Play(_currentMedia);

        ApplyPerTrackOptions(station);

        station.LastPlayed = DateTime.UtcNow;
        station.PlayCount++;
        MediaCache.SetLastPlayed(station.Id, station.LastPlayed.Value);
        MediaCache.IncrementPlayCount(station.Id);

        UpdateNavigationButtons();
    }

    private void ClearPlayback()
    {
        _playbackContext?.Release();
        _playbackContext = null;
        OnPropertyChanged(nameof(PlaybackContextUpcoming));

        _currentMedia?.Dispose();
        _currentMedia = null;

        // Don't dispose — Avalonia's ref-counted bitmap lifecycle handles cleanup.
        // Explicit Dispose() while a render pass is in flight causes ObjectDisposedException.
        CurrentAlbumArt = null;
        CurrentTrackLine1 = string.Empty;
        CurrentTrackLine2 = string.Empty;
        CurrentTrackTime = "00:00";
        CurrentTrackDuration = "00:00";
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

        FilteredItems = _allItems
            .Where(i => i.Kind == MediaKind.Music)
            .Where(i => string.Equals(i.Artist, artist, StringComparison.OrdinalIgnoreCase))
            .Where(i => string.Equals(i.Album, album, StringComparison.OrdinalIgnoreCase))
            .ToList();

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
        // Volume adjustment: multiplier on the current global volume
        var multiplier = 1.0 + (item.VolumeAdjustment / 100.0);
        var effectiveVolume = (int)Math.Clamp(CurrentVolume * multiplier, 0, 200);
        _player.Volume = effectiveVolume;

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
            return;
        }

        _cdScanning = true;

        try
        {
            var drives = CdAudioService.GetCdDrivesWithMedia();

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

                // Store cover art for playback display
                if (discInfo.CoverArtBytes != null)
                {
                    _cdCoverArt = BitmapFromBytes(discInfo.CoverArtBytes);
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
        ListViewConfigs.Register(viewKey, ListViewConfigs.BuildDeviceConfig(device.MountPath, device.DeviceType));

        var sidebarItem = new SidebarItem
        {
            Name = device.SidebarLabel,
            Icon = device.Icon,
            IconBitmap = device.GenerationImage,
            Category = "DEVICES",
            IsEnabled = true,
            ViewConfigKey = viewKey,
        };
        DeviceItems.Add(sidebarItem);

        var activity = AddActivity($"Scanning {device.Name}");
        _log.Information("Device scan starting: MountPath={MountPath} Type={DeviceType} Name={Name}", device.MountPath, device.DeviceType, device.Name);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            List<MediaItem> scanned;

            if (device.DeviceType == DeviceType.StockIPod)
            {
                scanned = await Task.Run(() => ScanStockIPod(device, activity));
            }
            else
            {
                scanned = await Task.Run(() => ScanRockboxDevice(device, activity));
            }

            sw.Stop();
            var beforeCount = _allItems.Count;
            _allItems.AddRange(scanned);
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

        // Stop playback if we're playing from this device
        if (CurrentPlayingItem?.Source == source)
        {
            ClearPlayback();
        }

        _allItems.RemoveAll(i => i.Source == source);

        var sidebarItem = DeviceItems.FirstOrDefault(d => d.ViewConfigKey == viewKey);
        if (sidebarItem != null)
        {
            DeviceItems.Remove(sidebarItem);
        }

        ListViewConfigs.Remove(viewKey);

        // If the user was viewing this device, fall back to the default library view
        if (SelectedSidebarItem?.ViewConfigKey == viewKey)
        {
            SelectedSidebarItem = LibraryItems.FirstOrDefault() ?? null;
        }
    }

    /// <summary>
    /// Reads an iTunesDB from a stock iPod and converts tracks to MediaItems. The iTunesDB
    /// is authoritative — FilePaths point to scrambled names like F23/ABCD.mp3 but all
    /// metadata (title/artist/album/play counts/ratings) comes from the DB.
    /// </summary>
    private static List<MediaItem> ScanStockIPod(ConnectedDevice device, ActivityItem activity)
    {
        var dbPath = Path.Combine(device.MountPath, "iPod_Control", "iTunes", "iTunesDB");
        if (!File.Exists(dbPath))
        {
            activity.Detail = "iTunesDB not found";
            return [];
        }

        activity.Detail = "Parsing iTunesDB...";
        var source = $"device:{device.MountPath}";
        var tracks = ITunesDbReader.Read(dbPath, device.MountPath);

        var items = new List<MediaItem>(tracks.Count);
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            var ext = !string.IsNullOrEmpty(t.FilePath) ? Path.GetExtension(t.FilePath) : null;

            items.Add(new MediaItem
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
            });

            if ((i & 0xFF) == 0)
            {
                activity.Detail = $"Read {i + 1} of {tracks.Count} tracks";
                activity.Progress = tracks.Count > 0 ? (double)(i + 1) / tracks.Count : 1.0;
            }
        }

        return items;
    }

    /// <summary>
    /// Walks a Rockbox device filesystem, analyzing every supported audio file with TagLib.
    /// Slower than iTunesDB parsing but necessary because Rockbox has no central database.
    /// Updates <see cref="ConnectedDevice.AudioSpace"/> progressively so the capacity bar
    /// in the DeviceInfoBar fills in as the scan runs.
    /// </summary>
    private static List<MediaItem> ScanRockboxDevice(ConnectedDevice device, ActivityItem activity)
    {
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

        var source = $"device:{device.MountPath}";
        var items = new List<MediaItem>(files.Count);
        long audioBytes = 0;

        for (int i = 0; i < files.Count; i++)
        {
            var path = files[i];
            MediaItem? item;
            try
            {
                var info = new FileInfo(path);
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
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Rockbox scan failed on file {Path}", path);
                continue;
            }

            items.Add(item);
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
            }
        }

        // Final sync of the accumulated total
        var finalTotal = audioBytes;
        Dispatcher.UIThread.Post(() => device.AudioSpace = finalTotal);

        return items;
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
        _currentMedia?.Dispose();
        _player?.Dispose();
        _vlc?.Dispose();
    }
}
