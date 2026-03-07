// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Models;

internal class SidebarItem
{
    public string Name { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public bool IsEnabled { get; init; }
    public MediaKind? Kind { get; init; }
}
