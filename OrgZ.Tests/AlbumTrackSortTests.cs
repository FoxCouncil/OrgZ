// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Helpers;

namespace OrgZ.Tests;

public class AlbumTrackSortTests
{
    private static MediaItem T(string title, uint? track = null, uint? disc = null) => new()
    {
        Id = title,
        Kind = MediaKind.Music,
        Title = title,
        Track = track,
        Disc = disc,
    };

    [Fact]
    public void Orders_by_track_number_when_all_tracks_have_numbers()
    {
        var tracks = new[]
        {
            T("C", track: 3),
            T("A", track: 1),
            T("B", track: 2),
        };
        var ordered = AlbumTrackSort.Order(tracks).ToList();
        Assert.Equal(["A", "B", "C"], ordered.Select(t => t.Title));
    }

    [Fact]
    public void Orders_by_disc_first_then_track()
    {
        // Disc 2 track 1 should come AFTER Disc 1 track 99
        var tracks = new[]
        {
            T("Disc2Track1", track: 1, disc: 2),
            T("Disc1Track99", track: 99, disc: 1),
            T("Disc1Track1", track: 1, disc: 1),
        };
        var ordered = AlbumTrackSort.Order(tracks).ToList();
        Assert.Equal(["Disc1Track1", "Disc1Track99", "Disc2Track1"], ordered.Select(t => t.Title));
    }

    [Fact]
    public void Missing_disc_sorts_as_disc_1()
    {
        var tracks = new[]
        {
            T("B-Disc2", track: 1, disc: 2),
            T("A-NoDisc", track: 1, disc: null),
        };
        var ordered = AlbumTrackSort.Order(tracks).ToList();
        // Both are "track 1" but the null-disc one sorts as disc=1, so it comes first
        Assert.Equal(["A-NoDisc", "B-Disc2"], ordered.Select(t => t.Title));
    }

    [Fact]
    public void Tracks_without_number_sort_to_the_end()
    {
        var tracks = new[]
        {
            T("BonusUnnumbered", track: null),
            T("Track1", track: 1),
            T("Track2", track: 2),
        };
        var ordered = AlbumTrackSort.Order(tracks).ToList();
        Assert.Equal(["Track1", "Track2", "BonusUnnumbered"], ordered.Select(t => t.Title));
    }

    [Fact]
    public void Title_is_final_tiebreaker_for_tracks_sharing_disc_and_number()
    {
        // Very unusual, but: two tracks with the same disc+track should still sort deterministically
        var tracks = new[]
        {
            T("Zebra",  track: 1, disc: 1),
            T("Apple",  track: 1, disc: 1),
            T("Mango",  track: 1, disc: 1),
        };
        var ordered = AlbumTrackSort.Order(tracks).ToList();
        Assert.Equal(["Apple", "Mango", "Zebra"], ordered.Select(t => t.Title));
    }

    [Fact]
    public void Title_tiebreaker_is_case_insensitive()
    {
        var tracks = new[]
        {
            T("bravo", track: 1),
            T("ALPHA", track: 1),
        };
        var ordered = AlbumTrackSort.Order(tracks).ToList();
        Assert.Equal(["ALPHA", "bravo"], ordered.Select(t => t.Title));
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        Assert.Empty(AlbumTrackSort.Order([]));
    }

    [Fact]
    public void Single_item_returns_unchanged()
    {
        var t = T("Solo", track: 5, disc: 2);
        var ordered = AlbumTrackSort.Order([t]).ToList();
        Assert.Single(ordered);
        Assert.Equal("Solo", ordered[0].Title);
    }
}
