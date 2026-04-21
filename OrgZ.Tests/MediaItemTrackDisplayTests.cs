// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class MediaItemTrackDisplayTests
{
    private static MediaItem Make(uint? track, uint? totalTracks)
    {
        return new MediaItem
        {
            Id = "t",
            Kind = MediaKind.Music,
            Track = track,
            TotalTracks = totalTracks,
        };
    }

    [Theory]
    [InlineData(1u, 12u, "1 of 12")]
    [InlineData(5u, 5u,  "5 of 5")]
    [InlineData(1u, 1u,  "1 of 1")]
    public void Both_track_and_total_produces_X_of_Y(uint track, uint total, string expected)
    {
        Assert.Equal(expected, Make(track, total).TrackDisplay);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(12u)]
    public void Track_without_total_shows_just_the_number(uint track)
    {
        Assert.Equal(track.ToString(), Make(track, null).TrackDisplay);
    }

    [Fact]
    public void Track_with_zero_total_shows_just_the_number()
    {
        // TotalTracks=0 is iTunesDB's "unset" representation; treat it as no-total.
        Assert.Equal("3", Make(3, 0).TrackDisplay);
    }

    [Fact]
    public void Missing_track_returns_empty()
    {
        Assert.Equal(string.Empty, Make(null, null).TrackDisplay);
        Assert.Equal(string.Empty, Make(null, 12).TrackDisplay);
    }

    [Fact]
    public void Track_change_fires_PropertyChanged_for_TrackDisplay()
    {
        var item = Make(null, null);
        var notified = new List<string?>();
        item.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        item.Track = 1;

        Assert.Contains(nameof(MediaItem.TrackDisplay), notified);
    }

    [Fact]
    public void TotalTracks_change_fires_PropertyChanged_for_TrackDisplay()
    {
        var item = Make(1, null);
        var notified = new List<string?>();
        item.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        item.TotalTracks = 12;

        Assert.Contains(nameof(MediaItem.TrackDisplay), notified);
    }
}
