// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;

namespace OrgZ.Models;

/// <summary>
/// Which top-level control hosts a view.
///
/// There used to be three grid hosts rather than one, because rebuilding a DataGrid's columns
/// after a grouped collection view was bound crashed inside Avalonia's column collection, so each
/// distinct grouped column set needed a grid of its own, built exactly once. That crash has a
/// cause and a workaround now - see <c>MediaGrid.SetColumns</c> and
/// <c>MediaGridColumnRebuildTests</c> - so one grid serves every view, and whether it groups is
/// decided by <see cref="ListViewConfig.GroupByPath"/> alone.
/// </summary>
public enum ViewHost
{
    /// <summary>The shared media grid - every list view, grouped or flat.</summary>
    Grid,
    /// <summary>The Podcasts panel UserControl - no DataGrid at all.</summary>
    PodcastsPanel,
    /// <summary>The Audiobooks composite: the golden store panel over the library audiobooks grid.</summary>
    AudiobooksPanel,
}

public record ListViewConfig
{
    public required string Key { get; init; }
    public required List<ColumnDef> Columns { get; init; }
    public required Func<MediaItem, bool> BaseFilter { get; init; }
    public required Func<MediaItem, string, bool> SearchFilter { get; init; }
    public required List<ContextMenuItemDef> ContextMenuItems { get; init; }
    public bool ShowRadioFilterPanel { get; init; }
    public string? DefaultSortColumn { get; init; }
    public bool DefaultSortDescending { get; init; }
    public Func<IEnumerable<MediaItem>, IEnumerable<MediaItem>>? Sorter { get; init; }
    public int? PlaylistId { get; init; }

    /// <summary>
    /// Whether this view shows an editable "checked" box per row (iTunes' include-in-
    /// playback/sync tick). Library-ish views want it; device, CD, and share views
    /// don't - their contents aren't ours to include or exclude.
    /// </summary>
    public bool ShowCheckedColumn { get; init; }

    /// <summary>
    /// When set, the view wraps FilteredItems in a DataGridCollectionView grouped by this property path.
    /// Avalonia's DataGrid renders collapsible group headers automatically.
    /// </summary>
    public string? GroupByPath { get; init; }

    /// <summary>The control that hosts this view - see <see cref="ViewHost"/>.</summary>
    public ViewHost Host { get; init; } = ViewHost.Grid;
}

public static class ListViewConfigs
{
    // Concurrent, not a plain Dictionary. Device connect/disconnect registers and removes
    // view configs while other code reads them, and the test suite drives Register/Remove
    // from several classes xunit runs in parallel. Concurrent write+read on a plain
    // Dictionary is undefined behaviour: torn enumeration, spurious
    // InvalidOperationException, or silent corruption.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ListViewConfig> _configs = new()
    {
        ["Music"] = BuildMusicConfig(),
        ["Radio"] = BuildRadioConfig(),
        ["Favorites"] = BuildFavoritesConfig(),
        ["BadFormat"] = BuildBadFormatConfig(),
        ["CdAudio"] = BuildCdAudioConfig(),
        ["Podcasts"] = BuildPodcastsConfig(),
        ["Audiobooks"] = BuildAudiobooksConfig(),
    };

