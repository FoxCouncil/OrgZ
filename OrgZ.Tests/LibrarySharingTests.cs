// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using OrgZ.Services.Sharing;

namespace OrgZ.Tests;

/// <summary>
/// Library sharing: the mDNS wire format, the HTTP surface's pure helpers, and the
/// share service ops. Adversarial cases attack hostile DNS packets (pointer loops,
/// truncation), malformed Range headers, and port/name abuse - a share is reachable
/// by anyone on the LAN, so its parsers get treated as hostile input.
/// </summary>
public class LibrarySharingTests
{
    // ── mDNS wire ─────────────────────────────────────────────

    [Fact]
    public void Announcement_round_trips_through_the_reader()
    {
        var instance = new MdnsWire.ServiceInstance("Fox Library", "foxbox.local", 7391,
            ["name=Fox Library", "version=1", "readonly=1"], "192.168.1.50");

        var decoded = MdnsWire.ReadResponse(MdnsWire.BuildResponse(instance));

        var found = Assert.Single(decoded);
        Assert.Equal("Fox Library", found.InstanceName);
        Assert.Equal("foxbox.local", found.HostName);
        Assert.Equal(7391, found.Port);
        Assert.Equal("192.168.1.50", found.Address);
        Assert.Contains("name=Fox Library", found.TxtRecords);
    }

    [Fact]
    public void Browse_query_asks_for_the_orgz_service_type()
    {
        var questions = MdnsWire.ReadQuestions(MdnsWire.BuildQuery());

        var q = Assert.Single(questions);
        Assert.Equal(MdnsWire.ServiceType, q.Name);
        Assert.Equal(MdnsWire.TypePtr, q.Type);
    }

    [Fact]
    public void A_response_is_not_mistaken_for_a_query_and_vice_versa()
    {
        var response = MdnsWire.BuildResponse(new MdnsWire.ServiceInstance("x", "h.local", 1, []));
        Assert.Empty(MdnsWire.ReadQuestions(response));
        Assert.Empty(MdnsWire.ReadResponse(MdnsWire.BuildQuery()));
    }

    [Fact]
    public void Truncated_and_empty_packets_parse_to_nothing()
    {
        Assert.Empty(MdnsWire.ReadQuestions([]));
        Assert.Empty(MdnsWire.ReadQuestions([1, 2, 3]));
        Assert.Empty(MdnsWire.ReadResponse([]));
        Assert.Empty(MdnsWire.ReadResponse([0, 0, 0x84, 0]));

        // Header promises a question that isn't there.
        byte[] lying = [0, 0, 0, 0, 0, 5, 0, 0, 0, 0, 0, 0];
        Assert.Empty(MdnsWire.ReadQuestions(lying));
    }

    [Fact]
    public void A_compression_pointer_loop_terminates_instead_of_hanging()
    {
        // Question name at offset 12 is a pointer to itself - a classic DoS packet.
        byte[] packet = [0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 0x0C, 0, 12, 0, 1];

        var questions = MdnsWire.ReadQuestions(packet);

        Assert.Empty(questions);   // refused, and critically: returned
    }

    [Fact]
    public void Names_with_dots_in_the_label_are_sanitized_before_advertising()
    {
        Assert.Equal("Fox-s Library", MdnsAdvertiser.SanitizeLabel("Fox.s Library"));
        Assert.Equal("OrgZ", MdnsAdvertiser.SanitizeLabel("   "));
    }

    // ── HTTP surface ──────────────────────────────────────────

    [Theory]
    [InlineData("/stream/abc123", true, "abc123")]
    [InlineData("/stream/a%20b", true, "a b")]
    [InlineData("/stream/", false, "")]
    [InlineData("/catalogue", false, "")]
    [InlineData("/", false, "")]
    [InlineData("/streams/x", false, "")]
    public void Stream_paths_parse_only_when_they_carry_an_id(string path, bool expected, string expectedId)
    {
        Assert.Equal(expected, LibraryShareServer.TryParseStreamId(path, out var id));
        if (expected)
        {
            Assert.Equal(expectedId, id);
        }
    }

    [Fact]
    public void Range_headers_resolve_to_the_right_byte_window()
    {
        Assert.Equal(new LibraryShareServer.ByteRange(0, 99), LibraryShareServer.ParseRange("bytes=0-99", 1000));
        Assert.Equal(new LibraryShareServer.ByteRange(500, 999), LibraryShareServer.ParseRange("bytes=500-", 1000));
        Assert.Equal(new LibraryShareServer.ByteRange(900, 999), LibraryShareServer.ParseRange("bytes=-100", 1000));

        // An end past EOF clamps rather than over-reading.
        Assert.Equal(new LibraryShareServer.ByteRange(0, 999), LibraryShareServer.ParseRange("bytes=0-99999", 1000));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("items=0-10")]          // wrong unit
    [InlineData("bytes=abc-def")]
    [InlineData("bytes=999-0")]         // inverted
    [InlineData("bytes=5000-6000")]     // entirely past EOF
    [InlineData("bytes=0-10,20-30")]    // multi-range
    [InlineData("bytes=")]
    [InlineData("bytes=-0")]
    public void Hostile_range_headers_fall_back_to_the_whole_file(string? header)
    {
        Assert.Null(LibraryShareServer.ParseRange(header, 1000));
    }

