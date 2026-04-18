// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using static OrgZ.Tests.TestHelpers;

namespace OrgZ.Tests;

/// <summary>
/// Coverage gaps in ListViewConfigs — registry methods, drill-down column factories,
/// CD audio config, device config (both stock-iPod and Rockbox column shapes), and
/// the various uncovered SearchFilter branches.
/// </summary>
public class ListViewConfigsGapTests
{
    // ===== Registry methods =====

    [Fact]
    public void Get_null_key_returns_null()
    {
        Assert.Null(ListViewConfigs.Get(null));
    }

    [Fact]
    public void Get_unknown_key_returns_null()
    {
        Assert.Null(ListViewConfigs.Get("ThisDoesNotExist"));
    }

    [Fact]
    public void Register_then_Remove_round_trip()
    {
        var key = "Test:Registry:" + Guid.NewGuid();
        var cfg = ListViewConfigs.BuildPlaylistConfig(99, []);

        ListViewConfigs.Register(key, cfg);
        Assert.NotNull(ListViewConfigs.Get(key));

        ListViewConfigs.Remove(key);
        Assert.Null(ListViewConfigs.Get(key));
    }

    [Fact]
    public void Remove_unknown_key_does_not_throw()
    {
        // Idempotent — Remove on a missing key just no-ops
        ListViewConfigs.Remove("NotPresent:" + Guid.NewGuid());
    }

    // ===== CD audio config =====

    [Fact]
    public void CdAudioConfig_BaseFilter_accepts_cdda_source()
    {
        var cfg = ListViewConfigs.Get("CdAudio");
        Assert.NotNull(cfg);

        var cdTrack = Music("cd:track1", source: "cdda");
        var libTrack = Music("local-track");

        Assert.True(cfg!.BaseFilter(cdTrack));
        Assert.False(cfg.BaseFilter(libTrack));
    }

    [Fact]
    public void CdAudioConfig_SearchFilter_matches_title_only()
    {
        var cfg = ListViewConfigs.Get("CdAudio")!;
        var track = Music("cd:1", title: "Money");

        Assert.True(cfg.SearchFilter(track, "Money"));
        Assert.True(cfg.SearchFilter(track, "money"));   // case-insensitive
        Assert.False(cfg.SearchFilter(track, "Floyd"));
    }

    // ===== BuildDeviceConfig — stock iPod vs Rockbox column shapes =====

    [Fact]
    public void BuildDeviceConfig_StockIPod_columns_include_play_count_and_rating()
    {
        var cfg = ListViewConfigs.BuildDeviceConfig(@"L:\", DeviceType.StockIPod);
        var headers = cfg.Columns.Select(c => c.Header).ToList();

        Assert.Contains("Plays", headers);
        Assert.Contains("Rating", headers);
        Assert.Contains("Genre", headers);
    }

    [Fact]
    public void BuildDeviceConfig_RockboxOther_uses_simpler_column_set()
    {
        var cfg = ListViewConfigs.BuildDeviceConfig(@"E:\", DeviceType.RockboxOther);
        var headers = cfg.Columns.Select(c => c.Header).ToList();

        // Rockbox doesn't expose play counts / ratings, so its column set is leaner
        Assert.DoesNotContain("Plays", headers);
        Assert.DoesNotContain("Rating", headers);
        Assert.Contains("Title", headers);
        Assert.Contains("Artist", headers);
        Assert.Contains("Extension", headers);
    }

    [Fact]
    public void BuildDeviceConfig_BaseFilter_filters_by_device_source_string()
    {
        var cfg = ListViewConfigs.BuildDeviceConfig(@"L:\", DeviceType.StockIPod);

        var ours = Music("track-1", source: @"device:L:\");
        var theirs = Music("track-2", source: @"device:E:\");

        Assert.True(cfg.BaseFilter(ours));
        Assert.False(cfg.BaseFilter(theirs));
    }

    [Fact]
    public void BuildDeviceConfig_SearchFilter_matches_title_artist_album_filename()
    {
        var cfg = ListViewConfigs.BuildDeviceConfig(@"L:\", DeviceType.StockIPod);
        var track = Music("t1", title: "Tom Sawyer", artist: "Rush", album: "Moving Pictures", fileName: "01-tom-sawyer.mp3");

        Assert.True(cfg.SearchFilter(track, "Sawyer"));
        Assert.True(cfg.SearchFilter(track, "Rush"));
        Assert.True(cfg.SearchFilter(track, "Moving"));
        Assert.True(cfg.SearchFilter(track, "01-tom"));
        Assert.False(cfg.SearchFilter(track, "Floyd"));
    }

    [Fact]
    public void BuildDeviceConfig_Key_includes_mount_path()
    {
        var cfg = ListViewConfigs.BuildDeviceConfig(@"L:\", DeviceType.StockIPod);
        Assert.Equal(@"Device:L:\", cfg.Key);
    }

    // ===== Drill-down column factories =====

    [Fact]
    public void BuildArtistsColumns_returns_two_grouped_columns()
    {
        var cols = ListViewConfigs.BuildArtistsColumns();
        Assert.Equal(2, cols.Count);
        Assert.Equal("Artist", cols[0].Header);
        Assert.Equal("Info", cols[1].Header);
        Assert.Equal("GroupKey", cols[0].BindingPath);
        Assert.Equal("SecondaryInfo", cols[1].BindingPath);
    }

    [Fact]
    public void BuildAlbumsColumns_returns_two_grouped_columns()
    {
        var cols = ListViewConfigs.BuildAlbumsColumns();
        Assert.Equal(2, cols.Count);
        Assert.Equal("Album", cols[0].Header);
        Assert.Equal("Info", cols[1].Header);
    }

    // ===== Search filters (additional uncovered branches) =====

    [Fact]
    public void FavoritesConfig_SearchFilter_matches_artist_album_title()
    {
        var cfg = ListViewConfigs.Get("Favorites")!;
        var track = Music("t1", title: "Money", artist: "Pink Floyd", album: "Dark Side of the Moon", isFavorite: true);

        Assert.True(cfg.SearchFilter(track, "Money"));
        Assert.True(cfg.SearchFilter(track, "Floyd"));
        Assert.True(cfg.SearchFilter(track, "Dark"));
        Assert.False(cfg.SearchFilter(track, "Rush"));
    }

    [Fact]
    public void IgnoredConfig_SearchFilter_matches_filename()
    {
        var cfg = ListViewConfigs.Get("Ignored")!;
        var track = Music("t1", title: null, artist: null, album: null, fileName: "weird-track.mp3");
        track.IsIgnored = true;

        Assert.True(cfg.SearchFilter(track, "weird"));
        Assert.False(cfg.SearchFilter(track, "missing"));
    }

    [Fact]
    public void MusicConfig_SearchFilter_matches_year_as_string()
    {
        var cfg = ListViewConfigs.Get("Music")!;
        var track = Music("t1", title: "Tom Sawyer", artist: "Rush", year: 1981);

        Assert.True(cfg.SearchFilter(track, "1981"));
        Assert.False(cfg.SearchFilter(track, "1999"));
    }
}
