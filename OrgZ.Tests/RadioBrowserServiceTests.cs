// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

/// <summary>
/// Tests RadioBrowserService.ParseStations against:
///   1. A real captured snapshot from the radio-browser API (Fixtures/radiobrowser-topclick-3.json),
///      so we'd notice if the upstream wire format changes in a way that breaks parsing.
///   2. Hand-crafted edge cases (Fixtures/radiobrowser-edge-cases.json) that cover the
///      filter/normalization branches: blank URLs, url_resolved fallback, empty UUID,
///      zero-as-null counter coercion, whitespace strings, missing optional fields.
///
/// We deliberately do NOT make live HTTP calls in these tests — the snapshot is the contract.
/// To refresh: curl -H "User-Agent: Mozilla/5.0 ..." "https://de1.api.radio-browser.info/json/stations/topclick/3" -o Fixtures/radiobrowser-topclick-3.json
/// </summary>
public class RadioBrowserServiceTests
{
    private static string LoadFixture(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", filename);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Test fixture missing: {path}. Check OrgZ.Tests.csproj <Content Include=\"Fixtures\\**\\*\" />.", path);
        }
        return File.ReadAllText(path);
    }

    // ===== Real upstream snapshot =====

    [Fact]
    public void ParseStations_real_snapshot_returns_expected_count_and_shape()
    {
        var json = LoadFixture("radiobrowser-topclick-3.json");
        var stations = RadioBrowserService.ParseStations(json);

        Assert.Equal(3, stations.Count);

        // Every station from the real snapshot should have a streamable URL and an Id of "rb:{uuid}"
        Assert.All(stations, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.StreamUrl));
            Assert.StartsWith("rb:", s.Id);
            Assert.Equal(MediaKind.Radio, s.Kind);
            Assert.Equal("radiobrowser", s.Source);
        });
    }

    [Fact]
    public void ParseStations_real_snapshot_BBC_World_Service_normalized_correctly()
    {
        var json = LoadFixture("radiobrowser-topclick-3.json");
        var stations = RadioBrowserService.ParseStations(json);

        var bbc = stations.SingleOrDefault(s => s.Title == "BBC World Service");
        Assert.NotNull(bbc);
        Assert.Equal("rb:98adecf7-2683-4408-9be7-02d3f9098eb8", bbc!.Id);
        Assert.Equal("MP3", bbc.Codec);
        Assert.Equal(56, bbc.Bitrate);
        Assert.Equal("GB", bbc.CountryCode);
        Assert.Equal("news,talk", bbc.Tags);
        Assert.False(bbc.IsHls);
        Assert.NotNull(bbc.Votes);
        Assert.True(bbc.Votes > 100_000);
    }

    [Fact]
    public void ParseStations_real_snapshot_Classic_Vinyl_HD_normalized_correctly()
    {
        var json = LoadFixture("radiobrowser-topclick-3.json");
        var stations = RadioBrowserService.ParseStations(json);

        var vinyl = stations.SingleOrDefault(s => s.Title == "Classic Vinyl HD");
        Assert.NotNull(vinyl);
        Assert.Equal("rb:d1a54d2e-623e-4970-ab11-35f7b56c5ec3", vinyl!.Id);
        Assert.Equal(320, vinyl.Bitrate);
        Assert.Equal("US", vinyl.CountryCode);
        Assert.Equal("https://walmradio.com/classic", vinyl.HomepageUrl);
        Assert.False(vinyl.IsHls);
    }

    // ===== Empty / malformed input =====

    [Fact]
    public void ParseStations_empty_array_returns_empty_list()
    {
        var json = LoadFixture("radiobrowser-empty.json");
        Assert.Empty(RadioBrowserService.ParseStations(json));
    }

    [Fact]
    public void ParseStations_malformed_JSON_throws_or_returns_empty()
    {
        var json = LoadFixture("radiobrowser-malformed.json");
        // The current implementation lets JsonException propagate. If we ever swallow it,
        // verify either way: malformed input must not yield ghost stations.
        try
        {
            var result = RadioBrowserService.ParseStations(json);
            Assert.Empty(result);
        }
        catch (System.Text.Json.JsonException)
        {
            // Acceptable — caller is expected to handle network/parse failures upstream
        }
    }

    // ===== Edge-case filter branches =====

    [Fact]
    public void ParseStations_drops_stations_with_blank_or_whitespace_URL()
    {
        var json = LoadFixture("radiobrowser-edge-cases.json");
        var stations = RadioBrowserService.ParseStations(json);

        Assert.DoesNotContain(stations, s => s.Title == "Blank URL Station");
        Assert.DoesNotContain(stations, s => s.Title == "Whitespace URL Station");
    }

    [Fact]
    public void ParseStations_prefers_url_resolved_over_url()
    {
        var json = LoadFixture("radiobrowser-edge-cases.json");
        var stations = RadioBrowserService.ParseStations(json);

        var s = stations.Single(x => x.Title == "URL Resolved Preferred");
        Assert.Equal("http://stream.example.com/live", s.StreamUrl);
        // NOT the .m3u playlist URL
        Assert.DoesNotContain(".m3u", s.StreamUrl);
    }

    [Fact]
    public void ParseStations_falls_back_to_url_when_url_resolved_null()
    {
        var json = LoadFixture("radiobrowser-edge-cases.json");
        var stations = RadioBrowserService.ParseStations(json);

        var s = stations.Single(x => x.Title == "Url Field Only");
        Assert.Equal("http://stream.example.com/live2", s.StreamUrl);
    }

    [Fact]
    public void ParseStations_synthesizes_new_GUID_when_stationuuid_is_empty()
    {
        var json = LoadFixture("radiobrowser-edge-cases.json");
        var stations = RadioBrowserService.ParseStations(json);

        var s = stations.Single(x => x.Title == "Stationless UUID");
        // Must have been replaced — the original empty GUID would yield Id="rb:00000000-..."
        Assert.False(s.Id.EndsWith("00000000-0000-0000-0000-000000000000"));
        Assert.StartsWith("rb:", s.Id);
        var guid = s.Id["rb:".Length..];
        Assert.True(Guid.TryParse(guid, out var parsed) && parsed != Guid.Empty);
    }

    [Fact]
    public void ParseStations_marks_HLS_stream_with_IsHls_true()
    {
        var json = LoadFixture("radiobrowser-edge-cases.json");
        var stations = RadioBrowserService.ParseStations(json);

        var hls = stations.Single(x => x.Title == "HLS Stream");
        Assert.True(hls.IsHls);
    }

    [Fact]
    public void ParseStations_coerces_zero_counters_to_null()
    {
        var json = LoadFixture("radiobrowser-edge-cases.json");
        var stations = RadioBrowserService.ParseStations(json);

        var s = stations.Single(x => x.Title == "Zero Counters");
        // bitrate=0, votes=0, clickcount=0 → all null in the MediaItem
        Assert.Null(s.Bitrate);
        Assert.Null(s.Votes);
        Assert.Null(s.ClickCount);
    }

    [Fact]
    public void ParseStations_null_optional_fields_normalize_correctly()
    {
        var json = LoadFixture("radiobrowser-edge-cases.json");
        var stations = RadioBrowserService.ParseStations(json);

        var s = stations.Single(x => x.Id.EndsWith("00000007"));
        // name=null falls back to "Unknown"
        Assert.Equal("Unknown", s.Title);
        // null homepage/favicon → null on MediaItem
        Assert.Null(s.HomepageUrl);
        Assert.Null(s.FaviconUrl);
        Assert.Null(s.Country);
        Assert.Null(s.CountryCode);
        Assert.Null(s.Tags);
        Assert.Null(s.Codec);
    }

    [Fact]
    public void ParseStations_whitespace_homepage_and_favicon_become_null()
    {
        var json = LoadFixture("radiobrowser-edge-cases.json");
        var stations = RadioBrowserService.ParseStations(json);

        var s = stations.Single(x => x.Title == "Whitespace Strings");
        // "   " → null, NOT empty string
        Assert.Null(s.HomepageUrl);
        Assert.Null(s.FaviconUrl);
    }

    [Fact]
    public void ParseStations_minimal_sparse_station_parses_with_defaults()
    {
        var json = LoadFixture("radiobrowser-edge-cases.json");
        var stations = RadioBrowserService.ParseStations(json);

        var s = stations.Single(x => x.Title == "Sparse Station");
        Assert.Equal("Spain", s.Country);
        Assert.Equal(MediaKind.Radio, s.Kind);
        Assert.Equal("radiobrowser", s.Source);
        Assert.False(s.IsHls);   // hls field absent → default 0
        Assert.Null(s.Bitrate);  // bitrate absent → default 0 → coerced to null
    }

    [Fact]
    public void ParseStations_total_count_matches_filtered_stations()
    {
        // Fixture has 10 entries, 2 of which (blank/whitespace URL) should be filtered out
        var json = LoadFixture("radiobrowser-edge-cases.json");
        var stations = RadioBrowserService.ParseStations(json);
        Assert.Equal(8, stations.Count);
    }

    [Fact]
    public void ParseStations_every_returned_station_has_id_with_rb_prefix()
    {
        var json = LoadFixture("radiobrowser-edge-cases.json");
        var stations = RadioBrowserService.ParseStations(json);

        Assert.All(stations, s =>
        {
            Assert.StartsWith("rb:", s.Id);
            Assert.Equal(MediaKind.Radio, s.Kind);
            Assert.Equal("radiobrowser", s.Source);
        });
    }
}
