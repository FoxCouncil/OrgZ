// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using OrgZ.Services.AudioOutput;
using OrgZ.ViewModels;

namespace OrgZ.Views;

public partial class SettingsDialog : Window
{
    private string _pendingFolderPath;
    private readonly List<MediaItem> _allItems;
    private readonly AudioOutputManager? _audioOutput;
    private readonly Dictionary<string, DeviceRow> _deviceRows = [];

    public bool FolderChanged { get; private set; }
    public bool SettingsReset { get; private set; }
    public bool RadioCacheCleared { get; private set; }

    public SettingsDialog() : this([], null) { }

    public SettingsDialog(List<MediaItem> allItems) : this(allItems, null) { }

    public SettingsDialog(List<MediaItem> allItems, AudioOutputManager? audioOutput)
    {
        InitializeComponent();

        _pendingFolderPath = App.FolderPath;
        _allItems = allItems;
        _audioOutput = audioOutput;

        WindowSizeTracker.Track(this, "Settings");

        LoadSettings();
        PopulateStats();
        PopulateDeviceList();

        if (_audioOutput != null)
        {
            _audioOutput.DevicesChanged += OnAudioDevicesChanged;
            Closed += (_, _) => _audioOutput.DevicesChanged -= OnAudioDevicesChanged;
        }
    }

    private void OnAudioDevicesChanged(object? sender, EventArgs e)
    {
        // Fires from the manager's background polling thread; marshal to UI.
        Avalonia.Threading.Dispatcher.UIThread.Post(PopulateDeviceList);
    }

    private sealed class DeviceRow
    {
        public required AudioDeviceInfo Device { get; init; }
        public required CheckBox Checkbox { get; init; }
        public required Slider VolumeSlider { get; init; }
        public required CheckBox MuteCheck { get; init; }
    }

    private void PopulateDeviceList()
    {
        if (_audioOutput == null)
        {
            DeviceDiscoveryHint.Text = "(audio output unavailable)";
            return;
        }

        _deviceRows.Clear();
        DeviceListPanel.Children.Clear();

        var activeSinkIds = _audioOutput.Bus.Sinks.ToDictionary(s => s.Id, s => s);
        var devices = _audioOutput.EnumerateAllDevices();
        DeviceDiscoveryHint.Text = $"{devices.Count} device(s) found.  AirPlay devices may take a few seconds to appear.";

        string? lastProvider = null;
        foreach (var device in devices)
        {
            if (device.ProviderName != lastProvider)
            {
                lastProvider = device.ProviderName;
                DeviceListPanel.Children.Add(new TextBlock
                {
                    Text = device.ProviderName,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    FontSize = 12,
                    Margin = new Avalonia.Thickness(0, 8, 0, 4),
                });
            }

            var active = activeSinkIds.TryGetValue(device.QualifiedId, out var sink);
            var row = BuildDeviceRow(device, active, sink?.Volume ?? 1f, sink?.IsMuted ?? false);
            _deviceRows[device.QualifiedId] = row;
        }
    }

