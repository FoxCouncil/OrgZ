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
}
