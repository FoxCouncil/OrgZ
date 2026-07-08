// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace OrgZ.Services;

public enum StreamSessionStatus { Ok, Dead, GeoBlocked }

/// <summary>
/// Everything one connection to a radio stream can tell us. Header facts land during
/// <see cref="StreamSession.ConnectAsync"/>; measured facts (real codec, bitrate, first
/// title, tune-in time) settle once audio flows - <see cref="StreamSession.FactsSettled"/>
/// announces that moment.
/// </summary>
public sealed class StreamFacts
{
    public StreamSessionStatus Status { get; internal set; } = StreamSessionStatus.Dead;
    /// <summary>Human-facing base description: icy-name / content type for live streams, the error for dead ones.</summary>
    public string? Detail { get; internal set; }
    public string? FinalUrl { get; internal set; }
    public int Redirects { get; internal set; }
    public bool GeoSuspect { get; internal set; }
    public string? IcyName { get; internal set; }
    public string? IcyGenre { get; internal set; }
    /// <summary>icy-br - the server's claim, as opposed to <see cref="MeasuredBitrate"/>.</summary>
    public int? AdvertisedBitrate { get; internal set; }
    /// <summary>Normalized from Content-Type ("mp3", "aac", "ogg", "flac", "hls", "").</summary>
    public string? ContentFormat { get; internal set; }
    public int? MetaInt { get; internal set; }
    /// <summary>HLS timed-ID3 channel: "ts" (0x15 PES) | "seg" (packed-audio tag) | "emsg" (fMP4).</summary>
    public string? HlsMetaKind { get; internal set; }
    public string? MeasuredFormat { get; internal set; }
    public int? MeasuredBitrate { get; internal set; }
    /// <summary>First StreamTitle seen on this connection.</summary>
    public string? LiveTitle { get; internal set; }
    /// <summary>Per-track artwork accompanying <see cref="LiveTitle"/>, when the metadata channel carries one (iHeart EXTINF).</summary>
    public string? LiveArtUrl { get; internal set; }
    /// <summary>Connect start → first audio byte, across every redirect, playlist hop, and TLS handshake.</summary>
    public int? TuneInMs { get; internal set; }
}

/// <summary>
/// One now-playing update parsed off the stream's own bytes: the composed display title,
/// plus per-track artwork when the metadata channel carries it - iHeart EXTINF does, ICY
/// almost never does (ArtUrl null just means "no track art"; consumers fall back).
/// </summary>
public sealed record StreamNowPlaying(string Title, string? ArtUrl);

/// <summary>
/// ONE upstream pull per station, shared by every consumer: playback, now-playing metadata,
/// and probing all read from the same connection instead of opening their own. Connect
/// walks redirects and playlist indirections by hand (hop counts survive), handles classic
/// "ICY 200 OK" servers over raw TCP, and lands on one of three connection kinds:
///
///  - direct ICY audio (http/https): the body is de-interleaved inline - clean audio out
///    one side, StreamTitle events out the other, codec sniffed from the first bytes;
///  - raw ICY (SHOUTcast v1): same, over a hand-rolled HTTP/1.0 socket;
///  - HLS: WE are the client - variant pick, playlist refresh, one fetch per segment,
///    timed ID3 parsed from the same bytes VLC is about to play, AES-128 decrypted inline.
///
/// Consumption is either <see cref="CompleteProbeAsync"/> (bounded sample → facts → close;
/// the curator's probe path) or <see cref="StartPumping"/> (live audio into an
/// <see cref="AudioPipe"/> for a <see cref="PipeMediaInput"/>-backed Media; upstream drops
/// reconnect transparently). LibVLC never touches the network for radio - which also fixes
/// its HTTP/2 ICY blindness and HLS timed-ID3 deafness outright.
/// </summary>
public sealed class StreamSession : IDisposable
{
    private static readonly ILogger _log = Logging.For("StreamSession");

    // CDNs that geo-fence per station. A 200 from here still means "works from THIS vantage
    // point"; the flag marks the stream as worth suspicion before shipping it worldwide.
    private static readonly string[] GeoFencedHosts =
    [
        "streamtheworld.com",
        "ihrhls.com",
        "iheart.com",
        "revma.com",
        "tritondigital.com",
    ];

    // Redirects are walked by hand (hop counting) and live bodies are read forever, so the
    // client itself must not impose a timeout - per-phase deadlines come from linked CTSes.
    private static readonly HttpClient Http = CreateHttpClient();

