// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// The guard that keeps AvaloniaUI/Avalonia#16233 from killing the process. The corrupt grid
/// state comes from a machine suspend, so it can't be produced in a test - what these pin is the
/// predicate: it has to recognise the real crash and refuse everything else, because a guard
/// that swallows unrelated exceptions hides real bugs.
/// </summary>
public sealed class DataGridRowGroupGuardTests
{
    /// <summary>An exception carrying a stack we choose, since we can't provoke the real one.</summary>
    private sealed class StackedException(string stack) : ArgumentOutOfRangeException("index")
    {
        public override string StackTrace { get; } = stack;
    }

    private sealed class StackedInvalidOp(string stack) : InvalidOperationException("nope")
    {
        public override string StackTrace { get; } = stack;
    }

    /// <summary>Verbatim from Fox's log, 2026-08-06 19:04:11, trimmed to the frames that matter.</summary>
    private const string ReportedStack = """
           at Avalonia.Controls.DataGrid.RemoveDisplayedElement(Control element, Int32 slot, Boolean wasDeleted, Boolean updateSlotInformation)
           at Avalonia.Controls.DataGrid.RemoveNonDisplayedRows(Int32 newFirstDisplayedSlot, Int32 newLastDisplayedSlot)
           at Avalonia.Controls.DataGrid.UpdateDisplayedRows(Int32 newFirstDisplayedSlot, Double displayHeight)
           at Avalonia.Controls.DataGrid.UpdateRowGroupVisibility(DataGridRowGroupInfo targetRowGroupInfo, Boolean newIsVisible, Boolean isDisplayed)
           at Avalonia.Controls.DataGrid.OnRowGroupHeaderToggled(DataGridRowGroupHeader groupHeader, Boolean newIsVisible, Boolean setCurrent)
           at Avalonia.Controls.DataGridRowGroupHeader.ToggleExpandCollapse(Boolean isVisible, Boolean setCurrent)
           at Avalonia.Controls.DataGridRowGroupHeader.DataGridRowGroupHeader_PointerPressed(PointerPressedEventArgs e)
        """;

    [Fact]
    public void The_reported_crash_is_recognised()
    {
        Assert.True(DataGridRowGroupGuard.IsKnownRowGroupToggleBug(new StackedException(ReportedStack)));
    }

    [Fact]
    public void A_different_exception_type_on_the_same_stack_is_not_swallowed()
    {
        // Only the index fault is the known bug. Anything else from that path is news.
        Assert.False(DataGridRowGroupGuard.IsKnownRowGroupToggleBug(new StackedInvalidOp(ReportedStack)));
    }

    [Fact]
    public void An_index_error_from_our_own_code_stays_fatal()
    {
        Assert.False(DataGridRowGroupGuard.IsKnownRowGroupToggleBug(new StackedException(
            "   at OrgZ.ViewModels.MainWindowViewModel.ApplyFilter(Boolean fromViewSwitch)")));
    }

    [Fact]
    public void An_index_error_elsewhere_in_the_datagrid_stays_fatal()
    {
        // Same control, different operation - column rebuild, not row-group toggling. That one
        // has its own handling in MediaGrid and must not be masked here.
        Assert.False(DataGridRowGroupGuard.IsKnownRowGroupToggleBug(new StackedException(
            "   at Avalonia.Controls.DataGridColumnCollection.InsertItem(Int32 index, DataGridColumn item)")));
    }

    [Fact]
    public void A_stackless_exception_is_not_matched()
    {
        Assert.False(DataGridRowGroupGuard.IsKnownRowGroupToggleBug(new ArgumentOutOfRangeException("index")));
        Assert.False(DataGridRowGroupGuard.IsKnownRowGroupToggleBug(null));
    }
}
