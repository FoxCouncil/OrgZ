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
