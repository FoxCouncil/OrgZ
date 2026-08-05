// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.VisualTree;
using OrgZ.Controls;

namespace OrgZ.Tests;

/// <summary>
/// The behaviour the three-grid split was standing in for: one grid surviving the actual sequence
/// of view switches a user performs - flat to grouped to a differently-grouped view and back -
/// rebuilding its columns each time and landing collapsed, not expanded.
///
/// Every one of these switches was previously impossible on a single grid: the first would have
/// thrown out of Avalonia's column collection, and any that survived would have flashed open.
/// </summary>
[Collection(HeadlessUiCollection.Name)]
public sealed class MediaGridViewSwitchTests
{
    private sealed record ViewDef(string Key, string[] Columns, string? GroupBy);

    private static readonly ViewDef Music = new("Music", ["Title", "Artist", "Album"], null);
    private static readonly ViewDef Radio = new("Radio", ["Title", "Tags", "Country"], "Tags");
    private static readonly ViewDef Podcasts = new("Podcasts", ["Title", "Album", "Duration"], "Album");

    private static List<MediaItem> Items() =>
    [
        new() { Id = "1", Kind = MediaKind.Music, Title = "A", Artist = "X", Album = "LP1", Genre = "Rock", Tags = "Rock",     Country = "CA" },
        new() { Id = "2", Kind = MediaKind.Music, Title = "B", Artist = "Y", Album = "LP1", Genre = "Rock", Tags = "Rock",     Country = "CA" },
        new() { Id = "3", Kind = MediaKind.Radio, Title = "C", Artist = "Z", Album = "LP2", Genre = "Jazz", Tags = "Synthwave", Country = "JP" },
    ];

    private static DataGridCollectionView ViewFor(ViewDef def)
    {
        var view = new DataGridCollectionView(Items());
        if (def.GroupBy is { } path)
        {
            view.GroupDescriptions.Add(new DataGridPathGroupDescription(path));
            view.SortDescriptions.Add(DataGridSortDescription.FromPath(path, System.ComponentModel.ListSortDirection.Ascending));
        }

        return view;
    }

    private static int VisibleRows(DataGrid grid) => grid.GetVisualDescendants().OfType<DataGridRow>().Count(r => r.IsVisible);

    /// <summary>
    /// What MainWindow does on a view switch: rebuild columns, bind the new source, then apply
    /// collapse state - all in one dispatcher turn.
    /// </summary>
    private static void SwitchTo(DataGrid grid, ViewDef def, IDictionary<string, bool> expansion)
    {
        MediaGrid.ReplaceColumns(grid, def.Columns.Select(c => new DataGridTextColumn
        {
            Header = c,
            Binding = new Binding(c),
        }));

        var view = ViewFor(def);
        grid.ItemsSource = view;

        MediaGrid.ApplyGroupExpansion(grid, view, key =>
        {
            if (!expansion.TryGetValue($"{def.Key}/{key}", out var expanded))
            {
                expansion[$"{def.Key}/{key}"] = false;
                return false;
            }

            return expanded;
        });
    }

    [Fact]
    public async Task Switching_between_flat_and_two_differently_grouped_views_rebuilds_columns_every_time()
    {
        var trail = await HeadlessUi.RunAsync(() =>
        {
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true };
            var window = new Window { Width = 900, Height = 600, Content = grid };
            window.Show();

            var expansion = new Dictionary<string, bool>(StringComparer.Ordinal);
            var log = new List<string>();

            // Radio and Podcasts group by DIFFERENT paths with DIFFERENT columns - the pair that
            // used to need two separate grids because neither could rebuild over the other.
            foreach (var def in new[] { Music, Radio, Podcasts, Radio, Music, Podcasts })
            {
                SwitchTo(grid, def, expansion);
                window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                window.UpdateLayout();

                var headers = string.Join("/", grid.Columns.Select(c => c.Header));
                log.Add($"{def.Key}:{headers}:rows={VisibleRows(grid)}");
            }

            window.Close();
            return log;
        });

        Assert.Equal(
        [
            // Flat views show every row; grouped views land fully collapsed, first visit included.
            "Music:Title/Artist/Album:rows=3",
            "Radio:Title/Tags/Country:rows=0",
            "Podcasts:Title/Album/Duration:rows=0",
            "Radio:Title/Tags/Country:rows=0",
            "Music:Title/Artist/Album:rows=3",
            "Podcasts:Title/Album/Duration:rows=0",
        ], trail);
    }

    [Fact]
    public async Task A_group_the_user_expanded_stays_expanded_when_the_view_is_re_entered()
    {
        // The state the dedicated-source-per-grid trick was protecting. It now rides on the
        // persisted dictionary instead of on a grid that never rebinds, so it has to survive a
        // real rebind - including a trip through a differently-grouped view in between.
        var (before, after) = await HeadlessUi.RunAsync(() =>
        {
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true };
            var window = new Window { Width = 900, Height = 600, Content = grid };
            window.Show();

            var expansion = new Dictionary<string, bool>(StringComparer.Ordinal);

            SwitchTo(grid, Radio, expansion);
            window.UpdateLayout();

            // The user opens "Rock" (2 of the 3 items).
            expansion["Radio/Rock"] = true;
            SwitchTo(grid, Radio, expansion);
            window.UpdateLayout();
            var openedRows = VisibleRows(grid);

            SwitchTo(grid, Podcasts, expansion);
            window.UpdateLayout();

            SwitchTo(grid, Radio, expansion);
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
            var returnedRows = VisibleRows(grid);

            window.Close();
            return (openedRows, returnedRows);
        });

        Assert.Equal(2, before);
        Assert.Equal(2, after);
    }
}
