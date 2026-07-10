// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net.Http;
using System.Text.Json;
using OrgZ.StationCurator.Models;

using OrgZ.Services;

namespace OrgZ.StationCurator.Services;

/// <summary>
/// What a DNAS v2 server says about itself at <c>/stats?sid=1&amp;json=1</c> - the directory
/// API carries no images or homepage at all, but the stream server does. Homepage yields a
/// favicon-derived logo; the rest is investigable curation detail.
/// </summary>
public sealed record ShoutcastServerDetails(
    string? Homepage,
    string? Title,
    string? Genre,
    int? CurrentListeners,
    int? PeakListeners,
    int? MaxListeners,
    int? BitrateKbps,
    string? ContentType,
    string? Version,
    TimeSpan? Uptime,
    string? SongTitle)
{
    /// <summary>One investigable line for the source pane.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (CurrentListeners is { } cur)
            {
                parts.Add($"{cur:N0} listening");
            }
            if (PeakListeners is { } peak)
            {
                parts.Add($"peak {peak:N0}");
            }
            if (MaxListeners is { } max)
            {
                parts.Add($"max {max:N0}");
            }
            if (Uptime is { } up)
            {
                parts.Add(up.TotalDays >= 1 ? $"up {(int)up.TotalDays}d{up.Hours}h" : $"up {(int)up.TotalHours}h{up.Minutes:00}m");
            }
            if (BitrateKbps is { } kbps)
            {
                parts.Add($"{kbps}k");
            }
            if (!string.IsNullOrEmpty(ContentType))
            {
                parts.Add(ContentType!);
            }
            if (!string.IsNullOrEmpty(Genre))
            {
                parts.Add(Genre!);
            }
            if (!string.IsNullOrEmpty(Version))
            {
                parts.Add($"DNAS {Version}");
            }
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// SHOUTcast directory via the endpoints the directory.shoutcast.com website itself calls
/// (the official Yellow Pages API needs a dev key). Station rows carry a directory ID, not a
/// stream URL - the tune-in .pls for that ID is fetched and its first File entry becomes the
/// stream URL at import time.
/// </summary>
public sealed class ShoutcastClient
{
    private const string DirectoryBase = "https://directory.shoutcast.com";

    public Task<List<SourceStation>> TopAsync(CancellationToken ct) =>
        PostAsync("/Home/Top", new Dictionary<string, string>(), ct);

    public Task<List<SourceStation>> BrowseGenreAsync(string genre, CancellationToken ct) =>
        PostAsync("/Home/BrowseByGenre", new Dictionary<string, string> { ["genrename"] = genre.Trim() }, ct);

    public Task<List<SourceStation>> SearchAsync(string query, CancellationToken ct) =>
        PostAsync("/Home/Search", new Dictionary<string, string> { ["query"] = query.Trim() }, ct);

    /// <summary>Resolves a directory station ID to a direct stream URL via its tune-in playlist.</summary>
    public static async Task<string?> ResolveStreamUrlAsync(string stationId, CancellationToken ct)
    {
        foreach (var baseUrl in new[] { "https://yp.shoutcast.com", "http://yp.shoutcast.com" })
        {
            try
            {
                var pls = await Web.Http.GetStringAsync($"{baseUrl}/sbin/tunein-station.pls?id={stationId}", ct);
                foreach (var line in pls.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("File", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('='))
                    {
                        var url = trimmed[(trimmed.IndexOf('=') + 1)..].Trim();
                        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        {
                            return url;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Try the next scheme.
            }
        }
        return null;
    }

    /// <summary>Asks the resolved stream server to describe itself (DNAS v2 stats endpoint). Null when the server is v1, dead, or not SHOUTcast at all.</summary>
    public static async Task<ShoutcastServerDetails?> FetchServerDetailsAsync(string streamUrl, CancellationToken ct)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(6));
            var authority = new Uri(streamUrl).GetLeftPart(UriPartial.Authority);
            var json = await Web.Http.GetStringAsync($"{authority}/stats?sid=1&json=1", deadline.Token);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new ShoutcastServerDetails(
                Homepage: Str(root, "serverurl"),
                Title: Str(root, "servertitle"),
                Genre: Str(root, "servergenre"),
                CurrentListeners: Num(root, "currentlisteners"),
                PeakListeners: Num(root, "peaklisteners"),
                MaxListeners: Num(root, "maxlisteners"),
                BitrateKbps: Num(root, "bitrate"),      // DNAS serializes this one as a string
                ContentType: Str(root, "content"),
                Version: Str(root, "version"),
                Uptime: Num(root, "streamuptime") is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                SongTitle: Str(root, "songtitle"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Best decodable logo for a homepage: apple-touch-icon.png when the site has one (real
    /// PNG, typically 180×180 - renders everywhere), else the favicon.ico as a last resort
    /// (Skia can't decode ICO, so it may only ever show as a URL, but it's still the honest
    /// answer for the Logo field).
    /// </summary>
    public static async Task<string?> ResolveLogoUrlAsync(string? homepage, CancellationToken ct)
    {
        if (!Uri.TryCreate(homepage, UriKind.Absolute, out var home) || home.Scheme is not ("http" or "https"))
        {
            return null;
        }
        var authority = home.GetLeftPart(UriPartial.Authority);
        var touchIcon = authority + "/apple-touch-icon.png";
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            using var resp = await Web.Http.GetAsync(touchIcon, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (resp.IsSuccessStatusCode && (resp.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                return touchIcon;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // No touch icon - fall through to the favicon.
        }
        return authority + "/favicon.ico";
    }

    /// <summary>DNAS mixes number and stringified-number fields - accept both.</summary>
    private static int? Num(JsonElement e, string prop) =>
        !e.TryGetProperty(prop, out var v) ? null
        : v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n
        : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s) ? s
        : null;

    private static async Task<List<SourceStation>> PostAsync(string path, Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, DirectoryBase + path) { Content = new FormUrlEncodedContent(form) };
        using var resp = await Web.Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var results = new List<SourceStation>();
        foreach (var s in doc.RootElement.EnumerateArray())
        {
            var id = Int(s, "ID");
            if (id == 0)
            {
                continue;
            }

            results.Add(new SourceStation
            {
                Source = "shoutcast",
                SourceId = id.ToString(),
                Name = (Str(s, "Name") ?? "").Trim(),
                StreamUrl = "",  // resolved from the tune-in .pls at import
                Format = MimeToFormat(Str(s, "Format")),
                Bitrate = Int(s, "Bitrate"),
                Tags = Str(s, "Genre") ?? "",
                Popularity = Int(s, "Listeners"),
            });
        }
        return results;
    }

    private static string MimeToFormat(string? mime) => (mime ?? "").ToLowerInvariant() switch
    {
        "audio/mpeg" => "mp3",
        "audio/aacp" or "audio/aac" => "aac",
        "audio/ogg" or "application/ogg" => "ogg",
        _ => "",
    };

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}
