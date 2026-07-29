// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.VisualTree;

namespace OrgZ.Tests;

/// <summary>
/// The second reason the three-grid split existed: each grouped grid owned a dedicated
/// collection view so its row-group collapse state survived a view switch. Rebinding a grouped
/// source rebuilds every group EXPANDED, and the saved collapse state was re-applied from a
/// `Dispatcher.Post(..., Background)` - one frame later, which is the visible "flash".
///
/// A single shared grid has to rebind on every switch, so it can only work if collapse can be
/// applied before anything paints. These tests establish exactly when
/// <see cref="DataGrid.CollapseRowGroup"/> starts working.
/// </summary>
public sealed class MediaGridGroupCollapseTests
{
    private static DataGridCollectionView TwoGroups()
    {
        var view = new DataGridCollectionView(new List<MediaItem>
        {
            new() { Id = "1", Kind = MediaKind.Radio, Title = "A", Genre = "Rock" },
            new() { Id = "2", Kind = MediaKind.Radio, Title = "B", Genre = "Rock" },
            new() { Id = "3", Kind = MediaKind.Radio, Title = "C", Genre = "Jazz" },
        });
        view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(MediaItem.Genre)));
        return view;
    }

    private static DataGrid NewGrid() => new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        Columns = { new DataGridTextColumn { Header = "Title", Binding = new Binding(nameof(MediaItem.Title)) } },
    };

    /// <summary>
    /// Rows the user can actually see. Counting <see cref="DataGridRow"/>s in the visual tree is
    /// NOT the same thing and will mislead you: the DataGrid recycles row containers and leaves
    /// them parented with <c>IsVisible = false</c>, so a fully collapsed grid still reports a
    /// couple of them.
    /// </summary>
    private static int VisibleRows(DataGrid grid) => grid.GetVisualDescendants().OfType<DataGridRow>().Count(r => r.IsVisible);

    private static int GroupHeaders(DataGrid grid) => grid.GetVisualDescendants().OfType<DataGridRowGroupHeader>().Count();

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
    }

    [Fact]
    public async Task A_freshly_bound_grouped_source_realizes_its_rows_expanded()
    {
        // The premise. If this ever stops being true, the flash is gone and so is the need for
        // everything below it.
        var (rows, headers) = await HeadlessUi.RunAsync(() =>
        {
            var grid = NewGrid();
            var window = new Window { Width = 800, Height = 600, Content = grid };
            window.Show();

            grid.ItemsSource = TwoGroups();
            Settle(window);

            var result = (VisibleRows(grid), GroupHeaders(grid));
            window.Close();
            return result;
        });

        Assert.Equal(2, headers);
        Assert.Equal(3, rows);
    }

    [Fact]
    public async Task Collapsing_before_any_layout_pass_silently_does_nothing()
    {
        // Why the old code had to defer: the DataGrid hasn't built its row-group info yet, so
        // CollapseRowGroup no-ops and the visual stays expanded while our dictionary says
        // collapsed - the desync the existing comment warns about.
        var rows = await HeadlessUi.RunAsync(() =>
        {
            var grid = NewGrid();
            var window = new Window { Width = 800, Height = 600, Content = grid };
            window.Show();

            var view = TwoGroups();
            grid.ItemsSource = view;
            foreach (var group in view.Groups!.OfType<DataGridCollectionViewGroup>())
            {
                grid.CollapseRowGroup(group, collapseAllSubgroups: false);
            }

            Settle(window);

            var result = VisibleRows(grid);
            window.Close();
            return result;
        });

        // Every row that realized is still on screen - the collapse was thrown away.
        Assert.NotEqual(0, rows);
    }

    [Fact]
    public async Task Collapsing_after_a_synchronous_layout_pass_works_with_nothing_painted_in_between()
    {
        // The finding the shared grid rests on: one synchronous UpdateLayout() is enough to
        // build the row-group info, and the collapse then takes effect in the SAME dispatcher
        // turn. Nothing renders expanded, so there is no frame to flash - which is what lets a
        // single MediaDataGrid rebind on every view switch instead of hiding three grids.
        var (rows, headers) = await HeadlessUi.RunAsync(() =>
        {
            var grid = NewGrid();
            var window = new Window { Width = 800, Height = 600, Content = grid };
            window.Show();

            var view = TwoGroups();
            grid.ItemsSource = view;
            grid.UpdateLayout();

            foreach (var group in view.Groups!.OfType<DataGridCollectionViewGroup>())
            {
                grid.CollapseRowGroup(group, collapseAllSubgroups: false);
            }

            Settle(window);

            var result = (VisibleRows(grid), GroupHeaders(grid));
            window.Close();
            return result;
        });

        Assert.Equal(2, headers);
        Assert.Equal(0, rows);
    }
}
