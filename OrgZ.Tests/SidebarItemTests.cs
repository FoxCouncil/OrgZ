// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class SidebarItemTests
{
    [Fact]
    public void Defaults_are_safe_empty_values()
    {
        var item = new SidebarItem();

        Assert.Equal(string.Empty, item.Name);
        Assert.Equal(string.Empty, item.Icon);
        Assert.Equal(string.Empty, item.Category);
        Assert.Equal(string.Empty, item.ViewConfigKey);
        Assert.False(item.IsEnabled);
        Assert.False(item.IsFavorites);
        Assert.False(item.IsNewPlaylistAction);
        Assert.False(item.IsImportPlaylistAction);
        Assert.Null(item.Kind);
        Assert.Null(item.PlaylistId);
        Assert.Null(item.IconBitmap);
        Assert.False(item.HasIconBitmap);
    }

    [Fact]
    public void Init_only_properties_are_set_via_object_initializer()
    {
        var item = new SidebarItem
        {
            Name = "Music",
            Icon = "fa-solid fa-music",
            Category = "LIBRARY",
            IsEnabled = true,
            Kind = MediaKind.Music,
            ViewConfigKey = "Music",
        };

        Assert.Equal("Music", item.Name);
        Assert.Equal("fa-solid fa-music", item.Icon);
        Assert.Equal("LIBRARY", item.Category);
        Assert.True(item.IsEnabled);
        Assert.Equal(MediaKind.Music, item.Kind);
        Assert.Equal("Music", item.ViewConfigKey);
    }

    [Fact]
    public void Playlist_action_flags_are_independent()
    {
        var newAction = new SidebarItem { IsNewPlaylistAction = true };
        var importAction = new SidebarItem { IsImportPlaylistAction = true };
        var favorites = new SidebarItem { IsFavorites = true };

        Assert.True(newAction.IsNewPlaylistAction);
        Assert.False(newAction.IsImportPlaylistAction);
        Assert.False(newAction.IsFavorites);

        Assert.True(importAction.IsImportPlaylistAction);
        Assert.False(importAction.IsNewPlaylistAction);

        Assert.True(favorites.IsFavorites);
    }

    [Fact]
    public void PlaylistId_set_through_initializer()
    {
        var item = new SidebarItem { PlaylistId = 42 };
        Assert.Equal(42, item.PlaylistId);
    }

    [Fact]
    public void HasIconBitmap_false_when_bitmap_null()
    {
        Assert.False(new SidebarItem().HasIconBitmap);
    }
}
