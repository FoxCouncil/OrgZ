// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net.Http;
using System.Text.Json;
using OrgZ.Models;
using Serilog;

namespace OrgZ.Services.Audiobooks;

/// <summary>
/// The audiobook store's whole backend: archive.org's advancedsearch (scoped to the LibriVox
/// collection - public domain, DRM-free, human-read) for search/popular/new lists, and its
/// metadata API for an item's file list. One backend on purpose: the LibriVox catalog API can't
/// do partial-title search (exact match or 404) and carries nothing the archive.org pair doesn't,
/// since LibriVox hosts every file on archive.org anyway. Response shapes verified live -
/// the test fixtures ARE captured responses.
/// </summary>
public static class ArchiveOrgClient
{
    private const string Collection = "librivoxaudio";

    private static readonly ILogger _log = Logging.For("ArchiveOrg");

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36" },
        },
    };

    // advancedsearch serializes numeric fields as bare numbers, but item metadata stringifies
    // nearly everything ("size":"26502144") - one options object that tolerates both.
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    // Disk-backed response cache, same regime as the podcast store: popular/new lists move
    // daily-ish, so 12 hours keeps the store warm across launches without serving stale rows.
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrgZ", "audiobookcache");
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);
    private const int CacheVersion = 2;   // v2: queries gained the mediatype:audio clause

    public static Task<List<AudiobookListing>> SearchAsync(string query, int rows = 25, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(new List<AudiobookListing>());
        }
        return RunSearchAsync(BuildSearchUrl(query, rows), ct);
    }

    public static Task<List<AudiobookListing>> GetPopularAsync(int rows = 25, CancellationToken ct = default)
        => RunSearchAsync(BuildListUrl("downloads desc", rows), ct);

    public static Task<List<AudiobookListing>> GetRecentAsync(int rows = 25, CancellationToken ct = default)
        => RunSearchAsync(BuildListUrl("publicdate desc", rows), ct);

    public static async Task<ArchiveItemResponse?> GetItemAsync(string identifier, CancellationToken ct = default)
        => await GetAsync<ArchiveItemResponse>($"https://archive.org/metadata/{Uri.EscapeDataString(identifier)}", ct);

    /// <summary>The canonical download redirector - archive.org routes it to the right datanode.</summary>
    public static string DownloadUrlFor(string identifier, string fileName)
        => $"https://archive.org/download/{Uri.EscapeDataString(identifier)}/{Uri.EscapeDataString(fileName)}";

    // ── pure pieces (unit-tested) ──────────────────────────────────────────────

    /// <summary>Search matches title OR author within the LibriVox collection.</summary>
    internal static string BuildSearchUrl(string query, int rows)
    {
        var q = Uri.EscapeDataString($"collection:{Collection} AND mediatype:(audio) AND (title:({query}) OR creator:({query}))");
        return $"https://archive.org/advancedsearch.php?q={q}{FieldList}&rows={rows}&output=json";
    }

    internal static string BuildListUrl(string sort, int rows)
    {
        // mediatype:audio keeps the collection's housekeeping items (cover-art packs and the
        // like) out of the store - sorting by downloads surfaced them right in "Popular".
        var q = Uri.EscapeDataString($"collection:{Collection} AND mediatype:(audio)");
        return $"https://archive.org/advancedsearch.php?q={q}{FieldList}&sort%5B%5D={Uri.EscapeDataString(sort)}&rows={rows}&output=json";
    }

    private const string FieldList = "&fl%5B%5D=identifier&fl%5B%5D=title&fl%5B%5D=creator&fl%5B%5D=runtime&fl%5B%5D=downloads&fl%5B%5D=publicdate";

    /// <summary>
    /// Picks the files worth downloading from an item: a chaptered .m4b when the item carries one
    /// (single file, iPod-native, bookmarkable), otherwise the 64Kbps MP3 chapter set in
    /// section order. Empty when the item has neither.
    /// </summary>
    internal static List<ArchiveItemFile> PickDownloadFiles(IReadOnlyList<ArchiveItemFile> files)
    {
        var m4bs = files
            .Where(f => f.Name?.EndsWith(".m4b", StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (m4bs.Count > 0)
        {
            return m4bs;
        }

        return files
            .Where(f => string.Equals(f.Format, "64Kbps MP3", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// File durations come in two shapes: clock strings ("55:12", "1:02:03") on originals and
    /// decimal seconds ("3068.71") on derivatives. Null on anything else.
    /// </summary>
    internal static TimeSpan? ParseFileLength(string? length)
    {
        if (string.IsNullOrWhiteSpace(length))
        {
            return null;
        }

        if (length.Contains(':'))
        {
            var parts = length.Split(':');
            return parts.Length switch
            {
                2 when int.TryParse(parts[0], out var m) && int.TryParse(parts[1], out var s) => new TimeSpan(0, m, s),
                3 when int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m) && int.TryParse(parts[2], out var s) => new TimeSpan(h, m, s),
                _ => null,
            };
        }

        return double.TryParse(length, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var secs)
            ? TimeSpan.FromSeconds(secs)
            : null;
    }

    /// <summary>
    /// The item's best cover image file: the largest non-thumbnail JPEG/PNG the item carries -
    /// the real cover LibriVox uploads, not the ~180px image-service thumb. Null when the item
    /// has no usable image (callers can fall back to <see cref="AudiobookListing.CoverUrl"/>).
    /// </summary>
    internal static ArchiveItemFile? PickCoverFile(IReadOnlyList<ArchiveItemFile> files)
        => files
            .Where(f => f.Name is not null
                        && (f.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                            || f.Name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                            || f.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        && f.Format?.Contains("thumb", StringComparison.OrdinalIgnoreCase) != true)
            .OrderByDescending(f => long.TryParse(f.Size, out var s) ? s : 0)
            .FirstOrDefault();

    /// <summary>
    /// archive.org descriptions are HTML fragments. Renders them as plain text: br/p become
    /// line breaks, tags drop, entities decode, whitespace collapses.
    /// </summary>
    internal static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = System.Text.RegularExpressions.Regex.Replace(html, @"<\s*(br|/p)\s*/?\s*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]+", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    /// <summary>
    /// The narrator, pulled from a LibriVox description's "Read [in {language}] by {Name}" line -
    /// archive.org exposes no structured narrator field (creator is the AUTHOR), so the description
    /// is the only source. Returns null when the pattern isn't present (multi-reader collaboratives
    /// often say "Read by various readers", which we treat as unknown). Runs on the RAW HTML - the
    /// &lt;br/&gt; that ends the line is the terminator.
    /// </summary>
    internal static string? ExtractNarrator(string? descriptionHtml)
    {
        if (string.IsNullOrWhiteSpace(descriptionHtml))
        {
            return null;
        }
        var match = System.Text.RegularExpressions.Regex.Match(
            descriptionHtml, @"Read\s+(?:in\s+\w+\s+)?by\s+([^<.\r\n]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }
        var name = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim().TrimEnd(',', ';');
        if (name.Length == 0 || name.Contains("various", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return name;
    }

    /// <summary>Test seam: the exact deserialization the live path uses, runnable on fixtures.</summary>
    internal static T? ParseJson<T>(string json) where T : class
        => JsonSerializer.Deserialize<T>(json, JsonOpts);

    // ── plumbing (mirrors PodcastIndexClient) ─────────────────────────────────

    private static async Task<List<AudiobookListing>> RunSearchAsync(string url, CancellationToken ct)
    {
        var resp = await GetAsync<ArchiveSearchResponse>(url, ct);
        return resp?.Response?.Docs ?? [];
    }

    private static async Task<T?> GetAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var cachePath = Path.Combine(CacheDir, $"{UrlHash(url)}.v{CacheVersion}.json");
            if (File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < CacheTtl)
            {
                try
                {
                    var cached = await File.ReadAllTextAsync(cachePath, ct);
                    var hit = ParseJson<T>(cached);
                    if (hit != null) return hit;
                }
                catch
                {
                    // Corrupt cache entry - drop and refetch.
                    try { File.Delete(cachePath); } catch { }
                }
            }

            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            var parsed = ParseJson<T>(json);
            // Only persist payloads that actually carry data, so a failed upstream response
            // doesn't poison the cache for the next 12h.
            if (parsed is not null && HasContent(parsed))
            {
                try { await File.WriteAllTextAsync(cachePath, json, ct); } catch { }
            }
            return parsed;
        }
        catch (Exception ex)
        {
            _log.Warning("archive.org {Url} failed: {Message}", url, ex.Message);
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
        ArchiveSearchResponse s => s.Response?.Docs is { Count: > 0 },
        ArchiveItemResponse i   => i.Files is { Count: > 0 },
        _                       => true,
    };
}
