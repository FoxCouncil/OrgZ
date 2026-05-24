// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class MusicBrainzServiceTests
{
    // -- Null / malformed handling --

    [Fact]
    public void ParseDiscResponse_EmptyReleasesArray_ReturnsNull()
    {
        Assert.Null(MusicBrainzService.ParseDiscResponse("""{ "releases": [] }"""));
    }

    [Fact]
    public void ParseDiscResponse_NoReleasesProperty_ReturnsNull()
    {
        Assert.Null(MusicBrainzService.ParseDiscResponse("""{ "id": "whatever" }"""));
    }

    [Fact]
    public void ParseDiscResponse_InvalidJson_ReturnsNull()
    {
        Assert.Null(MusicBrainzService.ParseDiscResponse("not json at all"));
    }

    // -- Happy path: full release maps every field --

    [Fact]
    public void ParseDiscResponse_FullRelease_MapsAllFields()
    {
        const string json = """
        {
          "releases": [
            {
              "id": "abc-123",
              "title": "Test Album",
              "date": "1997-05-21",
              "release-group": {
                "id": "rg-456",
                "genres": [ { "name": "rock", "count": 5 }, { "name": "alternative rock", "count": 10 } ]
              },
              "artist-credit": [
                { "name": "Artist A", "joinphrase": " feat. " },
                { "name": "Artist B", "joinphrase": "" }
              ],
              "media": [
                { "tracks": [
                  { "position": 1, "title": "Track One", "recording": { "artist-credit": [ { "name": "Solo Artist", "joinphrase": "" } ] } },
                  { "position": 2, "title": "Track Two" }
                ] }
              ]
            }
          ]
        }
        """;

        var result = MusicBrainzService.ParseDiscResponse(json);

        Assert.NotNull(result);
        Assert.Equal("abc-123", result!.ReleaseMbid);
        Assert.Equal("rg-456", result.ReleaseGroupMbid);
        Assert.Equal("Test Album", result.Title);
        Assert.Equal((uint)1997, result.Year);

        // Highest-count genre, title-cased.
        Assert.Equal("Alternative Rock", result.Genre);

        // artist-credit name + joinphrase concatenation, trimmed.
        Assert.Equal("Artist A feat. Artist B", result.Artist);

        Assert.Equal(2, result.Tracks.Count);
        Assert.Equal(1, result.Tracks[0].Position);
        Assert.Equal("Track One", result.Tracks[0].Title);
        Assert.Equal("Solo Artist", result.Tracks[0].Artist);
        Assert.Equal(2, result.Tracks[1].Position);
        Assert.Equal("Track Two", result.Tracks[1].Title);
        Assert.Null(result.Tracks[1].Artist); // no recording → no per-track artist
    }

    // -- Edge cases --

    [Fact]
    public void ParseDiscResponse_NullTitle_DefaultsToUnknownAlbum()
    {
        // An explicit JSON null for title hits the "Unknown Album" fallback
        // (GetString() returns null). A truly absent title would instead make
        // GetProperty throw, which the parser swallows into a null result - see below.
        const string json = """{ "releases": [ { "id": "x", "title": null } ] }""";

        var result = MusicBrainzService.ParseDiscResponse(json);

        Assert.NotNull(result);
        Assert.Equal("Unknown Album", result!.Title);
    }

    [Fact]
    public void ParseDiscResponse_AbsentRequiredField_ReturnsNull()
    {
        // No "title" key at all → GetProperty throws → swallowed → null result.
        const string json = """{ "releases": [ { "id": "x" } ] }""";

        Assert.Null(MusicBrainzService.ParseDiscResponse(json));
    }

    [Theory]
    [InlineData("1997-05-21", true, 1997u)]
    [InlineData("2003", true, 2003u)]
    [InlineData("19", false, 0u)]   // too short to parse a year
    [InlineData("", false, 0u)]
    [InlineData("abcd-01", false, 0u)] // 4 chars but not numeric
    public void ParseDiscResponse_YearParsedFromDatePrefix(string date, bool hasYear, uint expectedYear)
    {
        string json = $$"""
        {
          "releases": [ { "id": "x", "title": "T", "date": "{{date}}" } ]
        }
        """;

        var result = MusicBrainzService.ParseDiscResponse(json);

        Assert.NotNull(result);
        if (hasYear)
        {
            Assert.Equal(expectedYear, result!.Year);
        }
        else
        {
            Assert.Null(result!.Year);
        }
    }

    [Fact]
    public void ParseDiscResponse_PrefersReleaseGroupGenreOverReleaseGenre()
    {
        // release-group genre wins even though the release-level genre has a higher count.
        const string json = """
        {
          "releases": [
            {
              "id": "x", "title": "T",
              "release-group": { "id": "rg", "genres": [ { "name": "metal", "count": 3 } ] },
              "genres": [ { "name": "pop", "count": 99 } ]
            }
          ]
        }
        """;

        var result = MusicBrainzService.ParseDiscResponse(json);

        Assert.Equal("Metal", result!.Genre);
    }

    [Fact]
    public void ParseDiscResponse_FallsBackToReleaseGenreWhenGroupHasNone()
    {
        const string json = """
        {
          "releases": [
            { "id": "x", "title": "T", "genres": [ { "name": "jazz", "count": 1 } ] }
          ]
        }
        """;

        var result = MusicBrainzService.ParseDiscResponse(json);

        Assert.Equal("Jazz", result!.Genre);
    }

    [Fact]
    public void ParseDiscResponse_NoGenreAnywhere_LeavesGenreNull()
    {
        const string json = """{ "releases": [ { "id": "x", "title": "T" } ] }""";

        var result = MusicBrainzService.ParseDiscResponse(json);

        Assert.Null(result!.Genre);
    }

    [Fact]
    public void ParseDiscResponse_NoMedia_YieldsNoTracks()
    {
        const string json = """{ "releases": [ { "id": "x", "title": "T" } ] }""";

        var result = MusicBrainzService.ParseDiscResponse(json);

        Assert.Empty(result!.Tracks);
    }
}
