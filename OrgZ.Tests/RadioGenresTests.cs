// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;

namespace OrgZ.Tests;

/// <summary>The radio genre taxonomy - display names the Radio grid groups by.</summary>
public class RadioGenresTests
{
    [Theory]
    [InlineData(RadioGenre.Fifties, "50s")]
    [InlineData(RadioGenre.TwoThousands, "2000s")]
    [InlineData(RadioGenre.AlternativeRock, "Alternative Rock")]
    [InlineData(RadioGenre.AmbientChillout, "Ambient/Chillout")]
    [InlineData(RadioGenre.DiscoFunk, "Disco/Funk")]
    [InlineData(RadioGenre.ElectronicDance, "Electronic/Dance")]
    [InlineData(RadioGenre.HipHopRap, "Hip-Hop/Rap")]
    [InlineData(RadioGenre.LoFi, "Lo-Fi")]
    [InlineData(RadioGenre.MotownSoul, "Motown/Soul")]
    [InlineData(RadioGenre.NewsTalkRadio, "News/Talk Radio")]
    [InlineData(RadioGenre.SportsTalk, "Sports Talk")]
    [InlineData(RadioGenre.Synthwave, "Synthwave")]
    public void DisplayName_maps_genre_to_label(RadioGenre genre, string expected)
        => Assert.Equal(expected, genre.DisplayName());

    [Fact]
    public void DisplayName_unknown_is_empty()
        => Assert.Equal("", RadioGenre.Unknown.DisplayName());

    [Fact]
    public void DisplayName_int_overload_matches_enum_and_handles_unknown()
    {
        Assert.Equal("Jazz", RadioGenres.DisplayName((int)RadioGenre.Jazz));
        Assert.Equal("", RadioGenres.DisplayName(9999));
    }

    [Fact]
    public void All_lists_every_named_genre_once_and_excludes_Unknown()
    {
        var all = RadioGenres.All.ToList();
        Assert.Equal(30, all.Count);
        Assert.DoesNotContain(RadioGenre.Unknown, all);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.All(all, g => Assert.NotEqual("", g.DisplayName()));
    }

    [Fact]
    public void FromDisplayName_round_trips_every_genre()
    {
        foreach (var genre in RadioGenres.All)
        {
            Assert.Equal(genre, RadioGenres.FromDisplayName(genre.DisplayName()));
        }
    }

    [Fact]
    public void FromDisplayName_is_case_insensitive_and_unknown_for_garbage()
    {
        Assert.Equal(RadioGenre.HipHopRap, RadioGenres.FromDisplayName("hip-hop/rap"));
        Assert.Equal(RadioGenre.Unknown, RadioGenres.FromDisplayName("Polka Marathon"));
        Assert.Equal(RadioGenre.Unknown, RadioGenres.FromDisplayName(null));
        Assert.Equal(RadioGenre.Unknown, RadioGenres.FromDisplayName(""));
    }
}
