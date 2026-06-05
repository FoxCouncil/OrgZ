// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using OrgZ.Models;
using OrgZ.ViewModels;

namespace OrgZ.Controls;

public partial class PodcastsPanel : UserControl
{
    public PodcastsPanel()
    {
        InitializeComponent();
    }

    private PodcastsViewModel? ViewModel => DataContext as PodcastsViewModel;

    private async void FeedTile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is PodcastFeed feed && ViewModel is { } vm)
        {
            await vm.OpenFeedAsync(feed);
        }
    }

    private async void SubscriptionTile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is PodcastSubscription sub && ViewModel is { } vm)
        {
            await vm.OpenFeedAsync(new PodcastFeed
            {
                Id          = sub.FeedId,
                PodcastGuid = sub.PodcastGuid,
                Title       = sub.Title,
                Author      = sub.Author,
                Description = sub.Description,
                HomepageUrl = sub.HomepageUrl,
                FeedUrl     = sub.FeedUrl,
                Image       = sub.ImageUrl,
                Artwork     = sub.ImageUrl,
            });
        }
    }

    private void DownloadEpisode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && ResolveEpisode(b) is { } ep && ViewModel is { } vm)
        {
            vm.DownloadEpisode(ep);
        }
    }

    private void StreamEpisode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && ResolveEpisode(b) is { } ep && ViewModel is { } vm)
        {
            vm.StreamEpisode(ep);
        }
    }

    /// <summary>
    /// Buttons inside a DataGridTemplateColumn end up with a DataContext that
    /// looks like a PodcastEpisode but is missing fields like DurationSec --
    /// the cell presenter does something to its binding source. The reliable
    /// source of truth is the row's DataGridRow.DataContext, which we find
    /// by walking up the visual tree. Fallback chain handles non-DataGrid
    /// callsites (ItemsControl rows in subscriptions / downloads).
    /// </summary>
    private static PodcastEpisode? ResolveEpisode(Button b)
    {
        Avalonia.Visual? cur = b;
        while (cur is not null)
        {
            if (cur is Avalonia.Controls.DataGridRow row && row.DataContext is PodcastEpisode rowEp)
            {
                return rowEp;
            }
            cur = cur.GetVisualParent();
        }
        return (b.DataContext as PodcastEpisode) ?? (b.Tag as PodcastEpisode);
    }

    private void EpisodesGrid_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is DataGrid g && g.SelectedItem is PodcastEpisode ep && ViewModel is { } vm)
        {
            vm.StreamEpisode(ep);
        }
    }

    private PodcastEpisode? GetEpisodesGridSelection()
    {
        return EpisodesGrid.SelectedItem as PodcastEpisode;
    }

    private void ContextPlay_Click(object? sender, RoutedEventArgs e)
    {
        var ep = GetEpisodesGridSelection();

        if (ep is null || ViewModel is not { } vm)
        {
            return;
        }

        vm.StreamEpisode(ep);
    }

    private void ContextDownload_Click(object? sender, RoutedEventArgs e)
    {
        var ep = GetEpisodesGridSelection();

        if (ep is null || ViewModel is not { } vm)
        {
            return;
        }

        vm.DownloadEpisode(ep);
    }

    private async void ContextGetInfo_Click(object? sender, RoutedEventArgs e)
    {
        var ep = GetEpisodesGridSelection();

        if (ep is null || ViewModel is not { } vm)
        {
            return;
        }

        await vm.ShowEpisodeInfoAsync(ep);
    }

    private void PlayDownload_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is PodcastDownload d && ViewModel is { } vm)
        {
            vm.PlayDownload(d);
        }
    }

    private async void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel is { } vm)
        {
            await vm.SearchAsync();
        }
    }
}
