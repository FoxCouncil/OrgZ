// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// Track identity across the library/device boundary: the library path is the id, a device track
/// carries it as its dbid, and the only fallback is artist + title + album + length. The case that
/// motivated it - a live take and a studio version with the same name - is pinned as distinct.
/// </summary>
public class DeviceTrackIdentityTests
{
    private static MediaItem Library(string path, string artist, string title, string album, int seconds) => new()
    {
        Id = path,
        Kind = MediaKind.Music,
        FilePath = path,
        Artist = artist,
        Title = title,
        Album = album,
        Duration = TimeSpan.FromSeconds(seconds),
    };

    private static MediaItem OnDevice(ulong? dbid, string artist, string title, string album, int seconds) => new()
    {
        Id = $"device:{dbid}",
        Kind = MediaKind.Music,
        Source = "device:X:",
        FilePath = "X:/iPod_Control/Music/F07/ABCD.m4a",
        Artist = artist,
        Title = title,
        Album = album,
        Duration = TimeSpan.FromSeconds(seconds),
        Dbid = dbid,
    };

    [Fact]
    public void The_same_file_always_gets_the_same_id_and_never_zero()
    {
        var a = DeviceTrackIdentity.DbidFor(@"D:\DJ\Zero 7\Simple Things\01 - Polaris.flac");
        var b = DeviceTrackIdentity.DbidFor(@"D:\DJ\Zero 7\Simple Things\01 - Polaris.flac");
        var c = DeviceTrackIdentity.DbidFor(@"D:\DJ\Zero 7\Simple Things\02 - Distractions.flac");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(0UL, a);
    }

    [Fact]
    public void Separator_style_does_not_change_the_id()
    {
        Assert.Equal(
            DeviceTrackIdentity.DbidFor(@"D:\DJ\Zero 7\01 - Polaris.flac"),
            DeviceTrackIdentity.DbidFor("D:/DJ/Zero 7/01 - Polaris.flac"));
    }

    [Fact]
    public void A_device_track_with_the_files_id_matches_whatever_its_tags_say()
    {
        var file = @"D:\DJ\Zero 7\01 - Polaris.flac";
        var library = Library(file, "Zero 7", "Polaris", "Simple Things", 288);
        // Retagged since it was synced - the id still finds it.
        var matcher = new DeviceTrackIdentity.DeviceMatcher([OnDevice(DeviceTrackIdentity.DbidFor(file), "ZERO SEVEN", "Polaris (remaster)", "Best Of", 290)]);

        Assert.NotNull(matcher.Match(library));
        Assert.True(matcher.Contains(library));
    }

    [Fact]
    public void A_live_take_and_the_studio_version_are_different_tracks()
    {
        // Same artist and title; different album and length. The old artist+title rule called them
        // one song and never synced the second.
        var studio = Library(@"D:\DJ\A\Studio\01 - Song.flac", "Artist", "Song", "Studio Album", 240);
        var live = Library(@"D:\DJ\A\Live\05 - Song.flac", "Artist", "Song", "Live at the Den", 312);

        // The device holds the studio one, written by an older build (random dbid).
        var matcher = new DeviceTrackIdentity.DeviceMatcher([OnDevice(0x1234, "Artist", "Song", "Studio Album", 240)]);

        Assert.True(matcher.Contains(studio));
        Assert.False(matcher.Contains(live));
    }

    [Fact]
    public void The_fallback_tolerates_a_second_of_length_disagreement()
    {
        var library = Library(@"D:\DJ\A\01 - Song.flac", "Artist", "Song", "Album", 240);
        Assert.True(new DeviceTrackIdentity.DeviceMatcher([OnDevice(0x1, "Artist", "Song", "Album", 241)]).Contains(library));
        Assert.True(new DeviceTrackIdentity.DeviceMatcher([OnDevice(0x1, "Artist", "Song", "Album", 239)]).Contains(library));
        Assert.False(new DeviceTrackIdentity.DeviceMatcher([OnDevice(0x1, "Artist", "Song", "Album", 245)]).Contains(library));
    }

    [Fact]
    public void An_untitled_track_never_matches_by_fallback()
    {
        var library = Library(@"D:\DJ\untagged.flac", "", "", "", 100);
        var matcher = new DeviceTrackIdentity.DeviceMatcher([OnDevice(0x1, "", "", "", 100)]);
        Assert.False(matcher.Contains(library));
    }

    [Fact]
    public void The_keep_set_covers_a_device_track_by_id_or_by_strict_key_only()
    {
        var keep = new DeviceTrackIdentity.KeepSet();
        var kept = Library(@"D:\DJ\A\01 - Keep.flac", "Artist", "Keep", "Album", 200);
        keep.Add(kept);

        Assert.True(keep.Covers(OnDevice(DeviceTrackIdentity.DbidFor(kept.FilePath!), "x", "y", "z", 1)));   // by id
        Assert.True(keep.Covers(OnDevice(0x99, "Artist", "Keep", "Album", 200)));                         // by strict key
        Assert.False(keep.Covers(OnDevice(0x99, "Artist", "Keep", "Live", 260)));                         // same name, other record
        Assert.False(keep.Covers(OnDevice(0x99, "Artist", "Other", "Album", 200)));

        Assert.False(DeviceTrackIdentity.KeepSet.IsIdentifiable(OnDevice(0x5, "", "", "", 0)));
    }
}
