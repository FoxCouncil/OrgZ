// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;
using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// The bundled-stations mapping: stations.json -> MediaItem (genre tag, MIME codec, HLS
/// flag, zero-bitrate normalization, and dropping entries missing an id or stream URL).
/// </summary>
public class BundledStationsServiceTests
{
    private const string Json = """
        {
          "schemaVersion": 1,
          "stations": [
            { "id": "s1", "name": "Eighties FM", "streamUrl": "http://e.fm/s", "streamFormat": "mp3", "bitrate": 128, "genreId": 2, "country": "USA", "countryCode": "US", "homepage": "http://e.fm", "logoUrl": "http://e.fm/l.png" },
            { "id": "s2", "name": "Pop HLS", "streamUrl": "http://h.fm/s.m3u8", "streamFormat": "hls", "bitrate": 0, "genreId": 26, "country": "UK", "countryCode": "GB" },
            { "id": "", "name": "No Id", "streamUrl": "http://x", "streamFormat": "mp3", "bitrate": 64, "genreId": 1, "country": "X", "countryCode": "X" },
            { "id": "s4", "name": "No Url", "streamUrl": "", "streamFormat": "mp3", "bitrate": 64, "genreId": 1, "country": "X", "countryCode": "X" }
          ]
        }
        """;

    [Fact]
    public void ParseStations_drops_entries_missing_id_or_stream_url()
    {
        var items = BundledStationsService.ParseStations(Json);
        Assert.Equal(2, items.Count);
        Assert.Equal(new[] { "s1", "s2" }, items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void ParseStations_maps_core_fields()
    {
        var s1 = BundledStationsService.ParseStations(Json).First(i => i.Id == "s1");
        Assert.Equal(MediaKind.Radio, s1.Kind);
        Assert.Equal("Eighties FM", s1.Title);
        Assert.Equal("http://e.fm/s", s1.StreamUrl);
        Assert.Equal("USA", s1.Country);
        Assert.Equal("US", s1.CountryCode);
        Assert.Equal("bundled", s1.Source);
        Assert.Equal(128, s1.Bitrate!.Value);
        Assert.False(s1.IsHls);
    }

    [Fact]
    public void ParseStations_maps_genreId_to_display_tag()
    {
        var items = BundledStationsService.ParseStations(Json);
        Assert.Equal("80's", items.First(i => i.Id == "s1").Tags);          // genreId 2
        Assert.Equal("Top 40 / Pop", items.First(i => i.Id == "s2").Tags);  // genreId 26
    }

    [Fact]
    public void ParseStations_maps_stream_format_to_mime_codec()
    {
        var items = BundledStationsService.ParseStations(Json);
        Assert.Equal("audio/mpeg", items.First(i => i.Id == "s1").Codec);                     // mp3
        Assert.Equal("application/vnd.apple.mpegurl", items.First(i => i.Id == "s2").Codec);  // hls
    }

    [Fact]
    public void ParseStations_flags_hls_and_nulls_zero_bitrate()
    {
        var s2 = BundledStationsService.ParseStations(Json).First(i => i.Id == "s2");
        Assert.True(s2.IsHls);
        Assert.Null(s2.Bitrate);   // bitrate 0 -> null
    }

    [Fact]
    public void ParseStations_empty_or_missing_stations_yields_no_items()
    {
        Assert.Empty(BundledStationsService.ParseStations("""{ "schemaVersion": 1, "stations": [] }"""));
        Assert.Empty(BundledStationsService.ParseStations("""{ "schemaVersion": 1 }"""));
    }
}
