// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class GenreNormalizerTests
{
    // -- Single-tag Normalize --

    [Theory]
    [InlineData("rock", "Classic Rock")]
    [InlineData("Rock", "Classic Rock")]
    [InlineData("ROCK", "Classic Rock")]
    [InlineData("classic rock", "Classic Rock")]
    [InlineData("alternative", "Alt/Modern Rock")]
    [InlineData("indie rock", "Alt/Modern Rock")]
    [InlineData("punk", "Alt/Modern Rock")]
    [InlineData("grunge", "Alt/Modern Rock")]
    [InlineData("heavy metal", "Hard Rock / Metal")]
    [InlineData("death metal", "Hard Rock / Metal")]
    [InlineData("hard rock", "Hard Rock / Metal")]
    [InlineData("pop", "Top 40/Pop")]
    [InlineData("top 40", "Top 40/Pop")]
    [InlineData("hits", "Top 40/Pop")]
    public void Normalize_RockAndPopFamilies(string input, string expected)
    {
        Assert.Equal(expected, GenreNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("jazz", "Jazz")]
    [InlineData("smooth jazz", "Jazz")]
    [InlineData("big band", "Jazz")]
    [InlineData("blues", "Blues")]
    [InlineData("delta blues", "Blues")]
    [InlineData("classical", "Classical")]
    [InlineData("opera", "Classical")]
    [InlineData("orchestra", "Classical")]
    public void Normalize_JazzBluesClassical(string input, string expected)
    {
        Assert.Equal(expected, GenreNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("hip hop", "Urban")]
    [InlineData("hip-hop", "Urban")]
    [InlineData("rap", "Urban")]
    [InlineData("r&b", "Urban")]
    [InlineData("soul", "Urban")]
    [InlineData("funk", "Urban")]
    public void Normalize_UrbanFamily(string input, string expected)
    {
        Assert.Equal(expected, GenreNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("electronica", "Electronica")]
    [InlineData("techno", "Electronica")]
    [InlineData("house", "Electronica")]
    [InlineData("dance", "Electronica")]
    [InlineData("edm", "Electronica")]
    [InlineData("ambient", "Ambient")]
    [InlineData("chillout", "Ambient")]
    [InlineData("lounge", "Ambient")]
    public void Normalize_ElectronicAndAmbient(string input, string expected)
    {
        Assert.Equal(expected, GenreNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("country", "Country")]
    [InlineData("bluegrass", "Americana")]
    [InlineData("folk", "Americana")]
    [InlineData("singer-songwriter", "Americana")]
    public void Normalize_CountryAndAmericana(string input, string expected)
    {
        Assert.Equal(expected, GenreNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("reggae", "Reggae/Island")]
    [InlineData("dub", "Reggae/Island")]
    [InlineData("ska", "Reggae/Island")]
    [InlineData("dancehall", "Reggae/Island")]
    [InlineData("island", "Reggae/Island")]
    public void Normalize_ReggaeAndIsland(string input, string expected)
    {
        Assert.Equal(expected, GenreNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("latin", "International")]
    [InlineData("salsa", "International")]
    [InlineData("k-pop", "International")]
    [InlineData("bollywood", "International")]
    [InlineData("celtic", "International")]
    [InlineData("arabic", "International")]
    public void Normalize_International(string input, string expected)
    {
        Assert.Equal(expected, GenreNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("news", "Talk/Spoken Word")]
    [InlineData("talk", "Talk/Spoken Word")]
    [InlineData("comedy", "Talk/Spoken Word")]
    [InlineData("audiobook", "Talk/Spoken Word")]
    [InlineData("npr", "Public")]
    [InlineData("public radio", "Public")]
    [InlineData("sports", "Sports")]
    [InlineData("nfl", "Sports")]
    public void Normalize_SpokenWordCategories(string input, string expected)
    {
        Assert.Equal(expected, GenreNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("christian", "Religious")]
    [InlineData("gospel", "Religious")]
    [InlineData("worship", "Religious")]
    public void Normalize_Religious(string input, string expected)
    {
        Assert.Equal(expected, GenreNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("50s", "50s/60s Pop")]
    [InlineData("60s", "50s/60s Pop")]
    [InlineData("doo-wop", "50s/60s Pop")]
    [InlineData("oldies", "50s/60s Pop")]
    [InlineData("70s", "70s/80s Pop")]
    [InlineData("80s", "70s/80s Pop")]
    [InlineData("new wave", "70s/80s Pop")]
    [InlineData("disco", "70s/80s Pop")]
    public void Normalize_DecadeBuckets(string input, string expected)
    {
        Assert.Equal(expected, GenreNormalizer.Normalize(input));
    }

    // -- Specificity / rule order --

    [Fact]
    public void Normalize_ChristianRock_PrefersReligiousOverRock()
    {
        Assert.Equal("Religious", GenreNormalizer.Normalize("christian rock"));
    }

    [Fact]
    public void Normalize_HardRock_PrefersMetalOverRock()
    {
        Assert.Equal("Hard Rock / Metal", GenreNormalizer.Normalize("hard rock"));
    }

    [Fact]
    public void Normalize_IndieRock_PrefersAltOverRock()
    {
        Assert.Equal("Alt/Modern Rock", GenreNormalizer.Normalize("indie rock"));
    }

    [Fact]
    public void Normalize_ClassicRock_WinsBeforeGenericRock()
    {
        Assert.Equal("Classic Rock", GenreNormalizer.Normalize("classic rock"));
    }

    [Fact]
    public void Normalize_PopRock_PopWinsDueToRuleOrder()
    {
        // "pop rock" contains both "pop" and "rock" as whole words. "pop" appears earlier
        // in the rule table than the catch-all "rock", so Top 40/Pop wins. Documented
        // behavior — pop-rock stations tend to be chart/radio-friendly.
        Assert.Equal("Top 40/Pop", GenreNormalizer.Normalize("pop rock"));
    }

    [Fact]
    public void Normalize_Unknown_ReturnsOther()
    {
        Assert.Equal("Other", GenreNormalizer.Normalize("klingon ceremonial"));
    }

    [Fact]
    public void Normalize_NullOrWhitespace_ReturnsOther()
    {
        Assert.Equal("Other", GenreNormalizer.Normalize(""));
        Assert.Equal("Other", GenreNormalizer.Normalize("   "));
    }

    // -- ExtractPrimaryGenre (multi-tag) --

    [Fact]
    public void ExtractPrimaryGenre_CommaSeparated_ReturnsFirstMatch()
    {
        Assert.Equal("Classic Rock", GenreNormalizer.ExtractPrimaryGenre("rock,indie,alternative"));
    }

    [Fact]
    public void ExtractPrimaryGenre_UnrecognizedFirst_FallsThroughToMatch()
    {
        Assert.Equal("Jazz", GenreNormalizer.ExtractPrimaryGenre("unrecognized,bebop,jazz"));
    }

    [Fact]
    public void ExtractPrimaryGenre_SlashSeparated_TakesFirstMatch()
    {
        // Split on '/' gives ["Electronic", "Dance", "Pop"]. "Electronic" matches first.
        Assert.Equal("Electronica", GenreNormalizer.ExtractPrimaryGenre("Electronic/Dance/Pop"));
    }

    [Fact]
    public void ExtractPrimaryGenre_AllUnknown_ReturnsOther()
    {
        Assert.Equal("Other", GenreNormalizer.ExtractPrimaryGenre("xyz,abc,foo"));
    }

    [Fact]
    public void ExtractPrimaryGenre_NullOrEmpty_ReturnsOther()
    {
        Assert.Equal("Other", GenreNormalizer.ExtractPrimaryGenre(null));
        Assert.Equal("Other", GenreNormalizer.ExtractPrimaryGenre(""));
    }

    [Fact]
    public void ExtractPrimaryGenre_Whitespace_Trimmed()
    {
        Assert.Equal("Jazz", GenreNormalizer.ExtractPrimaryGenre("  jazz  "));
    }

    // -- Canonical list coverage --

    [Fact]
    public void AllCanonical_ContainsExpectedBuckets()
    {
        Assert.Contains("50s/60s Pop", GenreNormalizer.AllCanonical);
        Assert.Contains("Classic Rock", GenreNormalizer.AllCanonical);
        Assert.Contains("Jazz", GenreNormalizer.AllCanonical);
        Assert.Contains("Other", GenreNormalizer.AllCanonical);
    }

    [Fact]
    public void AllCanonical_OtherIsLast()
    {
        Assert.Equal("Other", GenreNormalizer.AllCanonical[^1]);
    }

    [Fact]
    public void AllCanonical_HasUniqueEntries()
    {
        var distinct = GenreNormalizer.AllCanonical.Distinct().Count();
        Assert.Equal(GenreNormalizer.AllCanonical.Count, distinct);
    }
}
