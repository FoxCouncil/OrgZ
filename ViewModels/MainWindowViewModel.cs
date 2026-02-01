// Copyright (c) 2025 Fox Diller

using Avalonia;
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
    private uint _currentVolume = 100;

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
    }

    public MainWindowViewModel(MainWindow window)
    {
        _window = window;

        _vlc = new();
        _vlc.SetAppId("org.foxdiller.orgz", "0.1", "Assets/app.ico");
        _vlc.SetUserAgent("OrgZ 0.1", "orgz0.1/player");

        _player = new(_vlc);

        IsBackTrackButtonEnabled = false;
        IsNextTrackButtonEnabled = false;

        ButtonPlayPausePadding = ICON_PLAY_PADDING;

        _player.EndReached += (s, e) => UI(() =>
        {
            // Handle end of playback if needed
            ButtonPlayPauseIcon = ICON_PLAY;
            ButtonPlayPausePadding = ICON_PLAY_PADDING;

            UpdateMainStatus("Finished");
        });

        _player.Paused += (s, e) => UI(() =>
        {
            ButtonPlayPauseIcon = ICON_PLAY;
            ButtonPlayPausePadding = ICON_PLAY_PADDING;

            UpdateMainStatus("Paused");
        });

        _player.Playing += (s, e) => UI(() =>
        {
            ButtonPlayPauseIcon = ICON_PAUSE;
            ButtonPlayPausePadding = ICON_PAUSE_PADDING;

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

    #region UI Events

    public void ButtonPreviousTrack()
    {

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
            _currentFile = file;

            Media media = new(_vlc, _currentFile.FilePath, FromType.FromPath);

            _ = _player.Play(media);
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

        List<AudioFileInfo> audioFiles = await FileScanner.ScanDirectoryAsync(App.FolderPath, recursive: true);

        AudioFiles.Clear();

        foreach (AudioFileInfo file in audioFiles)
        {
            AudioFiles.Add(file);
        }

        ApplyFilter();

        UpdateTitle();

        await AnalyzeAllFilesAsync();

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

    private async Task AnalyzeAllFilesAsync()
    {
        await Task.Run(() =>
        {
            int idx = 0;

            foreach (AudioFileInfo audioFile in AudioFiles)
            {
                AudioFileAnalyzer.AnalyzeFile(audioFile);

                UpdateMainStatus($"Analyzing file {++idx} of {AudioFiles.Count}");
            }

            UpdateMainStatus($"Analyzing file {idx} of {AudioFiles.Count} | COMPLETE!");

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

            parts.Add("OrgZ");
            parts.Add(App.FolderPath);

            if (AudioFiles.Count > 0)
            {
                parts.Add($"({AudioFiles.Count} files)");
            }

            _window.Title = string.Join(sep, parts);
        });
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

    #endregion

    public void Dispose()
    {
        _player?.Dispose();
        _vlc?.Dispose();
    }
}
