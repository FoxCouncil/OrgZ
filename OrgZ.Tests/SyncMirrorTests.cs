// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;
using OrgZ.ViewModels;

namespace OrgZ.Tests;

/// <summary>
/// The auto-sync (mirror) removal decision - the destructive core, so it's pinned as a pure
/// function: device music whose artist+title isn't in the keep-set is removed, matches are kept
/// (case-insensitively), and untagged tracks are never removed (we can't prove they were deselected).
/// </summary>
public class SyncMirrorTests
{
    private static MediaItem Track(string? artist, string? title) => new()
    {
        Id = $"{artist}|{title}",
        Kind = MediaKind.Music,
        Source = "device:X:",
        Artist = artist,
        Title = title,
        FilePath = "X:/Music/x.mp3",
    };

    /// <summary>A library track the plan keeps. No file path on purpose: these tests exercise the
    /// strict-key fallback (the device tracks here carry no library ids either).</summary>
    private static MediaItem LibraryTrack(string artist, string title) => new()
    {
        Id = $"lib|{artist}|{title}",
        Kind = MediaKind.Music,
        Artist = artist,
        Title = title,
    };

    private static DeviceTrackIdentity.KeepSet Keep(params (string Artist, string Title)[] items)
    {
        var keep = new DeviceTrackIdentity.KeepSet();
        foreach (var (artist, title) in items)
        {
            keep.Add(LibraryTrack(artist, title));
        }
        return keep;
    }

    // -- Entire-library capacity preflight: the pure pieces --

    [Theory]
    [InlineData(0, 1_000_000, true)]                                  // free space unknown: proceed
    [InlineData(-1, 1_000_000, true)]
    [InlineData(8_000_000_000, 1_000_000_000, true)]                  // plenty of room
    [InlineData(1_000_000_000, 1_000_000_000, false)]                 // exactly full: margin refused
    [InlineData(1_000_000_000, 1_000_000_000 - 200L * 1024 * 1024, true)]   // fits with the margin to spare
    [InlineData(1_000_000_000, 1_000_000_000 - 200L * 1024 * 1024 + 1, false)]  // one byte into the margin
    public void FitsOnDevice_respects_the_working_space_margin(long free, long needed, bool expected)
    {
        Assert.Equal(expected, MainWindowViewModel.FitsOnDevice(free, needed));
    }

    [Fact]
    public void BytesMissingFromDevice_only_counts_tracks_the_device_lacks()
    {
        var onDevice = new MediaItem { Id = "a", Kind = MediaKind.Music, Artist = "Radiohead", Title = "Creep", FileSize = 5_000_000 };
        var missing = new MediaItem { Id = "b", Kind = MediaKind.Music, Artist = "Boards of Canada", Title = "Roygbiv", FileSize = 7_000_000 };
        var untagged = new MediaItem { Id = "c", Kind = MediaKind.Music, FileSize = 3_000_000 };   // no key: always counted
        var sizeless = new MediaItem { Id = "d", Kind = MediaKind.Music, Artist = "X", Title = "Y" };

        var device = new DeviceTrackIdentity.DeviceMatcher([Track("Radiohead", "Creep")]);

        Assert.Equal(10_000_000, MainWindowViewModel.BytesMissingFromDevice([onDevice, missing, untagged, sizeless], device));
    }

    [Fact]
    public void Removes_device_music_not_in_the_keep_set()
    {
        var kept = Track("Radiohead", "Creep");
        var dropped = Track("Nickelback", "Photograph");

        var removals = MainWindowViewModel.MirrorRemovals([kept, dropped], Keep(("Radiohead", "Creep")));

        var only = Assert.Single(removals);
        Assert.Equal(dropped.Id, only.Id);
    }

    [Fact]
    public void Keeps_selected_tracks_case_insensitively()
    {
        var t = Track("Radiohead", "Creep");
        Assert.Empty(MainWindowViewModel.MirrorRemovals([t], Keep(("radiohead", "CREEP"))));
    }

    [Fact]
    public void Never_removes_untagged_tracks()
    {
        var untagged = Track(null, null);
        var blank = Track("", "");
        Assert.Empty(MainWindowViewModel.MirrorRemovals([untagged, blank], Keep(("Someone", "Something"))));
    }

    [Fact]
    public void Empty_keep_set_removes_every_tagged_track()
    {
        var removals = MainWindowViewModel.MirrorRemovals([Track("A", "1"), Track("B", "2")], new DeviceTrackIdentity.KeepSet());
        Assert.Equal(2, removals.Count);
    }
}