    private static ListViewConfig BuildAudiobooksConfig()
    {
        return new ListViewConfig
        {
            Key = "Audiobooks",
            Columns =
            [
                new ColumnDef { Header = "", BindingPath = "IsPlaying", Type = ColumnType.PlayIndicator, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 30, CanUserSort = false, CanUserResize = false, CanUserReorder = false },
                new ColumnDef { Header = "Title", BindingPath = "Title", Type = ColumnType.FavoriteTitle, WidthType = DataGridLengthUnitType.Star, WidthValue = 1.5 },
                // Audiobook vocabulary over the same tag fields: Artist carries the author, Album
                // the book (load-bearing for chapter-per-file MP3 rips), Composer the narrator -
                // the iTunes tagging convention .m4b files follow.
                new ColumnDef { Header = "Author", BindingPath = "Artist", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Book", BindingPath = "Album", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Narrator", BindingPath = "Composer", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                // Books run hours, so the duration format carries the hours place the Music view drops.
                new ColumnDef { Header = "Duration", BindingPath = "Duration", Type = ColumnType.Centered, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 90, StringFormat = "h\\:mm\\:ss" },
                new ColumnDef { Header = "Year", BindingPath = "Year", WidthType = DataGridLengthUnitType.Pixel, WidthValue = 60, Type = ColumnType.Centered },
                // Default-hidden - toggle via the column-header right-click menu.
                new ColumnDef { Header = "Plays", BindingPath = "PlayCount", Type = ColumnType.RightAligned, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 60, IsDefaultVisible = false },
                new ColumnDef { Header = "Extension", BindingPath = "Extension", IsDefaultVisible = false },
                new ColumnDef { Header = "Has Album Art", BindingPath = "HasAlbumArt", Type = ColumnType.CheckBox, IsDefaultVisible = false },
                new ColumnDef { Header = "Rating", BindingPath = "RatingDisplay", Type = ColumnType.Rating, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 110, CanUserSort = false },
            ],
            // Local library audiobooks only - a connected iPod's audiobooks belong to its own
            // device Audiobooks node, the same partition the Music view keeps.
            BaseFilter = item => item.Kind == MediaKind.Audiobook && IsLocalLibraryItem(item),
            SearchFilter = (item, search) =>
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Album?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Composer?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.FileName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false),
            ContextMenuItems = BuildAudiobookContextMenu(),
            Host = ViewHost.AudiobooksPanel,
        };
    }

    /// <summary>The library-track menu minus "Burn to CD..." - a multi-hour book has no audio-CD story.
    /// (Removing from the library IS deleting from disk; the store can re-download a book.)</summary>
    private static List<ContextMenuItemDef> BuildAudiobookContextMenu() => Menu(
        Header(),
        Playback(),
        Info(ratable: true),
        Organize(),
        Items(Cmd("Show in Explorer", "ShowInExplorer"), Cmd("Remove from Library", "RemoveFromLibrary")));

    private static ListViewConfig BuildPodcastsConfig()
    {
        // Podcasts uses the PodcastsPanel UserControl instead of a DataGrid. The
        // config exists so ApplyViewConfig + state restoration find a record for
        // the key; columns / filters are inert (the panel manages its own data).
        return new ListViewConfig
        {
            Key = "Podcasts",
            Columns = [],
            BaseFilter = static _ => false,
            SearchFilter = static (_, _) => false,
            ContextMenuItems = [],
            Host = ViewHost.PodcastsPanel,
        };
    }

    public static ListViewConfig? Get(string? key)
    {
        if (key == null)
        {
            return null;
        }

        return _configs.GetValueOrDefault(key);
    }

    public static void Register(string key, ListViewConfig config)
    {
        _configs[key] = config;
    }

    public static void Remove(string key)
    {
        _configs.TryRemove(key, out _);
    }

    /// <summary>
    /// Removes a view config together with its whole sub-view family ("{key}:Podcast",
    /// "{key}:Playlist:{id}", ...). Device teardown uses this so a different iPod arriving at the same
    /// mount path can't inherit the departed device's per-kind / per-playlist configs. Boundary-aware:
    /// "Device:/media/ipod" leaves "Device:/media/ipod-red"'s family alone.
    /// </summary>
    public static void RemoveWithSubViews(string key)
    {
        _configs.TryRemove(key, out _);
        var prefix = $"{key}:";
        foreach (var sub in _configs.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _configs.TryRemove(sub, out _);
        }
    }

