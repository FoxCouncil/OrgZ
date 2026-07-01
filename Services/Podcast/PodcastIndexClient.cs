// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net.Http;
using System.Text.Json;
using OrgZ.Models;
using Serilog;

namespace OrgZ.Services.Podcast;

/// <summary>
/// Thin HttpClient wrapper for the podcasts.foxcouncil.com proxy in front of
/// PodcastIndex. All requests carry the X-Client-Token header. The proxy
/// preserves PodcastIndex's response shape, so DTOs match the upstream docs
/// at podcastindex-org/docs-api.
/// </summary>
public static class PodcastIndexClient
{
    private const string BaseUrl = "https://podcasts.foxcouncil.com/api";
    private const string ClientToken = "89a42bd9-d615-49d8-938d-31f15d0efc7d";

    private static readonly ILogger _log = Logging.For("PodcastIndex");

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders =
        {
            { "X-Client-Token", ClientToken },
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36" },
        },
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        // PodcastIndex occasionally serializes integer fields ("episode",
        // "season", "explicit") as quoted strings -- "E1", "0" -- depending on
        // how the upstream feed was authored. Without this, the entire feed
        // response fails to parse and the episode list shows empty.
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    // Disk-backed response cache. Categories rarely change, trending changes
    // daily-ish; 12 hours is a sweet spot that keeps the store warm across
    // multiple launches per day without serving stale results.
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrgZ", "podcastcache");
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    // Bumping this invalidates every disk-cached response, useful after a DTO
    // shape change makes prior caches incompatible. Re-fetches replace them.
    private const int CacheVersion = 2;

    public static async Task<List<PodcastFeed>> SearchByTermAsync(string query, int max = 25, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var url = $"{BaseUrl}/search/byterm?q={Uri.EscapeDataString(query)}&max={max}";
        var resp = await GetAsync<PodcastFeedsResponse>(url, ct);
        return resp?.Feeds ?? [];
    }

    public static async Task<List<PodcastFeed>> GetTrendingAsync(int max = 25, int? categoryId = null, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/podcasts/trending?max={max}&lang=en";
        if (categoryId.HasValue)
        {
            url += $"&cat={categoryId.Value}";
        }
        var resp = await GetAsync<PodcastFeedsResponse>(url, ct);
        return resp?.Feeds ?? [];
    }

    public static async Task<List<PodcastFeed>> GetRecentFeedsAsync(int max = 25, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/recent/feeds?max={max}&lang=en";
        var resp = await GetAsync<PodcastFeedsResponse>(url, ct);
        return resp?.Feeds ?? [];
    }

    public static async Task<List<PodcastCategory>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/categories/list";
        var resp = await GetAsync<PodcastCategoriesResponse>(url, ct);
        return resp?.Feeds ?? [];
    }

    public static async Task<List<PodcastEpisode>> GetEpisodesByFeedIdAsync(long feedId, int max = 50, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/episodes/byfeedid?id={feedId}&max={max}";
        var resp = await GetAsync<PodcastEpisodesResponse>(url, ct);
        return resp?.Items ?? [];
    }

    /// <summary>Reads a feed's episodes from the on-disk cache ONLY - never the network - so the device
    /// sync can resolve episode publish dates while staying offline. Returns null when nothing's cached.</summary>
    public static List<PodcastEpisode>? GetCachedEpisodesByFeedId(long feedId)
    {
        foreach (var max in new[] { 200, 50 })
        {
            var cachePath = Path.Combine(CacheDir, $"{UrlHash($"{BaseUrl}/episodes/byfeedid?id={feedId}&max={max}")}.v{CacheVersion}.json");
            if (!File.Exists(cachePath)) { continue; }
            try
            {
                var items = JsonSerializer.Deserialize<PodcastEpisodesResponse>(File.ReadAllText(cachePath), JsonOpts)?.Items;
                if (items is { Count: > 0 }) { return items; }
            }
            catch { /* corrupt cache - ignore */ }
        }
        return null;
    }

    public static async Task<PodcastFeed?> GetPodcastByFeedIdAsync(long feedId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/podcasts/byfeedid?id={feedId}";
        var resp = await GetAsync<PodcastFeedsResponse>(url, ct);
        // Note: byfeedid returns "feed" (singular) upstream; the proxy may wrap as feeds[0].
        return resp?.Feeds?.FirstOrDefault();
    }

    private static async Task<T?> GetAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            // Disk cache check: serve a JSON file when it's still fresh.
            Directory.CreateDirectory(CacheDir);
            var cachePath = Path.Combine(CacheDir, $"{UrlHash(url)}.v{CacheVersion}.json");
            if (File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < CacheTtl)
            {
                try
                {
                    var cached = await File.ReadAllTextAsync(cachePath, ct);
                    var hit = JsonSerializer.Deserialize<T>(cached, JsonOpts);
                    if (hit != null) return hit;
                }
                catch
                {
                    // Corrupt cache entry -- drop and refetch.
                    try { File.Delete(cachePath); } catch { }
                }
            }

            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<T>(json, JsonOpts);
            // Only persist payloads that actually carry data, so a failed
            // upstream response doesn't poison the cache for the next 12h.
            if (parsed is not null && HasContent(parsed))
            {
                try { await File.WriteAllTextAsync(cachePath, json, ct); } catch { }
            }
            return parsed;
        }
        catch (Exception ex)
        {
            _log.Warning("PodcastIndex {Url} failed: {Message}", url, ex.Message);
            return null;
        }
    }

    private static string UrlHash(string url)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool HasContent<T>(T value) => value switch
    {
        PodcastFeedsResponse f      => f.Feeds is { Count: > 0 },
        PodcastEpisodesResponse e   => e.Items is { Count: > 0 },
        PodcastCategoriesResponse c => c.Feeds is { Count: > 0 },
        _                            => true,
    };
}
