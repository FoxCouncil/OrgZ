// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;

namespace OrgZ.Tests;

public class GenreNormalizerTests
{
    // -- Single-tag Normalize --

    [Theory]
    [InlineData("rock", "Alternative Rock")]
    [InlineData("Rock", "Alternative Rock")]
    [InlineData("ROCK", "Alternative Rock")]
    [InlineData("alternative", "Alternative Rock")]
    [InlineData("grunge", "Alternative Rock")]
    [InlineData("shoegaze", "Alternative Rock")]
    [InlineData("post-punk", "Alternative Rock")]
    [InlineData("punk", "Punk")]
    [InlineData("pop punk", "Punk")]
    [InlineData("hardcore", "Punk")]
    [InlineData("emo", "Punk")]
    [InlineData("indie", "Indie")]
    [InlineData("indie rock", "Indie")]
    [InlineData("college", "Indie")]
    [InlineData("heavy metal", "Metal")]
    [InlineData("death metal", "Metal")]
    [InlineData("hard rock", "Metal")]
    [InlineData("metalcore", "Metal")]
    public void Normalize_RockFamilies(string input, string expected)
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
    [InlineData("hip hop", "Hip-Hop/Rap")]
    [InlineData("hip-hop", "Hip-Hop/Rap")]
    [InlineData("rap", "Hip-Hop/Rap")]
    [InlineData("trap", "Hip-Hop/Rap")]
    [InlineData("urban", "Hip-Hop/Rap")]
    [InlineData("r&b", "Motown/Soul")]
    [InlineData("soul", "Motown/Soul")]
    [InlineData("motown", "Motown/Soul")]
    [InlineData("funk", "Disco/Funk")]
    [InlineData("disco", "Disco/Funk")]
    public void Normalize_UrbanSoulFunkFamilies(string input, string expected)
    {
        Assert.Equal(expected, GenreNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("electronica", "Electronic/Dance")]
    [InlineData("techno", "Electronic/Dance")]
    [InlineData("house", "Electronic/Dance")]
    [InlineData("dance", "Electronic/Dance")]
    [InlineData("edm", "Electronic/Dance")]
    [InlineData("drum and bass", "Electronic/Dance")]
    [InlineData("ambient", "Ambient/Chillout")]
    [InlineData("chillout", "Ambient/Chillout")]
    [InlineData("lounge", "Ambient/Chillout")]
    [InlineData("downtempo", "Ambient/Chillout")]
    [InlineData("lo-fi", "Lo-Fi")]
    [InlineData("lofi", "Lo-Fi")]
    [InlineData("chillhop", "Lo-Fi")]
    [InlineData("synthwave", "Synthwave")]
    [InlineData("retrowave", "Synthwave")]
    [InlineData("vaporwave", "Synthwave")]
    public void Normalize_ElectronicAmbientLoFiSynthwave(string input, string expected)
    {
        Assert.Equal(expected, GenreNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("country", "Country")]
    [InlineData("folk", "Country")]
    [InlineData("americana", "Country")]
    [InlineData("singer-songwriter", "Country")]
    [InlineData("bluegrass", "Bluegrass")]
    [InlineData("appalachian", "Bluegrass")]
    public void Normalize_CountryAndBluegrass(string input, string expected)
    {
        Assert.Equal(expected, GenreNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("reggae", "Reggae")]
    [InlineData("dub", "Reggae")]
    [InlineData("ska", "Reggae")]
    [InlineData("dancehall", "Reggae")]
    public void Normalize_Reggae(string input, string expected)
    {
        Assert.Equal(expected, GenreNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("latin", "Latin")]
    [InlineData("salsa", "Latin")]
    [InlineData("reggaeton", "Latin")]
    [InlineData("bossa nova", "Latin")]
    [InlineData("brazilian", "Latin")]
    [InlineData("k-pop", "World")]
    [InlineData("bollywood", "World")]
    [InlineData("celtic", "World")]
    [InlineData("arabic", "World")]
    [InlineData("world", "World")]
    public void Normalize_LatinAndWorld(string input, string expected)
    {
        Assert.Equal(expected, GenreNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("news", "News/Talk Radio")]
    [InlineData("talk", "News/Talk Radio")]
    [InlineData("npr", "News/Talk Radio")]
    [InlineData("public radio", "News/Talk Radio")]
    [InlineData("audiobook", "News/Talk Radio")]
    [InlineData("comedy", "Comedy")]
    [InlineData("stand-up", "Comedy")]
    [InlineData("sports", "Sports Talk")]
    [InlineData("nfl", "Sports Talk")]
    [InlineData("hockey", "Sports Talk")]
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
    [InlineData("50s", "50s")]
    [InlineData("1950s", "50s")]
    [InlineData("doo-wop", "50s")]
    [InlineData("rockabilly", "50s")]
    [InlineData("60s", "60s")]
    [InlineData("1960s", "60s")]
    [InlineData("oldies", "60s")]
    [InlineData("70s", "70s")]
    [InlineData("classic rock", "70s")]
    [InlineData("prog rock", "70s")]
    [InlineData("80s", "80s")]
    [InlineData("1980s", "80s")]
    [InlineData("new wave", "80s")]
    [InlineData("synthpop", "80s")]
    [InlineData("synth-pop", "80s")]
    [InlineData("90s", "90s")]
    [InlineData("britpop", "90s")]
    [InlineData("2000s", "2000s")]
    [InlineData("00s", "2000s")]
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
        Assert.Equal("Metal", GenreNormalizer.Normalize("hard rock"));
    }

    [Fact]
    public void Normalize_SportsTalk_PrefersSportsOverTalk()
    {
        Assert.Equal("Sports Talk", GenreNormalizer.Normalize("sports talk"));
    }

    [Fact]
    public void Normalize_ComedyTalk_PrefersComedyOverTalk()
    {
        Assert.Equal("Comedy", GenreNormalizer.Normalize("comedy talk"));
    }

    [Fact]
    public void Normalize_SynthwaveBeatsElectronicSynthCatchAll()
    {
        Assert.Equal("Synthwave", GenreNormalizer.Normalize("synthwave"));
        Assert.Equal("Electronic/Dance", GenreNormalizer.Normalize("synth"));
        Assert.Equal("80s", GenreNormalizer.Normalize("synth-pop"));
    }

    [Fact]
    public void Normalize_LoFiBeatsAmbientChill()
    {
        Assert.Equal("Lo-Fi", GenreNormalizer.Normalize("lofi chill"));
    }

    [Fact]
    public void Normalize_SkaPunk_PunkWinsDueToRuleOrder()
    {
        Assert.Equal("Punk", GenreNormalizer.Normalize("ska punk"));
    }

    [Fact]
    public void Normalize_DubstepIsElectronicNotReggaeDub()
    {
        Assert.Equal("Electronic/Dance", GenreNormalizer.Normalize("dubstep"));
    }

    [Fact]
    public void Normalize_ReggaetonIsLatinNotReggae()
    {
        Assert.Equal("Latin", GenreNormalizer.Normalize("reggaeton"));
    }

    [Fact]
    public void Normalize_PopAndChartTagsAreDeliberatelyOther()
    {
        // The taxonomy has no pop/chart bucket - these need a human call during curation.
        Assert.Equal("Other", GenreNormalizer.Normalize("pop"));
        Assert.Equal("Other", GenreNormalizer.Normalize("top 40"));
        Assert.Equal("Other", GenreNormalizer.Normalize("hits"));
        Assert.Equal("Other", GenreNormalizer.Normalize("adult contemporary"));
        Assert.Equal("Other", GenreNormalizer.Normalize("eclectic"));
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
        Assert.Equal("Alternative Rock", GenreNormalizer.ExtractPrimaryGenre("rock,indie,alternative"));
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
        Assert.Equal("Electronic/Dance", GenreNormalizer.ExtractPrimaryGenre("Electronic/Dance/Pop"));
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

    [Fact]
    public void ExtractPrimaryGenre_RoundTripsEveryCanonicalDisplayName()
    {
        // Bundled stations carry Tags = RadioGenre display name; the normalizer must map
        // each display name back onto itself so NormalizedGenre agrees with the taxonomy.
        foreach (var genre in RadioGenres.All)
        {
            var name = genre.DisplayName();
            Assert.Equal(name, GenreNormalizer.ExtractPrimaryGenre(name));
        }
    }

    // -- Canonical list coverage --

    [Fact]
    public void AllCanonical_MirrorsRadioGenreTaxonomyPlusOther()
    {
        var expected = RadioGenres.All.Select(g => g.DisplayName()).Append("Other").ToList();
        Assert.Equal(expected, GenreNormalizer.AllCanonical);
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

    [Fact]
    public void AllRuleOutputs_ExistInAllCanonical()
    {
        // Every genre that any rule can produce must be present in the AllCanonical list.
        // This catches typos in rule definitions (e.g., "Clasical" vs "Classical").
        var canonicalSet = new HashSet<string>(GenreNormalizer.AllCanonical);

        // Test a comprehensive set of inputs that exercise most rules
        var testInputs = new[]
        {
            "christian", "gospel", "npr", "public radio", "talk", "news", "comedy",
            "sports", "nfl", "death metal", "metal", "hard rock", "alternative", "indie",
            "punk", "emo", "classic rock", "prog rock", "50s", "60s", "70s", "80s", "90s",
            "2000s", "disco", "hip-hop", "rap", "r&b", "soul", "funk", "ambient", "chillout",
            "lounge", "electronic", "techno", "house", "dance", "country", "bluegrass",
            "folk", "acoustic", "jazz", "blues", "classical", "opera", "reggae", "ska",
            "latin", "salsa", "k-pop", "world", "synthwave", "lofi", "top 40", "pop",
            "hits", "eclectic", "variety", "rock", "completely unknown tag"
        };

        foreach (var input in testInputs)
        {
            var result = GenreNormalizer.Normalize(input);
            Assert.True(canonicalSet.Contains(result), $"Rule output \"{result}\" (from input \"{input}\") is not in AllCanonical");
        }
    }
}
