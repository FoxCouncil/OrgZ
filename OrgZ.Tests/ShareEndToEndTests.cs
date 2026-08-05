// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using OrgZ.Services.Sharing;

namespace OrgZ.Tests;

/// <summary>
/// The share pipe, end to end and for real: a live <see cref="LibraryShareServer"/> on a
/// loopback socket, a real HttpClient walking the same route a remote OrgZ walks
/// (browse → catalogue → MediaItem → stream), and libvlc actually opening the resulting
/// URL. Server and client were each unit-tested before this and had still never spoken
/// to each other - which is exactly how a hollow feature survives to a release.
///
/// Adversarial cases attack the parts a LAN service must not get wrong: mutation verbs,
/// path traversal, unknown ids, tracks whose file vanished, ranges past the end, and the
/// open-ended range libvlc really sends when it seeks.
/// </summary>
[Collection(RealSocketCollection.Name)]
public sealed class ShareEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"orgz-share-{Guid.NewGuid():N}");
    private readonly List<LibraryShareServer> _servers = [];
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>A hung socket must fail this test, not wedge the whole suite.</summary>
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));

    private CancellationToken Ct => _cts.Token;

    public ShareEndToEndTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var server in _servers)
        {
            server.Dispose();
        }

        _http.Dispose();
        _cts.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ── Rig ───────────────────────────────────────────────────

    /// <summary>
    /// A port the OS just told us was free. Inherently racy - HttpListener can't bind
    /// port 0, so we have to probe, release, and rebind, and anything on the machine can
    /// take it in between. <see cref="Host"/> retries rather than pretending otherwise.
    /// </summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// A real, decodable WAV - two seconds of a 440 Hz sine at 44.1/16/stereo. Hand-built
    /// rather than ffmpeg-generated so this suite has no external tool dependency, and
    /// long enough that a duration probe and a mid-file seek both mean something.
    /// </summary>
    private string MakeWav(string name = "track.wav", double seconds = 2.0)
    {
        var path = Path.Combine(_dir, name);

        const int rate = 44100;
        const int channels = 2;
        var frames = (int)(rate * seconds);
        var dataBytes = frames * channels * 2;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);

        for (var i = 0; i < frames; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000);
            w.Write(sample);
            w.Write(sample);
        }

        return path;
    }

    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static MediaItem Track(string id, string path, string title = "Shared Song") => new()
    {
        Id = id,
        Kind = MediaKind.Music,
        Title = title,
        Artist = "Someone Else",
        Album = "Their Library",
        FilePath = path,
        FileName = Path.GetFileName(path),
        Extension = Path.GetExtension(path),
        Duration = TimeSpan.FromSeconds(2),
    };

    /// <summary>
    /// Brings a share up on loopback (no mDNS) and hands back its base URL. Retries on a
    /// lost port race: this suite stands up a dozen listeners and the machine is running
    /// other things, so "the port I was promised got taken" is a normal event, not a
    /// product failure - and a test that fails for that reason teaches nothing.
    /// </summary>
    private (LibraryShareServer Server, DiscoveredShare Share) Host(params MediaItem[] library)
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = FreePort();
            var server = new LibraryShareServer("Test Library", port, () => [.. library], certificateDirectory: _dir);

            try
            {
                server.Start(advertise: false);
            }
            catch (SocketException) when (attempt < 5)
            {
                server.Dispose();
                continue;
            }

            _servers.Add(server);

            // Pin present → BaseUrl routes through the loopback relay over pinned TLS,
            // so every test below exercises the real client path: relay → TLS → server.
            return (server, new DiscoveredShare("Test Library", "localhost", port, "127.0.0.1", server.CertificatePin));
        }
    }

    // ── Verification: the whole client journey ────────────────

    [Fact]
    public async Task Catalogue_then_stream_reproduces_the_file_byte_for_byte()
    {
        var path = MakeWav();
        var (_, share) = Host(Track("t1", path));

        // Exactly what the client does: fetch the catalogue, map it to MediaItems.
        var json = await _http.GetStringAsync($"{share.BaseUrl}/catalogue", Ct);
        var items = ShareDiscovery.ParseCatalogue(json, share);

        var item = Assert.Single(items);
        Assert.Equal("Shared Song", item.Title);
        Assert.Null(item.FilePath);                       // read-only: nothing local to touch
        Assert.NotNull(item.StreamUrl);

        // And then plays it - which is the step that had never once run.
        var served = await _http.GetByteArrayAsync(item.StreamUrl!, Ct);
        Assert.Equal(await File.ReadAllBytesAsync(path, Ct), served);
    }

    [Fact]
    public async Task A_large_file_downloads_intact_through_the_relay()
    {
        // California Love (a 37 MB FLAC) came across "corrupt" - but a direct download
        // was byte-exact, leaving the one path the byte-exact WAV test never covered:
        // a LARGE file through the loopback TLS relay, streamed in chunks. And the client
        // download path (ResponseHeadersRead + CopyToAsync) whose short-timeout HttpClient
        // could truncate a slow transfer mid-copy. Both are exercised here.
        var path = Path.Combine(_dir, "big.bin");
        var payload = new byte[8 * 1024 * 1024 + 12345];   // 8 MB + change: not a chunk multiple
        new Random(1234).NextBytes(payload);
        await File.WriteAllBytesAsync(path, payload, Ct);

        var (_, share) = Host(Track("big", path));

        var items = ShareDiscovery.ParseCatalogue(await _http.GetStringAsync($"{share.BaseUrl}/catalogue", Ct), share);
        var item = Assert.Single(items);

        // The real client download path, not a buffered GetByteArrayAsync.
        var dest = Path.Combine(_dir, "downloaded.bin");
        Assert.True(await ShareDiscovery.DownloadTrackAsync(item, dest, Ct));

        Assert.Equal(payload.Length, new FileInfo(dest).Length);
        Assert.Equal(
            System.Security.Cryptography.SHA256.HashData(payload),
            System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(dest, Ct)));
    }

    [Fact]
    public async Task A_broken_library_answers_500_not_a_connection_reset()
    {
        // The service-hosted share once threw on every request (the LocalSystem empty
        // library db) and the handler ABORTED the response - which a client sees as a
        // bare connection reset and diagnoses as a network problem. A handler failure
        // must answer as HTTP so the failure is visible for what it is.
        for (var attempt = 1; ; attempt++)
        {
            var port = FreePort();
            var server = new LibraryShareServer("Broken Library", port, () => throw new InvalidOperationException("no such table: Media"), certificateDirectory: _dir);

            try
            {
                server.Start(advertise: false);
            }
            catch (SocketException) when (attempt < 5)
            {
                server.Dispose();
                continue;
            }

            _servers.Add(server);

            var share = new DiscoveredShare("Broken Library", "localhost", port, "127.0.0.1", server.CertificatePin);
            var response = await _http.GetAsync($"{share.BaseUrl}/catalogue", Ct);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            return;
        }
    }

    [Fact]
    public async Task A_wrong_pin_never_reaches_the_share()
    {
        // The point of the pin: a client that expects a different certificate refuses
        // the connection outright - no request crosses the wire to a server (or an
        // interceptor) that can't prove the announced identity.
        var (server, _) = Host(Track("t1", MakeWav()));

        var impostorPin = Convert.ToBase64String(new byte[32]);
        var impostor = new DiscoveredShare("Test Library", "localhost", server.Port, "127.0.0.1", impostorPin);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _http.GetStringAsync($"{impostor.BaseUrl}/catalogue", Ct));
    }

    [Fact]
    public async Task The_stream_url_carries_the_file_extension_so_a_player_can_pick_a_demuxer()
    {
        var (_, share) = Host(Track("t1", MakeWav()));

        var json = await _http.GetStringAsync($"{share.BaseUrl}/catalogue", Ct);
        var item = Assert.Single(ShareDiscovery.ParseCatalogue(json, share));

        Assert.EndsWith(".wav", item.StreamUrl, StringComparison.Ordinal);

        // ...and the server still resolves the id with that suffix attached.
        using var response = await _http.GetAsync(item.StreamUrl!, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_seek_gets_exactly_the_bytes_it_asked_for()
    {
        var path = MakeWav();
        var bytes = await File.ReadAllBytesAsync(path, Ct);
        var (_, share) = Host(Track("t1", path));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{share.BaseUrl}/stream/t1");
        request.Headers.Range = new RangeHeaderValue(1000, 1999);

        using var response = await _http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal($"bytes 1000-1999/{bytes.Length}", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(bytes[1000..2000], await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task An_open_ended_range_serves_to_the_end_which_is_what_libvlc_actually_sends()
    {
        var path = MakeWav();
        var bytes = await File.ReadAllBytesAsync(path, Ct);
        var (_, share) = Host(Track("t1", path));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{share.BaseUrl}/stream/t1");
        request.Headers.Range = new RangeHeaderValue(bytes.Length - 100, null);

        using var response = await _http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(100, (await response.Content.ReadAsByteArrayAsync(Ct)).Length);
        Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
    }

    [Fact]
    public async Task Head_reports_the_length_without_sending_the_audio()
    {
        var path = MakeWav();
        var (_, share) = Host(Track("t1", path));

        using var request = new HttpRequestMessage(HttpMethod.Head, $"{share.BaseUrl}/stream/t1");
        using var response = await _http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new FileInfo(path).Length, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Cover_art_comes_back_for_a_tagged_track_and_404s_for_a_bare_one()
    {
        var tagged = MakeWav("tagged.wav");
        var bare = MakeWav("bare.wav");
        Assert.True(OrgZ.Services.AlbumArtWriter.SetArtwork(tagged, TinyPng(), "image/png").Ok);

        var (_, share) = Host(Track("withArt", tagged), Track("noArt", bare));

        using var found = await _http.GetAsync($"{share.BaseUrl}/art/withArt", Ct);
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal("image/png", found.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TinyPng(), await found.Content.ReadAsByteArrayAsync(Ct));

        using var missing = await _http.GetAsync($"{share.BaseUrl}/art/noArt", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task The_art_url_the_client_derives_is_the_one_the_server_answers()
    {
        var tagged = MakeWav("tagged.wav");
        Assert.True(OrgZ.Services.AlbumArtWriter.SetArtwork(tagged, TinyPng(), "image/png").Ok);
        var (_, share) = Host(Track("t1", tagged));

        var json = await _http.GetStringAsync($"{share.BaseUrl}/catalogue", Ct);
        var item = Assert.Single(ShareDiscovery.ParseCatalogue(json, share));

        // The client rebuilds this from the namespaced id alone - no catalogue field.
        var artUrl = ShareDiscovery.ArtUrlFor(item);
        Assert.NotNull(artUrl);

        var art = await ShareDiscovery.FetchArtAsync(artUrl!, Ct);
        Assert.Equal(TinyPng(), art);
    }

    /// <summary>
    /// The one thing no amount of unit testing substitutes for: libvlc has to accept the
    /// URL shape and demux what comes back. It has bitten this codebase before - the
    /// cdda:// double-slash, the podcast redirect cap - and a share is hollow if the
    /// player won't open it. Silently skipped where libvlc's natives aren't present.
    /// </summary>
    [Fact]
    public async Task Libvlc_opens_the_share_url_and_reads_a_real_duration()
    {
        // Where the natives aren't deployed (headless Linux CI) there's nothing to prove.
        // Where they ARE - which is every Windows dev box and the packaged app - this test
        // must actually run and pass, so nothing below is wrapped in a swallowing catch.
        if (!Directory.Exists(Path.Combine(AppContext.BaseDirectory, "libvlc")))
        {
            return;
        }

        var (_, share) = Host(Track("t1", MakeWav(seconds: 2.0)));

        LibVLCSharp.Shared.Core.Initialize();
        using var vlc = new LibVLCSharp.Shared.LibVLC("--no-video", "--quiet");
        using var media = new LibVLCSharp.Shared.Media(vlc, $"{share.BaseUrl}/stream/t1.wav", LibVLCSharp.Shared.FromType.FromLocation);

        // Generous on purpose: this is a real decoder opening a real socket, and the
        // suite runs on build machines under load. A tight timeout here would fail for
        // being busy rather than for being broken.
        var status = await media.Parse(LibVLCSharp.Shared.MediaParseOptions.ParseNetwork, timeout: 30_000, cancellationToken: Ct);

        Assert.Equal(LibVLCSharp.Shared.MediaParsedStatus.Done, status);

        // 2 s of audio, pulled over HTTP and demuxed from a location with no local file:
        // a duration in the right neighbourhood proves the whole chain, not just a socket
        // that answered.
        Assert.InRange(media.Duration, 1_500, 2_500);
        Assert.NotEmpty(media.Tracks);
    }

    // ── Adversarial: a LAN service is reachable by anything ───

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task Every_mutating_verb_is_refused(string verb)
    {
        var (_, share) = Host(Track("t1", MakeWav()));

        using var request = new HttpRequestMessage(new HttpMethod(verb), $"{share.BaseUrl}/stream/t1");
        using var response = await _http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Theory]
    [InlineData("/stream/..%2F..%2Fwindows%2Fwin.ini")]
    [InlineData("/stream/....//....//etc/passwd")]
    [InlineData("/art/..%2F..%2Fsecret.png")]
    [InlineData("/stream/C%3A%5CWindows%5Cwin.ini")]
    public async Task A_path_cannot_be_walked_out_of_the_shared_set(string path)
    {
        // Two layers hold here: http.sys rejects an escaped traversal outright (403), and
        // for anything that does reach us, ids are looked up in the library and never
        // joined onto a directory - so there is nothing to traverse. Pinned so a future
        // "just resolve the file" refactor can't quietly turn this into a file server.
        var (_, share) = Host(Track("t1", MakeWav()));

        using var response = await _http.GetAsync($"{share.BaseUrl}{path}", Ct);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest });
    }

    [Fact]
    public async Task A_track_whose_file_vanished_is_a_404_not_a_dead_connection()
    {
        var path = MakeWav();
        var (_, share) = Host(Track("t1", path));
        File.Delete(path);

        using var response = await _http.GetAsync($"{share.BaseUrl}/stream/t1", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_ids_and_unknown_routes_404_rather_than_leaking_anything()
    {
        var (_, share) = Host(Track("t1", MakeWav()));

        foreach (var path in new[] { "/stream/nope", "/art/nope", "/", "/catalogue.json", "/stream/", "/admin" })
        {
            using var response = await _http.GetAsync($"{share.BaseUrl}{path}", Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_range_that_starts_past_the_end_serves_the_whole_file_instead_of_nothing()
    {
        var path = MakeWav();
        var length = new FileInfo(path).Length;
        var (_, share) = Host(Track("t1", path));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{share.BaseUrl}/stream/t1");
        request.Headers.Range = new RangeHeaderValue(length + 5000, null);

        using var response = await _http.SendAsync(request, Ct);

        // Deliberately generous rather than a 416: a player that miscounts gets audio,
        // not a stall. What must never happen is a truncated or empty body.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(length, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task An_id_containing_url_metacharacters_survives_the_round_trip()
    {
        var path = MakeWav();
        var (_, share) = Host(Track("a b/c?d#e", path, "Awkward Id"));

        var json = await _http.GetStringAsync($"{share.BaseUrl}/catalogue", Ct);
        var item = Assert.Single(ShareDiscovery.ParseCatalogue(json, share));

        var served = await _http.GetByteArrayAsync(item.StreamUrl!, Ct);
        Assert.Equal(await File.ReadAllBytesAsync(path, Ct), served);
    }

    [Fact]
    public async Task Two_shares_on_one_machine_stay_distinct_end_to_end()
    {
        var (_, first) = Host(Track("t1", MakeWav("one.wav", seconds: 0.5), "First Song"));
        var (_, second) = Host(Track("t1", MakeWav("two.wav", seconds: 1.5), "Second Song"));

        var a = ShareDiscovery.ParseCatalogue(await _http.GetStringAsync($"{first.BaseUrl}/catalogue", Ct), first);
        var b = ShareDiscovery.ParseCatalogue(await _http.GetStringAsync($"{second.BaseUrl}/catalogue", Ct), second);

        // Same remote id on both hosts - the namespacing is what keeps them apart.
        Assert.NotEqual(a[0].Id, b[0].Id);
        Assert.Equal("First Song", a[0].Title);
        Assert.Equal("Second Song", b[0].Title);

        // And each stream URL reaches its own host's file.
        Assert.NotEqual(
            await _http.GetByteArrayAsync(a[0].StreamUrl!, Ct),
            await _http.GetByteArrayAsync(b[0].StreamUrl!, Ct));
    }

    [Fact]
    public async Task Concurrent_streams_do_not_trip_over_each_other()
    {
        var path = MakeWav();
        var expected = await File.ReadAllBytesAsync(path, Ct);
        var (_, share) = Host(Track("t1", path));

        // One share, several listeners - the FileStream is opened per request with
        // FileShare.Read, and this is what proves it.
        var pulls = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => _http.GetByteArrayAsync($"{share.BaseUrl}/stream/t1", Ct)));

        Assert.All(pulls, pull => Assert.Equal(expected, pull));
    }
}
