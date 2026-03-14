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

    private MediaItem? _currentMusicItem;

    private MediaItem? _currentStation;

    private bool isSeeking = false;

    private List<MediaItem> _allItems = [];

    private List<MediaItem> _lastMusicFilteredList = [];

    [ObservableProperty]
    private StatusBarViewModel _statusBar = new();

    [ObservableProperty]
    private SidebarItem? _selectedSidebarItem;

    internal ObservableCollection<SidebarItem> LibraryItems { get; } =
    [
        new() { Name = "Music",      Icon = "fa-solid fa-music",           Category = "LIBRARY", IsEnabled = true,  Kind = MediaKind.Music },
        new() { Name = "Radio",      Icon = "fa-solid fa-tower-broadcast", Category = "LIBRARY", IsEnabled = true,  Kind = MediaKind.Radio },
        new() { Name = "Podcasts",   Icon = "fa-solid fa-podcast",         Category = "LIBRARY", IsEnabled = false },
        new() { Name = "Audiobooks", Icon = "fa-solid fa-headphones",      Category = "LIBRARY", IsEnabled = false },
    ];

    internal ObservableCollection<SidebarItem> PlaylistItems { get; } =
    [
        new() { Name = "Favorites", Icon = "fa-solid fa-star", Category = "PLAYLISTS", IsEnabled = true, IsFavorites = true },
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
    private string _currentTrackMetadata = string.Empty;

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
    private string _searchText = string.Empty;

    [ObservableProperty]
    private List<MediaItem> _filteredItems = [];

    // -- Radio Filters --

    internal ObservableCollection<string> Countries { get; } = [];

    internal ObservableCollection<string> Genres { get; } = [];

    [ObservableProperty]
    private string _selectedCountry = "All";

    [ObservableProperty]
    private string _selectedGenre = "All";

    // -- Radio Management --

    internal ObservableCollection<string> Messages { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isSyncing;

    // -- Computed --

    private IEnumerable<MediaItem> MusicItems => _allItems.Where(i => i.Kind == MediaKind.Music);

    internal bool IsMediaLoaded => _player?.Media != null;

    // -- Change Handlers --

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnSelectedCountryChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnSelectedGenreChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnSelectedSidebarItemChanged(SidebarItem? value)
    {
        StatusBar.ActiveKind = value?.Kind;

        ApplyFilter();

        // Restore selection for the active tab
        if (value?.Kind == MediaKind.Music && _currentMusicItem != null)
        {
            SelectedItem = _currentMusicItem;
        }
        else if (value?.Kind == MediaKind.Radio && _currentStation != null)
        {
            SelectedItem = _currentStation;
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
        var isFavorites = SelectedSidebarItem?.IsFavorites == true;
        var kind = SelectedSidebarItem?.Kind;

        IEnumerable<MediaItem> items = isFavorites
            ? _allItems.Where(i => i.IsFavorite)
            : _allItems.Where(i => i.Kind == kind);

        // Radio-specific filters
        if (kind == MediaKind.Radio)
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
            if (isFavorites)
            {
                items = items.Where(item =>
                    (item.Title?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (item.Artist?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (item.Album?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false));
            }
            else
            {
                items = kind switch
                {
                    MediaKind.Music => items.Where(file =>
                        (file.Artist?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (file.Album?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (file.Title?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (file.FileName?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (file.Year?.ToString().Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false)),
                    MediaKind.Radio => items.Where(s =>
                        (s.Title?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (s.Tags?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (s.Country?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false)),
                    _ => items
                };
            }
        }

        FilteredItems = items.ToList();

        // Keep music list snapshot for auto-advance
        if (kind == MediaKind.Music)
        {
            _lastMusicFilteredList = FilteredItems;
        }

        // Update radio station count in status bar
        if (kind == MediaKind.Radio)
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

        SelectedSidebarItem = LibraryItems[0];

        _player.EndReached += (s, e) => UI(() =>
        {
            if (_currentStation != null)
            {
                _currentStation.IsPlaying = false;
                _currentStation = null;

                ButtonPlayPauseIcon = ICON_PLAY;
                ButtonPlayPausePadding = ICON_PLAY_PADDING;

#if WINDOWS
                _smtcService?.SetPlaybackStatus(MediaPlaybackStatus.Stopped);
                _thumbBarService?.SetPlayingState(false);
#endif

                UpdateMainStatus("Stream ended");
                UpdateNavigationButtons();
                return;
            }

            if (_currentMusicItem != null && _lastMusicFilteredList.Count > 0)
            {
                int index = _lastMusicFilteredList.IndexOf(_currentMusicItem);
                if (index >= 0 && index < _lastMusicFilteredList.Count - 1)
                {
                    PlayMusicItem(_lastMusicFilteredList[index + 1]);
                    return;
                }
            }

            if (_currentMusicItem != null)
            {
                _currentMusicItem.IsPlaying = false;
                _currentMusicItem = null;
            }

            CurrentAlbumArt?.Dispose();
            CurrentAlbumArt = null;

            ButtonPlayPauseIcon = ICON_PLAY;
            ButtonPlayPausePadding = ICON_PLAY_PADDING;

#if WINDOWS
            _smtcService?.SetPlaybackStatus(MediaPlaybackStatus.Stopped);
            _thumbBarService?.SetPlayingState(false);
#endif

            UpdateMainStatus("Finished");
            UpdateNavigationButtons();
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
            var playing = _currentMusicItem ?? _currentStation;
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
                CurrentTrackMetadata = string.Empty;

                UpdateMainStatus("Ready");

                return;
            }

            if (_currentStation != null)
            {
                CurrentTrackDuration = "LIVE";
                CurrentTrackDurationNumber = 0;
                IsSeekEnabled = false;

                CurrentTrackMetadata = _currentStation.Title ?? "Unknown Station";
                if (!string.IsNullOrWhiteSpace(_currentStation.Tags))
                {
                    CurrentTrackMetadata += " - " + _currentStation.Tags;
                }

                return;
            }

            IsSeekEnabled = true;

            if (e.Media.ParsedStatus != MediaParsedStatus.Done)
            {
                _ = await e.Media.Parse();
            }

            CurrentTrackDuration = TimeSpan.FromMilliseconds(e.Media.Duration).ToString("mm\\:ss");
            CurrentTrackDurationNumber = e.Media.Duration;

            CurrentTrackMetadata = (_currentMusicItem?.Artist ?? "Unknown Artist") + " - " + (_currentMusicItem?.Title ?? "Unknown Title");
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
        if (_currentStation != null && _player?.IsPlaying == true)
        {
            return MediaKind.Radio;
        }

        if (_currentMusicItem != null && (_player?.IsPlaying == true || _player?.State == LibVLCSharp.Shared.VLCState.Paused))
        {
            return MediaKind.Music;
        }

        return SelectedItem?.Kind;
    }

    [RelayCommand]
    public void ButtonPreviousTrack()
    {
        if (FilteredItems.Count == 0)
        {
            return;
        }

        var isFavorites = SelectedSidebarItem?.IsFavorites == true;

        if (isFavorites)
        {
            var currentItem = (MediaItem?)_currentStation ?? _currentMusicItem;
            if (currentItem == null)
            {
                return;
            }

            int index = FilteredItems.IndexOf(currentItem);
            if (index > 0)
            {
                PlayItem(FilteredItems[index - 1]);
            }
            return;
        }

        var kind = GetEffectiveKind();

        if (kind == MediaKind.Radio)
        {
            if (_currentStation == null)
            {
                return;
            }

            int index = FilteredItems.IndexOf(_currentStation);
            if (index > 0)
            {
                PlayRadioStation(FilteredItems[index - 1]);
            }
            return;
        }

        if (_currentMusicItem == null)
        {
            return;
        }

        int fileIndex = FilteredItems.IndexOf(_currentMusicItem);
        if (fileIndex > 0)
        {
            PlayMusicItem(FilteredItems[fileIndex - 1]);
        }
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
                if (_currentStation != null && _player.IsPlaying)
                {
                    Stop();
                    return;
                }

                if (_currentStation != null)
                {
                    PlayRadioStation(_currentStation);
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

            if (_currentMusicItem == null)
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
        if (FilteredItems.Count == 0)
        {
            return;
        }

        var isFavorites = SelectedSidebarItem?.IsFavorites == true;

        if (isFavorites)
        {
            var currentItem = (MediaItem?)_currentStation ?? _currentMusicItem;
            if (currentItem == null)
            {
                return;
            }

            int index = FilteredItems.IndexOf(currentItem);
            if (index >= 0 && index < FilteredItems.Count - 1)
            {
                var next = FilteredItems[index + 1];
                PlayItem(next);
            }
            return;
        }

        var kind = GetEffectiveKind();

        if (kind == MediaKind.Radio)
        {
            if (_currentStation == null)
            {
                return;
            }

            int index = FilteredItems.IndexOf(_currentStation);
            if (index >= 0 && index < FilteredItems.Count - 1)
            {
                PlayRadioStation(FilteredItems[index + 1]);
            }
            return;
        }

        if (_currentMusicItem == null)
        {
            return;
        }

        int fileIndex = FilteredItems.IndexOf(_currentMusicItem);
        if (fileIndex >= 0 && fileIndex < FilteredItems.Count - 1)
        {
            PlayMusicItem(FilteredItems[fileIndex + 1]);
        }
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

        if (_currentMusicItem != null)
        {
            _currentMusicItem.IsPlaying = false;
            _currentMusicItem = null;
        }

        _currentMedia?.Dispose();
        _currentMedia = null;

        CurrentAlbumArt?.Dispose();
        CurrentAlbumArt = null;
        CurrentTrackMetadata = string.Empty;
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

        App.FolderPath = folders[0].Path.LocalPath;
        Settings.Set("OrgZ.FolderPath", App.FolderPath);
        Settings.Save();

        _allItems.RemoveAll(i => i.Kind == MediaKind.Music);
        FilteredItems = [];
        _lastMusicFilteredList = [];

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
                }
            }
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

            if (_currentMusicItem != null)
            {
                _currentMusicItem.IsPlaying = false;
                _currentMusicItem = null;
            }

            _currentMedia?.Dispose();
            _currentMedia = null;

            CurrentAlbumArt?.Dispose();
            CurrentAlbumArt = null;
            CurrentTrackMetadata = string.Empty;
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

            _allItems.RemoveAll(i => i.Kind == MediaKind.Music);
            FilteredItems = [];
            _lastMusicFilteredList = [];

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
            if (_currentStation != null)
            {
                _currentStation.IsPlaying = false;
                _currentStation = null;
            }

            if (_currentMusicItem != null)
            {
                _currentMusicItem.IsPlaying = false;
            }

            _currentMusicItem = file;
            _currentMusicItem.IsPlaying = true;
            SelectedItem = file;

            CurrentAlbumArt?.Dispose();
            var artBytes = ExtractAlbumArtBytes(_currentMusicItem.FilePath!);
            CurrentAlbumArt = artBytes != null ? BitmapFromBytes(artBytes) : null;

#if WINDOWS
            _smtcService?.UpdateMetadata(_currentMusicItem.Title, _currentMusicItem.Artist, _currentMusicItem.Album, artBytes);
#endif

            _currentMedia?.Dispose();
            _currentMedia = new Media(_vlc, _currentMusicItem.FilePath!, FromType.FromPath);

            _ = _player.Play(_currentMedia);

            ApplyPerTrackOptions(_currentMusicItem);

            _currentMusicItem.LastPlayed = DateTime.UtcNow;
            _currentMusicItem.PlayCount++;
            MediaCache.SetLastPlayed(_currentMusicItem.Id, _currentMusicItem.LastPlayed.Value);
            MediaCache.IncrementPlayCount(_currentMusicItem.Id);

            UpdateNavigationButtons();
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
            if (_currentMusicItem != null)
            {
                _currentMusicItem.IsPlaying = false;
                _currentMusicItem = null;
            }

            if (_currentStation != null)
            {
                _currentStation.IsPlaying = false;
            }

            _currentStation = station;
            _currentStation.IsPlaying = true;
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
            _currentMedia = new Media(_vlc, ProcessStreamUrl(station.StreamUrl), FromType.FromLocation);

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
                    CurrentTrackMetadata = nowPlaying;
                    UpdateMainStatus($"Playing: {nowPlaying}");

                    // Update SMTC with the live track info
                    string? artist = null;
                    string? title = nowPlaying;

                    // ICY metadata is typically "Artist - Title"
                    var dashIdx = nowPlaying.IndexOf(" - ", StringComparison.Ordinal);
                    if (dashIdx > 0)
                    {
                        artist = nowPlaying[..dashIdx].Trim();
                        title = nowPlaying[(dashIdx + 3)..].Trim();
                    }

#if WINDOWS
                    _smtcService?.UpdateMetadata(title, artist, _currentStation?.Title, null);
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
        });
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

        ApplyFilter();
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
        var isFavorites = SelectedSidebarItem?.IsFavorites == true;
        var kind = SelectedSidebarItem?.Kind;
        var currentItem = isFavorites
            ? (MediaItem?)_currentStation ?? _currentMusicItem
            : kind == MediaKind.Radio ? _currentStation : _currentMusicItem;

        if (currentItem == null || FilteredItems.Count == 0)
        {
            IsBackTrackButtonEnabled = false;
            IsNextTrackButtonEnabled = false;
#if WINDOWS
            _smtcService?.SetNavigationEnabled(false, false);
            _thumbBarService?.SetNavigationEnabled(false, false);
#endif
            return;
        }

        int index = FilteredItems.IndexOf(currentItem);
        IsBackTrackButtonEnabled = index > 0;
        IsNextTrackButtonEnabled = index >= 0 && index < FilteredItems.Count - 1;
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
                    _smtcService?.UpdateMetadata(_currentStation?.Title, _currentStation?.Tags, "Internet Radio", bytes);
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
