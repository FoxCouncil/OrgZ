// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using OrgZ.StationCurator.Models;

namespace OrgZ.StationCurator.Services;

/// <summary>
/// Point it at any Icecast server and it figures the mounts out from /status-json.xsl.
/// Handles the Icecast JSON quirks: "source" is an object for one mount and an array for
/// several, and listenurl frequently advertises the server's internal hostname - the
/// authority the user actually typed replaces it.
/// </summary>
public static class IcecastClient
{
    public static async Task<List<SourceStation>> FetchMountsAsync(string serverUrl, CancellationToken ct)
    {
        var input = serverUrl.Trim();
        if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            input = "http://" + input;
        }

        var baseUri = new Uri(input, UriKind.Absolute);
        var statusUri = new Uri(baseUri, "/status-json.xsl");
        var json = await Web.Http.GetStringAsync(statusUri, ct);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("icestats", out var stats))
        {
            return [];
        }

        var results = new List<SourceStation>();
        if (stats.TryGetProperty("source", out var source))
        {
            if (source.ValueKind == JsonValueKind.Array)
            {
                foreach (var mount in source.EnumerateArray())
                {
                    Add(results, mount, baseUri);
                }
            }
            else if (source.ValueKind == JsonValueKind.Object)
            {
                Add(results, source, baseUri);
            }
        }
        return results;
    }

    private static void Add(List<SourceStation> results, JsonElement mount, Uri baseUri)
    {
        var listenUrl = Str(mount, "listenurl");
        if (string.IsNullOrWhiteSpace(listenUrl))
        {
            return;
        }

        // Rewrite the advertised authority to the one the user reached the server by.
        string streamUrl;
        if (Uri.TryCreate(listenUrl, UriKind.Absolute, out var advertised))
        {
            streamUrl = new UriBuilder(advertised) { Scheme = baseUri.Scheme, Host = baseUri.Host, Port = baseUri.Port }.Uri.ToString();
        }
        else
        {
            streamUrl = new Uri(baseUri, listenUrl).ToString();
        }

        var bitrate = Int(mount, "bitrate");
        if (bitrate == 0 && Str(mount, "audio_info") is { } audioInfo)
        {
            // audio_info reads "channels=2;samplerate=44100;bitrate=128"
            foreach (var part in audioInfo.Split(';'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0].Trim().Equals("bitrate", StringComparison.OrdinalIgnoreCase) && int.TryParse(kv[1].Trim(), out var br))
                {
                    bitrate = br;
                }
            }
        }

        var name = Str(mount, "server_name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"{baseUri.Host}{new Uri(streamUrl).AbsolutePath}";
        }

        results.Add(new SourceStation
        {
            Source = $"icecast:{baseUri.Host}",
            SourceId = listenUrl,
            Name = name.Trim(),
            StreamUrl = streamUrl,
            Format = MimeToFormat(Str(mount, "server_type")),
            Bitrate = bitrate,
            Homepage = Str(mount, "server_url"),
            Tags = Str(mount, "genre") ?? "",
            Popularity = Int(mount, "listeners"),
        });
    }

    private static string MimeToFormat(string? mime) => (mime ?? "").ToLowerInvariant() switch
    {
        "audio/mpeg" => "mp3",
        "audio/aacp" or "audio/aac" => "aac",
        "audio/ogg" or "application/ogg" or "audio/opus" => "ogg",
        "audio/flac" or "audio/x-flac" => "flac",
        _ => "",
    };

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static int Int(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}
