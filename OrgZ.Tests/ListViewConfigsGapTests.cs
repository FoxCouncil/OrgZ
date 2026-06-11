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

    // ===== BuildDeviceConfig — device track grid mirrors the Music view columns =====

    [Fact]
    public void BuildDeviceConfig_columns_match_the_music_view()
    {
        var cfg = ListViewConfigs.BuildDeviceConfig(@"L:\");
        var headers = cfg.Columns.Select(c => c.Header).ToList();

        // The iPod/device track grid uses the same columns as the local Music view.
        Assert.Equal(
            new[] { "", "Title", "Artist", "Track #", "Album", "Duration", "Year", "Plays", "Extension", "Has Album Art", "Rating" },
            headers);
    }

    [Fact]
    public void BuildDeviceConfig_BaseFilter_filters_by_device_source_string()
    {
        var cfg = ListViewConfigs.BuildDeviceConfig(@"L:\");

        var ours = Music("track-1", source: @"device:L:\");
        var theirs = Music("track-2", source: @"device:E:\");

        Assert.True(cfg.BaseFilter(ours));
        Assert.False(cfg.BaseFilter(theirs));
    }

    [Fact]
    public void BuildDeviceConfig_SearchFilter_matches_title_artist_album_filename()
    {
        var cfg = ListViewConfigs.BuildDeviceConfig(@"L:\");
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
        var cfg = ListViewConfigs.BuildDeviceConfig(@"L:\");
        Assert.Equal(@"Device:L:\", cfg.Key);
    }

    // ===== BuildDevicePlaylistsConfig — placeholder view under the device tree =====

    [Fact]
    public void BuildDevicePlaylistsConfig_Key_includes_mount_path_and_Playlists_suffix()
    {
        var cfg = ListViewConfigs.BuildDevicePlaylistsConfig(@"L:\");
        Assert.Equal(@"Device:L:\:Playlists", cfg.Key);
    }

    [Fact]
    public void BuildDevicePlaylistsConfig_BaseFilter_rejects_everything_for_now()
    {
        // The Playlists child is a placeholder until iTunesDB MHYP / M3U scanning is wired
        // up — the filter returns false for every item so the grid renders empty.
        var cfg = ListViewConfigs.BuildDevicePlaylistsConfig(@"L:\");
        var anything = Music("t1", title: "Anything");
        Assert.False(cfg.BaseFilter(anything));
    }

    [Fact]
    public void BuildDevicePlaylistsConfig_columns_describe_playlists_not_tracks()
    {
        var cfg = ListViewConfigs.BuildDevicePlaylistsConfig(@"L:\");
        var headers = cfg.Columns.Select(c => c.Header).ToList();

        Assert.Contains("Playlist", headers);
        Assert.Contains("Tracks", headers);
        Assert.Contains("Duration", headers);
        Assert.DoesNotContain("Artist", headers);   // not a track list
        Assert.DoesNotContain("Album", headers);
    }

    [Fact]
    public void BuildDevicePlaylistsConfig_SearchFilter_matches_title()
    {
        var cfg = ListViewConfigs.BuildDevicePlaylistsConfig(@"L:\");
        var pl = Music("pl1", title: "Road Trip");
        Assert.True(cfg.SearchFilter(pl, "Road"));
        Assert.False(cfg.SearchFilter(pl, "Workout"));
    }

    // ===== ColumnDef defaults — IsDefaultVisible + Key =====

    [Fact]
    public void ColumnDef_IsDefaultVisible_defaults_to_true()
    {
        var col = new ColumnDef { Header = "Anything", BindingPath = "Title" };
        Assert.True(col.IsDefaultVisible);
    }

    [Fact]
    public void ColumnDef_Key_uses_BindingPath_by_default()
    {
        var col = new ColumnDef { Header = "Artist", BindingPath = "Artist" };
        Assert.Equal("Artist", col.Key);
    }

    [Fact]
    public void ColumnDef_Key_falls_back_to_Header_when_BindingPath_empty()
    {
        var col = new ColumnDef { Header = "Play", BindingPath = "" };
        Assert.Equal("Play", col.Key);
    }

    // ===== Music view: Extension/HasAlbumArt default-hidden, "#" track column present =====

    [Fact]
    public void MusicConfig_hides_programmer_columns_by_default()
    {
        var cfg = ListViewConfigs.Get("Music")!;
        foreach (var header in new[] { "Extension", "Has Album Art", "Plays" })
        {
            var col = cfg.Columns.Single(c => c.Header == header);
            Assert.False(col.IsDefaultVisible, $"{header} should be hidden by default");
        }
    }

    [Fact]
    public void MusicConfig_has_Duration_column_default_visible()
    {
        var cfg = ListViewConfigs.Get("Music")!;
        var dur = cfg.Columns.Single(c => c.Header == "Duration");
        Assert.Equal("Duration", dur.BindingPath);
        Assert.True(dur.IsDefaultVisible);
        Assert.Equal(@"m\:ss", dur.StringFormat);
    }

    [Fact]
    public void MusicConfig_has_Plays_column_bound_to_PlayCount()
    {
        var cfg = ListViewConfigs.Get("Music")!;
        var plays = cfg.Columns.Single(c => c.Header == "Plays");
        Assert.Equal("PlayCount", plays.BindingPath);
        Assert.Equal(ColumnType.RightAligned, plays.Type);
    }

    [Fact]
    public void MusicConfig_has_track_number_column_using_TrackDisplay()
    {
        var cfg = ListViewConfigs.Get("Music")!;
        var trackCol = cfg.Columns.Single(c => c.Header == "Track #");
        Assert.Equal("TrackDisplay", trackCol.BindingPath);
        Assert.True(trackCol.IsDefaultVisible);
        Assert.Equal(ColumnType.RightAligned, trackCol.Type);
    }

    [Fact]
    public void MusicConfig_core_columns_default_visible()
    {
        var cfg = ListViewConfigs.Get("Music")!;
        foreach (var header in new[] { "Title", "Artist", "Album", "Rating", "Year" })
        {
            var col = cfg.Columns.Single(c => c.Header == header);
            Assert.True(col.IsDefaultVisible, $"{header} should be visible by default");
        }
    }

    [Fact]
    public void MusicConfig_default_column_order_puts_Title_Artist_Track_first()
    {
        // Media-style layout: Title → Artist → # (track), with the play indicator as the
        // structural leader. Album / Rating / Year follow. Users can drag to reorder and
        // their preference persists per ColumnStateStore, but the default ships in this
        // order for every fresh install.
        var cfg = ListViewConfigs.Get("Music")!;
        var headersInOrder = cfg.Columns.Select(c => c.Header).ToList();

        // Find positions (play indicator has empty header so we look past index 0)
        var titleIdx  = headersInOrder.IndexOf("Title");
        var artistIdx = headersInOrder.IndexOf("Artist");
        var trackIdx  = headersInOrder.IndexOf("Track #");
        var albumIdx  = headersInOrder.IndexOf("Album");

        Assert.True(titleIdx < artistIdx, "Title must come before Artist");
        Assert.True(artistIdx < trackIdx, "Artist must come before Track #");
        Assert.True(trackIdx < albumIdx,  "Track # must come before Album");
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
