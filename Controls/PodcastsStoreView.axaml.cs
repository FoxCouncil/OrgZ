// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;
using OrgZ.Models;
using OrgZ.ViewModels;

namespace OrgZ.Controls;

public partial class PodcastsStoreView : UserControl
{
    public PodcastsStoreView()
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
}
