// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace OrgZ.Views;

public partial class SettingsDialog : Window
{
    private string _pendingFolderPath;
    private readonly List<MediaItem> _allItems;

    public bool FolderChanged { get; private set; }
    public bool SettingsReset { get; private set; }
    public bool RadioCacheCleared { get; private set; }

    public SettingsDialog() : this([]) { }

    public SettingsDialog(List<MediaItem> allItems)
    {
        InitializeComponent();

        _pendingFolderPath = App.FolderPath;
        _allItems = allItems;

        LoadSettings();
        PopulateStats();
    }

    private void LoadSettings()
    {
        // General
        FolderPathText.Text = string.IsNullOrEmpty(App.FolderPath) ? "(No folder selected)" : App.FolderPath;
        MinimizeToTrayCheck.IsChecked = Settings.Get("OrgZ.MinimizeToTray", false);
        RememberLastTrackCheck.IsChecked = Settings.Get("OrgZ.RememberLastTrack", false);

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

        // Advanced
        var dbDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrgZ");
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

        // Radio Sources
        var sourceGroups = radioItems
            .GroupBy(s => s.Source ?? "unknown")
            .OrderByDescending(g => g.Count())
            .ToList();

        foreach (var group in sourceGroups)
        {
            var label = MediaItem.GetSourceDisplayName(group.Key);
            RadioSourcesPanel.Children.Add(CreateBreakdownRow(label, $"{group.Count():N0} stations", null));
        }

        if (sourceGroups.Count == 0)
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
