// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
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
[Collection(HeadlessUiCollection.Name)]
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
        // build the row-group info, and the collapse then takes effect in the same dispatcher
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

    [Fact]
    public async Task A_headers_expanded_pseudo_class_tracks_its_real_state()
    {
        // How the app records what the user did. It used to listen for Tapped and flip a stored
        // boolean, which desynced the moment a click landed anywhere that ISN'T the expander -
        // and a desynced entry then re-applied the wrong state on the next visit, re-opening a
        // group you had closed. Reading `:expanded` reads the truth instead of predicting it.
        var trail = await HeadlessUi.RunAsync(() =>
        {
            var view = TwoGroups();
            var grid = NewGrid();
            var window = new Window { Width = 800, Height = 600, Content = grid };
            window.Show();

            grid.ItemsSource = view;
            Settle(window);

            string State()
            {
                var pairs = grid.GetVisualDescendants().OfType<DataGridRowGroupHeader>()
                    .Select(h => $"{(h.DataContext as DataGridCollectionViewGroup)?.Key}={h.Classes.Contains(":expanded")}")
                    .OrderBy(s => s, StringComparer.Ordinal);
                return string.Join(",", pairs);
            }

            var rock = view.Groups!.OfType<DataGridCollectionViewGroup>().Single(g => (string?)g.Key == "Rock");
            var log = new List<string> { State() };

            grid.CollapseRowGroup(rock, collapseAllSubgroups: false);
            Settle(window);
            log.Add(State());

            grid.ExpandRowGroup(rock, expandAllSubgroups: false);
            Settle(window);
            log.Add(State());

            window.Close();
            return log;
        });

        Assert.Equal(["Jazz=True,Rock=True", "Jazz=True,Rock=False", "Jazz=True,Rock=True"], trail);
    }

    [Fact]
    public async Task A_single_click_on_a_group_header_does_not_toggle_it()
    {
        // The Avalonia behaviour that made the old tap-listener wrong, pinned so we notice if it
        // ever changes: DataGridRowGroupHeader toggles on its expander button or a DOUBLE click,
        // never on a plain single click of the header itself.
        var (before, after) = await HeadlessUi.RunAsync<(bool Before, bool After)>(() =>
        {
            var view = TwoGroups();
            var grid = NewGrid();
            var window = new Window { Width = 800, Height = 600, Content = grid };
            window.Show();

            grid.ItemsSource = view;
            Settle(window);

            var header = grid.GetVisualDescendants().OfType<DataGridRowGroupHeader>().First();
            var was = header.Classes.Contains(":expanded");

            // Click the middle of the header, well clear of the expander glyph on the left.
            var centre = ((Avalonia.Visual)header)
                .TranslatePoint(new Avalonia.Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)
                ?? new Avalonia.Point(400, 10);
            window.MouseDown(centre, Avalonia.Input.MouseButton.Left);
            window.MouseUp(centre, Avalonia.Input.MouseButton.Left);
            Settle(window);

            var now = header.Classes.Contains(":expanded");
            window.Close();
            return (was, now);
        });

        Assert.True(before);
        Assert.Equal(before, after);
    }
}
