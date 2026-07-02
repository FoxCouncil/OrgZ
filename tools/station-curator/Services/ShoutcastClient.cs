// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net.Http;
using System.Text.Json;
using OrgZ.StationCurator.Models;

namespace OrgZ.StationCurator.Services;

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
