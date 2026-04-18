// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Media.Imaging;

namespace OrgZ.Models;

internal class SidebarItem
{
    public string Name { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;

    /// <summary>
    /// Optional bitmap to render instead of the FontAwesome <see cref="Icon"/>. Used by
    /// device sidebar entries so each connected iPod shows its generation-specific
    /// product illustration in place of the generic music icon.
    /// </summary>
    public Bitmap? IconBitmap { get; init; }

    public bool HasIconBitmap => IconBitmap != null;

    public string Category { get; init; } = string.Empty;
    public bool IsEnabled { get; init; }
    public bool IsFavorites { get; init; }
    public MediaKind? Kind { get; init; }
    public string ViewConfigKey { get; init; } = string.Empty;
    public int? PlaylistId { get; init; }
    public bool IsNewPlaylistAction { get; init; }
    public bool IsImportPlaylistAction { get; init; }
}
