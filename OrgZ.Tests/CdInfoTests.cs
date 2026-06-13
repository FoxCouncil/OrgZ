// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;

namespace OrgZ.Tests;

/// <summary>The CD info-bar display model: "-" placeholders, pluralization, time formatting.</summary>
public class CdInfoTests
{
    [Fact]
    public void Empty_fields_render_placeholders()
    {
        var cd = new CdInfo();
        Assert.Equal("Audio CD", cd.AlbumDisplay);   // no album -> generic
        Assert.Equal("—", cd.ArtistDisplay);
        Assert.Equal("—", cd.YearDisplay);
        Assert.Equal("—", cd.GenreDisplay);
        Assert.Equal("—", cd.DiscIdDisplay);
        Assert.Equal("—", cd.ReleaseMbidDisplay);
        Assert.False(cd.HasCoverArt);
    }

    [Fact]
    public void Populated_fields_render_verbatim()
    {
        var cd = new CdInfo
        {
            Album = "Rumours",
            Artist = "Fleetwood Mac",
            Year = 1977,
            Genre = "Rock",
            DiscId = "abc123",
            ReleaseMbid = "mbid-xyz",
        };
        Assert.Equal("Rumours", cd.AlbumDisplay);
        Assert.Equal("Fleetwood Mac", cd.ArtistDisplay);
        Assert.Equal("1977", cd.YearDisplay);
        Assert.Equal("Rock", cd.GenreDisplay);
        Assert.Equal("abc123", cd.DiscIdDisplay);
        Assert.Equal("mbid-xyz", cd.ReleaseMbidDisplay);
    }

    [Fact]
    public void YearDisplay_treats_zero_as_unknown()
        => Assert.Equal("—", new CdInfo { Year = 0 }.YearDisplay);

    [Fact]
    public void AlbumDisplay_falls_back_for_whitespace()
        => Assert.Equal("Audio CD", new CdInfo { Album = "   " }.AlbumDisplay);

    [Theory]
    [InlineData(1, "1 track")]
    [InlineData(0, "0 tracks")]
    [InlineData(12, "12 tracks")]
    public void TrackCountDisplay_pluralizes(int count, string expected)
        => Assert.Equal(expected, new CdInfo { TrackCount = count }.TrackCountDisplay);

    [Fact]
    public void TotalTimeDisplay_uses_m_ss_under_an_hour()
        => Assert.Equal("4:05", new CdInfo { TotalDuration = TimeSpan.FromSeconds(245) }.TotalTimeDisplay);

    [Fact]
    public void TotalTimeDisplay_uses_h_mm_ss_over_an_hour()
        => Assert.Equal("1:02:05", new CdInfo { TotalDuration = TimeSpan.FromSeconds(3725) }.TotalTimeDisplay);
}
