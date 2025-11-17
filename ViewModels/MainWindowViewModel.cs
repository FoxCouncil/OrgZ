// Copyright (c) 2025 Fox Diller

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LibVLCSharp.Shared;
using OrgZ.Models;
using OrgZ.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OrgZ.ViewModels;

internal partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly MainWindow _window;

    private readonly LibVLC _vlc = new();

    private readonly MediaPlayer? _player;

    private AudioFileInfo _currentFile;

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
    private bool _isPlayButtonEnabled = true;

    [ObservableProperty]
    private bool _isPauseButtonEnabled = false;

    [ObservableProperty]
    private bool _isStopButtonEnabled = false;

    [ObservableProperty]
    private bool _isNextTrackButtonEnabled = false;

    [ObservableProperty]
    private long _currentTrackTimeNumber = 0;

    [ObservableProperty]
    private string _currentTrackTime = "";

    [ObservableProperty]
    private string _currentTrackArtist = string.Empty;

    [ObservableProperty]
    private string _currentTrackTitle = string.Empty;

    [ObservableProperty]
    private string _currentTrackDuration = "";

    [ObservableProperty]
    private long _currentTrackDurationNumber = 0;

    [ObservableProperty]
    private string _mainStatus = "Ready";

    internal ObservableCollection<AudioFileInfo> AudioFiles { get; } = [];

    internal bool IsMediaLoaded => _player?.Media != null;

    public MainWindowViewModel(MainWindow window)
    {
        _window = window;

        _vlc = new();
        _player = new(_vlc);

        IsBackTrackButtonEnabled = false;
        IsPlayButtonEnabled = false;
        IsPauseButtonEnabled = false;
        IsStopButtonEnabled = false;
        IsNextTrackButtonEnabled = false;

        _player.EndReached += (s, e) => UI(() =>
        {
            // Handle end of playback if needed
            IsPlayButtonEnabled = true;
            IsPauseButtonEnabled = false;
            IsStopButtonEnabled = false;
        });

        _player.Paused += (s, e) => UI(() =>
        {
            IsPlayButtonEnabled = true;
            IsPauseButtonEnabled = false;
            IsStopButtonEnabled = true;
        });

        _player.Playing += (s, e) => UI(() =>
        {
            IsPlayButtonEnabled = false;
            IsPauseButtonEnabled = true;
            IsStopButtonEnabled = true;
        });

        _player.TimeChanged += (s, e) => UI(() =>
        {
            CurrentTrackTime = TimeSpan.FromMilliseconds(e.Time).ToString("mm\\:ss");
            CurrentTrackTimeNumber = e.Time;
        });

        _player.Stopped += (s, e) => UI(() =>
        {
            IsPlayButtonEnabled = true;
            IsPauseButtonEnabled = false;
            IsStopButtonEnabled = false;
        });

        _player.MediaChanged += (s, e) => UI(async () =>
        {
            if (e.Media == null)
            {
                CurrentTrackArtist = string.Empty;
                CurrentTrackTitle = string.Empty;

                return;
            }

            if (e.Media.ParsedStatus != MediaParsedStatus.Done)
            {
                _ = await e.Media.Parse();
            }

            CurrentTrackDuration = TimeSpan.FromMilliseconds(e.Media.Duration).ToString("mm\\:ss");
            CurrentTrackDurationNumber = e.Media.Duration;

            CurrentTrackArtist = _currentFile?.Artist ?? "Unknown Artist";
            CurrentTrackTitle = _currentFile?.Title ?? "Unknown Title";
        });
    }

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
                UpdateData();

                AudioFileAnalyzer.AnalyzeFile(audioFile);

                UpdateMainStatus($"Analyzing file {++idx} of {AudioFiles.Count}");
            }

            UpdateMainStatus($"Analyzing file {idx} of {AudioFiles.Count} | COMPLETE!");
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

        Dispatcher.UIThread.Post(() =>
        {
            TotalArtists = totalArtists.ToString();

            TotalAlbums = totalAlbums.ToString();

            TotalSongs = totalSongs.ToString();

            TotalDuration = totalDuration.ToString(@"dd\:hh\:mm\:ss");
        });
    }

    internal void UpdateTitle()
    {
        Dispatcher.UIThread.Post(() =>
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
        Dispatcher.UIThread.Post(() =>
        {
            MainStatus = status;
        });
    }

    #endregion

    #region Utils

    private void UI(Action action)
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
