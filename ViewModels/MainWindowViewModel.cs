// Copyright (c) 2025 Fox Diller

using Avalonia;
using Avalonia.Media.Imaging;
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

    private SmtcService? _smtcService;

    private TaskbarThumbBarService? _thumbBarService;

    private Media? _currentMedia;

    private AudioFileInfo? _currentFile;

    private bool isSeeking = false;

    [ObservableProperty]
    private StatusBarViewModel _statusBar = new();

    [ObservableProperty]
    private string _totalArtists = "-";

    [ObservableProperty]
    private string _totalAlbums = "-";

    [ObservableProperty]
    private string _totalSongs = "-";

    [ObservableProperty]
    private string _totalDuration = "00:00:00:00";


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

    [ObservableProperty]
    private Bitmap? _currentAlbumArt;

    [ObservableProperty]
    private AudioFileInfo? _selectedAudioFile;

    [ObservableProperty]
    private string _searchText = string.Empty;

    internal ObservableCollection<AudioFileInfo> AudioFiles { get; } = [];

    internal ObservableCollection<AudioFileInfo> FilteredAudioFiles { get; } = [];

    internal bool IsMediaLoaded => _player?.Media != null;

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        FilteredAudioFiles.Clear();

        var searchText = SearchText?.Trim() ?? string.Empty;

        IEnumerable<AudioFileInfo> filtered;

        if (string.IsNullOrEmpty(searchText))
        {
            filtered = AudioFiles;
        }
        else
        {
            filtered = AudioFiles.Where(file =>
                (file.Artist?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (file.Album?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (file.Title?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (file.FileName?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (file.Year?.ToString().Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        foreach (var file in filtered)
        {
            FilteredAudioFiles.Add(file);
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

        _player.EndReached += (s, e) => UI(() =>
        {
            if (_currentFile != null && FilteredAudioFiles.Count > 0)
            {
                int index = FilteredAudioFiles.IndexOf(_currentFile);
                if (index >= 0 && index < FilteredAudioFiles.Count - 1)
                {
                    PlayAudioFile(FilteredAudioFiles[index + 1]);
                    return;
                }
            }

            if (_currentFile != null)
            {
                _currentFile.IsPlaying = false;
                _currentFile = null;
            }

            CurrentAlbumArt?.Dispose();
            CurrentAlbumArt = null;

            ButtonPlayPauseIcon = ICON_PLAY;
            ButtonPlayPausePadding = ICON_PLAY_PADDING;

            _smtcService?.SetPlaybackStatus(MediaPlaybackStatus.Stopped);
            _thumbBarService?.SetPlayingState(false);

            UpdateMainStatus("Finished");
            UpdateNavigationButtons();
        });

        _player.Paused += (s, e) => UI(() =>
        {
            ButtonPlayPauseIcon = ICON_PLAY;
            ButtonPlayPausePadding = ICON_PLAY_PADDING;

            _smtcService?.SetPlaybackStatus(MediaPlaybackStatus.Paused);
            _thumbBarService?.SetPlayingState(false);

            UpdateMainStatus("Paused");
        });

        _player.Playing += (s, e) => UI(() =>
        {
            ButtonPlayPauseIcon = ICON_PAUSE;
            ButtonPlayPausePadding = ICON_PAUSE_PADDING;

            _smtcService?.SetPlaybackStatus(MediaPlaybackStatus.Playing);
            _thumbBarService?.SetPlayingState(true);

            UpdateMainStatus("Playing");
        });

        _player.TimeChanged += (s, e) => UI(() =>
        {
            CurrentTrackTime = TimeSpan.FromMilliseconds(e.Time).ToString("mm\\:ss");
            if (!isSeeking)
            {
                CurrentTrackTimeNumber = e.Time;
            }
        });

        _player.Stopped += (s, e) => UI(() =>
        {
            ButtonPlayPauseIcon = ICON_PLAY;
            ButtonPlayPausePadding = ICON_PLAY_PADDING;

            _smtcService?.SetPlaybackStatus(MediaPlaybackStatus.Stopped);
            _thumbBarService?.SetPlayingState(false);

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

            if (e.Media.ParsedStatus != MediaParsedStatus.Done)
            {
                _ = await e.Media.Parse();
            }

            CurrentTrackDuration = TimeSpan.FromMilliseconds(e.Media.Duration).ToString("mm\\:ss");
            CurrentTrackDurationNumber = e.Media.Duration;

            CurrentTrackMetadata = (_currentFile?.Artist ?? "Unknown Artist") + " - " + (_currentFile?.Title ?? "Unknown Title");
        });
    }

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

    #region UI Events

    public void ButtonPreviousTrack()
    {
        if (_currentFile == null || FilteredAudioFiles.Count == 0)
        {
            return;
        }

        int index = FilteredAudioFiles.IndexOf(_currentFile);
        if (index > 0)
        {
            PlayAudioFile(FilteredAudioFiles[index - 1]);
        }
    }

    public void ButtonPlayPause()
    {
        UI(() =>
        {
            if (_player == null)
            {
                return;
            }

            if (_currentFile == null)
            {
                if (SelectedAudioFile != null)
                {
                    PlayAudioFile(SelectedAudioFile);
                }
                else if (FilteredAudioFiles.Count > 0)
                {
                    PlayAudioFile(FilteredAudioFiles[0]);
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

    public void ButtonNextTrack()
    {
        if (_currentFile == null || FilteredAudioFiles.Count == 0)
        {
            return;
        }

        int index = FilteredAudioFiles.IndexOf(_currentFile);
        if (index >= 0 && index < FilteredAudioFiles.Count - 1)
        {
            PlayAudioFile(FilteredAudioFiles[index + 1]);
        }
    }

    public void DataGridRowDoubleClick()
    {
        if (SelectedAudioFile != null)
        {
            PlayAudioFile(SelectedAudioFile);
        }
    }

    internal void CurrentVolumeChanged()
    {
        _player.Volume = (int)CurrentVolume;
        Settings.Set("OrgZ.Volume", (int)CurrentVolume);
        Settings.Save();
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

    internal void PlayAudioFile(AudioFileInfo? file)
    {
        if (_player == null || file == null || string.IsNullOrEmpty(file.FilePath))
        {
            return;
        }

        UI(() =>
        {
            if (_currentFile != null)
            {
                _currentFile.IsPlaying = false;
            }

            _currentFile = file;
            _currentFile.IsPlaying = true;
            SelectedAudioFile = file;

            CurrentAlbumArt?.Dispose();
            var artBytes = ExtractAlbumArtBytes(_currentFile.FilePath);
            CurrentAlbumArt = artBytes != null ? BitmapFromBytes(artBytes) : null;

            _smtcService?.UpdateMetadata(
                _currentFile.Title, _currentFile.Artist,
                _currentFile.Album, artBytes);

            _currentMedia?.Dispose();
            _currentMedia = new Media(_vlc, _currentFile.FilePath, FromType.FromPath);

            _ = _player.Play(_currentMedia);

            UpdateNavigationButtons();
        });
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

    #region Loading and Analyzing Files

    internal async Task LoadAsync()
    {
        UpdateTitle();

        await LoadAudioFilesAsync();
    }

    internal async Task LoadAudioFilesAsync()
    {
        if (string.IsNullOrEmpty(App.FolderPath))
        {
            return;
        }

        LibraryCache.EnsureCreated();

        UpdateMainStatus("Scanning files...");

        List<AudioFileInfo> diskFiles = await FileScanner.ScanDirectoryAsync(App.FolderPath, recursive: true);

        UpdateMainStatus("Loading cache...");

        var cache = await Task.Run(() => LibraryCache.LoadAll());

        // Build diff: cached unchanged, new/modified, deleted
        var cachedFiles = new List<AudioFileInfo>();
        var filesToAnalyze = new List<AudioFileInfo>();
        var diskPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var diskFile in diskFiles)
        {
            diskPaths.Add(diskFile.FilePath);

            if (cache.TryGetValue(diskFile.FilePath, out var cached)
                && cached.LastModified == diskFile.LastModified
                && cached.File.FileSize == diskFile.FileSize)
            {
                // Cache hit — use cached entry directly
                cachedFiles.Add(cached.File);
            }
            else
            {
                // New or modified — needs analysis
                filesToAnalyze.Add(diskFile);
            }
        }

        var deletedPaths = cache.Keys.Where(p => !diskPaths.Contains(p)).ToList();

        AudioFiles.Clear();

        // Add cached (already analyzed) files first for instant display
        foreach (var file in cachedFiles)
        {
            AudioFiles.Add(file);
        }

        // Add new/modified files (not yet analyzed)
        foreach (var file in filesToAnalyze)
        {
            AudioFiles.Add(file);
        }

        ApplyFilter();
        UpdateTitle();

        // Only analyze the delta
        await AnalyzeAllFilesAsync(filesToAnalyze);

        // Clean up deleted entries from cache
        if (deletedPaths.Count > 0)
        {
            await Task.Run(() => LibraryCache.RemoveFiles(deletedPaths));
        }

        UpdateData();
    }

    internal List<AudioFileInfo> GetFlacFilesWithoutAlbumArt()
    {
        return [.. AudioFiles
            .Where(f => AudioFileAnalyzer.Filters.IsFlacFile(f) &&
                       AudioFileAnalyzer.Filters.HasMissingAlbumArt(f))];
    }

    internal List<AudioFileInfo> GetFilesWithExtensionMismatch()
    {
        return [.. AudioFiles.Where(AudioFileAnalyzer.Filters.HasExtensionMismatch)];
    }

    internal List<AudioFileInfo> GetMp3Files()
    {
        return [.. AudioFiles.Where(AudioFileAnalyzer.Filters.IsMp3File)];
    }

    private async Task AnalyzeAllFilesAsync(List<AudioFileInfo> filesToAnalyze)
    {
        if (filesToAnalyze.Count == 0)
        {
            UpdateMainStatus("Ready (loaded from cache)");
            return;
        }

        await Task.Run(() =>
        {
            int idx = 0;

            foreach (AudioFileInfo audioFile in filesToAnalyze)
            {
                AudioFileAnalyzer.AnalyzeFile(audioFile);

                LibraryCache.UpsertFile(audioFile, audioFile.LastModified);

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
        int totalArtists = AudioFiles
            .Where(f => !string.IsNullOrEmpty(f.Artist))
            .Select(f => f.Artist)
            .Distinct()
            .Count();

        int totalAlbums = AudioFiles
            .Where(f => !string.IsNullOrEmpty(f.Album))
            .Select(f => f.Album)
            .Distinct()
            .Count();

        int totalSongs = AudioFiles.Count;

        TimeSpan totalDuration = TimeSpan.FromTicks(AudioFiles.Sum(x => x.Duration?.Ticks ?? 0));

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

            if (AudioFiles.Count > 0)
            {
                parts.Add($"({AudioFiles.Count} files)");
            }

            _window.Title = string.Join(sep, parts);
        });
    }

    internal void UpdateNavigationButtons()
    {
        if (_currentFile == null || FilteredAudioFiles.Count == 0)
        {
            IsBackTrackButtonEnabled = false;
            IsNextTrackButtonEnabled = false;
            _smtcService?.SetNavigationEnabled(false, false);
            _thumbBarService?.SetNavigationEnabled(false, false);
            return;
        }

        int index = FilteredAudioFiles.IndexOf(_currentFile);
        IsBackTrackButtonEnabled = index > 0;
        IsNextTrackButtonEnabled = index >= 0 && index < FilteredAudioFiles.Count - 1;
        _smtcService?.SetNavigationEnabled(IsBackTrackButtonEnabled, IsNextTrackButtonEnabled);
        _thumbBarService?.SetNavigationEnabled(IsBackTrackButtonEnabled, IsNextTrackButtonEnabled);
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
        _thumbBarService?.Dispose();
        _smtcService?.Dispose();
        _currentMedia?.Dispose();
        _player?.Dispose();
        _vlc?.Dispose();
    }
}