    private DeviceRow BuildDeviceRow(AudioDeviceInfo device, bool active, float volume, bool muted)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,140,Auto"),
            Margin = new Avalonia.Thickness(0, 2, 0, 2),
        };

        var check = new CheckBox { IsChecked = active, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(check, 0);
        grid.Children.Add(check);

        var label = new TextBlock
        {
            Text = device.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(8, 0, 8, 0),
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = volume * 100,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 120,
            IsEnabled = active,
        };
        Grid.SetColumn(slider, 2);
        grid.Children.Add(slider);

        var mute = new CheckBox
        {
            Content = "Mute",
            IsChecked = muted,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = active,
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(mute, 3);
        grid.Children.Add(mute);

        check.IsCheckedChanged += (_, _) =>
        {
            slider.IsEnabled = check.IsChecked == true;
            mute.IsEnabled = check.IsChecked == true;
            ApplyRowLive(device);
        };

        // Live volume + mute updates - hook a sink that already exists and
        // adjust in-place, so the user hears changes immediately instead of
        // needing to close the dialog.  Checking/unchecking still requires
        // ApplySelections (it adds / removes the sink from the bus).
        slider.PropertyChanged += (_, ev) =>
        {
            if (ev.Property.Name == nameof(Slider.Value))
            {
                ApplyRowLive(device);
            }
        };
        mute.IsCheckedChanged += (_, _) => ApplyRowLive(device);

        DeviceListPanel.Children.Add(grid);

        return new DeviceRow
        {
            Device = device,
            Checkbox = check,
            VolumeSlider = slider,
            MuteCheck = mute,
        };
    }

    private void ApplyRowLive(AudioDeviceInfo device)
    {
        if (_audioOutput == null || !_deviceRows.TryGetValue(device.QualifiedId, out var row))
        {
            return;
        }

        var existing = _audioOutput.Bus.Sinks.FirstOrDefault(s => s.Id == device.QualifiedId);
        if (existing == null)
        {
            return;
        }

        existing.Volume = (float)(row.VolumeSlider.Value / 100.0);
        existing.IsMuted = row.MuteCheck.IsChecked == true;
    }

    private void RefreshDevicesButton_Click(object? sender, RoutedEventArgs e)
    {
        PopulateDeviceList();
    }

    private void ApplyDeviceSelections()
    {
        if (_audioOutput == null)
        {
            return;
        }

        var selections = new List<AudioOutputManager.SinkSelection>();
        foreach (var row in _deviceRows.Values)
        {
            if (row.Checkbox.IsChecked == true)
            {
                selections.Add(new AudioOutputManager.SinkSelection
                {
                    QualifiedId = row.Device.QualifiedId,
                    Volume = (float)(row.VolumeSlider.Value / 100.0),
                    IsMuted = row.MuteCheck.IsChecked == true,
                });
            }
        }

        _audioOutput.ApplySelections(selections);
        _audioOutput.SavePersistedSelections();
    }

    private void LoadSettings()
    {
        // General
        FolderPathText.Text = string.IsNullOrEmpty(App.FolderPath) ? "(No folder selected)" : App.FolderPath;
        MinimizeToTrayCheck.IsChecked = Settings.Get("OrgZ.MinimizeToTray", false);
        RememberLastTrackCheck.IsChecked = Settings.Get("OrgZ.RememberLastTrack", false);
        ShowIgnoredCheck.IsChecked = Settings.Get("OrgZ.ShowIgnored", true);

        BadFormatShowCheck.IsChecked = Settings.Get("OrgZ.BadFormat.ShowInSidebar", true);
        BadFormatNoTitleCheck.IsChecked = Settings.Get("OrgZ.BadFormat.NoTitle", true);
        BadFormatNoArtistCheck.IsChecked = Settings.Get("OrgZ.BadFormat.NoArtist", true);
        BadFormatNoYearCheck.IsChecked = Settings.Get("OrgZ.BadFormat.NoYear", true);
        BadFormatNoAlbumArtCheck.IsChecked = Settings.Get("OrgZ.BadFormat.NoAlbumArt", true);
        BadFormatLossyCheck.IsChecked = Settings.Get("OrgZ.BadFormat.LossyFormats", true);

        // Playback
        var bufferSize = Settings.Get("OrgZ.StreamingBufferSize", "Medium");
        BufferSizeCombo.SelectedIndex = bufferSize switch
        {
            "Small" => 0,
            "Medium" => 1,
            "Large" => 2,
            "Extra Large" => 3,
            _ => 1
        };

        var shuffleMode = Settings.Get("OrgZ.ShuffleMode", "Song");
        ShuffleSongRadio.IsChecked = shuffleMode == "Song";
        ShuffleAlbumRadio.IsChecked = shuffleMode == "Album";

        AutoAdvanceCheck.IsChecked = Settings.Get("OrgZ.AutoAdvance", true);

        // Playback → Mini-Player / Visualizer
        var miniMode = MainWindowViewModel.LoadMiniPlayerMode();
        MiniPlayerModeReplace.IsChecked = miniMode == MiniPlayerMode.Replace;
        MiniPlayerModeSideBySide.IsChecked = miniMode == MiniPlayerMode.SideBySide;

        VisualizerEnabledCheck.IsChecked = Settings.Get("OrgZ.Visualizer.Enabled", false);
        var visName = Settings.Get("OrgZ.Visualizer.Name", "spectrum");
        VisualizerNameCombo.SelectedIndex = visName switch
        {
            "spectrum" => 0,
            "spectrometer" => 1,
            "scope" => 2,
            "vumeter" => 3,
            "goom" => 4,
            _ => 0,
        };

        // Advanced
        var dbDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrgZ");
        DatabasePathText.Text = Path.Combine(dbDir, "library.db");
        SettingsPathText.Text = Settings.GetSettingsFilePath();
    }

    private void PopulateStats()
    {
        var musicItems = _allItems.Where(i => i.Kind == MediaKind.Music).ToList();
        var radioItems = _allItems.Where(i => i.Kind == MediaKind.Radio).ToList();

        // Library Overview
        StatMusicCount.Text = musicItems.Count.ToString("N0");
        StatRadioCount.Text = radioItems.Count.ToString("N0");

        // Music Stats
        var artists = musicItems.Where(f => !string.IsNullOrEmpty(f.Artist)).Select(f => f.Artist).Distinct().Count();
        var albums = musicItems.Where(f => !string.IsNullOrEmpty(f.Album)).Select(f => f.Album).Distinct().Count();
        var totalDuration = TimeSpan.FromTicks(musicItems.Sum(x => x.Duration?.Ticks ?? 0));
        var totalBytes = musicItems.Sum(x => x.FileSize ?? 0);

        StatArtistCount.Text = artists.ToString("N0");
        StatAlbumCount.Text = albums.ToString("N0");
        StatSongCount.Text = musicItems.Count.ToString("N0");
        StatMusicFavorites.Text = musicItems.Count(f => f.IsFavorite).ToString("N0");
        StatTotalDuration.Text = totalDuration.ToString(@"dd\:hh\:mm\:ss");
        StatTotalFileSize.Text = FormatHelper.FormatFileSize(totalBytes);

        // File Types (inside Music section)
        var extensionGroups = musicItems
            .Where(f => !string.IsNullOrEmpty(f.Extension))
            .GroupBy(f => f.Extension!.ToUpperInvariant())
            .OrderByDescending(g => g.Count())
            .ToList();

        foreach (var group in extensionGroups)
        {
            var totalSize = group.Sum(f => f.FileSize ?? 0);
            FileTypesPanel.Children.Add(CreateBreakdownRow(group.Key, $"{group.Count():N0} files", FormatHelper.FormatFileSize(totalSize)));
        }

        if (extensionGroups.Count == 0)
        {
            FileTypesPanel.Children.Add(CreateStatLabel("No music files found", 0.4));
        }

        // Radio Stats
        var favorites = radioItems.Count(s => s.IsFavorite);
        var countries = radioItems.Where(s => !string.IsNullOrEmpty(s.Country)).Select(s => s.Country).Distinct().Count();

        StatRadioStations.Text = radioItems.Count.ToString("N0");
        StatRadioFavorites.Text = favorites.ToString("N0");
        StatRadioCountries.Text = countries.ToString("N0");

        // Radio Genres (normalized, iTunes-style buckets)
        var genreGroups = radioItems
            .GroupBy(s => s.NormalizedGenre)
            .OrderByDescending(g => g.Count())
            .ToList();

        foreach (var group in genreGroups)
        {
            RadioSourcesPanel.Children.Add(CreateBreakdownRow(group.Key, $"{group.Count():N0} stations", null));
        }

        if (genreGroups.Count == 0)
        {
            RadioSourcesPanel.Children.Add(CreateStatLabel("No stations", 0.4));
        }

        // Radio Codecs
        var codecGroups = radioItems
            .Where(s => !string.IsNullOrEmpty(s.Codec))
            .GroupBy(s => s.CodecLabel)
            .OrderByDescending(g => g.Count())
            .ToList();

        foreach (var group in codecGroups)
        {
            RadioCodecsPanel.Children.Add(CreateBreakdownRow(group.Key, $"{group.Count():N0} stations", null));
        }

        if (codecGroups.Count == 0)
        {
            RadioCodecsPanel.Children.Add(CreateStatLabel("No codec data", 0.4));
        }
    }

    private void ScrollToElement(Control target)
    {
        target.BringIntoView();
    }

    private void LinkMusicCount_Click(object? sender, RoutedEventArgs e)
    {
        ScrollToElement(MusicStatsSection);
    }

    private void LinkRadioCount_Click(object? sender, RoutedEventArgs e)
    {
        ScrollToElement(RadioStatsSection);
    }

    private static Grid CreateBreakdownRow(string label, string count, string? size)
    {
        var row = new Grid
        {
            ColumnDefinitions = size != null
                ? ColumnDefinitions.Parse("140,120,*")
                : ColumnDefinitions.Parse("200,*"),
        };

        row.Children.Add(CreateStatCell(label, 0, 0.6));
        row.Children.Add(CreateStatCell(count, 1, 1.0));

        if (size != null)
        {
            row.Children.Add(CreateStatCell(size, 2, 0.5));
        }

        return row;
    }

    private static TextBlock CreateStatCell(string text, int column, double opacity)
    {
        var tb = new TextBlock
        {
            Text = text,
            Opacity = opacity,
        };
        Grid.SetColumn(tb, column);
        return tb;
    }

    private static TextBlock CreateStatLabel(string text, double opacity)
    {
        return new TextBlock
        {
            Text = text,
            Opacity = opacity,
        };
    }


    private void SaveSettings()
    {
        // General
        if (_pendingFolderPath != App.FolderPath)
        {
            App.FolderPath = _pendingFolderPath;
            Settings.Set("OrgZ.FolderPath", _pendingFolderPath);
            FolderChanged = true;
        }

        Settings.Set("OrgZ.MinimizeToTray", MinimizeToTrayCheck.IsChecked == true);
        Settings.Set("OrgZ.RememberLastTrack", RememberLastTrackCheck.IsChecked == true);
        Settings.Set("OrgZ.ShowIgnored", ShowIgnoredCheck.IsChecked == true);

        Settings.Set("OrgZ.BadFormat.ShowInSidebar", BadFormatShowCheck.IsChecked == true);
        Settings.Set("OrgZ.BadFormat.NoTitle", BadFormatNoTitleCheck.IsChecked == true);
        Settings.Set("OrgZ.BadFormat.NoArtist", BadFormatNoArtistCheck.IsChecked == true);
        Settings.Set("OrgZ.BadFormat.NoYear", BadFormatNoYearCheck.IsChecked == true);
        Settings.Set("OrgZ.BadFormat.NoAlbumArt", BadFormatNoAlbumArtCheck.IsChecked == true);
        Settings.Set("OrgZ.BadFormat.LossyFormats", BadFormatLossyCheck.IsChecked == true);

        // Playback
        var bufferSize = BufferSizeCombo.SelectedIndex switch
        {
            0 => "Small",
            1 => "Medium",
            2 => "Large",
            3 => "Extra Large",
            _ => "Medium"
        };
        Settings.Set("OrgZ.StreamingBufferSize", bufferSize);

        Settings.Set("OrgZ.ShuffleMode", ShuffleAlbumRadio.IsChecked == true ? "Album" : "Song");
        Settings.Set("OrgZ.AutoAdvance", AutoAdvanceCheck.IsChecked == true);

        // Playback → Mini-Player / Visualizer
        var miniMode = MiniPlayerModeSideBySide.IsChecked == true ? MiniPlayerMode.SideBySide : MiniPlayerMode.Replace;
        MainWindowViewModel.SaveMiniPlayerMode(miniMode);

        Settings.Set("OrgZ.Visualizer.Enabled", VisualizerEnabledCheck.IsChecked == true);
        var visTag = (VisualizerNameCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "spectrum";
        Settings.Set("OrgZ.Visualizer.Name", visTag);

        Settings.Save();
    }

    private async void ChangeFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Select OrgZ Music Folder",
                AllowMultiple = false
            });

        if (folders.Count > 0)
        {
            _pendingFolderPath = folders[0].Path.LocalPath;
            FolderPathText.Text = _pendingFolderPath;
        }
    }

    private void ResetFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        _pendingFolderPath = string.Empty;
        FolderPathText.Text = "(No folder selected)";
    }

    private void ClearRadioCacheButton_Click(object? sender, RoutedEventArgs e)
    {
        RadioCacheCleared = true;
        ClearRadioCacheButton.IsEnabled = false;
        ClearRadioCacheButton.Content = "Cleared";
    }

    private void ResetWindowSizesButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowSizeTracker.ResetAll();
        ResetWindowSizesButton.IsEnabled = false;
        ResetWindowSizesButton.Content = "Reset";
    }

    private void ResetSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        SettingsReset = true;
        ResetSettingsButton.IsEnabled = false;
        ResetSettingsButton.Content = "Will reset on OK";
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        if (SettingsReset)
        {
            Settings.Clear();
            Settings.Save();
        }
        else
        {
            SaveSettings();
            ApplyDeviceSelections();
        }

        Close(true);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        FolderChanged = false;
        RadioCacheCleared = false;
        SettingsReset = false;
        Close(false);
    }
}
