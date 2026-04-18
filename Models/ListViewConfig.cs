// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;

namespace OrgZ.Models;

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
    public bool SupportsDrillDown { get; init; }
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
    };

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

    public static ListViewConfig BuildDeviceConfig(string mountPath, DeviceType deviceType)
    {
        var source = $"device:{mountPath}";
        var key = $"Device:{mountPath}";

        var columns = deviceType == DeviceType.StockIPod
            ? new List<ColumnDef>
            {
                new() { Header = "", BindingPath = "IsPlaying", Type = ColumnType.PlayIndicator, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 30, CanUserSort = false, CanUserResize = false, CanUserReorder = false },
                new() { Header = "#", BindingPath = "Track", WidthType = DataGridLengthUnitType.Pixel, WidthValue = 40, Type = ColumnType.Centered },
                new() { Header = "Title", BindingPath = "Title", WidthType = DataGridLengthUnitType.Star, WidthValue = 2 },
                new() { Header = "Artist", BindingPath = "Artist", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new() { Header = "Album", BindingPath = "Album", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new() { Header = "Genre", BindingPath = "Genre", WidthType = DataGridLengthUnitType.Pixel, WidthValue = 100 },
                new() { Header = "Year", BindingPath = "Year", WidthType = DataGridLengthUnitType.Pixel, WidthValue = 60, Type = ColumnType.Centered },
                new() { Header = "Duration", BindingPath = "Duration", Type = ColumnType.Centered, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 80, StringFormat = "mm\\:ss" },
                new() { Header = "Rating", BindingPath = "RatingDisplay", WidthType = DataGridLengthUnitType.Pixel, WidthValue = 90 },
                new() { Header = "Plays", BindingPath = "PlayCount", WidthType = DataGridLengthUnitType.Pixel, WidthValue = 60, Type = ColumnType.RightAligned },
            }
            : new List<ColumnDef>
            {
                new() { Header = "", BindingPath = "IsPlaying", Type = ColumnType.PlayIndicator, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 30, CanUserSort = false, CanUserResize = false, CanUserReorder = false },
                new() { Header = "Title", BindingPath = "Title", Type = ColumnType.FavoriteTitle, WidthType = DataGridLengthUnitType.Star, WidthValue = 2 },
                new() { Header = "Artist", BindingPath = "Artist", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new() { Header = "Album", BindingPath = "Album", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new() { Header = "Year", BindingPath = "Year", WidthType = DataGridLengthUnitType.Pixel, WidthValue = 60, Type = ColumnType.Centered },
                new() { Header = "Duration", BindingPath = "Duration", Type = ColumnType.Centered, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 80, StringFormat = "mm\\:ss" },
                new() { Header = "Extension", BindingPath = "Extension", WidthType = DataGridLengthUnitType.Pixel, WidthValue = 70 },
            };

        return new ListViewConfig
        {
            Key = key,
            Columns = columns,
            BaseFilter = item => item.Source == source,
            SearchFilter = (item, search) =>
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Album?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.FileName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false),
            ContextMenuItems = BuildDeviceContextMenu(),
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
                new ColumnDef { Header = "Rating", BindingPath = "RatingDisplay", WidthType = DataGridLengthUnitType.Pixel, WidthValue = 90 },
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

    private static ListViewConfig BuildMusicConfig()
    {
        return new ListViewConfig
        {
            Key = "Music",
            Columns =
            [
                new ColumnDef { Header = "", BindingPath = "IsPlaying", Type = ColumnType.PlayIndicator, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 30, CanUserSort = false, CanUserResize = false, CanUserReorder = false },
                new ColumnDef { Header = "Title", BindingPath = "Title", Type = ColumnType.FavoriteTitle, WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Artist", BindingPath = "Artist", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Album", BindingPath = "Album", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Rating", BindingPath = "RatingDisplay", WidthType = DataGridLengthUnitType.Pixel, WidthValue = 90 },
                new ColumnDef { Header = "Year", BindingPath = "Year" },
                new ColumnDef { Header = "Extension", BindingPath = "Extension" },
                new ColumnDef { Header = "Has Album Art", BindingPath = "HasAlbumArt", Type = ColumnType.CheckBox },
            ],
            BaseFilter = item => item.Kind == MediaKind.Music,
            SearchFilter = (item, search) =>
                (item.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Album?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.FileName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Year?.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ?? false),
            ContextMenuItems = BuildMusicContextMenu(),
            SupportsDrillDown = true,
        };
    }

    public static List<ColumnDef> BuildArtistsColumns()
    {
        return
        [
            new ColumnDef { Header = "Artist", BindingPath = "GroupKey", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
            new ColumnDef { Header = "Info", BindingPath = "SecondaryInfo", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
        ];
    }

    public static List<ColumnDef> BuildAlbumsColumns()
    {
        return
        [
            new ColumnDef { Header = "Album", BindingPath = "GroupKey", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
            new ColumnDef { Header = "Info", BindingPath = "SecondaryInfo", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
        ];
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
                new ColumnDef { Header = "Country", BindingPath = "Country", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Bit Rate", BindingPath = "BitrateLabel", Type = ColumnType.RightAligned, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 80 },
                new ColumnDef { Header = "Codec", BindingPath = "CodecLabel", Type = ColumnType.Centered, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 70 },
            ],
            BaseFilter = item => item.Kind == MediaKind.Radio,
            SearchFilter = (item, search) =>
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Tags?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Country?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false),
            ContextMenuItems = BuildRadioContextMenu(),
            ShowRadioFilterPanel = true,
            GroupByPath = "NormalizedGenre",
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
                new ColumnDef { Header = "#", BindingPath = "Track", WidthType = DataGridLengthUnitType.Pixel, WidthValue = 40 },
                new ColumnDef { Header = "Title", BindingPath = "Title", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Duration", BindingPath = "Duration", Type = ColumnType.Centered, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 100, StringFormat = "mm\\:ss" },
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
                new ColumnDef { Header = "Rating", BindingPath = "RatingDisplay", WidthType = DataGridLengthUnitType.Pixel, WidthValue = 90 },
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
