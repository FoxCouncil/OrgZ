// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Collections;
using Avalonia.Controls;

namespace OrgZ.Controls;

/// <summary>
/// The two operations a shared media DataGrid needs that Avalonia doesn't do correctly on its own.
///
/// OrgZ used to carry three DataGrids - flat, radio-grouped, podcast-grouped - purely to avoid
/// these. Each grouped grid built its columns exactly once and owned a dedicated collection view,
/// so neither operation ever had to happen. That cost a grid per column set, a matching
/// <c>ViewHost</c> member, a matching view-model property, and the rule that no grouped view may
/// ever share columns with another. Both behaviours are covered by
/// <c>MediaGridColumnRebuildTests</c> and <c>MediaGridGroupCollapseTests</c>.
/// </summary>
internal static class MediaGrid
{
    /// <summary>
    /// Replaces a grid's columns, safely, even when a grouped collection view is bound.
    ///
    /// <c>Columns.Clear()</c> cannot be used here. Avalonia's <c>DataGridColumnCollection</c>
    /// inserts a hidden spacer column at index 0 to indent row-group headers, and tracks it with
    /// <c>RowGroupSpacerColumn.IsRepresented</c>. <c>ClearItems()</c> empties the backing list but
    /// leaves that flag set, while <c>InsertItem()</c> offsets by one whenever it IS set - so the
    /// first column added after a Clear() lands at index 1 of an empty list and throws
    /// <see cref="ArgumentOutOfRangeException"/>. <c>RemoveItem()</c> applies the very same offset,
    /// so removing columns one at a time keeps the spacer in place and the invariant intact.
    /// </summary>
    internal static void ReplaceColumns(DataGrid grid, IEnumerable<DataGridColumn> columns)
    {
        while (grid.Columns.Count > 0)
        {
            grid.Columns.RemoveAt(grid.Columns.Count - 1);
        }

        foreach (var column in columns)
        {
            grid.Columns.Add(column);
        }
    }

    /// <summary>
    /// Applies per-group expanded/collapsed state to a grouped grid without letting an expanded
    /// frame paint first.
    ///
    /// A freshly bound grouped source realizes every group EXPANDED, and
    /// <see cref="DataGrid.CollapseRowGroup"/> silently no-ops until the grid has built its
    /// row-group info - which is why this used to be deferred to a background-priority dispatcher
    /// post, one frame later, and why entering a grouped view flashed open before snapping shut.
    /// A single synchronous <see cref="Layoutable.UpdateLayout"/> is enough to build that state,
    /// and the collapse then takes effect in the same dispatcher turn, so nothing renders in
    /// between.
    /// </summary>
    /// <param name="grid">The grid showing <paramref name="view"/>.</param>
    /// <param name="view">The bound collection view; ignored when it isn't grouped.</param>
    /// <param name="isExpanded">
    /// Decides each group by key. Called once per group, and may record unseen keys.
    /// </param>
    internal static void ApplyGroupExpansion(DataGrid grid, DataGridCollectionView? view, Func<string, bool> isExpanded)
    {
        if (view?.Groups is null || view.GroupDescriptions.Count == 0)
        {
            return;
        }

        // Without this the collapses below are thrown away - see the remarks above.
        grid.UpdateLayout();

        // Snapshot: expanding or collapsing mutates what the grid is showing, and Groups can be a
        // live projection over it.
        foreach (var group in view.Groups.OfType<DataGridCollectionViewGroup>().ToList())
        {
            var key = group.Key?.ToString() ?? string.Empty;
            if (isExpanded(key))
            {
                grid.ExpandRowGroup(group, expandAllSubgroups: false);
            }
            else
            {
                grid.CollapseRowGroup(group, collapseAllSubgroups: false);
            }
        }
    }
}
