// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.ViewModels;

namespace OrgZ.Tests;

/// <summary>
/// Two bugs that only surfaced once the share pipe was exercised end to end, pinned so
/// they can't come back:
///
/// 1. A share track has no FilePath, and every play path guarded on FilePath - so a
///    mounted share's rows were silently unplayable. Nothing errored; double-clicking
///    just did nothing.
/// 2. A mounted share's tracks join the same backing list as the library, and the local
///    views only excluded CDs and devices - so a share dumped its whole catalogue into
///    Music, and into Bad Format as a to-do list of files that aren't ours to fix.
/// </summary>
public class SharePlaybackRoutingTests
{
    private static MediaItem Local(string path = @"C:\music\a.mp3") => new()
    {
        Id = "1",
        Kind = MediaKind.Music,
        Title = "Local",
        FilePath = path,
        Extension = Path.GetExtension(path),
    };

    private static MediaItem Shared() => new()
    {
        Id = "share:192.168.1.50:7391:abc",
        Kind = MediaKind.Music,
        Title = "Shared",
        StreamUrl = "http://192.168.1.50:7391/stream/abc.mp3",
        Source = "share:192.168.1.50:7391",
    };

    // ── Where the audio comes from ────────────────────────────

    [Fact]
    public void A_share_track_plays_from_its_stream_url_and_a_local_one_from_its_path()
    {
        Assert.Equal(@"C:\music\a.mp3", MainWindowViewModel.PlayableLocation(Local()));
        Assert.Equal("http://192.168.1.50:7391/stream/abc.mp3", MainWindowViewModel.PlayableLocation(Shared()));
    }

    [Fact]
    public void A_track_with_nothing_to_play_reads_as_unplayable()
    {
        Assert.Null(MainWindowViewModel.PlayableLocation(new MediaItem { Id = "1", Kind = MediaKind.Music }));
        Assert.Null(MainWindowViewModel.PlayableLocation(new MediaItem { Id = "1", Kind = MediaKind.Music, FilePath = "" }));

        // A share row whose catalogue entry produced no URL must not fall back to a
        // FilePath borrowed from somewhere else.
        Assert.Null(MainWindowViewModel.PlayableLocation(new MediaItem
        {
            Id = "share:h:1:x",
            Kind = MediaKind.Music,
            Source = "share:h:1",
            FilePath = @"C:\not\mine.mp3",
        }));
    }

    [Fact]
    public void Device_and_cd_tracks_keep_playing_from_the_paths_they_always_did()
    {
        var device = new MediaItem { Id = "d", Kind = MediaKind.Music, Source = "device:/mnt/ipod", FilePath = "/mnt/ipod/Music/F00/x.mp3" };
        var cd = new MediaItem { Id = "cd:D::3", Kind = MediaKind.Music, Source = "cdda", StreamUrl = "cdda:///D:/" };

        Assert.Equal("/mnt/ipod/Music/F00/x.mp3", MainWindowViewModel.PlayableLocation(device));
        Assert.Null(MainWindowViewModel.PlayableLocation(cd));   // CDs route through PlayCdTrack before this
    }

    // ── View partitioning ─────────────────────────────────────

    [Fact]
    public void Only_this_librarys_own_tracks_count_as_local()
    {
        Assert.True(ListViewConfigs.IsLocalLibraryItem(Local()));
        Assert.True(ListViewConfigs.IsLocalLibraryItem(new MediaItem { Id = "p", Kind = MediaKind.Podcast, FilePath = @"C:\p.mp3" }));

        Assert.False(ListViewConfigs.IsLocalLibraryItem(Shared()));
        Assert.False(ListViewConfigs.IsLocalLibraryItem(new MediaItem { Id = "c", Kind = MediaKind.Music, Source = "cdda" }));
        Assert.False(ListViewConfigs.IsLocalLibraryItem(new MediaItem { Id = "d", Kind = MediaKind.Music, Source = "device:/mnt/ipod" }));
    }

    [Fact]
    public void A_mounted_share_does_not_leak_into_the_music_view()
    {
        var music = ListViewConfigs.Get("Music");
        Assert.NotNull(music);

        Assert.True(music!.BaseFilter(Local()));
        Assert.False(music.BaseFilter(Shared()));
    }

    [Fact]
    public void A_mounted_share_does_not_leak_into_bad_format()
    {
        var badFormat = ListViewConfigs.Get("BadFormat");
        Assert.NotNull(badFormat);

        // A remote track with every metadata sin there is: still not our to-do item.
        var shabbyShare = new MediaItem
        {
            Id = "share:h:1:x",
            Kind = MediaKind.Music,
            Source = "share:h:1",
            StreamUrl = "http://h:1/stream/x",
            Extension = ".mp3",
        };

        Assert.False(badFormat!.BaseFilter(shabbyShare));

        // The same sins on a local file still land in the view.
        var shabbyLocal = new MediaItem { Id = "l", Kind = MediaKind.Music, FilePath = @"C:\a.mp3", Extension = ".mp3" };
        Assert.True(badFormat.BaseFilter(shabbyLocal));
    }

    [Fact]
    public void A_mounted_share_does_not_leak_into_audiobooks()
    {
        var audiobooks = ListViewConfigs.Get("Audiobooks");
        Assert.NotNull(audiobooks);

        var sharedBook = new MediaItem { Id = "share:h:1:b", Kind = MediaKind.Audiobook, Source = "share:h:1", StreamUrl = "http://h:1/stream/b" };
        var localBook = new MediaItem { Id = "b", Kind = MediaKind.Audiobook, FilePath = @"C:\b.m4b" };

        Assert.False(audiobooks!.BaseFilter(sharedBook));
        Assert.True(audiobooks.BaseFilter(localBook));
    }

    [Fact]
    public void The_share_view_shows_only_that_shares_tracks()
    {
        var config = ListViewConfigs.BuildShareConfig("192.168.1.50:7391");

        Assert.True(config.BaseFilter(Shared()));
        Assert.False(config.BaseFilter(Local()));

        // Another share on the same LAN keeps its own rows.
        var other = new MediaItem { Id = "share:192.168.1.51:7391:abc", Kind = MediaKind.Music, Source = "share:192.168.1.51:7391" };
        Assert.False(config.BaseFilter(other));
    }

    [Fact]
    public void The_share_view_offers_no_control_that_cannot_do_anything()
    {
        var config = ListViewConfigs.BuildShareConfig("h:1");

        // Read-only means read-only: no star to set, no rating to store. A control that
        // silently does nothing is worse than no control.
        Assert.DoesNotContain(config.Columns, c => c.Type == ColumnType.FavoriteTitle);
        Assert.DoesNotContain(config.Columns, c => c.Type == ColumnType.Rating);

        // But it still reads like the Music view otherwise.
        Assert.Contains(config.Columns, c => c.Header == "Title");
        Assert.Contains(config.Columns, c => c.Header == "Artist");
        Assert.Contains(config.Columns, c => c.Header == "Album");
        Assert.Contains(config.Columns, c => c.Header == "Duration");
    }
}
