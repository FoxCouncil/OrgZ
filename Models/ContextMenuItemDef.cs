// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Models;

public record ContextMenuItemDef
{
    public string Header { get; init; } = string.Empty;
    public string? CommandName { get; init; }
    public bool IsHeader { get; init; }
    public bool IsSeparator { get; init; }
    public bool IsEnabled { get; init; } = true;
    public List<ContextMenuItemDef>? Children { get; init; }

    /// <summary>
    /// Marker: when set, the view will populate this submenu with the user's current playlists at build time.
    /// </summary>
    public bool IsAddToPlaylistMarker { get; init; }

    /// <summary>
    /// Marker: when set, the view will populate this submenu with rating choices (No Rating, 1-5 stars).
    /// </summary>
    public bool IsRatingMarker { get; init; }
}