    [Fact]
    public void Catalogue_lists_playable_tracks_and_omits_unplayable_ones()
    {
        List<MediaItem> library =
        [
            new() { Id = "1", Kind = MediaKind.Music, Title = "Stop", Artist = "Spice Girls", FilePath = @"C:\a.mp3" },
            new() { Id = "2", Kind = MediaKind.Radio, Title = "A Station", StreamUrl = "http://x" },   // not a file
            new() { Id = "3", Kind = MediaKind.Music, Title = "No File" },                              // no path
            new() { Id = "4", Kind = MediaKind.Audiobook, Title = "Book", FilePath = @"C:\b.m4b" },
        ];

        using var doc = JsonDocument.Parse(LibraryShareServer.BuildCatalogueJson("Fox Library", library));
        var root = doc.RootElement;

        Assert.Equal("Fox Library", root.GetProperty("share").GetString());
        Assert.Equal(2, root.GetProperty("count").GetInt32());

        var ids = root.GetProperty("tracks").EnumerateArray().Select(t => t.GetProperty("id").GetString()).ToList();
        Assert.Equal(["1", "4"], ids);
    }

    [Fact]
    public void Catalogue_carries_the_extension_the_client_hangs_off_the_stream_url()
    {
        List<MediaItem> library =
        [
            new() { Id = "1", Kind = MediaKind.Music, Title = "Tagged", FilePath = @"C:\a.mp3", Extension = ".mp3" },
            new() { Id = "2", Kind = MediaKind.Music, Title = "From path", FilePath = @"C:\b.FLAC" },   // no Extension set
            new() { Id = "3", Kind = MediaKind.Music, Title = "Odd", FilePath = @"C:\c.xyz" },          // not one we serve
        ];

        using var doc = JsonDocument.Parse(LibraryShareServer.BuildCatalogueJson("Fox", library));
        var exts = doc.RootElement.GetProperty("tracks").EnumerateArray().Select(t => t.GetProperty("ext").GetString()).ToList();

        Assert.Equal([".mp3", ".flac", ""], exts);
    }

    [Fact]
    public void Stream_ids_resolve_with_or_without_the_extension_suffix()
    {
        List<MediaItem> library =
        [
            new() { Id = "abc", Kind = MediaKind.Music, FilePath = @"C:\a.mp3" },
            new() { Id = "abc.mp3", Kind = MediaKind.Music, FilePath = @"C:\literal.mp3" },
        ];

        // An exact id match always wins, so an id that genuinely ends in ".mp3" stays reachable.
        Assert.Equal(@"C:\literal.mp3", LibraryShareServer.ResolveTrack(library, "abc.mp3")?.FilePath);
        Assert.Equal(@"C:\a.mp3", LibraryShareServer.ResolveTrack(library, "abc")?.FilePath);

        // Only with the literal id gone does the suffix get stripped.
        Assert.Equal(@"C:\a.mp3", LibraryShareServer.ResolveTrack([library[0]], "abc.mp3")?.FilePath);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("abc.exe")]        // stripping only ever considers audio suffixes
    [InlineData("abc.")]
    [InlineData(".mp3")]
    [InlineData("")]
    public void Stream_ids_that_match_nothing_resolve_to_nothing(string segment)
    {
        List<MediaItem> library = [new() { Id = "abc", Kind = MediaKind.Music, FilePath = @"C:\a.mp3" }];

        Assert.Null(LibraryShareServer.ResolveTrack(library, segment));
    }

    [Fact]
    public void Content_types_cover_the_formats_the_library_holds()
    {
        Assert.Equal("audio/mpeg", LibraryShareServer.ContentTypeFor("a.MP3"));
        Assert.Equal("audio/flac", LibraryShareServer.ContentTypeFor("a.flac"));
        Assert.Equal("audio/mp4", LibraryShareServer.ContentTypeFor("a.m4b"));
        Assert.Equal("application/octet-stream", LibraryShareServer.ContentTypeFor("a.xyz"));
    }

    // ── Share ops ─────────────────────────────────────────────

    [Fact]
    public void Port_and_name_resolution_rejects_abuse_and_falls_back()
    {
        Assert.Equal(ShareServiceOps.DefaultPort, ShareServiceOps.ResolvePort(null));
        Assert.Equal(ShareServiceOps.DefaultPort, ShareServiceOps.ResolvePort(0));
        Assert.Equal(ShareServiceOps.DefaultPort, ShareServiceOps.ResolvePort(80));       // privileged
        Assert.Equal(ShareServiceOps.DefaultPort, ShareServiceOps.ResolvePort(-1));
        Assert.Equal(ShareServiceOps.DefaultPort, ShareServiceOps.ResolvePort(70000));
        Assert.Equal(8080, ShareServiceOps.ResolvePort(8080));

        Assert.Equal("Fox Library", ShareServiceOps.ResolveName("  Fox Library  "));
        Assert.Contains("Library", ShareServiceOps.ResolveName(null));
    }

    [Fact]
    public void Start_payload_parsing_survives_garbage()
    {
        Assert.Null(ShareServiceOps.ParseStartPayload(null).ShareName);
        Assert.Null(ShareServiceOps.ParseStartPayload("not json").Port);
        Assert.Equal("X", ShareServiceOps.ParseStartPayload("""{"shareName":"X","port":9000}""").ShareName);
        Assert.Equal(9000, ShareServiceOps.ParseStartPayload("""{"ShareName":"X","Port":9000}""").Port);
    }
}
