// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

public class TagRatingTests
{
    // -- POPM 0-255 → stars (the WMP bands) --

    [Theory]
    [InlineData(0, null)]
    [InlineData(-3, null)]
    [InlineData(1, 1)]
    [InlineData(31, 1)]
    [InlineData(32, 2)]
    [InlineData(64, 2)]
    [InlineData(96, 3)]
    [InlineData(128, 3)]
    [InlineData(160, 4)]
    [InlineData(196, 4)]
    [InlineData(224, 5)]
    [InlineData(255, 5)]
    public void StarsFromPopm_uses_the_wmp_bands(int popm, int? expected)
    {
        Assert.Equal(expected, TagRating.StarsFromPopm(popm));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Popm_round_trips_every_star_count(int stars)
    {
        Assert.Equal(stars, TagRating.StarsFromPopm(TagRating.PopmFromStars(stars)));
    }

    [Fact]
    public void PopmFromStars_zero_or_less_clears()
    {
        Assert.Equal(0, TagRating.PopmFromStars(0));
        Assert.Equal(0, TagRating.PopmFromStars(-1));
    }

    // -- Vorbis RATING: 0-100 scale in the wild, occasionally 1-5 --

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("junk", null)]
    [InlineData("0", null)]
    [InlineData("3", 3)]      // direct 1-5
    [InlineData("5", 5)]
    [InlineData("20", 1)]     // 0-100 scale
    [InlineData("40", 2)]
    [InlineData("60", 3)]
    [InlineData("80", 4)]
    [InlineData("100", 5)]
    [InlineData("90", 5)]     // rounds to the nearest band
    [InlineData("10", 1)]     // never rounds a real rating down to zero
    public void StarsFromVorbis_handles_both_scales(string? value, int? expected)
    {
        Assert.Equal(expected, TagRating.StarsFromVorbis(value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void Vorbis_round_trips_every_star_count(int stars)
    {
        // Write side stores stars*20; the read side must give the same stars back.
        Assert.Equal(stars, TagRating.StarsFromVorbis((stars * 20).ToString()));
    }
}
