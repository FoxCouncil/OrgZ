// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;

namespace OrgZ.Models;

/// <summary>
/// Which top-level control hosts a view. Each DataGrid host builds its columns exactly once -
/// rebuilding columns after a grouped DataGridCollectionView is bound crashes inside Avalonia's
/// column collection (the spacer-column bug) - so every distinct grouped column set needs its own
/// grid, and the config names its host here instead of view-specific flags being re-derived at
/// each consumer.
/// </summary>
public enum ViewHost
{
    /// <summary>The shared flat grid (Music, Favorites, playlists, devices, ...).</summary>
    MainGrid,
    /// <summary>The grouped grid carrying Radio's columns (genre group headers).</summary>
    GroupedGrid,
    /// <summary>The grouped grid carrying podcast columns (a device's Podcasts view, one group per show).</summary>
    PodcastGroupedGrid,
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
    /// When false (default), ApplyFilter strips out items where IsIgnored is true.
    /// The Ignored view sets this to true to do the opposite.
    /// </summary>
    public bool IncludeIgnored { get; init; }

    /// <summary>
    /// When set, the view wraps FilteredItems in a DataGridCollectionView grouped by this property path.
    /// Avalonia's DataGrid renders collapsible group headers automatically.
    /// </summary>
    public string? GroupByPath { get; init; }

    /// <summary>The control that hosts this view - see <see cref="ViewHost"/>.</summary>
    public ViewHost Host { get; init; } = ViewHost.MainGrid;
}

public static class ListViewConfigs
{
    private static readonly Dictionary<string, ListViewConfig> _configs = new()
    {
        ["Music"] = BuildMusicConfig(),
        ["Radio"] = BuildRadioConfig(),
        ["Favorites"] = BuildFavoritesConfig(),
        ["Ignored"] = BuildIgnoredConfig(),
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
            BaseFilter = item => item.Kind == MediaKind.Audiobook
                                 && item.Source != "cdda"
                                 && (item.Source == null || !item.Source.StartsWith("device:", StringComparison.Ordinal)),
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

    /// <summary>The Music menu minus "Burn to CD..." - a multi-hour book has no audio-CD story.</summary>
    private static List<ContextMenuItemDef> BuildAudiobookContextMenu()
    {
        return
        [
            new ContextMenuItemDef { Header = "{SelectedItem.Title}", IsHeader = true },
            new ContextMenuItemDef { Header = "{SelectedItem.Artist}", IsHeader = true },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Play", CommandName = "Play" },
            new ContextMenuItemDef { Header = "Play Next", CommandName = "PlayNext" },
            new ContextMenuItemDef { Header = "Add to Queue", CommandName = "AddToQueue" },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Toggle Favorite", CommandName = "Favorite" },
            new ContextMenuItemDef { Header = "Get Info", CommandName = "GetInfo" },
            new ContextMenuItemDef
            {
                Header = "Rating",
                IsRatingMarker = true,
            },
            new ContextMenuItemDef
            {
                Header = "Add to Playlist",
                IsAddToPlaylistMarker = true,
            },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Show in Explorer", CommandName = "ShowInExplorer" },
            new ContextMenuItemDef { Header = "Remove from Library", CommandName = "RemoveFromLibrary" },
        ];
    }

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
        _configs.Remove(key);
    }

