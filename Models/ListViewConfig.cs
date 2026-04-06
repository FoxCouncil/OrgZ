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
}

public static class ListViewConfigs
{
    private static readonly Dictionary<string, ListViewConfig> _configs = new()
    {
        ["Music"] = BuildMusicConfig(),
        ["Radio"] = BuildRadioConfig(),
        ["Favorites"] = BuildFavoritesConfig(),
    };

    public static ListViewConfig? Get(string? key)
    {
        if (key == null)
        {
            return null;
        }

        return _configs.GetValueOrDefault(key);
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
                new ColumnDef { Header = "", BindingPath = "SourceLabel", WidthType = DataGridLengthUnitType.Pixel, WidthValue = 45, CanUserResize = false },
                new ColumnDef { Header = "Title", BindingPath = "Title", Type = ColumnType.FavoriteTitle, WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Country", BindingPath = "Country" },
                new ColumnDef { Header = "Tags", BindingPath = "Tags", WidthType = DataGridLengthUnitType.Star, WidthValue = 1 },
                new ColumnDef { Header = "Codec", BindingPath = "CodecLabel", Type = ColumnType.Centered, WidthType = DataGridLengthUnitType.Pixel, WidthValue = 60 },
                new ColumnDef { Header = "Bitrate", BindingPath = "BitrateLabel", Type = ColumnType.RightAligned },
                new ColumnDef { Header = "Listeners", BindingPath = "ListenerCountLabel" },
            ],
            BaseFilter = item => item.Kind == MediaKind.Radio,
            SearchFilter = (item, search) =>
                (item.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Tags?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Country?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false),
            ContextMenuItems = BuildRadioContextMenu(),
            ShowRadioFilterPanel = true,
        };
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
            new ContextMenuItemDef { Header = "Play Next", IsEnabled = false },
            new ContextMenuItemDef { Header = "Add to Queue", IsEnabled = false },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Toggle Favorite", CommandName = "Favorite" },
            new ContextMenuItemDef { Header = "Get Info", CommandName = "GetInfo" },
            new ContextMenuItemDef
            {
                Header = "Rating", IsEnabled = false,
                Children =
                [
                    new ContextMenuItemDef { Header = "1 Star", IsEnabled = false },
                    new ContextMenuItemDef { Header = "2 Stars", IsEnabled = false },
                    new ContextMenuItemDef { Header = "3 Stars", IsEnabled = false },
                    new ContextMenuItemDef { Header = "4 Stars", IsEnabled = false },
                    new ContextMenuItemDef { Header = "5 Stars", IsEnabled = false },
                ]
            },
            new ContextMenuItemDef
            {
                Header = "Add to Playlist", IsEnabled = false,
                Children =
                [
                    new ContextMenuItemDef { Header = "New Playlist...", IsEnabled = false },
                ]
            },
            new ContextMenuItemDef { IsSeparator = true },
            new ContextMenuItemDef { Header = "Show in Explorer", IsEnabled = false },
            new ContextMenuItemDef { Header = "Remove from Library", IsEnabled = false },
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
}
