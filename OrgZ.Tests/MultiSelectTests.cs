// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.ViewModels;

namespace OrgZ.Tests;

/// <summary>
/// Multi-select's ordering seam: grid selection order is CLICK order, and every
/// plural verb (burn, queue, add-to-playlist, drag) must receive VIEW order.
/// Verification proves the reorder; adversarial cases attack shift-click-upward
/// selections, items missing from the view, and degenerate inputs.
/// </summary>
public class MultiSelectTests
{
    private static MediaItem Track(string id) => new() { Id = id, Kind = MediaKind.Music, Title = id };

    // ── What a drag carries ───────────────────────────────────

    [Fact]
    public void Dragging_a_selected_row_carries_the_whole_selection()
    {
        var a = Track("a"); var b = Track("b"); var c = Track("c");

        // THE BUG: this used to arrive as one row. The press handler is on the tunnel
        // route and runs BEFORE the DataGrid collapses the selection to the row under the
        // cursor, so the drag has to resolve against the selection captured at press time
        // - reading it later gets the collapsed one.
        var payload = Views.MainWindow.DragPayload(b, [a, b, c]);

        Assert.Equal(new[] { a, b, c }, payload);
    }

    [Fact]
    public void Dragging_a_row_outside_the_selection_carries_only_that_row()
    {
        var a = Track("a"); var b = Track("b"); var c = Track("c");

        // Explorer/iTunes: grabbing a row you hadn't selected moves THAT row, and does
        // not drag your selection somewhere you didn't ask for.
        Assert.Equal(new[] { c }, Views.MainWindow.DragPayload(c, [a, b]));
    }

    [Fact]
    public void A_single_selected_row_still_drags_itself()
    {
        var a = Track("a");

        Assert.Equal(new[] { a }, Views.MainWindow.DragPayload(a, [a]));
    }

    [Fact]
    public void The_payload_keeps_the_view_order_it_was_given()
    {
        var a = Track("a"); var b = Track("b"); var c = Track("c");

        // The captured selection is already view-ordered (SelectedTracks does that);
        // the payload must not re-shuffle it into click order.
        Assert.Equal(new[] { a, b, c }, Views.MainWindow.DragPayload(a, [a, b, c]));
    }

    [Fact]
    public void A_drag_with_nothing_under_the_cursor_carries_nothing()
    {
        var a = Track("a");

        Assert.Empty(Views.MainWindow.DragPayload(null, [a]));
        Assert.Empty(Views.MainWindow.DragPayload(null, []));
    }

    [Fact]
    public void An_empty_selection_falls_back_to_the_pressed_row()
    {
        var a = Track("a");

        // Can happen when the press lands on a row the grid hasn't selected yet.
        Assert.Equal(new[] { a }, Views.MainWindow.DragPayload(a, []));
    }

    [Fact]
    public void The_payload_is_a_copy_so_later_selection_changes_cannot_rewrite_it()
    {
        var a = Track("a"); var b = Track("b");
        List<MediaItem> selection = [a, b];

        var payload = Views.MainWindow.DragPayload(a, selection);
        selection.Clear();   // the grid collapsing the selection mid-drag

        Assert.Equal(new[] { a, b }, payload);
    }

    // ── What the drag ghost says ──────────────────────────────

    private static MediaItem Song(string title, string? artist = null, string? fileName = null)
        => new() { Id = title, Kind = MediaKind.Music, Title = title, Artist = artist, FileName = fileName };

    [Fact]
    public void One_track_reads_as_itself()
    {
        Assert.Equal("Stop — Spice Girls", Views.MainWindow.DragGhostLabel([Song("Stop", "Spice Girls")]));
    }

    [Fact]
    public void Several_tracks_read_as_a_count()
    {
        // Five titles stacked under the cursor is unreadable at pointer size; the number
        // is what you check before letting go.
        Assert.Equal("5 tracks", Views.MainWindow.DragGhostLabel([Song("a"), Song("b"), Song("c"), Song("d"), Song("e")]));
        Assert.Equal("2 tracks", Views.MainWindow.DragGhostLabel([Song("a"), Song("b")]));
    }

