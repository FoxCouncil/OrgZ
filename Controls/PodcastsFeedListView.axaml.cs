// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OrgZ.Models;
using OrgZ.ViewModels;

namespace OrgZ.Controls;

/// <summary>
/// Shared "list of feeds" surface used by both genre browse (left-column
/// Categories list) and store search (global header box). A flat grid of feed
/// tiles; clicking one opens the feed-detail view, same as the carousel and Top
/// Podcasts tiles.
///
/// The grid is a <see cref="UniformGrid"/> whose column count is recomputed from
/// the available width (see <see cref="ColumnCount"/>) so the tiles always span
/// the full panel width and reflow on resize.
/// </summary>
public partial class PodcastsFeedListView : UserControl
{
    // Target tile footprint (138px art + the 6px tile margins on each side +
    // breathing room). Column count = floor(width / this), so tiles land close
    // to their natural size and the grid never shows dead space on the right.
    private const double TargetTileWidth = 162;

    public static readonly StyledProperty<int> ColumnCountProperty =
        AvaloniaProperty.Register<PodcastsFeedListView, int>(nameof(ColumnCount), 1);

    public int ColumnCount
    {
        get => GetValue(ColumnCountProperty);
        set => SetValue(ColumnCountProperty, value);
    }

    public PodcastsFeedListView()
    {
        InitializeComponent();
    }

    private PodcastsViewModel? ViewModel => DataContext as PodcastsViewModel;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        // Subtract the StackPanel's 16px side margins before dividing.
        var usable = e.NewSize.Width - 32;
        ColumnCount = usable <= TargetTileWidth ? 1 : (int)(usable / TargetTileWidth);
    }

    private async void FeedTile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is PodcastFeed feed && ViewModel is { } vm)
        {
            await vm.OpenFeedAsync(feed);
        }
    }
}
