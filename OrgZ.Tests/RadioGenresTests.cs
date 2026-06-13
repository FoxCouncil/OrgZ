// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;

namespace OrgZ.Tests;

/// <summary>The radio genre taxonomy - display names the Radio grid groups by.</summary>
public class RadioGenresTests
{
    [Theory]
    [InlineData(RadioGenre.Seventies, "70's")]
    [InlineData(RadioGenre.Eighties, "80's")]
    [InlineData(RadioGenre.AdultContemporary, "Adult Contemporary")]
    [InlineData(RadioGenre.CollegeUniversity, "College / University")]
    [InlineData(RadioGenre.HardRockMetal, "Hard Rock / Metal")]
    [InlineData(RadioGenre.RnbSoul, "RnB / Soul")]
    [InlineData(RadioGenre.Top40Pop, "Top 40 / Pop")]
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
        Assert.Equal(26, all.Count);
        Assert.DoesNotContain(RadioGenre.Unknown, all);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.All(all, g => Assert.NotEqual("", g.DisplayName()));
    }
}
