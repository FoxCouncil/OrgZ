// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace OrgZ.StationCurator.Services;

public sealed record ProbeOutcome(
    bool Ok,
    string Status,              // ProbeStatus.* value
    string? Detail,             // icy-name / error text, whatever a human wants to see
    string? Format,             // normalized format per the server's Content-Type claim
    int? Bitrate,               // icy-br when the server reports it
    string? IcyGenre,
    string? ResolvedUrl,        // post-redirect, post-playlist direct URL
    bool GeoSuspect,
    int Redirects = 0,          // HTTP redirects + playlist indirections crossed before audio
    int? MetaInt = null,        // icy-metaint the server granted → in-stream metadata works
    string? StreamTitle = null, // live StreamTitle captured from the first metadata block
    string? MeasuredFormat = null,  // what the audio bytes actually are
    int? MeasuredBitrate = null,    // bitrate measured off real frames
    string? ServerIp = null,
    string? ServerCountry = null,
    string? ServerCountryCode = null);

/// <summary>
/// Connects to a stream and reports what is actually there. Redirects are followed by hand so
/// the hop count survives (playlist indirections count too - every hop delays tune-in), the
/// audio bytes are frame-parsed to measure the REAL codec and bitrate instead of trusting
/// Content-Type/icy-br, the Icy-MetaData handshake is checked (and a live StreamTitle pulled
/// from the first metadata block as proof), and the final server is geolocated via DNS+GeoIP.
/// Classic SHOUTcast v1 servers answer "ICY 200 OK", which HttpClient rejects as malformed -
/// those fall back to a raw TCP probe that speaks just enough HTTP to do all of the above.
/// </summary>
public static class StreamProber
{
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

    private static readonly HttpClient Http = Web.Create(TimeSpan.FromSeconds(15), allowRedirects: false);

    public static async Task<ProbeOutcome> ProbeAsync(string url, CancellationToken ct)
    {
        var outcome = await ProbeCoreAsync(url, ct, 0, 0);

        // Geolocate the server we actually ended up talking to (post-redirect, post-playlist).
        if ((outcome.Ok || outcome.Status == Models.ProbeStatus.Geo) && Uri.TryCreate(outcome.ResolvedUrl ?? url, UriKind.Absolute, out var final))
        {
            var geo = await GeoIp.LookupAsync(final.Host);
            if (geo != null)
            {
                outcome = outcome with { ServerIp = geo.Ip, ServerCountry = geo.Country, ServerCountryCode = geo.CountryCode };
            }
        }
        return outcome;
    }

