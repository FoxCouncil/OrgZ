// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net.Http;
using System.Text.Json;
using OrgZ.StationCurator.Models;

using OrgZ.Services;

namespace OrgZ.StationCurator.Services;

/// <summary>
/// radio-browser.info community directory. Mirrors are tried in order; the first one that
/// answers wins for the rest of the session.
/// </summary>
public sealed class RadioBrowserClient
{
    private static readonly string[] Mirrors =
    [
        "https://de1.api.radio-browser.info",
        "https://fi1.api.radio-browser.info",
        "https://nl1.api.radio-browser.info",
    ];

    private string? _mirror;

    public async Task<List<SourceStation>> SearchAsync(string? name, string? tag, string? country, string order, int limit, CancellationToken ct)
    {
        var query = new List<string>
        {
            $"order={Uri.EscapeDataString(order)}",
            "reverse=true",
            "hidebroken=true",
            $"limit={limit}",
        };
        if (!string.IsNullOrWhiteSpace(name)) { query.Add($"name={Uri.EscapeDataString(name.Trim())}"); }
        if (!string.IsNullOrWhiteSpace(tag)) { query.Add($"tag={Uri.EscapeDataString(tag.Trim())}"); }
        if (!string.IsNullOrWhiteSpace(country)) { query.Add($"country={Uri.EscapeDataString(country.Trim())}"); }

        var json = await GetAsync($"/json/stations/search?{string.Join('&', query)}", ct);
        using var doc = JsonDocument.Parse(json);

        var results = new List<SourceStation>();
        foreach (var s in doc.RootElement.EnumerateArray())
        {
            var url = (Str(s, "url_resolved") is { Length: > 0 } resolved ? resolved : Str(s, "url"))?.Trim();
            if (string.IsNullOrEmpty(url))
            {
                continue;
            }

            results.Add(new SourceStation
            {
                Source = "radio-browser",
                SourceId = Str(s, "stationuuid"),
                Name = (Str(s, "name") ?? "").Trim(),
                StreamUrl = url,
                Format = NormalizeCodec(Str(s, "codec")),
                Bitrate = Int(s, "bitrate"),
                Country = Str(s, "country"),
                CountryCode = Str(s, "countrycode"),
                Homepage = Str(s, "homepage"),
                LogoUrl = Str(s, "favicon"),
                Tags = Str(s, "tags") ?? "",
                Popularity = Int(s, "clickcount"),
            });
        }
        return results;
    }

    private async Task<string> GetAsync(string path, CancellationToken ct)
    {
        if (_mirror != null)
        {
            return await Web.Http.GetStringAsync(_mirror + path, ct);
        }

        Exception? last = null;
        foreach (var mirror in Mirrors)
        {
            try
            {
                var body = await Web.Http.GetStringAsync(mirror + path, ct);
                _mirror = mirror;
                return body;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                last = ex;
            }
        }
        throw last ?? new HttpRequestException("No radio-browser mirror answered");
    }

    internal static string NormalizeCodec(string? codec)
    {
        var c = (codec ?? "").ToUpperInvariant();
        if (c.Contains("MP3")) { return "mp3"; }
        if (c.Contains("AAC")) { return "aac"; }
        if (c.Contains("OGG") || c.Contains("OPUS") || c.Contains("VORBIS")) { return "ogg"; }
        if (c.Contains("FLAC")) { return "flac"; }
        if (c.Contains("HLS")) { return "hls"; }
        return c.ToLowerInvariant();
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}
