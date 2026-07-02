// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;
using OrgZ.Models;

namespace OrgZ.Views;

/// <summary>
/// Per-iPod sync configuration: what this device syncs (podcasts / audiobooks / favorites / which
/// playlists). Unsupported components are hidden per the device's tier claims, so a Shuffle never
/// offers audiobooks. Returns the edited <see cref="SyncPlan"/> on Save &amp; Sync, null on Cancel.
/// </summary>
public partial class SyncSettingsDialog : Window
{
    private readonly List<(int Id, CheckBox Box)> _playlistBoxes = [];

    public SyncSettingsDialog() : this("Device", true, true, true, [], new SyncPlan()) { }

    public SyncSettingsDialog(
        string deviceName,
        bool supportsPodcasts,
        bool supportsAudiobooks,
        bool supportsPlaylists,
        IReadOnlyList<(int Id, string Name)> playlists,
        SyncPlan current)
    {
        InitializeComponent();

        HeaderText.Text = $"Sync “{deviceName}”";

        // Only offer what the tier actually writes - a component the device can't carry never shows.
        PodcastsCheck.IsVisible = supportsPodcasts;
        PodcastsCheck.IsChecked = current.Podcasts;

        AudiobooksCheck.IsVisible = supportsAudiobooks;
        AudiobooksCheck.IsChecked = current.Audiobooks;

        var playlistsAllowed = supportsPlaylists;
        FavoritesCheck.IsVisible = playlistsAllowed;
        FavoritesCheck.IsChecked = current.Favorites;

        PlaylistsHeader.IsVisible = playlistsAllowed;
        if (playlistsAllowed)
        {
            if (playlists.Count == 0)
            {
                NoPlaylistsText.IsVisible = true;
            }
            foreach (var (id, name) in playlists)
            {
                var box = new CheckBox { Content = name, IsChecked = current.PlaylistIds.Contains(id) };
                _playlistBoxes.Add((id, box));
                PlaylistList.Children.Add(box);
            }
        }
    }

    private void SyncButton_Click(object? sender, RoutedEventArgs e)
    {
        var plan = new SyncPlan
        {
            Podcasts = PodcastsCheck.IsVisible && PodcastsCheck.IsChecked == true,
            Audiobooks = AudiobooksCheck.IsVisible && AudiobooksCheck.IsChecked == true,
            Favorites = FavoritesCheck.IsVisible && FavoritesCheck.IsChecked == true,
            PlaylistIds = _playlistBoxes.Where(p => p.Box.IsChecked == true).Select(p => p.Id).ToList(),
        };
        Close(plan);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
