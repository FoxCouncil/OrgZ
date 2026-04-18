// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace OrgZ.Services;

/// <summary>
/// MusicBrainz REST API client for CD metadata lookup.
/// Rate-limited to 1 request/second per MusicBrainz policy.
/// Falls back to TOC-based fuzzy matching on exact DiscID miss.
/// </summary>
public static class MusicBrainzService
{
    private static readonly ILogger _log = Logging.For("MusicBrainz");
    private static readonly HttpClient _http = new();
    private static readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private static DateTime _lastRequest = DateTime.MinValue;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    static MusicBrainzService()
    {
        _http.BaseAddress = new Uri("https://musicbrainz.org/ws/2/");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"OrgZ/{App.Version} (https://github.com/FoxCouncil/OrgZ)");
    }

    /// <summary>
    /// Look up a CD by its MusicBrainz DiscID. Returns null on miss.
    /// </summary>
    public static async Task<DiscLookupResult?> LookupByDiscIdAsync(string discId)
    {
        var json = await RateLimitedGetAsync($"discid/{discId}?inc=recordings+artist-credits&fmt=json");
        if (json == null)
        {
            return null;
        }

        return ParseDiscResponse(json);
    }

    /// <summary>
    /// Fuzzy lookup by TOC string when exact DiscID misses.
    /// Returns candidate releases — caller should present for user confirmation if multiple.
    /// </summary>
    public static async Task<DiscLookupResult?> LookupByTocAsync(string tocString)
    {
        // TOC separators must be literal + in the URL, not decoded as spaces.
        // Uri.EscapeDataString encodes + as %2B which MusicBrainz also accepts.
        var url = $"discid/-?toc={Uri.EscapeDataString(tocString)}&inc=recordings+artist-credits&fmt=json";
        var json = await RateLimitedGetAsync(url);
        if (json == null)
        {
            return null;
        }

        return ParseDiscResponse(json);
    }

    /// <summary>
    /// Fetch front cover art from the Cover Art Archive. Returns image bytes or null.
    /// </summary>
    public static async Task<byte[]?> FetchCoverArtAsync(string releaseMbid)
    {
        try
        {
            await EnforceRateLimit();
            var response = await _http.GetAsync($"https://coverartarchive.org/release/{releaseMbid}/front");
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            return await response.Content.ReadAsByteArrayAsync();
        }
        catch
        {
            return null;
        }
    }

    private static DiscLookupResult? ParseDiscResponse(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Direct disc lookup returns a single release list
            JsonElement releases;
            if (root.TryGetProperty("releases", out releases))
            {
                // Use the first release
                if (releases.GetArrayLength() == 0)
                {
                    return null;
                }

                return ParseRelease(releases[0]);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static DiscLookupResult ParseRelease(JsonElement release)
    {
        var result = new DiscLookupResult
        {
            ReleaseMbid = release.GetProperty("id").GetString() ?? "",
            Title = release.GetProperty("title").GetString() ?? "Unknown Album",
        };

        if (release.TryGetProperty("date", out var date))
        {
            var dateStr = date.GetString() ?? "";
            if (dateStr.Length >= 4 && uint.TryParse(dateStr[..4], out var year))
            {
                result.Year = year;
            }
        }

        // Artist from artist-credit
        if (release.TryGetProperty("artist-credit", out var credits) && credits.GetArrayLength() > 0)
        {
            var parts = new List<string>();
            foreach (var credit in credits.EnumerateArray())
            {
                if (credit.TryGetProperty("name", out var name))
                {
                    parts.Add(name.GetString() ?? "");
                }
                if (credit.TryGetProperty("joinphrase", out var join))
                {
                    parts.Add(join.GetString() ?? "");
                }
            }
            result.Artist = string.Concat(parts).Trim();
        }

        // Tracks from media[0].tracks
        if (release.TryGetProperty("media", out var media) && media.GetArrayLength() > 0)
        {
            var disc = media[0];
            if (disc.TryGetProperty("tracks", out var tracks))
            {
                foreach (var track in tracks.EnumerateArray())
                {
                    var trackInfo = new TrackInfo
                    {
                        Position = track.TryGetProperty("position", out var pos) ? pos.GetInt32() : 0,
                        Title = track.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                    };

                    if (track.TryGetProperty("recording", out var recording))
                    {
                        if (recording.TryGetProperty("artist-credit", out var trackCredits) && trackCredits.GetArrayLength() > 0)
                        {
                            var parts = new List<string>();
                            foreach (var credit in trackCredits.EnumerateArray())
                            {
                                if (credit.TryGetProperty("name", out var name))
                                {
                                    parts.Add(name.GetString() ?? "");
                                }
                                if (credit.TryGetProperty("joinphrase", out var join))
                                {
                                    parts.Add(join.GetString() ?? "");
                                }
                            }
                            trackInfo.Artist = string.Concat(parts).Trim();
                        }
                    }

                    result.Tracks.Add(trackInfo);
                }
            }
        }

        return result;
    }

    private static async Task<string?> RateLimitedGetAsync(string url)
    {
        try
        {
            await EnforceRateLimit();
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var body = await response.Content.ReadAsStringAsync();
            return body;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "MusicBrainz request failed");
            return null;
        }
    }

    private static async Task EnforceRateLimit()
    {
        await _rateLimiter.WaitAsync();
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequest;
            if (elapsed.TotalMilliseconds < 1100)
            {
                await Task.Delay(1100 - (int)elapsed.TotalMilliseconds);
            }
            _lastRequest = DateTime.UtcNow;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }
}

public class DiscLookupResult
{
    public string ReleaseMbid { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Artist { get; set; }
    public uint? Year { get; set; }
    public List<TrackInfo> Tracks { get; set; } = [];
}

public class TrackInfo
{
    public int Position { get; set; }
    public string Title { get; set; } = "";
    public string? Artist { get; set; }
}
