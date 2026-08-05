// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;

namespace OrgZ.Tests;

/// <summary>
/// OrgZ carried THREE media DataGrids (flat, radio-grouped, podcast-grouped) for one reason,
/// repeated in three comments and an enum doc: rebuilding a DataGrid's columns after a grouped
/// <see cref="DataGridCollectionView"/> has been bound crashes inside Avalonia's column
/// collection. Every grouped column set therefore got its own grid, built exactly once.
///
/// That claim was never tested - it came from a crash observed once. These tests pin it down:
/// what throws, from where, and which reset sequences survive it. The naive rebuild really does
/// crash (<see cref="Clearing_then_re_adding_columns_under_a_grouped_view_throws"/>), and the
/// sequence <see cref="MediaDataGrid"/> uses really does not.
/// </summary>
[Collection(HeadlessUiCollection.Name)]
public sealed class MediaGridColumnRebuildTests
{
    private static List<MediaItem> Tracks() =>
    [
        new() { Id = "1", Kind = MediaKind.Music,   Title = "A", Artist = "X", Genre = "Rock" },
        new() { Id = "2", Kind = MediaKind.Music,   Title = "B", Artist = "Y", Genre = "Rock" },
        new() { Id = "3", Kind = MediaKind.Podcast, Title = "C", Artist = "Z", Genre = "Talk" },
    ];

    private static DataGridCollectionView Grouped(string groupBy)
    {
        var view = new DataGridCollectionView(Tracks());
        view.GroupDescriptions.Add(new DataGridPathGroupDescription(groupBy));
        return view;
    }

    private static void AddColumns(DataGrid grid, params string[] paths)
    {
        foreach (var path in paths)
        {
            grid.Columns.Add(new DataGridTextColumn { Header = path, Binding = new Binding(path) });
        }
    }

    /// <summary>Lays the window out and pumps the render timer, so the grid really builds rows.</summary>
    private static void Settle(Window window)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
    }

    /// <summary>
    /// Runs a rebuild attempt against a live, laid-out, grouped grid and reports what happened,
    /// so each strategy below reads as one line.
    /// </summary>
    private static Task<string> RebuildUnderGrouping(Action<DataGrid, DataGridCollectionView> rebuild) =>
        HeadlessUi.RunAsync(() =>
        {
            var view = Grouped(nameof(MediaItem.Genre));
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                ItemsSource = view,
            };
            AddColumns(grid, nameof(MediaItem.Title), nameof(MediaItem.Artist));

            var window = new Window { Width = 800, Height = 600, Content = grid };
            window.Show();
            Settle(window);

            try
            {
                rebuild(grid, view);
                Settle(window);
                return $"ok:{grid.Columns.Count}";
            }
            catch (Exception ex)
            {
                return $"{ex.GetType().Name} :: {ex.StackTrace}";
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public async Task Clearing_then_re_adding_columns_under_a_grouped_view_throws()
    {
        // The exact sequence the old BuildColumnsOn performed on a view switch. This is the
        // crash the three-grid split existed to dodge; if Avalonia ever fixes it this test
        // fails and the workaround below can go.
        var outcome = await RebuildUnderGrouping((grid, _) =>
        {
            grid.Columns.Clear();
            AddColumns(grid, nameof(MediaItem.Title), nameof(MediaItem.Genre), nameof(MediaItem.Artist));
        });

        Assert.StartsWith("ArgumentOutOfRangeException", outcome, StringComparison.Ordinal);
        Assert.Contains("DataGridColumnCollection.InsertItem", outcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detaching_the_items_source_does_not_help()
    {
        // Worth recording, because it's the obvious guess and it's wrong: dropping ItemsSource
        // does not retire the spacer column, so Clear() is still unsafe afterwards.
        var outcome = await RebuildUnderGrouping((grid, view) =>
        {
            grid.ItemsSource = null;
            grid.Columns.Clear();
            AddColumns(grid, nameof(MediaItem.Title), nameof(MediaItem.Genre), nameof(MediaItem.Artist));
            grid.ItemsSource = view;
        });

        Assert.StartsWith("ArgumentOutOfRangeException", outcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_columns_one_at_a_time_makes_a_rebuild_safe()
    {
        // The root cause, from Avalonia's DataGridColumnCollection:
        //
        //   ClearItems()  empties ItemsInternal and DisplayIndexMap but never clears
        //                 RowGroupSpacerColumn.IsRepresented.
        //   InsertItem()  offsets by one when IsRepresented is true - so the first insert after
        //                 a Clear() on a GROUPED grid does ItemsInternal.Insert(1, col) into an
        //                 empty list, which is the ArgumentOutOfRangeException above.
        //
        // RemoveItem() applies the very same offset, so removing columns individually leaves
        // the spacer sitting at index 0 and the invariant intact. That's the whole workaround,
        // and it's why MediaDataGrid never calls Columns.Clear().
        var outcome = await RebuildUnderGrouping((grid, _) =>
        {
            while (grid.Columns.Count > 0)
            {
                grid.Columns.RemoveAt(grid.Columns.Count - 1);
            }

            AddColumns(grid, nameof(MediaItem.Title), nameof(MediaItem.Genre), nameof(MediaItem.Artist));
        });

        Assert.Equal("ok:3", outcome);
    }

    [Fact]
    public async Task A_flat_view_never_needed_the_workaround()
    {
        // Sanity check on the diagnosis: the flat grid rebuilt its columns on every view switch
        // for the app's whole life without crashing, so grouping has to be the trigger.
        var outcome = await HeadlessUi.RunAsync(() =>
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                ItemsSource = new DataGridCollectionView(Tracks()),
            };
            AddColumns(grid, nameof(MediaItem.Title), nameof(MediaItem.Artist));

            var window = new Window { Width = 800, Height = 600, Content = grid };
            window.Show();
            Settle(window);

            try
            {
                grid.Columns.Clear();
                AddColumns(grid, nameof(MediaItem.Title), nameof(MediaItem.Genre), nameof(MediaItem.Artist));
                Settle(window);
                return $"ok:{grid.Columns.Count}";
            }
            catch (Exception ex)
            {
                return ex.GetType().Name;
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("ok:3", outcome);
    }
}