    [Fact]
    public void A_track_missing_metadata_still_says_something_useful()
    {
        Assert.Equal("Untitled", Views.MainWindow.DragGhostLabel([Song("Untitled")]));                       // no artist
        Assert.Equal("track.mp3", Views.MainWindow.DragGhostLabel([Song(null!, "An Artist", "track.mp3")])); // no title
        Assert.Equal("1 track", Views.MainWindow.DragGhostLabel([Song(null!)]));                             // nothing at all
    }

    [Fact]
    public void Whitespace_metadata_is_treated_as_absent_not_rendered()
    {
        // A blank artist tag must not produce a dangling "Title — ".
        Assert.Equal("Stop", Views.MainWindow.DragGhostLabel([Song("Stop", "   ")]));
        Assert.Equal("fallback.mp3", Views.MainWindow.DragGhostLabel([Song("  ", "Someone", "fallback.mp3")]));
    }

    [Fact]
    public void An_empty_drag_has_no_label()
    {
        Assert.Equal("", Views.MainWindow.DragGhostLabel([]));
    }

    [Fact]
    public void Click_order_is_rewritten_to_view_order()
    {
        var a = Track("a"); var b = Track("b"); var c = Track("c"); var d = Track("d");
        List<MediaItem> view = [a, b, c, d];

        // User clicked d, then b, then a (ctrl-click hopping around).
        var ordered = MainWindowViewModel.OrderSelectionByView([d, b, a], view);

        Assert.Equal(new[] { a, b, d }, ordered);
    }

    [Fact]
    public void Shift_click_upward_selection_does_not_come_out_backwards()
    {
        var view = Enumerable.Range(0, 10).Select(i => Track($"t{i}")).ToList();

        // Anchor at row 7, shift-click row 2: many toolkits report the selection
        // bottom-up. A burn of this must still be rows 2..7 in order.
        var bottomUp = view.Skip(2).Take(6).Reverse().ToList();
        var ordered = MainWindowViewModel.OrderSelectionByView(bottomUp, view);

        Assert.Equal(view.Skip(2).Take(6).ToList(), ordered);
    }

    [Fact]
    public void Items_not_in_the_view_keep_selection_order_after_in_view_ones()
    {
        var a = Track("a"); var b = Track("b");
        var strayX = Track("x"); var strayY = Track("y");
        List<MediaItem> view = [a, b];

        var ordered = MainWindowViewModel.OrderSelectionByView([strayX, b, strayY, a], view);

        Assert.Equal(new[] { a, b, strayX, strayY }, ordered);
    }

    [Fact]
    public void Empty_selection_resolves_to_empty()
    {
        Assert.Empty(MainWindowViewModel.OrderSelectionByView([], [Track("a")]));
    }

    [Fact]
    public void Empty_view_preserves_selection_order()
    {
        var a = Track("a"); var b = Track("b");
        var ordered = MainWindowViewModel.OrderSelectionByView([b, a], []);
        Assert.Equal(new[] { b, a }, ordered);
    }

    [Fact]
    public void Duplicate_view_entries_do_not_break_ordering()
    {
        // A view should never contain the same reference twice, but a filter bug
        // must not turn into a KeyNotFound/duplicate-key crash here.
        var a = Track("a"); var b = Track("b");
        List<MediaItem> view = [a, b, a];

        var ordered = MainWindowViewModel.OrderSelectionByView([b, a], view);

        Assert.Equal(new[] { a, b }, ordered);
    }

    [Fact]
    public void Single_item_selection_is_untouched()
    {
        var a = Track("a");
        var ordered = MainWindowViewModel.OrderSelectionByView([a], [Track("z"), a]);
        Assert.Equal(new[] { a }, ordered);
    }
}
