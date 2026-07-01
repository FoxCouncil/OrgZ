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
    /// - same navigation as the tile buttons in the carousels.
    /// </summary>
    private async void NumberedFeedItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is NumberedFeed nf && ViewModel is { } vm)
        {
            await vm.OpenFeedAsync(nf.Feed);
        }
    }

    /// <summary>
    /// A row in the left-column Categories list. Opens the shared feed-list
    /// view filled with that genre's trending feeds.
    /// </summary>
    private async void CategoryItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PodcastCategory cat } && ViewModel is { } vm)
        {
            await vm.ShowCategoryAsync(cat);
        }
    }

    /// <summary>
    /// A tile in the left-column "Subscribed" preview. Opens that show's feed-detail page - the
    /// header's "Show All" opens the full subscriptions view instead.
    /// </summary>
    private async void SubscribedShow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PodcastSubscription sub } && ViewModel is { } vm)
        {
            await vm.OpenSubscriptionAsync(sub);
        }
    }
}