    /// <summary>
    /// Removes a view config together with its whole sub-view family ("{key}:Podcast",
    /// "{key}:Playlist:{id}", ...). Device teardown uses this so a different iPod arriving at the same
    /// mount path can't inherit the departed device's per-kind / per-playlist configs. Boundary-aware:
    /// "Device:/media/ipod" leaves "Device:/media/ipod-red"'s family alone.
    /// </summary>
    public static void RemoveWithSubViews(string key)
    {
        _configs.Remove(key);
        var prefix = $"{key}:";
        foreach (var sub in _configs.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _configs.Remove(sub);
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
            // Group podcast episodes by show (Album) on the dedicated podcast grid. Audiobooks stay flat.
            GroupByPath = isPodcast ? "Album" : null,
            Host = isPodcast ? ViewHost.PodcastGroupedGrid : ViewHost.MainGrid,
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

    private static List<ContextMenuItemDef> BuildDeviceContextMenu()
    {
        return
        [
            new ContextMenuItemDef { Header = "{SelectedItem.Title}", IsHeader = true },
            new ContextMenuItemDef { Header = "{SelectedItem.Artist}", IsHeader = true },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Play", CommandName = "Play" },
            new ContextMenuItemDef { Header = "Play Next", CommandName = "PlayNext" },
            new ContextMenuItemDef { Header = "Add to Queue", CommandName = "AddToQueue" },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Get Info", CommandName = "GetInfo" },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Remove from iPod", CommandName = "RemoveFromDevice" },
        ];
    }

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
            Columns =
            [
                new ColumnDef { Header = "", BindingPath = "IsPlaying", Type = ColumnType.PlayIndicator, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 30, CanUserSort = false, CanUserResize = false, CanUserReorder = false },
                new ColumnDef { Header = "Title", BindingPath = "Title", Type = ColumnType.FavoriteTitle, WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Artist", BindingPath = "Artist", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Album", BindingPath = "Album", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Rating", BindingPath = "RatingDisplay", Type = ColumnType.Rating, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 110, CanUserSort = false },
            ],
            BaseFilter = item => idSet.Contains(item.Id),
            SearchFilter = (item, search) =>
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Album?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false),
            ContextMenuItems = BuildPlaylistContextMenu(),
            Sorter = items => items.OrderBy(item => orderMap.TryGetValue(item.Id, out var idx) ? idx : int.MaxValue),
        };
    }

    /// <summary>
    /// The standard music-track columns, shared by the local-library "Music" view and the
    /// device (iPod) track view so they read identically. Returns fresh ColumnDef instances
    /// each call - per-view column visibility/width prefs mutate these and must not be shared.
    /// </summary>
    private static List<ColumnDef> MusicColumns() =>
    [
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

    private static ListViewConfig BuildMusicConfig()
    {
        return new ListViewConfig
        {
            Key = "Music",
            Columns = MusicColumns(),
            // Local library view - must NOT include CD-audio tracks (Source="cdda")
            // or connected-device tracks (Source="device:{mountPath}").  Those have
            // their own sidebar entries and their own views.  Without this, typing
            // in the search box while the Music tab is selected also matches iPod
            // tracks and leaks them into the local results list.
            BaseFilter = item => item.Kind == MediaKind.Music
                                 && item.Source != "cdda"
                                 && (item.Source == null || !item.Source.StartsWith("device:", StringComparison.Ordinal)),
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
            // matching the nubango taxonomy ("Alternative Rock", "Top 40 / Pop",
            // etc.) without going through GenreNormalizer's fuzzy rules.
            GroupByPath = "Tags",
            Host = ViewHost.GroupedGrid,
        };
    }

    private static ListViewConfig BuildIgnoredConfig()
    {
        return new ListViewConfig
        {
            Key = "Ignored",
            IncludeIgnored = true,
            Columns =
            [
                new ColumnDef { Header = "Title", BindingPath = "Title", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Artist", BindingPath = "Artist", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Album", BindingPath = "Album", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "File Name", BindingPath = "FileName", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
            ],
            BaseFilter = item => item.IsIgnored,
            SearchFilter = (item, search) =>
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Album?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.FileName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false),
            ContextMenuItems = BuildIgnoredContextMenu(),
        };
    }

    private static List<ContextMenuItemDef> BuildIgnoredContextMenu()
    {
        return
        [
            new ContextMenuItemDef { Header = "{SelectedItem.Title}", IsHeader = true },
            new ContextMenuItemDef { Header = "{SelectedItem.Artist}", IsHeader = true },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Restore to Library", CommandName = "RestoreFromIgnored" },
            new ContextMenuItemDef { Header = "Get Info", CommandName = "GetInfo" },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Show in Explorer", CommandName = "ShowInExplorer" },
        ];
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
            BaseFilter = item => item.Kind == MediaKind.Music && !string.IsNullOrEmpty(item.FormatIssues),
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
                new ColumnDef { Header = "Duration", BindingPath = "Duration", Type = ColumnType.Centered, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 100, StringFormat = "m\\:ss" },
            ],
            BaseFilter = item => item.Source == "cdda",
            SearchFilter = (item, search) =>
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false),
            ContextMenuItems = BuildCdAudioContextMenu(),
        };
    }

    private static List<ContextMenuItemDef> BuildCdAudioContextMenu()
    {
        return
        [
            new ContextMenuItemDef { Header = "{SelectedItem.Title}", IsHeader = true },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Play", CommandName = "Play" },
            new ContextMenuItemDef { Header = "Play Next", CommandName = "PlayNext" },
            new ContextMenuItemDef { Header = "Add to Queue", CommandName = "AddToQueue" },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Rip Track…", CommandName = "RipTrack" },
            new ContextMenuItemDef { Header = "Rip Whole CD…", CommandName = "RipCd" },
        ];
    }

    private static ListViewConfig BuildFavoritesConfig()
    {
        return new ListViewConfig
        {
            Key = "Favorites",
            Columns =
            [
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

    private static List<ContextMenuItemDef> BuildMusicContextMenu()
    {
        return
        [
            new ContextMenuItemDef { Header = "{SelectedItem.Title}", IsHeader = true },
            new ContextMenuItemDef { Header = "{SelectedItem.Artist}", IsHeader = true },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Play", CommandName = "Play" },
            new ContextMenuItemDef { Header = "Play Next", CommandName = "PlayNext" },
            new ContextMenuItemDef { Header = "Add to Queue", CommandName = "AddToQueue" },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Toggle Favorite", CommandName = "Favorite" },
            new ContextMenuItemDef { Header = "Get Info", CommandName = "GetInfo" },
            new ContextMenuItemDef
            {
                Header = "Rating",
                IsRatingMarker = true,
            },
            new ContextMenuItemDef
            {
                Header = "Add to Playlist",
                IsAddToPlaylistMarker = true,
            },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Burn to CD…", CommandName = "BurnToCd" },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Show in Explorer", CommandName = "ShowInExplorer" },
            new ContextMenuItemDef { Header = "Remove from Library", CommandName = "RemoveFromLibrary" },
        ];
    }

    private static List<ContextMenuItemDef> BuildRadioContextMenu()
    {
        return
        [
            new ContextMenuItemDef { Header = "{SelectedItem.Title}", IsHeader = true },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Play", CommandName = "Play" },
            new ContextMenuItemDef { Header = "Toggle Favorite", CommandName = "Favorite" },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Get Info", CommandName = "GetInfo" },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Copy Stream URL", CommandName = "CopyUrl" },
            new ContextMenuItemDef { Header = "Visit Homepage", CommandName = "Homepage" },
        ];
    }

    private static List<ContextMenuItemDef> BuildPlaylistContextMenu()
    {
        return
        [
            new ContextMenuItemDef { Header = "{SelectedItem.Title}", IsHeader = true },
            new ContextMenuItemDef { Header = "{SelectedItem.Artist}", IsHeader = true },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Play", CommandName = "Play" },
            new ContextMenuItemDef { Header = "Play Next", CommandName = "PlayNext" },
            new ContextMenuItemDef { Header = "Add to Queue", CommandName = "AddToQueue" },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Remove from Playlist", CommandName = "RemoveFromPlaylist" },
            new ContextMenuItemDef { Header = "Toggle Favorite", CommandName = "Favorite" },
            new ContextMenuItemDef { Header = "Get Info", CommandName = "GetInfo" },
        ];
    }
}
