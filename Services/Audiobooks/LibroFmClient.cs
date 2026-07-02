// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using OrgZ.Models;
using Serilog;

namespace OrgZ.Services.Audiobooks;

/// <summary>
/// The (unofficial) Libro.fm app API - login, the user's purchased library, and download metadata
/// for their own DRM-free files. Endpoints and shapes match what the community downloaders use
/// against the same backend the official apps talk to; requests carry the standard browser UA.
/// Purchasing has no API - checkout stays on libro.fm.
/// </summary>
public static class LibroFmClient
{
    private const string BaseUrl = "https://libro.fm";

    private static readonly ILogger _log = Logging.For("LibroFm");

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36" },
        },
    };

    /// <summary>Password-grant login; returns the bearer token, or null when the credentials are refused.</summary>
    public static async Task<string?> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync($"{BaseUrl}/oauth/token",
                new LibroLoginRequest { Username = username, Password = password }, JsonOpts, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.Warning("Libro.fm login refused: {Status}", (int)resp.StatusCode);
                return null;
            }
            var token = (await resp.Content.ReadFromJsonAsync<LibroTokenResponse>(JsonOpts, ct))?.AccessToken;
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Libro.fm login failed");
            return null;
        }
    }

    /// <summary>The user's whole purchased library, following total_pages.</summary>
    public static async Task<List<LibroBook>?> GetLibraryAsync(string token, CancellationToken ct = default)
    {
        var first = await GetAsync<LibroLibraryPage>($"{BaseUrl}/api/v10/library?page=1", token, ct);
        if (first is null)
        {
            return null;   // auth expired / network - callers distinguish null (error) from empty
        }

        var books = new List<LibroBook>(first.Audiobooks);
        for (int page = 2; page <= first.TotalPages; page++)
        {
            var next = await GetAsync<LibroLibraryPage>($"{BaseUrl}/api/v10/library?page={page}", token, ct);
            if (next is null)
            {
                break;   // partial library beats none
            }
            books.AddRange(next.Audiobooks);
        }
        return books;
    }

    /// <summary>The packaged single-file m4b URL, or null when the title has none (fall back to MP3 parts).</summary>
    public static async Task<string?> GetM4bUrlAsync(string token, string isbn, CancellationToken ct = default)
        => (await GetAsync<LibroM4bMetadata>($"{BaseUrl}/api/v10/audiobooks/{Uri.EscapeDataString(isbn)}/packaged_m4b", token, ct))?.M4bUrl;

    /// <summary>The MP3 zip-part manifest for a purchase.</summary>
    public static Task<LibroMp3Manifest?> GetMp3ManifestAsync(string token, string isbn, CancellationToken ct = default)
        => GetAsync<LibroMp3Manifest>($"{BaseUrl}/api/v10/download-manifest?isbn={Uri.EscapeDataString(isbn)}", token, ct);

    /// <summary>
    /// Libro.fm's pre-signed URLs carry the intended filename in a response-content-disposition
    /// query parameter - pull it out (null when the URL doesn't carry one).
    /// </summary>
    internal static string? FileNameFromPresignedUrl(string url)
    {
        try
        {
            var query = new Uri(url).Query;
            foreach (var pair in query.TrimStart('?').Split('&'))
            {
                var idx = pair.IndexOf('=');
                if (idx <= 0 || Uri.UnescapeDataString(pair[..idx]) != "response-content-disposition")
                {
                    continue;
                }
                var disposition = Uri.UnescapeDataString(pair[(idx + 1)..]);
                var match = System.Text.RegularExpressions.Regex.Match(disposition, "filename=\"?([^\";]+)\"?");
                return match.Success ? match.Groups[1].Value.Replace('+', ' ') : null;
            }
        }
        catch
        {
            // Malformed URL - the caller falls back to a constructed name.
        }
        return null;
    }

    private static async Task<T?> GetAsync<T>(string url, string token, CancellationToken ct) where T : class
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.Debug("Libro.fm {Url}: {Status}", url, (int)resp.StatusCode);
                return null;
            }
            return JsonSerializer.Deserialize<T>(await resp.Content.ReadAsStringAsync(ct), JsonOpts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Libro.fm request failed: {Url}", url);
            return null;
        }
    }
}