    public static ListViewConfig BuildDeviceConfig(string mountPath)
    {
        var source = $"device:{mountPath}";
        var key = $"Device:{mountPath}";

        // The device (iPod/Rockbox) track grid uses the same columns as the local Music
        // view so the two read identically.
        var columns = MusicColumns();

        return new ListViewConfig
        {
            Key = key,
            Columns = columns,
            BaseFilter = item => item.Source == source && item.Kind == MediaKind.Music,
            SearchFilter = (item, search) =>
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Album?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.FileName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false),
            ContextMenuItems = BuildDeviceContextMenu(),
        };
    }

    /// <summary>
    /// A remote OrgZ library share, mounted read-only. Same columns as Music so it reads
    /// identically, but the context menu carries only playback verbs - a share offers no
    /// files to edit, sync, or delete, and the menu must not imply otherwise.
    /// </summary>
    public static ListViewConfig BuildShareConfig(string shareKey)
    {
        var source = $"share:{shareKey}";

        return new ListViewConfig
        {
            Key = $"Share:{shareKey}",
            Columns = ShareColumns(),
            BaseFilter = item => item.Source == source,
            SearchFilter = (item, search) =>
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Album?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false),
            ContextMenuItems = BuildShareContextMenu(),
        };
    }

    /// <summary>Playback only - a read-only share has nothing to mutate.</summary>
    private static List<ContextMenuItemDef> BuildShareContextMenu() => Menu(
        Header(withArtist: true),
        Items(Cmd("Play", "Play"), Cmd("Play Next", "PlayNext"), Cmd("Add to Queue", "AddToQueue")),
        Items(Cmd("Get Info", "GetInfo")));

    /// <summary>
    /// One remote playlist under a share: the same ordered-ids view a device playlist
    /// uses, wearing the share's columns and its playback-only menu.
    /// </summary>
    public static ListViewConfig BuildSharePlaylistConfig(string viewKey, IReadOnlyList<string> orderedTrackIds)
    {
        var idSet = new HashSet<string>(orderedTrackIds);
        var orderMap = new Dictionary<string, int>(orderedTrackIds.Count);
        for (int i = 0; i < orderedTrackIds.Count; i++)
        {
            orderMap[orderedTrackIds[i]] = i;
        }

        return new ListViewConfig
        {
            Key = viewKey,
            Columns = ShareColumns(),
            BaseFilter = item => idSet.Contains(item.Id),
            SearchFilter = (item, search) =>
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Album?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false),
            ContextMenuItems = BuildShareContextMenu(),
            Sorter = items => items.OrderBy(item => orderMap.TryGetValue(item.Id, out var idx) ? idx : int.MaxValue),
        };
    }

    /// <summary>
    /// Device sub-view filtered to one media kind (the Podcasts / Audiobooks nodes under a device).
    /// Populated for real now that both readers tag tracks by kind (binary MHIT media_type at 0xD0 +
    /// Nano 5G media_kind). The Podcasts node groups episodes into one collapsible header per show
    /// (Album carries the show name, matching the iPod's own Podcasts submenu) on the dedicated
    /// PodcastGroupedDataGrid; Audiobooks reuses the flat device-music grid.
    /// </summary>
    public static ListViewConfig BuildDeviceKindConfig(string mountPath, MediaKind kind)
    {
        var source = $"device:{mountPath}";
        var key = $"Device:{mountPath}:{kind}";
        var isPodcast = kind == MediaKind.Podcast;

        // Podcast episodes: the show becomes the group header, so the row shows just the episode
        // title + duration. Everything else falls back to the full device-music column set.
        List<ColumnDef> columns = isPodcast
            ?
            [
                new ColumnDef { Header = "", BindingPath = "IsPlaying", Type = ColumnType.PlayIndicator, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 30, CanUserSort = false, CanUserResize = false, CanUserReorder = false },
                new ColumnDef { Header = "Episode", BindingPath = "Title", WidthType = DataGridLengthUnitType.Star, WidthValue = 2 },
                new ColumnDef { Header = "Duration", BindingPath = "Duration", Type = ColumnType.Centered, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 90, StringFormat = "h\\:mm\\:ss" },
            ]
            : MusicColumns();

        return new ListViewConfig
        {
            Key = key,
            Columns = columns,
            BaseFilter = item => item.Source == source && item.Kind == kind,
            SearchFilter = (item, search) =>
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Album?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false),
            ContextMenuItems = BuildDeviceContextMenu(),
            // Group podcast episodes by show (Album), one collapsible header per show. Audiobooks stay flat.
            GroupByPath = isPodcast ? "Album" : null,
        };
    }

    /// <summary>
    /// View config for a single device playlist (one tree leaf under the device's
    /// Playlists node). Filters the main items list by the given Id set, preserving the
    /// order from the source playlist so "Track 1, Track 2..." matches the device's own
    /// display order rather than the library's sort.
    /// </summary>
    public static ListViewConfig BuildDevicePlaylistConfig(string viewKey, IReadOnlyList<string> orderedTrackIds)
    {
        var idSet = new HashSet<string>(orderedTrackIds);
        var orderMap = new Dictionary<string, int>(orderedTrackIds.Count);
        for (int i = 0; i < orderedTrackIds.Count; i++)
        {
            orderMap[orderedTrackIds[i]] = i;
        }

        return new ListViewConfig
        {
            Key = viewKey,
            Columns =
            [
                new() { Header = "", BindingPath = "IsPlaying", Type = ColumnType.PlayIndicator, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 30, CanUserSort = false, CanUserResize = false, CanUserReorder = false },
                new() { Header = "#", BindingPath = "Track", WidthType = DataGridLengthUnitType.Pixel, WidthValue = 40, Type = ColumnType.Centered },
                new() { Header = "Title", BindingPath = "Title", WidthType = DataGridLengthUnitType.Star, WidthValue = 2 },
                new() { Header = "Artist", BindingPath = "Artist", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new() { Header = "Album", BindingPath = "Album", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new() { Header = "Duration", BindingPath = "Duration", Type = ColumnType.Centered, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 80, StringFormat = "m\\:ss" },
            ],
            BaseFilter = item => idSet.Contains(item.Id),
            SearchFilter = (item, search) =>
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Album?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false),
            ContextMenuItems = BuildDeviceContextMenu(),
            Sorter = items => items.OrderBy(item => orderMap.TryGetValue(item.Id, out var idx) ? idx : int.MaxValue),
        };
    }

    /// <summary>
    /// View config for the "Playlists" parent node under a connected device. Currently
    /// a placeholder - clicking it doesn't show a master grid; the tree's route-to-first-
    /// child behavior in the Sidebar.axaml.cs handler navigates to the first playlist
    /// instead when the user clicks this row.
    /// </summary>
    public static ListViewConfig BuildDevicePlaylistsConfig(string mountPath)
    {
        var key = $"Device:{mountPath}:Playlists";

        return new ListViewConfig
        {
            Key = key,
            Columns =
            [
                new() { Header = "Playlist", BindingPath = "Title", WidthType = DataGridLengthUnitType.Star, WidthValue = 2 },
                new() { Header = "Tracks", BindingPath = "TotalTracks", Type = ColumnType.RightAligned, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 80 },
                new() { Header = "Duration", BindingPath = "Duration", Type = ColumnType.Centered, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 100, StringFormat = "h\\:mm\\:ss" },
            ],
            // THE SPEC, not a stub: this node is a pure navigation container. Clicking it routes to
            // its first playlist child (Sidebar.axaml.cs), so its own grid renders only when the
            // device genuinely has zero playlists - where an empty grid is the correct answer. The
            // filter therefore rejects everything by design; a playlist master LIST view (rows =
            // playlists, not tracks) is a separate roadmap item and would need its own row type.
            BaseFilter = _ => false,
            SearchFilter = (item, search) =>
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false),
            ContextMenuItems = [],
        };
    }

    private static List<ContextMenuItemDef> BuildDeviceContextMenu() => Menu(
        Header(),
        Playback(),
        Info(),
        Items(new ContextMenuItemDef { Header = "Sync to Library", IsSyncToLibraryMarker = true },
              Cmd("Remove from iPod", "RemoveFromDevice")));

    public static ListViewConfig BuildPlaylistConfig(int playlistId, List<string> orderedTrackIds)
    {
        var idSet = new HashSet<string>(orderedTrackIds);
        var orderMap = new Dictionary<string, int>(orderedTrackIds.Count);
        for (int i = 0; i < orderedTrackIds.Count; i++)
        {
            orderMap[orderedTrackIds[i]] = i;
        }

        return new ListViewConfig
        {
            Key = $"Playlist:{playlistId}",
            PlaylistId = playlistId,
            ShowCheckedColumn = true,
            // The library's columns, plus the position column in front. A playlist showed
            // Title/Artist/Album/Rating and nothing else, so Track #, Duration and Year were
            // missing from a view that is otherwise the same list of the same songs.
            Columns =
            [
                CheckedColumn(),
                PlaylistPositionColumn(),
                .. MusicColumns().Where(c => c.BindingPath != "IsChecked"),
            ],
            BaseFilter = item => idSet.Contains(item.Id),
            SearchFilter = (item, search) =>
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Album?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false),
            ContextMenuItems = BuildPlaylistContextMenu(),

            // Stamps each row's position as it orders them, so the "#" column has something to
            // show and something to sort on. Sorting by "#" is what gets playlist order back
            // after a header sort - without it, clicking Artist replaced the user's order with
            // no way to return to it.
            Sorter = items =>
            {
                var ordered = items
                    .OrderBy(item => orderMap.TryGetValue(item.Id, out var idx) ? idx : int.MaxValue)
                    .ToList();

                for (var i = 0; i < ordered.Count; i++)
                {
                    ordered[i].PlaylistPosition = orderMap.TryGetValue(ordered[i].Id, out var idx) ? idx + 1 : null;
                }

                return ordered;
            },
        };
    }

    /// <summary>
    /// The standard music-track columns, shared by the local-library "Music" view and the
    /// device (iPod) track view so they read identically. Returns fresh ColumnDef instances
    /// each call - per-view column visibility/width prefs mutate these and must not be shared.
    /// </summary>
    /// <summary>
    /// The iTunes row tick: included in play-through and sync when checked. Editable,
    /// unsortable, and pinned narrow at the front like iTunes had it.
    /// </summary>
    /// <summary>
    /// The playlist's own running order. Sortable on purpose: it is the way back to playlist
    /// order once a header sort has been applied.
    /// </summary>
    internal static ColumnDef PlaylistPositionColumn() =>
        new() { Header = "#", BindingPath = "PlaylistPosition", Type = ColumnType.RightAligned, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 44, FontSize = 11, CanUserReorder = false };

    internal static ColumnDef CheckedColumn() =>
        new() { Header = "", BindingPath = "IsChecked", Type = ColumnType.RowCheck, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 28, CanUserSort = false, CanUserResize = false, CanUserReorder = false };

    /// <param name="withChecked">
    /// Library-ish views (Music, Favorites, playlists) get the iTunes tick; device, CD,
    /// and share views don't - their contents aren't ours to include or exclude.
    /// </param>
    private static List<ColumnDef> MusicColumns(bool withChecked = false) =>
    [
        .. withChecked ? new[] { CheckedColumn() } : [],
        new ColumnDef { Header = "", BindingPath = "IsPlaying", Type = ColumnType.PlayIndicator, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 30, CanUserSort = false, CanUserResize = false, CanUserReorder = false },
        new ColumnDef { Header = "Title", BindingPath = "Title", Type = ColumnType.FavoriteTitle, WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
        new ColumnDef { Header = "Artist", BindingPath = "Artist", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
        new ColumnDef { Header = "Track #", BindingPath = "TrackDisplay", WidthType = DataGridLengthUnitType.Pixel, WidthValue = 65, Type = ColumnType.RightAligned, FontSize = 11, LetterSpacing = -0.5 },
        new ColumnDef { Header = "Album", BindingPath = "Album", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
        new ColumnDef { Header = "Duration", BindingPath = "Duration", Type = ColumnType.Centered, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 80, StringFormat = "m\\:ss" },
        new ColumnDef { Header = "Year", BindingPath = "Year", WidthType = DataGridLengthUnitType.Pixel, WidthValue = 60, Type = ColumnType.Centered },
        // Default-hidden columns - toggle via the column-header right-click menu.
        new ColumnDef { Header = "Plays", BindingPath = "PlayCount", Type = ColumnType.RightAligned, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 60, IsDefaultVisible = false },
        new ColumnDef { Header = "Extension", BindingPath = "Extension", IsDefaultVisible = false },
        new ColumnDef { Header = "Has Album Art", BindingPath = "HasAlbumArt", Type = ColumnType.CheckBox, IsDefaultVisible = false },
        new ColumnDef { Header = "Rating", BindingPath = "RatingDisplay", Type = ColumnType.Rating, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 110, CanUserSort = false },
    ];

    /// <summary>
    /// Music's columns with the two interactive ones removed: a read-only share offers no
    /// star to set and no rating to store, and a control that silently does nothing is
    /// worse than no control at all.
    /// </summary>
    private static List<ColumnDef> ShareColumns() =>
    [
        .. MusicColumns()
            .Where(c => c.Type != ColumnType.Rating)
            .Select(c => c.Type != ColumnType.FavoriteTitle
                ? c
                : new ColumnDef { Header = c.Header, BindingPath = c.BindingPath, WidthType = c.WidthType, WidthValue = c.WidthValue }),
    ];

    /// <summary>
    /// True for items that belong to this library - not a CD in the drive, not a connected
    /// device, not a mounted network share. Every local view filters on it; without the
    /// share clause a mounted share dumps its whole catalogue into Music and Bad Format.
    /// </summary>
    internal static bool IsLocalLibraryItem(MediaItem item)
        => item.Source != "cdda"
           && (item.Source == null
               || (!item.Source.StartsWith("device:", StringComparison.Ordinal)
                   && !item.Source.StartsWith("share:", StringComparison.Ordinal)));

    private static ListViewConfig BuildMusicConfig()
    {
        return new ListViewConfig
        {
            Key = "Music",
            Columns = MusicColumns(withChecked: true),
            ShowCheckedColumn = true,
            // Local library view - must NOT include CD-audio tracks (Source="cdda"),
            // connected-device tracks (Source="device:{mountPath}"), or a mounted
            // share's catalogue (Source="share:{host}:{port}").  Those have their own
            // sidebar entries and their own views.  Without this, typing in the search
            // box while the Music tab is selected also matches iPod tracks and leaks
            // them into the local results list.
            BaseFilter = item => item.Kind == MediaKind.Music && IsLocalLibraryItem(item),
            SearchFilter = (item, search) =>
                (item.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Album?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.FileName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Year?.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ?? false),
            ContextMenuItems = BuildMusicContextMenu(),
        };
    }

    private static ListViewConfig BuildRadioConfig()
    {
        return new ListViewConfig
        {
            Key = "Radio",
            Columns =
            [
                new ColumnDef { Header = "", BindingPath = "IsPlaying", Type = ColumnType.PlayIndicator, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 30, CanUserSort = false, CanUserResize = false, CanUserReorder = false },
                new ColumnDef { Header = "Stream", BindingPath = "Title", Type = ColumnType.FavoriteTitle, WidthType = DataGridLengthUnitType.Star, WidthValue = 2 },
                new ColumnDef { Header = "Country", BindingPath = "CountryCode", Type = ColumnType.Flag, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 60 },
                new ColumnDef { Header = "Bit Rate", BindingPath = "BitrateLabel", Type = ColumnType.RightAligned, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 70, FontSize = 11 },
                new ColumnDef { Header = "Codec", BindingPath = "CodecLabel", Type = ColumnType.Badge, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 70 },
            ],
            BaseFilter = item => item.Kind == MediaKind.Radio,
            SearchFilter = (item, search) =>
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Tags?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Country?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false),
            ContextMenuItems = BuildRadioContextMenu(),
            ShowRadioFilterPanel = true,
            // Tags carries the canonical RadioGenre display name set by the
            // bundled-stations loader. Grouping by it gives clean headers
            // matching the OrgZ taxonomy ("Alternative Rock", "Synthwave",
            // etc.) without going through GenreNormalizer's fuzzy rules.
            GroupByPath = "Tags",
        };
    }

    private static ListViewConfig BuildBadFormatConfig()
    {
        return new ListViewConfig
        {
            Key = "BadFormat",
            Columns =
            [
                new ColumnDef { Header = "", BindingPath = "IsPlaying", Type = ColumnType.PlayIndicator, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 30, CanUserSort = false, CanUserResize = false, CanUserReorder = false },
                new ColumnDef { Header = "Title", BindingPath = "Title", Type = ColumnType.FavoriteTitle, WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Artist", BindingPath = "Artist", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Album", BindingPath = "Album", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Extension", BindingPath = "Extension", WidthType = DataGridLengthUnitType.Pixel, WidthValue = 70 },
                new ColumnDef { Header = "Reason", BindingPath = "FormatIssues", WidthType = DataGridLengthUnitType.Star, WidthValue = 2 },
            ],
            // Bad Format is a to-do list for files this library can actually fix - a
            // device's or a share's tracks aren't ours to retag.
            BaseFilter = item => item.Kind == MediaKind.Music && IsLocalLibraryItem(item) && !string.IsNullOrEmpty(item.FormatIssues),
            SearchFilter = (item, search) =>
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Album?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.FormatIssues.Contains(search, StringComparison.OrdinalIgnoreCase)),
            ContextMenuItems = BuildMusicContextMenu(),
        };
    }

    private static ListViewConfig BuildCdAudioConfig()
    {
        return new ListViewConfig
        {
            Key = "CdAudio",
            Columns =
            [
                new ColumnDef { Header = "", BindingPath = "IsPlaying", Type = ColumnType.PlayIndicator, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 30, CanUserSort = false, CanUserResize = false, CanUserReorder = false },
                new ColumnDef { Header = "#", BindingPath = "Track", Type = ColumnType.RightAligned, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 60 },
                new ColumnDef { Header = "Title", BindingPath = "Title", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Artist", BindingPath = "Artist", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Duration", BindingPath = "Duration", Type = ColumnType.Centered, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 100, StringFormat = "m\\:ss" },
            ],
            BaseFilter = item => item.Source == "cdda",
            SearchFilter = (item, search) =>
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false),
            ContextMenuItems = BuildCdAudioContextMenu(),
        };
    }

    private static List<ContextMenuItemDef> BuildCdAudioContextMenu() => Menu(
        Header(withArtist: false),
        Playback(),
        Items(Cmd("Rip Track…", "RipTrack"), Cmd("Rip Whole CD…", "RipCd")));

    private static ListViewConfig BuildFavoritesConfig()
    {
        return new ListViewConfig
        {
            Key = "Favorites",
            ShowCheckedColumn = true,
            Columns =
            [
                CheckedColumn(),
                new ColumnDef { Header = "", BindingPath = "IsPlaying", Type = ColumnType.PlayIndicator, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 30, CanUserSort = false, CanUserResize = false, CanUserReorder = false },
                new ColumnDef { Header = "Title", BindingPath = "Title", Type = ColumnType.FavoriteTitle, WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Artist", BindingPath = "Artist", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Album", BindingPath = "Album", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Year", BindingPath = "Year", WidthType = DataGridLengthUnitType.Pixel, WidthValue = 60, Type = ColumnType.Centered },
                new ColumnDef { Header = "Rating", BindingPath = "RatingDisplay", Type = ColumnType.Rating, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 100, CanUserSort = false },
            ],
            BaseFilter = item => item.IsFavorite,
            SearchFilter = (item, search) =>
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Album?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false),
            ContextMenuItems = BuildMusicContextMenu(),
        };
    }

    private static List<ContextMenuItemDef> BuildMusicContextMenu() => Menu(
        Header(),
        Playback(),
        Info(ratable: true),
        Organize(),
        Items(Cmd("Check", "Check"), Cmd("Uncheck", "Uncheck")),
        Items(Cmd("Show in Explorer", "ShowInExplorer"), Cmd("Burn to CD…", "BurnToCd"), Cmd("Remove from Library", "RemoveFromLibrary")));

    private static List<ContextMenuItemDef> BuildRadioContextMenu() => Menu(
        Header(withArtist: false),
        Playback(queueable: false),
        Info(),
        Items(Cmd("Toggle Favorite", "Favorite"), Cmd("Copy Stream URL", "CopyUrl"), Cmd("Visit Homepage", "Homepage")),
        Items(Cmd("Remove Station", "RemoveStation")));

    private static List<ContextMenuItemDef> BuildPlaylistContextMenu() => Menu(
        Header(),
        Playback(),
        Info(ratable: true),
        Organize(),
        Items(Cmd("Check", "Check"), Cmd("Uncheck", "Uncheck")),
        Items(Cmd("Show in Explorer", "ShowInExplorer"), Cmd("Remove from Playlist", "RemoveFromPlaylist"), Cmd("Remove from Library", "RemoveFromLibrary")));

    // --- shared context-menu vocabulary ---------------------------------------------------
    // Every track menu is the same skeleton in the same order - header · playback · info ·
    // organize · file/destructive - with a separator between the sections a view actually uses.
    // Menu() drops empty sections and never leaves a dangling separator, so each builder above
    // just lists the sections it wants; that's what keeps the six menus from drifting apart.

    // A get-only property, NOT a static field: Menu() runs during _configs' static
    // initialization (far above), which precedes any static field defined here - a field
    // would still be null then, salting every menu with null separators. Evaluated per use.
    private static ContextMenuItemDef Sep => new() { IsSeparator = true };

    private static ContextMenuItemDef Cmd(string header, string command) => new() { Header = header, CommandName = command };

    private static IEnumerable<ContextMenuItemDef> Items(params ContextMenuItemDef[] items) => items;

    private static IEnumerable<ContextMenuItemDef> Header(bool withArtist = true)
    {
        yield return new ContextMenuItemDef { Header = "{SelectedItem.Title}", IsHeader = true };
        if (withArtist)
        {
            yield return new ContextMenuItemDef { Header = "{SelectedItem.Artist}", IsHeader = true };
        }
    }

    private static IEnumerable<ContextMenuItemDef> Playback(bool queueable = true)
    {
        yield return Cmd("Play", "Play");
        if (queueable)
        {
            yield return Cmd("Play Next", "PlayNext");
            yield return Cmd("Add to Queue", "AddToQueue");
        }
    }

    private static IEnumerable<ContextMenuItemDef> Info(bool ratable = false)
    {
        yield return Cmd("Get Info", "GetInfo");
        if (ratable)
        {
            yield return new ContextMenuItemDef { Header = "Rating", IsRatingMarker = true };
        }
    }

    private static IEnumerable<ContextMenuItemDef> Organize(bool playlist = true, bool sync = true)
    {
        if (playlist)
        {
            yield return new ContextMenuItemDef { Header = "Add to Playlist", IsAddToPlaylistMarker = true };
        }
        if (sync)
        {
            yield return new ContextMenuItemDef { Header = "Sync", IsSyncToDeviceMarker = true };
        }
    }

    /// <summary>Composes the sections that apply into one menu, a separator between each non-empty section.</summary>
    private static List<ContextMenuItemDef> Menu(params IEnumerable<ContextMenuItemDef>[] sections)
    {
        var result = new List<ContextMenuItemDef>();
        foreach (var section in sections)
        {
            var items = section.ToList();
            if (items.Count == 0)
            {
                continue;
            }
            if (result.Count > 0)
            {
                result.Add(Sep);
            }
            result.AddRange(items);
        }
        return result;
    }
}