    private static readonly TimeSpan ConnectDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReadStall = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan[] ReconnectDelays = [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];
    private const int SniffBudget = 64 * 1024;
    private const int MaxSegmentBytes = 8 * 1024 * 1024;

    private enum Kind { Failed, DirectIcy, RawIcy, Hls }

    private readonly Stopwatch _tuneIn = Stopwatch.StartNew();
    private readonly CancellationTokenSource _cts = new();

    private Kind _kind = Kind.Failed;
    private HttpResponseMessage? _resp;     // direct ICY: kept open, body is the stream
    private Stream? _bodyStream;            // direct + raw ICY
    private TcpClient? _tcp;                // raw ICY socket ownership
    private byte[] _prefetched = [];        // raw ICY: body bytes that arrived with the headers
    private string _playlistBody = "";      // HLS: the verified playlist
    private AudioPipe? _pipe;
    private Task? _pumpTask;
    private bool _consumed;                 // probe completed or pump started - one consumer per session
    private string? _lastTitle;
    private bool _factsSettled;
    private MemoryStream? _sniffBuffer;

    public StreamFacts Facts { get; } = new();
    public bool IsLive => Facts.Status == StreamSessionStatus.Ok;

    /// <summary>
    /// Raised from the pump thread with each NEW now-playing (deduplicated by title,
    /// unscrubbed; ArtUrl rides along when the channel has one). Null means the stream
    /// positively signalled a non-music break (iHeart ad/talk/sweeper spots) - consumers
    /// should flip back to station branding. Only ever null after a real title was raised.
    /// </summary>
    public event Action<StreamNowPlaying?>? NowPlayingChanged;
    /// <summary>Raised from the pump thread once measured facts (codec/bitrate/tune-in) are complete.</summary>
    public event Action? FactsSettled;

    private StreamSession()
    {
    }

