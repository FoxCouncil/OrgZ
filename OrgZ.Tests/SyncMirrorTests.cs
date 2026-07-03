// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;
using OrgZ.ViewModels;

namespace OrgZ.Tests;

/// <summary>
/// The auto-sync (mirror) removal decision - the destructive core, so it's pinned as a pure
/// function: device music whose artist+title isn't in the keep-set is removed, matches are kept
/// (case-insensitively), and untagged tracks are NEVER removed (we can't prove they were deselected).
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

    private static HashSet<string> Keep(params (string Artist, string Title)[] items)
        => items.Select(x => MainWindowViewModel.NormalizeMatchKey(x.Artist, x.Title))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
        var removals = MainWindowViewModel.MirrorRemovals([Track("A", "1"), Track("B", "2")], []);
        Assert.Equal(2, removals.Count);
    }
}
