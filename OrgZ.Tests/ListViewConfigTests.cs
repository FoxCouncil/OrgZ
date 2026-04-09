// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using static OrgZ.Tests.TestHelpers;

namespace OrgZ.Tests;

public class ListViewConfigTests
{
    // -- Music view --

    [Fact]
    public void MusicConfig_BaseFilter_AcceptsMusicOnly()
    {
        var config = ListViewConfigs.Get("Music")!;

        Assert.True(config.BaseFilter(Music("a")));
        Assert.False(config.BaseFilter(Radio("b")));
    }

    [Fact]
    public void MusicConfig_SearchFilter_MatchesAcrossFields()
    {
        var config = ListViewConfigs.Get("Music")!;
        var item = Music("id1", title: "Beatles - Hey Jude", artist: "The Beatles", album: "Let It Be", fileName: "hey_jude.mp3", year: 1968);

        Assert.True(config.SearchFilter(item, "Beatles"));
        Assert.True(config.SearchFilter(item, "beatles")); // case-insensitive
        Assert.True(config.SearchFilter(item, "Let It Be"));
        Assert.True(config.SearchFilter(item, "hey_jude"));
        Assert.True(config.SearchFilter(item, "1968"));
        Assert.False(config.SearchFilter(item, "Stones"));
    }

    [Fact]
    public void MusicConfig_SupportsDrillDown()
    {
        Assert.True(ListViewConfigs.Get("Music")!.SupportsDrillDown);
    }

    // -- Radio view --

    [Fact]
    public void RadioConfig_BaseFilter_AcceptsRadioOnly()
    {
        var config = ListViewConfigs.Get("Radio")!;

        Assert.True(config.BaseFilter(Radio("a")));
        Assert.False(config.BaseFilter(Music("b")));
    }

    [Fact]
    public void RadioConfig_SearchFilter_MatchesTitleTagsCountry()
    {
        var config = ListViewConfigs.Get("Radio")!;
        var item = Radio("id1", title: "BBC Radio 1", country: "United Kingdom", tags: "pop,rock,top40");

        Assert.True(config.SearchFilter(item, "BBC"));
        Assert.True(config.SearchFilter(item, "United"));
        Assert.True(config.SearchFilter(item, "rock"));
        Assert.False(config.SearchFilter(item, "country"));
    }

    [Fact]
    public void RadioConfig_ShowsRadioFilterPanel()
    {
        Assert.True(ListViewConfigs.Get("Radio")!.ShowRadioFilterPanel);
    }

    [Fact]
    public void RadioConfig_GroupsByNormalizedGenre()
    {
        Assert.Equal("NormalizedGenre", ListViewConfigs.Get("Radio")!.GroupByPath);
    }

    [Fact]
    public void MusicConfig_HasNoGroupByPath()
    {
        Assert.Null(ListViewConfigs.Get("Music")!.GroupByPath);
    }

    // -- Favorites view --

    [Fact]
    public void FavoritesConfig_BaseFilter_AcceptsFavoritesOnly()
    {
        var config = ListViewConfigs.Get("Favorites")!;

        Assert.True(config.BaseFilter(Music("a", isFavorite: true)));
        Assert.True(config.BaseFilter(Radio("b", isFavorite: true)));
        Assert.False(config.BaseFilter(Music("c", isFavorite: false)));
        Assert.False(config.BaseFilter(Radio("d", isFavorite: false)));
    }

    // -- Playlist view (the most complex one) --

    [Fact]
    public void BuildPlaylistConfig_BaseFilter_OnlyMatchesIdsInPlaylist()
    {
        var config = ListViewConfigs.BuildPlaylistConfig(42, ["a", "b", "c"]);

        Assert.True(config.BaseFilter(Music("a")));
        Assert.True(config.BaseFilter(Music("b")));
        Assert.False(config.BaseFilter(Music("d")));
    }

    [Fact]
    public void BuildPlaylistConfig_PlaylistIdIsExposed()
    {
        var config = ListViewConfigs.BuildPlaylistConfig(42, ["a"]);
        Assert.Equal(42, config.PlaylistId);
    }

    [Fact]
    public void BuildPlaylistConfig_Sorter_PreservesOrderingOfTrackIds()
    {
        // Items in arbitrary order
        var allItems = new List<MediaItem>
        {
            Music("c", title: "C track"),
            Music("a", title: "A track"),
            Music("b", title: "B track"),
            Music("d", title: "D track"), // not in playlist
        };

        var config = ListViewConfigs.BuildPlaylistConfig(1, ["b", "a", "c"]);

        // Apply BaseFilter then Sorter — mimics ApplyFilter
        var filtered = allItems.Where(config.BaseFilter);
        var sorted = config.Sorter!(filtered).ToList();

        Assert.Equal(3, sorted.Count);
        Assert.Equal("b", sorted[0].Id);
        Assert.Equal("a", sorted[1].Id);
        Assert.Equal("c", sorted[2].Id);
    }

    [Fact]
    public void BuildPlaylistConfig_Sorter_HandlesEmptyOrder()
    {
        var config = ListViewConfigs.BuildPlaylistConfig(1, []);
        var result = config.Sorter!(new List<MediaItem>()).ToList();
        Assert.Empty(result);
    }

    [Fact]
    public void BuildPlaylistConfig_RegisterAndRetrieve()
    {
        var key = "Playlist:9999";
        var config = ListViewConfigs.BuildPlaylistConfig(9999, ["x"]);
        ListViewConfigs.Register(key, config);

        var retrieved = ListViewConfigs.Get(key);

        Assert.NotNull(retrieved);
        Assert.Equal(9999, retrieved!.PlaylistId);

        // Cleanup so we don't poison other tests
        ListViewConfigs.Remove(key);
    }

    [Fact]
    public void Get_UnknownKey_ReturnsNull()
    {
        Assert.Null(ListViewConfigs.Get("not-a-real-view"));
        Assert.Null(ListViewConfigs.Get(null));
    }

    // -- Ignored view --

    [Fact]
    public void IgnoredConfig_BaseFilter_AcceptsIgnoredOnly()
    {
        var config = ListViewConfigs.Get("Ignored")!;

        Assert.True(config.BaseFilter(new MediaItem { Id = "a", Kind = MediaKind.Music, IsIgnored = true }));
        Assert.False(config.BaseFilter(new MediaItem { Id = "b", Kind = MediaKind.Music, IsIgnored = false }));
    }

    [Fact]
    public void IgnoredConfig_IncludeIgnoredIsTrue()
    {
        Assert.True(ListViewConfigs.Get("Ignored")!.IncludeIgnored);
    }

    [Fact]
    public void NormalViews_DoNotIncludeIgnoredByDefault()
    {
        Assert.False(ListViewConfigs.Get("Music")!.IncludeIgnored);
        Assert.False(ListViewConfigs.Get("Radio")!.IncludeIgnored);
        Assert.False(ListViewConfigs.Get("Favorites")!.IncludeIgnored);
    }

    [Fact]
    public void BuildPlaylistConfig_DoesNotIncludeIgnored()
    {
        var config = ListViewConfigs.BuildPlaylistConfig(1, ["a"]);
        Assert.False(config.IncludeIgnored);
    }
}