    /// <summary>Never throws: a failed connect returns a session whose <see cref="Facts"/> carry the status and error detail.</summary>
    public static async Task<StreamSession> ConnectAsync(string url, CancellationToken ct)
    {
        var session = new StreamSession();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, session._cts.Token);
            deadline.CancelAfter(ConnectDeadline);
            await session.WalkAsync(url, 0, 0, deadline.Token);
        }
        catch (Exception ex)
        {
            session._kind = Kind.Failed;
            session.Facts.Status = StreamSessionStatus.Dead;
            session.Facts.Detail = ct.IsCancellationRequested ? "cancelled" : Shorten(ex);
        }
        return session;
    }

    // -- Connect: redirect + playlist walk (ported from the curator's prober so hop counts and status semantics survive) --

    private async Task WalkAsync(string url, int depth, int hops, CancellationToken ct)
    {
        if (depth > 3)
        {
            Fail(StreamSessionStatus.Dead, "playlist redirect loop", url, hops);
            return;
        }

        Facts.GeoSuspect |= IsGeoFencedHost(url);

        HttpResponseMessage resp;
        var current = url;
        while (true)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, current) { Version = new Version(1, 1), VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
                req.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
                resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex) when (LooksLikeIcyResponse(ex))
            {
                await RawConnectAsync(current, hops, ct);
                return;
            }

            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location != null)
            {
                var location = resp.Headers.Location.IsAbsoluteUri ? resp.Headers.Location.ToString() : new Uri(new Uri(current), resp.Headers.Location).ToString();
                resp.Dispose();
                hops++;
                if (hops > 10)
                {
                    Fail(StreamSessionStatus.Dead, "too many redirects", current, hops);
                    return;
                }
                current = location;
                Facts.GeoSuspect |= IsGeoFencedHost(current);
                continue;
            }
            break;
        }

        var finalUrl = current;
        var code = (int)resp.StatusCode;
        if (code is 403 or 451)
        {
            resp.Dispose();
            Facts.GeoSuspect = true;
            Fail(StreamSessionStatus.GeoBlocked, $"http {code} — likely geoblocked", finalUrl, hops);
            return;
        }
        if (!resp.IsSuccessStatusCode)
        {
            resp.Dispose();
            Fail(StreamSessionStatus.Dead, $"http {code}", finalUrl, hops);
            return;
        }

        var contentType = (resp.Content.Headers.ContentType?.MediaType ?? "").ToLowerInvariant();
        Facts.IcyName = Header(resp, "icy-name") ?? Facts.IcyName;
        Facts.IcyGenre = Header(resp, "icy-genre") ?? Facts.IcyGenre;
        if (int.TryParse(Header(resp, "icy-br")?.Split(',')[0], out var br) && br > 0 && br < 10000)
        {
            Facts.AdvertisedBitrate = br;
        }
        int? metaint = int.TryParse(Header(resp, "icy-metaint"), out var mi) && mi > 0 ? mi : null;

        // Playlist container (.pls / .m3u) → pull the first entry and walk THAT.
        if (IsPlaylist(contentType, finalUrl))
        {
            var body = await ReadTextAsync(resp, 64 * 1024, ct);
            resp.Dispose();
            if (body.Contains("#EXT-X-", StringComparison.Ordinal))
            {
                BecomeHls(body, finalUrl, hops);
                return;
            }
            var target = FirstPlaylistEntry(body);
            if (target == null)
            {
                Fail(StreamSessionStatus.Dead, "playlist with no entries", finalUrl, hops);
                return;
            }
            await WalkAsync(target, depth + 1, hops + 1, ct);
            return;
        }

        if (contentType is "application/vnd.apple.mpegurl" || finalUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            var body = await ReadTextAsync(resp, 64 * 1024, ct);
            resp.Dispose();
            if (!body.Contains("#EXTM3U", StringComparison.Ordinal))
            {
                Fail(StreamSessionStatus.Dead, Facts.IcyName ?? "not an HLS playlist", finalUrl, hops, "hls");
                return;
            }
            BecomeHls(body, finalUrl, hops);
            return;
        }

        // SHOUTcast DNAS sniffs the User-Agent on its root path: media players get the
        // stream, browsers get the status webpage - and our stock browser UA gets the
        // webpage. The legacy player path "/;" serves the raw stream to ANY UA, so walk
        // there when a root-ish URL came back as HTML. Anything else claiming text/html
        // is a webpage, not a stream - never pump markup into the decoder.
        if (contentType.StartsWith("text/html", StringComparison.Ordinal))
        {
            resp.Dispose();
            var uri = new Uri(finalUrl);
            if (uri.AbsolutePath is "/" or "" or "/index.html")
            {
                await WalkAsync(uri.GetLeftPart(UriPartial.Authority) + "/;", depth + 1, hops + 1, ct);
                return;
            }
            Fail(StreamSessionStatus.Dead, "html page, not a stream", finalUrl, hops);
            return;
        }

        // Direct audio stream - hold the connection open; probe or pump consumes it.
        _kind = Kind.DirectIcy;
        _resp = resp;
        _bodyStream = await resp.Content.ReadAsStreamAsync(ct);
        Facts.Status = StreamSessionStatus.Ok;
        Facts.Detail = Facts.IcyName ?? contentType;
        Facts.FinalUrl = finalUrl;
        Facts.Redirects = hops;
        Facts.MetaInt = metaint;
        Facts.ContentFormat = MimeToFormat(contentType);
    }

    private void BecomeHls(string playlistBody, string finalUrl, int hops)
    {
        _kind = Kind.Hls;
        _playlistBody = playlistBody;
        Facts.Status = StreamSessionStatus.Ok;
        Facts.Detail = Facts.IcyName ?? "HLS playlist";
        Facts.FinalUrl = finalUrl;
        Facts.Redirects = hops;
        Facts.ContentFormat = "hls";
    }

    private void Fail(StreamSessionStatus status, string detail, string? finalUrl, int hops, string? contentFormat = null)
    {
        _kind = Kind.Failed;
        Facts.Status = status;
        Facts.Detail = detail;
        Facts.FinalUrl = finalUrl;
        Facts.Redirects = hops;
        Facts.ContentFormat = contentFormat ?? Facts.ContentFormat;
    }

    /// <summary>Hand-rolled HTTP/1.0 over TCP for servers that answer "ICY 200 OK" - HttpClient rejects the status line as malformed.</summary>
    private async Task RawConnectAsync(string url, int hops, CancellationToken ct)
    {
        var uri = new Uri(url);
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(uri.Host, uri.Port, ct);
            Stream stream = tcp.GetStream();
            if (uri.Scheme == "https")
            {
                var ssl = new SslStream(stream);
                await ssl.AuthenticateAsClientAsync(uri.Host);
                stream = ssl;
            }

            var request = $"GET {uri.PathAndQuery} HTTP/1.0\r\nHost: {uri.Host}\r\nUser-Agent: {Web.BrowserUa}\r\nIcy-MetaData: 1\r\nAccept: */*\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);

            // Read just past the header terminator; whatever body bytes rode along are kept.
            var buffer = new byte[16 * 1024];
            var total = 0;
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
                if (read == 0)
                {
                    break;
                }
                total += read;
                var head = Encoding.ASCII.GetString(buffer, 0, Math.Min(total, 8 * 1024));
                headerEnd = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd < 0 && total > 8 * 1024)
                {
                    tcp.Dispose();
                    Fail(StreamSessionStatus.Dead, "unterminated response headers", url, hops);
                    return;
                }
                if (total == buffer.Length)
                {
                    break;
                }
            }
            if (headerEnd < 0)
            {
                tcp.Dispose();
                Fail(StreamSessionStatus.Dead, "no response headers", url, hops);
                return;
            }

            var headText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
            if (!headText.StartsWith("ICY 200", StringComparison.OrdinalIgnoreCase) && !headText.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase))
            {
                tcp.Dispose();
                Fail(StreamSessionStatus.Dead, "unrecognized server response", url, hops);
                return;
            }

            string? contentType = null;
            foreach (var line in headText.Split("\r\n"))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0)
                {
                    continue;
                }
                var key = line[..idx].Trim().ToLowerInvariant();
                var value = line[(idx + 1)..].Trim();
                switch (key)
                {
                    case "icy-name": { Facts.IcyName = value; } break;
                    case "icy-genre": { Facts.IcyGenre = value; } break;
                    case "icy-br": { if (int.TryParse(value.Split(',')[0], out var br) && br > 0) { Facts.AdvertisedBitrate = br; } } break;
                    case "icy-metaint": { if (int.TryParse(value, out var m) && m > 0) { Facts.MetaInt = m; } } break;
                    case "content-type": { contentType = value.ToLowerInvariant(); } break;
                }
            }

            _kind = Kind.RawIcy;
            _tcp = tcp;
            _bodyStream = stream;
            _prefetched = buffer[(headerEnd + 4)..total];
            Facts.Status = StreamSessionStatus.Ok;
            Facts.Detail = Facts.IcyName ?? "ICY stream";
            Facts.FinalUrl = url;
            Facts.Redirects = hops;
            Facts.ContentFormat = MimeToFormat(contentType ?? "");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    // -- Probe mode: bounded sample → facts → close. The curator's health check, one pull. --

    public async Task<StreamFacts> CompleteProbeAsync(CancellationToken ct)
    {
        ThrowIfConsumed();
        _consumed = true;

        if (_kind == Kind.Failed)
        {
            return Facts;
        }

        try
        {
            if (_kind == Kind.Hls)
            {
                // Bounded per-request by HlsProber's own client timeout - no overall cap,
                // matching the prober's historical behavior on slow CDNs.
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
                var hls = await HlsProber.InspectAsync(Facts.FinalUrl!, _playlistBody, linked.Token, _tuneIn);
                Facts.HlsMetaKind = hls.MetaKind;
                Facts.LiveTitle = hls.StreamTitle;
                Facts.MeasuredFormat = hls.MeasuredFormat;
                Facts.MeasuredBitrate = hls.MeasuredBitrate;
                Facts.Redirects += hls.ExtraHops;
                Facts.TuneInMs = hls.FirstAudioMs;
                return Facts;
            }

            // Direct or raw ICY: sample enough to cover one metadata block when the server
            // granted metaint, otherwise enough to frame-parse the codec and bitrate.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var budget = Facts.MetaInt is int m && m <= 96 * 1024 ? m + 1 + 255 * 16 + 1024 : 32 * 1024;
            var sample = await ReadSampleAsync(budget, deadline.Token, ct);
            if (sample.Length == 0)
            {
                Facts.Status = StreamSessionStatus.Dead;
                Facts.Detail = "connected but no audio bytes";
                return Facts;
            }

            var audio = sample;
            if (Facts.MetaInt is int stride && stride < sample.Length)
            {
                (audio, Facts.LiveTitle) = IcyMetadata.Deinterleave(sample, stride);
            }
            var sniff = AudioSniffer.Sniff(audio);
            Facts.MeasuredFormat = sniff?.Format;
            Facts.MeasuredBitrate = sniff?.Bitrate;
            return Facts;
        }
        catch (Exception ex)
        {
            if (Facts.Status == StreamSessionStatus.Ok)
            {
                Facts.Status = StreamSessionStatus.Dead;
                Facts.Detail = ct.IsCancellationRequested ? "cancelled" : Shorten(ex);
            }
            return Facts;
        }
        finally
        {
            SettleFacts();
            CloseConnection();
        }
    }

    private async Task<byte[]> ReadSampleAsync(int budget, CancellationToken deadline, CancellationToken caller)
    {
        var buffer = new byte[budget];
        var total = 0;
        if (_prefetched.Length > 0)
        {
            var take = Math.Min(_prefetched.Length, budget);
            Array.Copy(_prefetched, buffer, take);
            total = take;
            Facts.TuneInMs ??= (int)_tuneIn.ElapsedMilliseconds;
        }
        try
        {
            while (total < budget)
            {
                var read = await _bodyStream!.ReadAsync(buffer.AsMemory(total), deadline);
                if (read == 0)
                {
                    break;
                }
                Facts.TuneInMs ??= (int)_tuneIn.ElapsedMilliseconds;
                total += read;
            }
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            // Slow stream hit the sample deadline - a partial sample still sniffs fine.
        }
        return buffer[..total];
    }

    // -- Playback mode: live pump into the AudioPipe, reconnecting across upstream drops --

    /// <summary>Starts the pump and returns the pipe a <see cref="PipeMediaInput"/> should read. Call once, only when <see cref="IsLive"/>.</summary>
    public AudioPipe StartPumping()
    {
        ThrowIfConsumed();
        if (!IsLive)
        {
            throw new InvalidOperationException($"session is not live: {Facts.Detail}");
        }
        _consumed = true;
        _pipe = new AudioPipe();
        _sniffBuffer = new MemoryStream();
        _pumpTask = Task.Run(async () =>
        {
            try
            {
                if (_kind == Kind.Hls)
                {
                    await HlsPumpAsync(_cts.Token);
                }
                else
                {
                    await IcyPumpAsync(_cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Session disposed - playback moved on.
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Pump died for {Url}", Facts.FinalUrl);
            }
            finally
            {
                SettleFacts();
                _pipe.Complete();
                CloseConnection();
            }
        });
        return _pipe;
    }

    private async Task IcyPumpAsync(CancellationToken ct)
    {
        var attempts = 0;
        var prefetch = _prefetched;
        _prefetched = [];

        while (!ct.IsCancellationRequested)
        {
            var connectionStart = Stopwatch.GetTimestamp();
            try
            {
                var deinterleaver = new IcyDeinterleaver(Facts.MetaInt ?? 0);
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(ReadStall);

                if (prefetch.Length > 0)
                {
                    FeedPump(deinterleaver, prefetch, 0, prefetch.Length, ct);
                    prefetch = [];
                }

                var buffer = new byte[16 * 1024];
                while (true)
                {
                    var read = await _bodyStream!.ReadAsync(buffer, stall.Token);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("upstream closed the stream");
                    }
                    stall.CancelAfter(ReadStall);
                    FeedPump(deinterleaver, buffer, 0, read, ct);

                    // A connection that has streamed for 30s is healthy - forgive old failures.
                    if (attempts > 0 && Stopwatch.GetElapsedTime(connectionStart) > TimeSpan.FromSeconds(30))
                    {
                        attempts = 0;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (PipeClosedException)
            {
                return; // VLC side ended (station switch) - nothing to reconnect for
            }
            catch (Exception ex)
            {
                _log.Information("Stream dropped for {Url}: {Reason}", Facts.FinalUrl, Shorten(ex));
                var reconnected = false;
                while (attempts < ReconnectDelays.Length)
                {
                    var delay = ReconnectDelays[attempts];
                    attempts++;
                    await Task.Delay(delay, ct);
                    try
                    {
                        prefetch = await ReconnectAsync(ct);
                        reconnected = true;
                        _log.Information("Reconnected to {Url} (attempt {Attempt})", Facts.FinalUrl, attempts);
                        break;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception rex)
                    {
                        _log.Information("Reconnect {Attempt}/{Max} failed: {Reason}", attempts, ReconnectDelays.Length, Shorten(rex));
                        prefetch = [];
                    }
                }
                if (!reconnected)
                {
                    _log.Warning("Giving up on {Url} after {Attempts} reconnect attempts", Facts.FinalUrl, attempts);
                    return;
                }
            }
        }
    }

    /// <summary>Re-establishes the ICY connection after a drop; returns body bytes that rode in with the headers (raw path).</summary>
    private async Task<byte[]> ReconnectAsync(CancellationToken ct)
    {
        CloseConnection();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ConnectDeadline);

        if (_kind == Kind.RawIcy)
        {
            await RawConnectAsync(Facts.FinalUrl!, Facts.Redirects, deadline.Token);
            if (_kind != Kind.RawIcy || _bodyStream == null)
            {
                throw new EndOfStreamException(Facts.Detail ?? "raw reconnect failed");
            }
            var prefetch = _prefetched;
            _prefetched = [];
            return prefetch;
        }

        // Direct: re-GET the resolved URL, following simple redirects.
        var current = Facts.FinalUrl!;
        for (var hop = 0; hop <= 5; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, current) { Version = new Version(1, 1), VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
            req.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
            var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location != null)
            {
                current = resp.Headers.Location.IsAbsoluteUri ? resp.Headers.Location.ToString() : new Uri(new Uri(current), resp.Headers.Location).ToString();
                resp.Dispose();
                continue;
            }
            if (!resp.IsSuccessStatusCode)
            {
                resp.Dispose();
                throw new EndOfStreamException($"reconnect got http {(int)resp.StatusCode}");
            }
            _resp = resp;
            _bodyStream = await resp.Content.ReadAsStreamAsync(deadline.Token);
            // The server may grant a different metaint on the new connection.
            Facts.MetaInt = int.TryParse(Header(resp, "icy-metaint"), out var mi) && mi > 0 ? mi : null;
            return [];
        }
        throw new EndOfStreamException("reconnect redirect loop");
    }

    /// <summary>Routes de-interleaved audio to the pipe (+ the sniffer until facts settle) and titles to subscribers.</summary>
    private void FeedPump(IcyDeinterleaver deinterleaver, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        deinterleaver.Feed(buffer, offset, count,
            onAudio: (data, start, length) =>
            {
                Facts.TuneInMs ??= (int)_tuneIn.ElapsedMilliseconds;
                var chunk = new byte[length];
                Array.Copy(data, start, chunk, 0, length);
                if (!_factsSettled && _sniffBuffer != null)
                {
                    _sniffBuffer.Write(chunk, 0, chunk.Length);
                    if (_sniffBuffer.Length >= SniffBudget)
                    {
                        var sniff = AudioSniffer.Sniff(_sniffBuffer.ToArray());
                        Facts.MeasuredFormat = sniff?.Format;
                        Facts.MeasuredBitrate = sniff?.Bitrate;
                        SettleFacts();
                    }
                }
                if (!_pipe!.Write(chunk, ct))
                {
                    throw new PipeClosedException();
                }
            },
            onTitle: title => RaiseNowPlaying(title, artUrl: null));   // ICY carries no per-track art
    }

    private void RaiseNowPlaying(string title, string? artUrl)
    {
        if (title == _lastTitle)
        {
            return;
        }
        _lastTitle = title;
        if (Facts.LiveTitle == null)
        {
            // Snapshot the FIRST now-playing (title + its art together) for the
            // subscribe-after-pump-start race - consumers re-read it once wired.
            Facts.LiveTitle = title;
            Facts.LiveArtUrl = artUrl;
        }
        NowPlayingChanged?.Invoke(new StreamNowPlaying(title, artUrl));
    }

    /// <summary>The stream positively signalled a non-music break - clear the displayed track (once; consecutive break segments are silent, and a break before any title is a no-op).</summary>
    private void RaiseBreak()
    {
        if (_lastTitle == null)
        {
            return;
        }
        _lastTitle = null;
        NowPlayingChanged?.Invoke(null);
    }

    private void SettleFacts()
    {
        if (_factsSettled)
        {
            return;
        }
        _factsSettled = true;
        _sniffBuffer = null;
        FactsSettled?.Invoke();
    }

    private sealed class PipeClosedException : Exception;

    // -- HLS pump: we are the playlist client; every segment is fetched once and shared --

    private async Task HlsPumpAsync(CancellationToken ct)
    {
        var url = Facts.FinalUrl!;
        var body = _playlistBody;

        // Master playlist → the variant the player would favor (highest bandwidth).
        if (body.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
        {
            var variant = HlsProber.PickVariant(body, url);
            if (variant == null)
            {
                _log.Warning("HLS master playlist with no usable variant: {Url}", url);
                return;
            }
            url = variant;
            Facts.Redirects++;
            body = await FetchPlaylistWithRetriesAsync(url, ct) ?? throw new EndOfStreamException("variant playlist unreachable");
        }

        var keyCache = new Dictionary<string, byte[]>();
        string? sentInitUrl = null;
        var lastSequence = -1L;
        var first = true;
        var playlistFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            var playlist = HlsMediaPlaylist.Parse(body, url);
            if (playlist.UnsupportedKeyMethod != null)
            {
                _log.Warning("HLS stream uses {Method} encryption — cannot play: {Url}", playlist.UnsupportedKeyMethod, url);
                return;
            }

            var fresh = first
                ? playlist.Segments.Skip(Math.Max(0, playlist.Segments.Count - 3)).ToList()
                : playlist.Segments.Where(s => s.Sequence > lastSequence).ToList();
            first = false;

            var pumped = false;
            foreach (var segment in fresh)
            {
                var bytes = await FetchSegmentAsync(segment, keyCache, ct);
                lastSequence = segment.Sequence;
                if (bytes.Length == 0)
                {
                    continue;
                }
                Facts.TuneInMs ??= (int)_tuneIn.ElapsedMilliseconds;

                // The same bytes VLC is about to play carry the metadata - scan, then pump.
                // iHeart/revma: now-playing (and per-track art) rides the chunklist's EXTINF
                // attributes in plaintext, music spots only - ad/talk spots carry junk and
                // are filtered by Segment.NowPlaying. An in-segment ID3 title still wins,
                // but the EXTINF artwork accompanies it either way. Same connection, zero
                // extra requests.
                var info = HlsProber.SniffSegment(bytes);
                Facts.HlsMetaKind ??= info.MetaKind;
                if (segment.SpotType != null || segment.Title != null)
                {
                    // EXTINF attributes present at all = the channel exists - even when a
                    // promo block means every current entry is filtered junk, so an
                    // audition during a break still records the station as metadata-capable.
                    Facts.HlsMetaKind ??= "extinf";
                }
                var extinfNowPlaying = segment.NowPlaying;
                if ((info.Title ?? extinfNowPlaying) is { } nowPlaying)
                {
                    RaiseNowPlaying(nowPlaying, extinfNowPlaying != null ? segment.ArtUrl : null);
                }
                else if (segment.IsSpotBreak)
                {
                    // Ad/talk/sweeper spot - flip consumers back to station branding
                    // (only a POSITIVE spot marker counts; "no metadata" is not a break).
                    RaiseBreak();
                }
                if (!_factsSettled)
                {
                    Facts.MeasuredFormat = info.Codec != null ? $"hls+{info.Codec}" : null;
                    Facts.MeasuredBitrate = info.SniffedKbps ?? (segment.Duration is > 0 ? (int)Math.Round(bytes.Length * 8.0 / segment.Duration.Value / 1000.0) : null);
                    SettleFacts();
                }

                if (playlist.InitSegmentUrl != null && playlist.InitSegmentUrl != sentInitUrl)
                {
                    var (init, _, _) = await HlsProber.FetchBytesAsync(playlist.InitSegmentUrl, MaxSegmentBytes, ct);
                    if (init.Length > 0 && !_pipe!.Write(init, ct))
                    {
                        return;
                    }
                    sentInitUrl = playlist.InitSegmentUrl;
                }

                var payload = info.HasLeadingId3 ? StripLeadingId3(bytes) : bytes;
                if (payload.Length > 0 && !_pipe!.Write(payload, ct))
                {
                    return;
                }
                pumped = true;
            }

            if (playlist.EndList)
            {
                _log.Information("HLS playlist ended (ENDLIST): {Url}", url);
                return;
            }

            // Live cadence: near the edge wait a full target duration; when the refresh
            // brought nothing new, poll at half cadence to catch up faster.
            var wait = TimeSpan.FromSeconds(Math.Clamp(playlist.TargetDuration * (pumped ? 1.0 : 0.5), 1, 10));
            await Task.Delay(wait, ct);

            var next = await FetchPlaylistWithRetriesAsync(url, ct);
            if (next == null)
            {
                playlistFailures++;
                if (playlistFailures > 3)
                {
                    _log.Warning("HLS playlist unreachable after {Failures} rounds; giving up: {Url}", playlistFailures, url);
                    return;
                }
                continue;
            }
            playlistFailures = 0;
            body = next;
        }
    }

    private static async Task<string?> FetchPlaylistWithRetriesAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var body = await HlsProber.FetchTextAsync(url, ct);
                if (body != null && body.Contains("#EXTM3U", StringComparison.Ordinal))
                {
                    return body;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Transient CDN hiccup - retry below.
            }
            await Task.Delay(TimeSpan.FromSeconds(1 + attempt), ct);
        }
        return null;
    }

    private async Task<byte[]> FetchSegmentAsync(HlsMediaPlaylist.Segment segment, Dictionary<string, byte[]> keyCache, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var (bytes, _, _) = await HlsProber.FetchBytesAsync(segment.Url, MaxSegmentBytes, ct);
                if (bytes.Length == 0)
                {
                    continue;
                }
                if (segment.KeyUrl == null)
                {
                    return bytes;
                }

                if (!keyCache.TryGetValue(segment.KeyUrl, out var key))
                {
                    (key, _, _) = await HlsProber.FetchBytesAsync(segment.KeyUrl, 64, ct);
                    if (key.Length != 16)
                    {
                        _log.Warning("AES-128 key fetch returned {Length} bytes (expected 16): {Url}", key.Length, segment.KeyUrl);
                        return [];
                    }
                    keyCache[segment.KeyUrl] = key;
                }

                // IV: explicit from the tag, else the segment's media sequence number big-endian.
                var iv = segment.KeyIv ?? SequenceIv(segment.Sequence);
                using var aes = Aes.Create();
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                return aes.CreateDecryptor().TransformFinalBlock(bytes, 0, bytes.Length);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Information("Segment fetch failed (attempt {Attempt}): {Reason}", attempt + 1, Shorten(ex));
            }
        }
        return [];
    }

    private static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        for (var i = 0; i < 8; i++)
        {
            iv[15 - i] = (byte)(sequence >> (8 * i));
        }
        return iv;
    }

    /// <summary>Packed-audio segments front their ES with ID3 tags (timestamp PRIV + optional text frames) - VLC wants the bare elementary stream.</summary>
    internal static byte[] StripLeadingId3(byte[] segment)
    {
        var offset = 0;
        while (offset + 10 < segment.Length && segment[offset] == 'I' && segment[offset + 1] == 'D' && segment[offset + 2] == '3')
        {
            var (_, tagSize) = Id3.Parse(segment, offset);
            if (tagSize <= 10)
            {
                break;
            }
            offset += tagSize;
        }
        return offset == 0 ? segment : segment[offset..];
    }

    // -- Shared helpers --

    private void CloseConnection()
    {
        try
        {
            _bodyStream?.Dispose();
        }
        catch
        {
            // Native/socket teardown races are not actionable.
        }
        _bodyStream = null;
        _resp?.Dispose();
        _resp = null;
        _tcp?.Dispose();
        _tcp = null;
    }

    private void ThrowIfConsumed()
    {
        if (_consumed)
        {
            throw new InvalidOperationException("a StreamSession has exactly one consumer (probe or pump)");
        }
    }

    private int _disposed;

    public void Dispose()
    {
        // Idempotent by contract: teardown paths legitimately dispose twice (close-the-
        // upstream-now at swap time, then again with the deferred MediaInput dispose).
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _cts.Cancel();
        _pipe?.Complete();
        if (_pumpTask == null)
        {
            // No pump owns the connection - close it here and release the CTS now. When a
            // pump exists it still holds the token (linked stall/deadline sources), so the
            // CTS is released only after the pump task drains.
            CloseConnection();
            _cts.Dispose();
        }
        else
        {
            _pumpTask.ContinueWith(_ => _cts.Dispose(), TaskScheduler.Default);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Web.BrowserUa);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        return client;
    }

    private static string? Header(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static bool LooksLikeIcyResponse(HttpRequestException ex) =>
        ex.Message.Contains("invalid", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase);

    private static bool IsGeoFencedHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && GeoFencedHosts.Any(h => uri.Host.EndsWith(h, StringComparison.OrdinalIgnoreCase));

    private static bool IsPlaylist(string contentType, string url) =>
        contentType is "audio/x-scpls" or "application/pls+xml" or "audio/x-mpegurl" or "application/x-mpegurl" or "audio/mpegurl"
        || url.EndsWith(".pls", StringComparison.OrdinalIgnoreCase)
        || (url.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) && !url.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase));

    private static string? FirstPlaylistEntry(string body)
    {
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("File", StringComparison.OrdinalIgnoreCase) && line.Contains('='))
            {
                line = line[(line.IndexOf('=') + 1)..].Trim();
            }
            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }
        return null;
    }

    private static async Task<string> ReadTextAsync(HttpResponseMessage resp, int maxBytes, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[maxBytes];
        var total = 0;
        while (total < maxBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    public static string MimeToFormat(string contentType) => contentType switch
    {
        "audio/mpeg" or "audio/mp3" => "mp3",
        "audio/aac" or "audio/aacp" or "audio/x-aac" => "aac",
        "audio/ogg" or "application/ogg" or "audio/opus" => "ogg",
        "audio/flac" or "audio/x-flac" => "flac",
        "application/vnd.apple.mpegurl" => "hls",
        _ => "",
    };

    private static string Shorten(Exception ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message;
        return msg.Length > 80 ? msg[..80] : msg;
    }
}