    private static async Task<ProbeOutcome> ProbeCoreAsync(string url, CancellationToken ct, int depth, int hops)
    {
        if (depth > 3)
        {
            return new ProbeOutcome(false, Models.ProbeStatus.Dead, "playlist redirect loop", null, null, null, null, false, Redirects: hops);
        }

        var geoSuspect = IsGeoFencedHost(url);

        // Follow redirects by hand so the chain length is measurable.
        HttpResponseMessage resp;
        var current = url;
        while (true)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, current);
                req.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
                resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex) when (LooksLikeIcyResponse(ex))
            {
                return await RawIcyProbeAsync(current, geoSuspect, hops, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
            {
                var reason = ct.IsCancellationRequested ? "cancelled" : Shorten(ex);
                return new ProbeOutcome(false, Models.ProbeStatus.Dead, reason, null, null, null, null, geoSuspect, Redirects: hops);
            }

            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location != null)
            {
                var location = resp.Headers.Location.IsAbsoluteUri ? resp.Headers.Location.ToString() : new Uri(new Uri(current), resp.Headers.Location).ToString();
                resp.Dispose();
                hops++;
                if (hops > 10)
                {
                    return new ProbeOutcome(false, Models.ProbeStatus.Dead, "too many redirects", null, null, null, current, geoSuspect, Redirects: hops);
                }
                current = location;
                geoSuspect |= IsGeoFencedHost(current);
                continue;
            }
            break;
        }

        using (resp)
        {
            var finalUrl = current;

            var code = (int)resp.StatusCode;
            if (code is 403 or 451)
            {
                return new ProbeOutcome(false, Models.ProbeStatus.Geo, $"http {code} — likely geoblocked", null, null, null, finalUrl, true, Redirects: hops);
            }
            if (!resp.IsSuccessStatusCode)
            {
                return new ProbeOutcome(false, Models.ProbeStatus.Dead, $"http {code}", null, null, null, finalUrl, geoSuspect, Redirects: hops);
            }

            var contentType = (resp.Content.Headers.ContentType?.MediaType ?? "").ToLowerInvariant();
            var icyName = Header(resp, "icy-name");
            var icyGenre = Header(resp, "icy-genre");
            int? icyBr = int.TryParse(Header(resp, "icy-br")?.Split(',')[0], out var br) && br > 0 && br < 10000 ? br : null;
            int? metaint = int.TryParse(Header(resp, "icy-metaint"), out var mi) && mi > 0 ? mi : null;

            // Playlist container → pull the first entry and probe that instead.
            if (IsPlaylist(contentType, finalUrl))
            {
                var body = await ReadTextAsync(resp, 64 * 1024, ct);
                if (body.Contains("#EXT-X-", StringComparison.Ordinal))
                {
                    return new ProbeOutcome(true, Models.ProbeStatus.Ok, icyName ?? "HLS playlist", "hls", icyBr, icyGenre, finalUrl, geoSuspect, Redirects: hops);
                }

                var target = FirstPlaylistEntry(body);
                if (target == null)
                {
                    return new ProbeOutcome(false, Models.ProbeStatus.Dead, "playlist with no entries", null, null, null, finalUrl, geoSuspect, Redirects: hops);
                }
                return await ProbeCoreAsync(target, ct, depth + 1, hops + 1);
            }

            if (contentType is "application/vnd.apple.mpegurl" || finalUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                var body = await ReadTextAsync(resp, 64 * 1024, ct);
                var hlsOk = body.Contains("#EXTM3U", StringComparison.Ordinal);
                return new ProbeOutcome(hlsOk, hlsOk ? Models.ProbeStatus.Ok : Models.ProbeStatus.Dead, icyName ?? (hlsOk ? "HLS playlist" : "not an HLS playlist"), "hls", icyBr, icyGenre, finalUrl, geoSuspect, Redirects: hops);
            }

            // Direct audio stream: sample enough bytes to cover one metadata block when the
            // server granted metaint, otherwise enough to frame-parse the codec and bitrate.
            var budget = metaint is int m && m <= 96 * 1024 ? m + 1 + 255 * 16 + 1024 : 32 * 1024;
            var sample = await ReadStreamSampleAsync(resp, budget, ct);
            var format = MimeToFormat(contentType);
            if (sample.Length == 0)
            {
                return new ProbeOutcome(false, Models.ProbeStatus.Dead, "connected but no audio bytes", format, icyBr, icyGenre, finalUrl, geoSuspect, Redirects: hops, MetaInt: metaint);
            }

            var audio = sample;
            string? title = null;
            if (metaint is int stride && stride < sample.Length)
            {
                (audio, title) = IcyDeinterleave(sample, stride);
            }
            var sniff = AudioSniffer.Sniff(audio);

            var detail = icyName ?? contentType;
            if (!string.IsNullOrEmpty(title))
            {
                detail = $"{detail} ♪ {title}";
            }
            return new ProbeOutcome(true, Models.ProbeStatus.Ok, detail, format, icyBr, icyGenre, finalUrl, geoSuspect,
                Redirects: hops, MetaInt: metaint, StreamTitle: title, MeasuredFormat: sniff?.Format, MeasuredBitrate: sniff?.Bitrate);
        }
    }

    /// <summary>Minimal HTTP/1.0-over-TCP probe for servers that answer "ICY 200 OK".</summary>
    private static async Task<ProbeOutcome> RawIcyProbeAsync(string url, bool geoSuspect, int hops, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(url);
            using var tcp = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));

            await tcp.ConnectAsync(uri.Host, uri.Port, timeout.Token);
            Stream stream = tcp.GetStream();
            if (uri.Scheme == "https")
            {
                var ssl = new SslStream(stream);
                await ssl.AuthenticateAsClientAsync(uri.Host);
                stream = ssl;
            }

            var request = $"GET {uri.PathAndQuery} HTTP/1.0\r\nHost: {uri.Host}\r\nUser-Agent: {Web.BrowserUa}\r\nIcy-MetaData: 1\r\nAccept: */*\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);

            // Read headers first; once icy-metaint is known, extend the read to cover one
            // full metadata block so the sniff sees clean audio and we can grab the title.
            var buffer = new byte[64 * 1024];
            var total = 0;
            var headerEnd = -1;
            var desired = buffer.Length;
            string? icyName = null, icyGenre = null, contentType = null;
            int? icyBr = null, metaint = null;
            try
            {
                while (total < desired)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total, desired - total), timeout.Token);
                    if (read == 0)
                    {
                        break;
                    }
                    total += read;

                    if (headerEnd < 0)
                    {
                        var head = Encoding.ASCII.GetString(buffer, 0, Math.Min(total, 8 * 1024));
                        headerEnd = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (headerEnd < 0)
                        {
                            if (total > 8 * 1024)
                            {
                                return new ProbeOutcome(false, Models.ProbeStatus.Dead, "unterminated response headers", null, null, null, url, geoSuspect, Redirects: hops);
                            }
                            continue;
                        }

                        if (!head.StartsWith("ICY 200", StringComparison.OrdinalIgnoreCase) && !head.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase))
                        {
                            return new ProbeOutcome(false, Models.ProbeStatus.Dead, "unrecognized server response", null, null, null, url, geoSuspect, Redirects: hops);
                        }

                        foreach (var line in head[..headerEnd].Split("\r\n"))
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
                                case "icy-name": { icyName = value; } break;
                                case "icy-genre": { icyGenre = value; } break;
                                case "icy-br": { if (int.TryParse(value.Split(',')[0], out var br) && br > 0) { icyBr = br; } } break;
                                case "icy-metaint": { if (int.TryParse(value, out var m) && m > 0) { metaint = m; } } break;
                                case "content-type": { contentType = value.ToLowerInvariant(); } break;
                            }
                        }

                        var budget = metaint is int stride && stride <= 40 * 1024 ? stride + 1 + 255 * 16 + 1024 : 32 * 1024;
                        desired = Math.Min(buffer.Length, headerEnd + 4 + budget);
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Slow stream - work with whatever arrived.
            }

            if (headerEnd < 0)
            {
                return new ProbeOutcome(false, Models.ProbeStatus.Dead, "no response headers", null, null, null, url, geoSuspect, Redirects: hops);
            }

            var body = buffer[(headerEnd + 4)..total];
            var ok = body.Length > 0;
            var audio = body;
            string? title = null;
            if (metaint is int stride2 && stride2 < body.Length)
            {
                (audio, title) = IcyDeinterleave(body, stride2);
            }
            var sniff = ok ? AudioSniffer.Sniff(audio) : null;

            var detail = icyName ?? "ICY stream";
            if (!string.IsNullOrEmpty(title))
            {
                detail = $"{detail} ♪ {title}";
            }
            return new ProbeOutcome(ok, ok ? Models.ProbeStatus.Ok : Models.ProbeStatus.Dead, detail, MimeToFormat(contentType ?? ""), icyBr, icyGenre, url, geoSuspect,
                Redirects: hops, MetaInt: metaint, StreamTitle: title, MeasuredFormat: sniff?.Format, MeasuredBitrate: sniff?.Bitrate);
        }
        catch (Exception ex)
        {
            return new ProbeOutcome(false, Models.ProbeStatus.Dead, Shorten(ex), null, null, null, url, geoSuspect, Redirects: hops);
        }
    }

    /// <summary>
    /// Splits an Icy-MetaData:1 response body back into clean audio and the first embedded
    /// metadata block: [metaint audio bytes][length byte][length*16 metadata]... repeating.
    /// </summary>
    private static (byte[] Audio, string? Title) IcyDeinterleave(byte[] raw, int metaint)
    {
        using var audio = new MemoryStream(raw.Length);
        string? title = null;
        var pos = 0;
        while (pos < raw.Length)
        {
            var chunk = Math.Min(metaint, raw.Length - pos);
            audio.Write(raw, pos, chunk);
            pos += chunk;
            if (pos >= raw.Length)
            {
                break;
            }

            var metaLen = raw[pos] * 16;
            pos++;
            if (metaLen > 0)
            {
                var available = Math.Min(metaLen, raw.Length - pos);
                title ??= ParseStreamTitle(DecodeMetadata(raw, pos, available));
                pos += available;
            }
        }
        return (audio.ToArray(), title);
    }

    private static string DecodeMetadata(byte[] raw, int offset, int length)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(raw, offset, length);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(raw, offset, length);
        }
    }

    private static string? ParseStreamTitle(string metadata)
    {
        const string marker = "StreamTitle='";
        var start = metadata.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }
        start += marker.Length;
        var end = metadata.IndexOf("';", start, StringComparison.Ordinal);
        if (end < 0)
        {
            end = metadata.LastIndexOf('\'');
        }
        if (end <= start)
        {
            return null;
        }
        var title = metadata[start..end].Trim('\0', ' ');
        return title.Length == 0 ? null : title;
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

    private static async Task<byte[]> ReadStreamSampleAsync(HttpResponseMessage resp, int budget, CancellationToken ct)
    {
        var buffer = new byte[budget];
        var total = 0;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await using var stream = await resp.Content.ReadAsStreamAsync(timeout.Token);
            while (total < budget)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token);
                if (read == 0)
                {
                    break;
                }
                total += read;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Slow stream - a partial sample still sniffs fine.
        }
        return buffer[..total];
    }

    internal static string MimeToFormat(string contentType) => contentType switch
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
