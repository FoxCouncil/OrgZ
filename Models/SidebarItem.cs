// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OrgZ.Models;

internal partial class SidebarItem : ObservableObject
{
    /// <summary>Mutable + observable, unlike the rest of the row: a device rename updates the label
    /// in place (rebuilding the tree item would collapse it and drop selection).</summary>
    [ObservableProperty]
    private string _name = string.Empty;

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

    /// <summary>
    /// True for an indented child row in a flat section (e.g. "Subscriptions" under
    /// Podcasts in the LIBRARY list). The item template adds a left indent.
    /// </summary>
    public bool IsSubItem { get; init; }

    /// <summary>
    /// Nested sidebar entries rendered under this one. Currently used by the DEVICES
    /// section so a connected iPod expands to show its Playlists and future sub-views
    /// (browse, settings, sync). The device row itself is the music view, so Music is
    /// not listed as a child. The collection is mutable so the scan pipeline can add
    /// items (e.g. discovered playlists) after the initial expansion.
    /// Empty for leaf items; TreeView renders them without an expander chevron.
    /// </summary>
    public ObservableCollection<SidebarItem> Children { get; init; } = [];

    public bool HasChildren => Children.Count > 0;
}
