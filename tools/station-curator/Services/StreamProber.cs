// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace OrgZ.StationCurator.Services;

public sealed record ProbeOutcome(
    bool Ok,
    string Status,          // ProbeStatus.* value
    string? Detail,         // icy-name / error text, whatever a human wants to see
    string? Format,         // normalized format measured off the wire
    int? Bitrate,           // icy-br when the server reports it
    string? IcyGenre,
    string? ResolvedUrl,    // post-redirect, post-playlist direct URL
    bool GeoSuspect);

/// <summary>
/// Connects to a stream and reports what is actually there: resolves playlists (.pls/.m3u)
/// and redirects to a direct URL, reads ICY headers for the station's own name/bitrate,
/// verifies HLS playlists parse, and confirms audio bytes flow. Classic SHOUTcast v1 servers
/// answer "ICY 200 OK", which HttpClient rejects as malformed - those fall back to a raw
/// TCP probe that speaks just enough HTTP to read the ICY response itself.
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

    private static readonly HttpClient Http = Web.Create(TimeSpan.FromSeconds(15));

    public static async Task<ProbeOutcome> ProbeAsync(string url, CancellationToken ct, int depth = 0)
    {
        if (depth > 3)
        {
            return new ProbeOutcome(false, Models.ProbeStatus.Dead, "playlist redirect loop", null, null, null, null, false);
        }

        var geoSuspect = IsGeoFencedHost(url);

        HttpResponseMessage resp;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
            resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex) when (LooksLikeIcyResponse(ex))
        {
            return await RawIcyProbeAsync(url, geoSuspect, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            var reason = ct.IsCancellationRequested ? "cancelled" : Shorten(ex);
            return new ProbeOutcome(false, Models.ProbeStatus.Dead, reason, null, null, null, null, geoSuspect);
        }

        using (resp)
        {
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
            geoSuspect |= IsGeoFencedHost(finalUrl);

            var code = (int)resp.StatusCode;
            if (code is 403 or 451)
            {
                return new ProbeOutcome(false, Models.ProbeStatus.Geo, $"http {code} — likely geoblocked", null, null, null, finalUrl, true);
            }
            if (!resp.IsSuccessStatusCode)
            {
                return new ProbeOutcome(false, Models.ProbeStatus.Dead, $"http {code}", null, null, null, finalUrl, geoSuspect);
            }

            var contentType = (resp.Content.Headers.ContentType?.MediaType ?? "").ToLowerInvariant();
            var icyName = Header(resp, "icy-name");
            var icyGenre = Header(resp, "icy-genre");
            int? icyBr = int.TryParse(Header(resp, "icy-br")?.Split(',')[0], out var br) && br > 0 && br < 10000 ? br : null;

            // Playlist container → pull the first entry and probe that instead.
            if (IsPlaylist(contentType, finalUrl))
            {
                var body = await ReadTextAsync(resp, 64 * 1024, ct);
                if (body.Contains("#EXT-X-", StringComparison.Ordinal))
                {
                    return new ProbeOutcome(true, Models.ProbeStatus.Ok, icyName ?? "HLS playlist", "hls", icyBr, icyGenre, finalUrl, geoSuspect);
                }

                var target = FirstPlaylistEntry(body);
                if (target == null)
                {
                    return new ProbeOutcome(false, Models.ProbeStatus.Dead, "playlist with no entries", null, null, null, finalUrl, geoSuspect);
                }
                return await ProbeAsync(target, ct, depth + 1);
            }

            if (contentType is "application/vnd.apple.mpegurl" || finalUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                var body = await ReadTextAsync(resp, 64 * 1024, ct);
                var ok = body.Contains("#EXTM3U", StringComparison.Ordinal);
                return new ProbeOutcome(ok, ok ? Models.ProbeStatus.Ok : Models.ProbeStatus.Dead, icyName ?? (ok ? "HLS playlist" : "not an HLS playlist"), "hls", icyBr, icyGenre, finalUrl, geoSuspect);
            }

            // Direct audio stream: confirm bytes actually flow.
            var format = MimeToFormat(contentType);
            var gotBytes = await ReadSomeBytesAsync(resp, ct);
            if (!gotBytes)
            {
                return new ProbeOutcome(false, Models.ProbeStatus.Dead, "connected but no audio bytes", format, icyBr, icyGenre, finalUrl, geoSuspect);
            }

            var detail = icyName ?? contentType;
            return new ProbeOutcome(true, Models.ProbeStatus.Ok, detail, format, icyBr, icyGenre, finalUrl, geoSuspect);
        }
    }

    /// <summary>Minimal HTTP/1.0-over-TCP probe for servers that answer "ICY 200 OK".</summary>
    private static async Task<ProbeOutcome> RawIcyProbeAsync(string url, bool geoSuspect, CancellationToken ct)
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

            var buffer = new byte[16 * 1024];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token);
                if (read == 0)
                {
                    break;
                }
                total += read;
                if (total > 4096 && Encoding.ASCII.GetString(buffer, 0, Math.Min(total, 4096)).Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    break;
                }
            }

            var head = Encoding.ASCII.GetString(buffer, 0, Math.Min(total, 4096));
            if (!head.StartsWith("ICY 200", StringComparison.OrdinalIgnoreCase) && !head.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase))
            {
                return new ProbeOutcome(false, Models.ProbeStatus.Dead, "unrecognized server response", null, null, null, url, geoSuspect);
            }

            string? icyName = null, icyGenre = null, contentType = null;
            int? icyBr = null;
            foreach (var line in head.Split("\r\n"))
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
                    case "content-type": { contentType = value.ToLowerInvariant(); } break;
                }
            }

            var headerEnd = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var bodyBytes = headerEnd >= 0 ? total - (headerEnd + 4) : 0;
            var ok = bodyBytes > 0;
            return new ProbeOutcome(ok, ok ? Models.ProbeStatus.Ok : Models.ProbeStatus.Dead, icyName ?? "ICY stream", MimeToFormat(contentType ?? ""), icyBr, icyGenre, url, geoSuspect);
        }
        catch (Exception ex)
        {
            return new ProbeOutcome(false, Models.ProbeStatus.Dead, Shorten(ex), null, null, null, url, geoSuspect);
        }
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

    private static async Task<bool> ReadSomeBytesAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await using var stream = await resp.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[8 * 1024];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                {
                    break;
                }
                total += read;
            }
            return total > 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
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
