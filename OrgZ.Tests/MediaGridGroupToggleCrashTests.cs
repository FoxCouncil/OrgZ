// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.VisualTree;

namespace OrgZ.Tests;

/// <summary>
/// Expanding a seeded-collapsed row group at real Radio scale. Reported 2026-08-06: opening a
/// genre in the Radio view killed the process with an ArgumentOutOfRangeException raised inside
/// Avalonia's own DataGrid.RemoveDisplayedElement, reached from a pointer press on the header.
///
/// Scale is the whole point - the small fixtures in MediaGridGroupCollapseTests never virtualize,
/// so nothing is ever scrolled out of view and RemoveNonDisplayedRows never runs.
/// </summary>
[Collection(HeadlessUiCollection.Name)]
public sealed class MediaGridGroupToggleCrashTests
{
    private const int Genres = 30;
    private const int PerGenre = 23;   // 690 rows, close to the reported 697

    private static DataGridCollectionView RadioScale()
    {
        var items = new List<MediaItem>();
        for (var g = 0; g < Genres; g++)
        {
            for (var i = 0; i < PerGenre; i++)
            {
                items.Add(new MediaItem { Id = $"{g}-{i}", Kind = MediaKind.Radio, Title = $"Station {g}-{i}", Genre = $"Genre {g:00}" });
            }
        }
        var view = new DataGridCollectionView(items);
        view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(MediaItem.Genre)));
        return view;
    }

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

    /// <summary>Runs a scenario and returns the exception text, or null when it survived.</summary>
    private static async Task<string?> Scenario(Action<DataGrid, DataGridCollectionView, Window> afterSeed)
    {
        return await HeadlessUi.RunAsync(() =>
        {
            var view = RadioScale();
            var grid = NewGrid();
            var window = new Window { Width = 900, Height = 600, Content = grid };
            window.Show();

            grid.ItemsSource = view;
            Controls.MediaGrid.ApplyGroupExpansion(grid, view, _ => false);
            Settle(window);

            try
            {
                afterSeed(grid, view, window);
            }
            catch (Exception ex)
            {
                window.Close();
                return ex.GetType().Name + ": " + ex.Message;
            }

            window.Close();
            return null;
        });
    }

    [Fact]
    public async Task Expand_after_seeded_collapse()
    {
        var error = await Scenario((grid, view, window) =>
        {
            grid.ExpandRowGroup(view.Groups!.OfType<DataGridCollectionViewGroup>().First(), expandAllSubgroups: false);
            Settle(window);
        });
        Assert.Null(error);
    }

    [Fact]
    public async Task Expand_after_a_scroll_into_view_restore()
    {
        // What entering the view actually does: seed collapsed, then restore the remembered
        // scroll anchor before the user touches anything.
        var error = await Scenario((grid, view, window) =>
        {
            grid.ScrollIntoView(view.Cast<object>().Skip(400).First(), null);
            Settle(window);
            grid.ExpandRowGroup(view.Groups!.OfType<DataGridCollectionViewGroup>().First(), expandAllSubgroups: false);
            Settle(window);
        });
        Assert.Null(error);
    }

    [Fact]
    public async Task Expand_a_group_far_down_after_scrolling_there()
    {
        var error = await Scenario((grid, view, window) =>
        {
            var groups = view.Groups!.OfType<DataGridCollectionViewGroup>().ToList();
            grid.ScrollIntoView(groups[^1], null);
            Settle(window);
            grid.ExpandRowGroup(groups[^1], expandAllSubgroups: false);
            Settle(window);
        });
        Assert.Null(error);
    }

    [Fact]
    public async Task Expand_then_collapse_then_expand_again()
    {
        var error = await Scenario((grid, view, window) =>
        {
            var g = view.Groups!.OfType<DataGridCollectionViewGroup>().First();
            grid.ExpandRowGroup(g, expandAllSubgroups: false);
            Settle(window);
            grid.CollapseRowGroup(g, collapseAllSubgroups: false);
            Settle(window);
            grid.ExpandRowGroup(g, expandAllSubgroups: false);
            Settle(window);
        });
        Assert.Null(error);
    }

    [Fact]
    public async Task Expand_several_groups_in_a_row()
    {
        var error = await Scenario((grid, view, window) =>
        {
            foreach (var g in view.Groups!.OfType<DataGridCollectionViewGroup>().Take(6))
            {
                grid.ExpandRowGroup(g, expandAllSubgroups: false);
                Settle(window);
            }
        });
        Assert.Null(error);
    }

    [Fact]
    public async Task Expand_by_double_clicking_the_header()
    {
        // The reported path: DataGridRowGroupHeader_PointerPressed -> ToggleExpandCollapse.
        var error = await Scenario((grid, view, window) =>
        {
            var header = grid.GetVisualDescendants().OfType<DataGridRowGroupHeader>().First();
            var centre = ((Visual)header).TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)
                ?? new Point(400, 10);
            window.MouseDown(centre, Avalonia.Input.MouseButton.Left);
            window.MouseUp(centre, Avalonia.Input.MouseButton.Left);
            window.MouseDown(centre, Avalonia.Input.MouseButton.Left);
            window.MouseUp(centre, Avalonia.Input.MouseButton.Left);
            Settle(window);
        });
        Assert.Null(error);
    }
}
