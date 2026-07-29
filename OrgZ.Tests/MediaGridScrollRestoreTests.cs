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
            .Select(i => new MediaItem { Id = i.ToString(), Kind = MediaKind.Music, Title = $"Track {i:000}", Album = $"Album {i / 20:00}" })
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
    public async Task Scrolling_from_inside_the_ItemsSource_change_notification_corrupts_the_grid()
    {
        // The crash: restoring scroll synchronously from the ItemsSource PropertyChanged handler
        // re-enters the DataGrid while it is still rebuilding its slot bookkeeping, and a later
        // layout pass then blows up in DataGridDisplayData.LoadScrollingSlot.
        var outcome = await HeadlessUi.RunAsync(() =>
        {
            var items = ManyRows();
            var grid = NewGrid();
            var window = new Window { Width = 800, Height = 400, Content = grid };
            window.Show();
            grid.ItemsSource = new DataGridCollectionView(ManyRows());
            Settle(window);

            DataGridCollectionView Grouped()
            {
                var v = new DataGridCollectionView(items);
                v.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(MediaItem.Album)));
                return v;
            }

            grid.PropertyChanged += (_, e) =>
            {
                if (e.Property != DataGrid.ItemsSourceProperty || grid.ItemsSource is not DataGridCollectionView v
                    || v.GroupDescriptions.Count == 0)
                {
                    return;
                }

                // Exactly the app's order: scroll the anchor into view, THEN collapse the groups -
                // so the scroll targets a layout that is about to be torn down underneath it.
                grid.UpdateLayout();
                grid.ScrollIntoView(items[320], null);

                foreach (var g in v.Groups!.OfType<DataGridCollectionViewGroup>().ToList())
                {
                    grid.CollapseRowGroup(g, collapseAllSubgroups: false);
                }
            };

            try
            {
                grid.ItemsSource = Grouped();
                Settle(window);
                return "ok";
            }
            catch (Exception ex)
            {
                return $"{ex.GetType().Name}: {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}";
            }
            finally
            {
                window.Close();
            }
        });

        Assert.NotEqual("ok", outcome);
    }

    [Fact]
    public async Task Scrolling_after_the_layout_pass_instead_is_safe()
    {
        // The fix: same work, moved out of the notification and onto the following layout pass -
        // which is what MainWindow.QueueViewStateRestore does. Collapse still happens inline
        // (that part is safe and has to be, or the groups flash open); only the scroll waits.
        var outcome = await HeadlessUi.RunAsync(() =>
        {
            var items = ManyRows();
            var grid = NewGrid();
            var window = new Window { Width = 800, Height = 400, Content = grid };
            window.Show();
            grid.ItemsSource = new DataGridCollectionView(ManyRows());
            Settle(window);

            grid.PropertyChanged += (_, e) =>
            {
                if (e.Property != DataGrid.ItemsSourceProperty || grid.ItemsSource is not DataGridCollectionView v
                    || v.GroupDescriptions.Count == 0)
                {
                    return;
                }

                foreach (var g in v.Groups!.OfType<DataGridCollectionViewGroup>().ToList())
                {
                    grid.CollapseRowGroup(g, collapseAllSubgroups: false);
                }

                EventHandler? once = null;
                once = (_, _) =>
                {
                    grid.LayoutUpdated -= once;
                    grid.ScrollIntoView(items[320], null);
                };
                grid.LayoutUpdated += once;
            };

            try
            {
                var grouped = new DataGridCollectionView(items);
                grouped.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(MediaItem.Album)));
                grid.ItemsSource = grouped;
                Settle(window);
                Settle(window);
                return "ok";
            }
            catch (Exception ex)
            {
                return $"{ex.GetType().Name}: {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}";
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("ok", outcome);
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

            // Restore in ONE downward scroll, with no overshoot.
            //
            // ScrollIntoView docks the target to the nearest edge. From the top of the list that
            // edge is the BOTTOM - so scrolling to the row that should END the viewport puts the
            // anchor exactly at its start. Scrolling to the anchor itself would leave it at the
            // bottom, a whole screen off; overshooting to the end and coming back lands it right
            // but moves the viewport twice, which reads as a jump to the bottom and a slam back.
            grid.UpdateLayout();
            var visible = grid.GetVisualDescendants().OfType<DataGridRow>().Count(r => r.IsVisible);
            var anchorIndex = items.FindIndex(i => i.Id == anchor!.Id);
            grid.ScrollIntoView(items[Math.Min(anchorIndex + visible - 1, items.Count - 1)], null);
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
