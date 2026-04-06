// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using LibVLCSharp.Shared;

namespace OrgZ.ViewModels;

internal partial class MainWindowViewModel : ObservableObject, IDisposable
{
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

    internal ObservableCollection<SidebarItem> LibraryItems { get; } =
    [
        new() { Name = "Music",      Icon = "fa-solid fa-music",           Category = "LIBRARY", IsEnabled = true,  Kind = MediaKind.Music, ViewConfigKey = "Music" },
        new() { Name = "Radio",      Icon = "fa-solid fa-tower-broadcast", Category = "LIBRARY", IsEnabled = true,  Kind = MediaKind.Radio, ViewConfigKey = "Radio" },
        new() { Name = "Podcasts",   Icon = "fa-solid fa-podcast",         Category = "LIBRARY", IsEnabled = false },
        new() { Name = "Audiobooks", Icon = "fa-solid fa-headphones",      Category = "LIBRARY", IsEnabled = false },
    ];

    internal ObservableCollection<SidebarItem> PlaylistItems { get; } =
    [
        new() { Name = "Favorites", Icon = "fa-solid fa-star", Category = "PLAYLISTS", IsEnabled = true, IsFavorites = true, ViewConfigKey = "Favorites" },
        new() { Name = "New Playlist...", Icon = "fa-solid fa-plus", Category = "PLAYLISTS", IsEnabled = false },
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

    // -- Unified Data --

    [ObservableProperty]
    private MediaItem? _selectedItem;

    [ObservableProperty]
    private string _searchText = Settings.Get("OrgZ.SearchText", string.Empty);

    [ObservableProperty]
    private List<MediaItem> _filteredItems = [];

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

    // -- Change Handlers --

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
    internal void NavigateToPlaying()
    {
        var item = CurrentPlayingItem;
        if (item == null)
        {
            return;
        }

        // Find the sidebar item that matches this media kind
        SidebarItem? target = item.Kind switch
        {
            MediaKind.Music => LibraryItems.FirstOrDefault(i => i.Kind == MediaKind.Music),
            MediaKind.Radio => LibraryItems.FirstOrDefault(i => i.Kind == MediaKind.Radio),
            _ => null
        };

        if (target == null)
        {
            return;
        }

        // Clear search so the item is visible in the unfiltered list
        SearchText = string.Empty;

        // Switch view (this triggers ApplyFilter)
        SelectedSidebarItem = target;

        // Select the playing item and scroll to it
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
        StatusBar.ActiveKind = value?.Kind;

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
            FilteredItems = [];
            UpdateNavigationButtons();
            return;
        }

        IEnumerable<MediaItem> items = _allItems.Where(_activeViewConfig.BaseFilter);

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
                    s.Tags != null && s.Tags.Contains(SelectedGenre, StringComparison.OrdinalIgnoreCase));
            }
        }

        // Search text filter
        var searchText = SearchText?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(searchText))
        {
            var search = searchText;
            items = items.Where(item => _activeViewConfig.SearchFilter(item, search));
        }

        FilteredItems = items.ToList();

        // Update radio station count in status bar
        if (_activeViewConfig.ShowRadioFilterPanel)
        {
            UI(() => StatusBar.StationCount = FilteredItems.Count.ToString());
        }

        UpdateNavigationButtons();
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
            Services.MediaCache.RemoveRadioBySource("shoutcast");

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
        if (CurrentPlayingItem != null) { CurrentPlayingItem.IsPlaying = false; }

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
        if (_player == null || file == null || file.Kind != MediaKind.Music || string.IsNullOrEmpty(file.FilePath))
        {
            return;
        }

        UI(() =>
        {
            if (CurrentPlayingItem != null) { CurrentPlayingItem.IsPlaying = false; }
            _playbackContext = new PlaybackContext(FilteredItems, file);
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
            if (CurrentPlayingItem != null) { CurrentPlayingItem.IsPlaying = false; }
            _playbackContext = new PlaybackContext(FilteredItems, station);
            ExecutePlayRadio(station);
        });
    }

    private void ExecutePlayMusic(MediaItem file)
    {
        file.IsPlaying = true;
        SelectedItem = file;

        CurrentAlbumArt?.Dispose();
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
        station.IsPlaying = true;
        SelectedItem = station;

        CurrentAlbumArt?.Dispose();
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
        if (CurrentPlayingItem != null) { CurrentPlayingItem.IsPlaying = false; }
        _playbackContext = null;

        _currentMedia?.Dispose();
        _currentMedia = null;

        CurrentAlbumArt?.Dispose();
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
            var errors = new List<string>();

            try
            {
                var rbStations = await RadioBrowserService.GetTopStationsAsync(250);
                if (rbStations.Count > 0)
                {
                    await Task.Run(() => MediaCache.UpsertRadioStations(rbStations));
                    _allItems.AddRange(rbStations);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"RadioBrowser: {ex.Message}");
            }

            try
            {
                var scStations = await ShoutcastService.GetTop500Async();
                if (scStations.Count > 0)
                {
                    await Task.Run(() => MediaCache.UpsertRadioStations(scStations));
                    _allItems.AddRange(scStations);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Shoutcast: {ex.Message}");
            }

            RebuildRadioFilterOptions();
            ApplyFilter();

            foreach (var error in errors)
            {
                Messages.Add(error);
            }

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
            dialog.UpdateSource("Syncing RadioBrowser...");
            dialog.SetIndeterminate(true);
            int rbCount = 0;

            await foreach (var batch in RadioBrowserService.GetAllStationsAsync(ct))
            {
                await Task.Run(() => MediaCache.UpsertRadioStations(batch), ct);
                rbCount += batch.Count;
                dialog.UpdateProgress(rbCount, $"RadioBrowser: {rbCount:N0}");
            }

            totalSynced += rbCount;
            MediaCache.RecordSync("radiobrowser", rbCount, dialog.ElapsedMs);

            dialog.UpdateSource($"RadioBrowser done ({rbCount:N0}). Syncing Shoutcast...");
            int scCount = 0;

            await foreach (var batch in ShoutcastService.GetAllStationsAsync(ct))
            {
                await Task.Run(() => MediaCache.UpsertRadioStations(batch), ct);
                scCount += batch.Count;
                totalSynced = rbCount + scCount;
                dialog.UpdateProgress(totalSynced, $"Shoutcast: {scCount:N0}");
            }

            MediaCache.RecordSync("shoutcast", scCount, dialog.ElapsedMs);

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
        foreach (var genre in radioItems
            .Where(s => !string.IsNullOrWhiteSpace(s.Tags))
            .SelectMany(s => s.Tags!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order())
        {
            Genres.Add(genre);
        }

        SelectedCountry = Countries.Contains(prevCountry) ? prevCountry : "All";
        SelectedGenre = Genres.Contains(prevGenre) ? prevGenre : "All";
    }

    #endregion

    #region Loading and Analyzing Files

    internal async Task LoadAsync()
    {
        UpdateTitle();

        MediaCache.EnsureCreated();

        UpdateMainStatus("Loading library...");

        _allItems = await Task.Run(() => MediaCache.LoadAll());

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

        // Apply initial filter for the current tab
        ApplyFilter();

        // Scan and analyze music files
        await ScanAndAnalyzeMusicAsync();

        // Start watching for file changes
        StartFolderWatcher();
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

    internal List<MediaItem> GetFlacFilesWithoutAlbumArt()
    {
        return [.. MusicItems.Where(f => AudioFileAnalyzer.Filters.IsFlacFile(f) && AudioFileAnalyzer.Filters.HasMissingAlbumArt(f))];
    }

    internal List<MediaItem> GetFilesWithExtensionMismatch()
    {
        return [.. MusicItems.Where(AudioFileAnalyzer.Filters.HasExtensionMismatch)];
    }

    internal List<MediaItem> GetMp3Files()
    {
        return [.. MusicItems.Where(AudioFileAnalyzer.Filters.IsMp3File)];
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
                    CurrentAlbumArt?.Dispose();
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
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch { }

        return null;
    }


    #endregion

    public void Dispose()
    {
        _folderWatcher?.Dispose();
#if WINDOWS
        _thumbBarService?.Dispose();
        _smtcService?.Dispose();
#endif
        _currentMedia?.Dispose();
        _player?.Dispose();
        _vlc?.Dispose();
    }
}
