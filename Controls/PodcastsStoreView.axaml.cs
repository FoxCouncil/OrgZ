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

    /// <summary>
    /// A row in the numbered Top Podcasts list. The Tag carries the
    /// <see cref="NumberedFeed"/> wrapper; we reach through to its
    /// <c>Feed</c> and hand off to <see cref="PodcastsViewModel.OpenFeedAsync"/>
    /// — same navigation as the tile buttons in the carousels.
    /// </summary>
    private async void NumberedFeedItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is NumberedFeed nf && ViewModel is { } vm)
        {
            await vm.OpenFeedAsync(nf.Feed);
        }
    }

    /// <summary>
    /// A row in the left-column Categories list. Surfaces the click but
    /// doesn't navigate anywhere yet — there's no "feeds in category"
    /// view to hand off to, so we leave the action unwired and let Fox
    /// direct what tapping a category should do.
    /// </summary>
    private void CategoryItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PodcastCategory })
        {
            // TODO: navigate to a category browse view.
        }
    }
}
