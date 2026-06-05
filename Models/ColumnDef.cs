// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;

namespace OrgZ.Models;

public enum ColumnType
{
    Text,
    CheckBox,
    PlayIndicator,
    FavoriteTitle,
    Centered,
    RightAligned,
    Badge,
    Flag,
}

public record ColumnDef
{
    public required string Header { get; init; }
    public required string BindingPath { get; init; }
    public ColumnType Type { get; init; } = ColumnType.Text;
    public DataGridLengthUnitType WidthType { get; init; } = DataGridLengthUnitType.Auto;
    public double WidthValue { get; init; } = 1;
    public bool CanUserSort { get; init; } = true;
    public bool CanUserResize { get; init; } = true;
    public bool CanUserReorder { get; init; } = true;
    public string? StringFormat { get; init; }

    /// <summary>
    /// Whether this column is visible when the view loads for the first time with no
    /// saved user preference. Users can toggle visibility via the column-header right-
    /// click menu, and that override is persisted per view; this flag only controls
    /// the initial state. Default true so adding a column to a config doesn't silently
    /// hide it.
    /// </summary>
    public bool IsDefaultVisible { get; init; } = true;

    /// <summary>
    /// Stable identifier used to key saved column-state preferences (visibility, order)
    /// independent of the Header text (which is user-facing and could be renamed for
    /// localization). Defaults to <see cref="BindingPath"/> when not set explicitly —
    /// works for most columns since binding paths are already stable identifiers.
    /// </summary>
    public string Key => !string.IsNullOrEmpty(BindingPath) ? BindingPath : Header;

    /// <summary>
    /// Per-column font size override, in points. Null means "use DataGrid default" (about
    /// 14pt in Avalonia's theme). Applied to text-bearing column types
    /// (<see cref="ColumnType.Text"/>, <see cref="ColumnType.Centered"/>,
    /// <see cref="ColumnType.RightAligned"/>). The "#" track column uses this to squeeze
    /// "12 of 16" into a narrow column without wrapping.
    /// </summary>
    public double? FontSize { get; init; }

    /// <summary>
    /// Per-column letter spacing override, in pixels. Negative values tighten; positive
    /// loosens. Null means "use the font's default". Pairs with <see cref="FontSize"/>
    /// for dense, narrow columns where character-level kerning matters.
    /// </summary>
    public double? LetterSpacing { get; init; }
}
