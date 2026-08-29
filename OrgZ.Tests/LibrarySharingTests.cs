// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net;
using System.Text.Json;
using OrgZ.Services.Sharing;

namespace OrgZ.Tests;

/// <summary>
/// Library sharing: the mDNS wire format, the HTTP surface's pure helpers, and the
/// share service ops. Adversarial cases attack hostile DNS packets (pointer loops,
/// truncation), malformed Range headers, and port/name abuse - a share is reachable
/// by anyone on the LAN, so its parsers get treated as hostile input.
/// </summary>
[Collection(RealSocketCollection.Name)]
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

    [Fact]
    public void A_legacy_reply_echoes_the_query_id_and_drops_the_cache_flush_bit()
    {
        // Deliberately tiny instance: no name label, port, or TTL byte can be 0x80, so
        // the only 0x80s in the packet are the response flag (byte 2) and any cache-flush
        // class bytes - which a legacy reply must not carry.
        var instance = new MdnsWire.ServiceInstance("x", "h.local", 1, []);

        var legacy = MdnsWire.BuildResponse(instance, ttlSeconds: 10, id: 0xBEEF, cacheFlush: false);

        Assert.Equal(0xBEEF, MdnsWire.ReadId(legacy));
        Assert.DoesNotContain((byte)0x80, legacy.Skip(4));

        // Still a well-formed response the browser's reader accepts.
        var decoded = Assert.Single(MdnsWire.ReadResponse(legacy));
        Assert.Equal("x", decoded.InstanceName);

        // The multicast announcement keeps its id-0 / cache-flush shape.
        var multicast = MdnsWire.BuildResponse(instance);
        Assert.Equal(0, MdnsWire.ReadId(multicast));
        Assert.Contains((byte)0x80, multicast.Skip(4));

        // And a runt packet has no id to echo.
        Assert.Equal(0, MdnsWire.ReadId([]));
    }

    [Fact]
    public void The_reply_carries_the_address_the_querier_can_reach()
    {
        // The addresses of the night it bit: host on a Hyper-V bridge (192.168.1.20),
        // a NAT'd Default Switch (172.17.208.1) and Tailscale (100.x). The A record must
        // be the one on the QUERIER's subnet - advertising the default-route address to
        // a peer on a different attached network hands them an address that can't route.
        List<(IPAddress, IPAddress)> interfaces =
        [
            (IPAddress.Parse("172.17.208.1"), IPAddress.Parse("255.255.240.0")),
            (IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.255.255.0")),
            (IPAddress.Parse("100.74.3.9"), IPAddress.Parse("255.192.0.0")),
        ];

        Assert.Equal(IPAddress.Parse("192.168.1.20"), MdnsAdvertiser.BestAddressFor(IPAddress.Parse("192.168.1.172"), interfaces));
        Assert.Equal(IPAddress.Parse("172.17.208.1"), MdnsAdvertiser.BestAddressFor(IPAddress.Parse("172.17.213.7"), interfaces));

        // No shared subnet: null, so the caller falls back to the default rather than lying.
        Assert.Null(MdnsAdvertiser.BestAddressFor(IPAddress.Parse("10.9.9.9"), interfaces));

        // A mask-less interface (IPv4Mask unavailable) never matches anything.
        Assert.Null(MdnsAdvertiser.BestAddressFor(IPAddress.Parse("1.2.3.4"), [(IPAddress.Parse("1.2.3.1"), IPAddress.None)]));
    }

    [Fact]
    public async Task An_advertiser_and_a_browser_find_each_other_over_real_sockets()
    {
        // The pair over real UDP, not the codec in memory - this is the test whose absence
        // hid that responses went multicast to :5353, a port the ephemeral-bound browser
        // can never hear. The name is unique per run so a genuine OrgZ share on the same
        // machine (or a parallel test run) can't satisfy the assertion - and the port is
        // NOT the product default, because the browser dedups on host:port and a real
        // share on this machine at 7391 would swallow ours (its reply wins the address
        // sort, and the test-named share is never seen).
        var shareName = $"OrgZ Pair Test {Guid.NewGuid():N}";
        using var advertiser = new MdnsAdvertiser(shareName, 27391);
        advertiser.Start();

        List<DiscoveredShare> found = [];
        // Patient on purpose: this runs in the full suite alongside every other test,
        // and on a busy multi-NIC box the unicast reply can miss a short window. It
        // returns the instant it finds its own share, so the happy path stays fast; the
        // retries only cost time on a machine that would otherwise flake.
        for (var attempt = 0; attempt < 8 && found.Count == 0; attempt++)
        {
            found = (await ShareDiscovery.BrowseAsync(TimeSpan.FromSeconds(3)))
                .Where(s => s.Name == shareName)
                .ToList();
        }

        var share = Assert.Single(found);
        Assert.Equal(27391, share.Port);
        Assert.False(string.IsNullOrEmpty(share.Address));
    }

    [Fact]
    public void Favorites_ride_the_playlists_payload_as_the_first_playlist()
    {
        // Favorites is a per-track flag, not a playlist row - forgetting to synthesize
        // it meant a remote saw every playlist EXCEPT the one that matters most.
        List<MediaItem> library =
        [
            new() { Id = "a", Kind = MediaKind.Music, IsFavorite = true },
            new() { Id = "b", Kind = MediaKind.Music },
            new() { Id = "c", Kind = MediaKind.Music, IsFavorite = true },
        ];

        var favorites = LibraryShareServer.FavoritesPlaylist(library);
        Assert.NotNull(favorites);
        Assert.Equal("Favorites", favorites!.Value.Name);
        Assert.Equal(["a", "c"], favorites.Value.TrackIds);

        // An all-unfavorited library sends no empty ghost playlist.
        Assert.Null(LibraryShareServer.FavoritesPlaylist([new MediaItem { Id = "x", Kind = MediaKind.Music }]));
    }

    [Fact]
    public void Playlist_type_rides_the_wire_and_names_never_stand_in_for_it()
    {
        // The client stars Favorites by TYPE - a remote's ordinary playlist that
        // happens to be named "Favorites" must come through as a plain playlist.
        var json = LibraryShareServer.BuildPlaylistsJson(
        [
            new LibraryShareServer.ServedPlaylist("Favorites", ["a", "b"], "favorites"),
            new LibraryShareServer.ServedPlaylist("Favorites", ["c"]),
            new LibraryShareServer.ServedPlaylist("Road Trip", ["d"]),
        ]);

        var share = new DiscoveredShare("S", "h.local", 7391, "192.168.1.50");
        var parsed = ShareDiscovery.ParsePlaylists(json, share);

        Assert.Equal(3, parsed.Count);
        Assert.True(parsed[0].IsFavorites);
        Assert.False(parsed[1].IsFavorites);   // same name, not the type
        Assert.False(parsed[2].IsFavorites);
        Assert.Equal(["share:192.168.1.50:7391:a", "share:192.168.1.50:7391:b"], parsed[0].TrackIds);
    }

    // ── TLS identity + the hand-rolled HTTP head ─────────────

    [Fact]
    public void Imported_share_tracks_follow_the_artist_album_track_layout()
    {
        // Same rules as a CD rip: {Music}/{Artist}/{Album}/{NN - Title}.ext, NOT a flat
        // "Artist - Title.ext" dumped at the library root (which sorted nowhere).
        var root = Path.Combine(Path.GetTempPath(), $"orgz-lib-{Guid.NewGuid():N}");
        var original = OrgZ.App.FolderPath;
        OrgZ.App.FolderPath = root;
        try
        {
            var track = new MediaItem
            {
                Id = "share:h:127.0.0.1:7391:x",
                Kind = MediaKind.Music,
                Title = "California Love (original mix)",
                Artist = "2Pac",
                Album = "The Best of 2Pac, Pt. 1 - Thug",
                Track = 2,
                Extension = ".flac",
                Source = "share:127.0.0.1:7391",
            };

            var dest = OrgZ.ViewModels.MainWindowViewModel.LibraryDestinationFor(track);

            Assert.Equal(
                Path.Combine(root, "2Pac", "The Best of 2Pac, Pt. 1 - Thug", "02 - California Love (original mix).flac"),
                dest);
        }
        finally
        {
            OrgZ.App.FolderPath = original;
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void A_share_url_never_doubles_the_file_extension()
    {
        // The share's ids ARE file paths, so appending the ext produced "...flac.flac" -
        // it played only because the server strips one suffix. The extension rides the
        // URL only when the id doesn't already carry it.
        var flac = new MediaItem { Id = "song.flac", Kind = MediaKind.Music, Title = "S", FilePath = @"C:\song.flac", Extension = ".flac" };
        var opaque = new MediaItem { Id = "42", Kind = MediaKind.Music, Title = "S", FilePath = @"C:\42.mp3", Extension = ".mp3" };

        var share = new DiscoveredShare("S", "h", 7391, "127.0.0.1");
        var fromFlac = Assert.Single(ShareDiscovery.ParseCatalogue(LibraryShareServer.BuildCatalogueJson("S", [flac]), share));
        var fromOpaque = Assert.Single(ShareDiscovery.ParseCatalogue(LibraryShareServer.BuildCatalogueJson("S", [opaque]), share));

        Assert.EndsWith("/song.flac", fromFlac.StreamUrl);      // not song.flac.flac
        Assert.DoesNotContain(".flac.flac", fromFlac.StreamUrl!, StringComparison.Ordinal);
        Assert.EndsWith("/42.mp3", fromOpaque.StreamUrl);       // opaque id still gets the ext
    }

    [Fact]
    public void The_share_certificate_persists_and_the_pin_stays_stable()
    {
        // The pin IS the trust anchor a client remembers - a cert that silently
        // regenerated per start would make every remembered pin worthless.
        var dir = Path.Combine(Path.GetTempPath(), $"orgz-cert-{Guid.NewGuid():N}");
        try
        {
            var first = ShareCertificate.LoadOrCreate(dir);
            var second = ShareCertificate.LoadOrCreate(dir);

            Assert.Equal(ShareCertificate.PinOf(first), ShareCertificate.PinOf(second));
            Assert.True(File.Exists(Path.Combine(dir, ShareCertificate.FileName)));
            Assert.True(first.HasPrivateKey);   // Schannel can't serve without it
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Tls_http_head_parsing_accepts_ours_and_rejects_junk()
    {
        var request = TlsHttpServer.ParseHead("GET /catalogue?x=1 HTTP/1.1\r\nHost: h\r\nRange: bytes=0-1");

        Assert.NotNull(request);
        Assert.Equal("GET", request!.Method);
        Assert.Equal("/catalogue", request.Path);            // query stripped: routes are path-only
        Assert.Equal("bytes=0-1", request.Headers["Range"]);

        // A LAN port sees every scanner on the network - none of these parse.
        Assert.Null(TlsHttpServer.ParseHead("SSH-2.0-OpenSSH_9.4"));
        Assert.Null(TlsHttpServer.ParseHead("GET catalogue HTTP/1.1"));   // no leading slash
        Assert.Null(TlsHttpServer.ParseHead(""));
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
    public void The_catalogue_round_trips_the_full_track_record()
    {
        // Everything the local grid can show, the share grid can show. Found in the
        // field: the slim catalogue starved the remote of duration (no seek bar),
        // genre, rating - the lot.
        var track = new MediaItem
        {
            Id = "t1",
            Kind = MediaKind.Music,
            Title = "Song",
            Artist = "Artist",
            Album = "Album",
            FilePath = @"C:\a.flac",
            Extension = ".flac",
            Duration = TimeSpan.FromSeconds(245),
            Track = 3,
            TotalTracks = 12,
            Disc = 1,
            TotalDiscs = 2,
            Year = 1999,
            Genre = "Eurobeat",
            Composer = "Someone",
            Comment = "a note",
            Bpm = 158,
            Rating = 5,
            PlayCount = 42,
            FileSize = 31_337_420,
            AudioBitrate = 1411,
            SampleRate = 44100,
            BitDepth = 16,
            AudioChannels = 2,
            CodecDescription = "FLAC",
            DateAdded = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        };

        var share = new DiscoveredShare("S", "h.local", 7391, "192.168.1.50");
        var item = Assert.Single(ShareDiscovery.ParseCatalogue(LibraryShareServer.BuildCatalogueJson("S", [track]), share));

        Assert.Equal(TimeSpan.FromSeconds(245), item.Duration);   // the seek bar hangs off this
        Assert.Equal(3u, item.Track);
        Assert.Equal(12u, item.TotalTracks);
        Assert.Equal(1u, item.Disc);
        Assert.Equal(2u, item.TotalDiscs);
        Assert.Equal(1999u, item.Year);
        Assert.Equal("Eurobeat", item.Genre);
        Assert.Equal("Someone", item.Composer);
        Assert.Equal("a note", item.Comment);
        Assert.Equal(158u, item.Bpm);
        Assert.Equal(5, item.Rating);
        Assert.Equal(42, item.PlayCount);
        Assert.Equal(31_337_420, item.FileSize);
        Assert.Equal(1411, item.AudioBitrate);
        Assert.Equal(44100, item.SampleRate);
        Assert.Equal(16, item.BitDepth);
        Assert.Equal(2, item.AudioChannels);
        Assert.Equal("FLAC", item.CodecDescription);
        Assert.Equal(track.DateAdded, item.DateAdded);
        Assert.Null(item.FilePath);   // still read-only: nothing local to touch
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
        // The literal, not the constant: comparing DefaultPort to itself would stay green
        // if someone retyped it as 80 - which this very test declares privileged. The port
        // is also baked into the installer's firewall rule, so it isn't free to change.
        Assert.Equal(7391, ShareServiceOps.DefaultPort);

        Assert.Equal(7391, ShareServiceOps.ResolvePort(null));
        Assert.Equal(7391, ShareServiceOps.ResolvePort(0));
        Assert.Equal(7391, ShareServiceOps.ResolvePort(80));       // privileged
        Assert.Equal(7391, ShareServiceOps.ResolvePort(-1));
        Assert.Equal(7391, ShareServiceOps.ResolvePort(70000));
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
