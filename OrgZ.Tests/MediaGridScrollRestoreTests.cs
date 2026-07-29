// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.VisualTree;

namespace OrgZ.Tests;

/// <summary>
/// How a view's scroll position gets remembered - and why the obvious way doesn't work.
///
/// The old code saved <c>ScrollViewer.Offset.Y</c> and assigned it back on the way in. Avalonia
/// 12's DataGrid contains NO ScrollViewer: it scrolls itself with a
/// <c>DataGridRowsPresenter</c> and bare ScrollBars, so the lookup returned null, every save
/// recorded zero, and every restore did nothing. That was true long before the grid was shared -
/// it just looked like a regression because the whole area came under scrutiny at once.
///
/// The public scroll API is <see cref="DataGrid.ScrollIntoView"/>, so position is remembered as
/// the ITEM at the top of the viewport rather than a pixel offset. That also survives a change of
/// row height, a filter, or a re-sort, none of which a pixel offset does.
/// </summary>
public sealed class MediaGridScrollRestoreTests
{
    private static List<MediaItem> ManyRows() =>
        Enumerable.Range(0, 400)
            .Select(i => new MediaItem { Id = i.ToString(), Kind = MediaKind.Music, Title = $"Track {i:000}" })
            .ToList();

    private static DataGrid NewGrid() => new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        Columns = { new DataGridTextColumn { Header = "Title", Binding = new Binding(nameof(MediaItem.Title)) } },
    };

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
    }

    /// <summary>The item in the topmost visible row - what MainWindow saves as the scroll anchor.</summary>
    private static MediaItem? TopVisibleItem(DataGrid grid) =>
        grid.GetVisualDescendants()
            .OfType<DataGridRow>()
            .Where(r => r.IsVisible && r.DataContext is MediaItem)
            .OrderBy(r => r.Bounds.Y)
            .Select(r => r.DataContext as MediaItem)
            .FirstOrDefault();

    [Fact]
    public async Task The_grid_has_no_ScrollViewer_which_is_why_offset_based_restore_never_worked()
    {
        // Pinned deliberately. If a future Avalonia gives the DataGrid a real ScrollViewer this
        // fails, and the anchor-item approach below can be reconsidered.
        var found = await HeadlessUi.RunAsync(() =>
        {
            var grid = NewGrid();
            var window = new Window { Width = 800, Height = 400, Content = grid };
            window.Show();
            grid.ItemsSource = new DataGridCollectionView(ManyRows());
            Settle(window);

            var count = grid.GetVisualDescendants().OfType<ScrollViewer>().Count();
            window.Close();
            return count;
        });

        Assert.Equal(0, found);
    }

    [Fact]
    public async Task An_anchor_item_can_be_saved_and_scrolled_back_to()
    {
        var (start, afterScroll, afterRestore) = await HeadlessUi.RunAsync<(string?, string?, string?)>(() =>
        {
            var items = ManyRows();
            var grid = NewGrid();
            var window = new Window { Width = 800, Height = 400, Content = grid };
            window.Show();
            grid.ItemsSource = new DataGridCollectionView(items);
            Settle(window);

            var first = TopVisibleItem(grid)?.Title;

            // Scroll a long way down, as a user would.
            grid.ScrollIntoView(items[300], null);
            Settle(window);
            var anchor = TopVisibleItem(grid);

            // Leave the view and come back: a rebind resets position to the top.
            grid.ItemsSource = new DataGridCollectionView(ManyRows());
            Settle(window);
            grid.ItemsSource = new DataGridCollectionView(items);
            Settle(window);
            var reset = TopVisibleItem(grid)?.Title;
            Assert.Equal(first, reset);

            // Restore by anchor. ScrollIntoView docks to the NEAREST edge, so approaching the
            // anchor from above would leave it at the BOTTOM of the viewport - a whole screen off.
            // Overshooting past it first means the final scroll approaches from below, which docks
            // it to the top, where it was.
            var target = items.First(i => i.Id == anchor!.Id);
            grid.ScrollIntoView(items[^1], null);
            Settle(window);
            grid.ScrollIntoView(target, null);
            Settle(window);
            var restored = TopVisibleItem(grid)?.Title;

            window.Close();
            return (first, anchor?.Title, restored);
        });

        Assert.Equal("Track 000", start);
        Assert.NotEqual(start, afterScroll);
        Assert.Equal(afterScroll, afterRestore);
    }
}
