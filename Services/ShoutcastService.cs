// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace OrgZ.Services;

public static class ShoutcastService
{
    private const string ApiKey = "sh1t7hyn3Kh0jhlV";
    private const string BaseUrl = "http://api.shoutcast.com";
    private const string TuneInBase = "http://yp.shoutcast.com/sbin/tunein-station.m3u";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders =
        {
            { "User-Agent", $"OrgZ/{App.Version}" }
        }
    };

    public static async Task<List<MediaItem>> GetTop500Async()
    {
        var url = $"{BaseUrl}/legacy/Top500?k={ApiKey}";
        var xml = await _http.GetStringAsync(url);
        return ParseStationList(xml);
    }

    public static async Task<List<MediaItem>> SearchStationsAsync(string query, int limit = 100)
    {
        var url = $"{BaseUrl}/legacy/stationsearch?k={ApiKey}&search={Uri.EscapeDataString(query)}&limit={limit}";
        var xml = await _http.GetStringAsync(url);
        return ParseStationList(xml);
    }

    public static async Task<List<MediaItem>> GetByGenreAsync(string genre, int limit = 100)
    {
        var url = $"{BaseUrl}/legacy/genresearch?k={ApiKey}&genre={Uri.EscapeDataString(genre)}&limit={limit}";
        var xml = await _http.GetStringAsync(url);
        return ParseStationList(xml);
    }

    public static async IAsyncEnumerable<List<MediaItem>> GetAllStationsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var genres = await GetGenresAsync();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var genre in genres)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await Task.Delay(300, cancellationToken);

            int offset = 0;
            const int pageSize = 500;

            while (!cancellationToken.IsCancellationRequested)
            {
                List<MediaItem> batch;
                try
                {
                    var url = $"{BaseUrl}/legacy/genresearch?k={ApiKey}&genre={Uri.EscapeDataString(genre)}&limit={offset},{pageSize}";
                    var xml = await _http.GetStringAsync(url, cancellationToken);
                    batch = ParseStationList(xml);
                }
                catch
                {
                    break;
                }

                if (batch.Count == 0)
                {
                    break;
                }

                var deduped = new List<MediaItem>();
                foreach (var station in batch)
                {
                    if (seen.Add(station.Id))
                    {
                        deduped.Add(station);
                    }
                }

                if (deduped.Count > 0)
                {
                    yield return deduped;
                }

                if (batch.Count < pageSize)
                {
                    break;
                }

                offset += pageSize;
                await Task.Delay(300, cancellationToken);
            }
        }
    }

    public static async Task<List<string>> GetGenresAsync()
    {
        var url = $"{BaseUrl}/legacy/genrelist?k={ApiKey}";
        var xml = await _http.GetStringAsync(url);

        var genres = new List<string>();

        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var element in doc.Descendants("genre"))
            {
                var name = element.Attribute("name")?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    genres.Add(name);
                }
            }
        }
        catch
        {
            // Malformed XML
        }

        return genres;
    }

    private static List<MediaItem> ParseStationList(string xml)
    {
        var stations = new List<MediaItem>();

        try
        {
            var doc = XDocument.Parse(xml);

            foreach (var element in doc.Descendants("station"))
            {
                var id = element.Attribute("id")?.Value;
                var name = element.Attribute("name")?.Value;

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var streamUrl = $"{TuneInBase}?id={id}";

                int.TryParse(element.Attribute("br")?.Value, out var bitrate);
                int.TryParse(element.Attribute("lc")?.Value, out var listeners);

                stations.Add(new MediaItem
                {
                    Id = $"sc:{id}",
                    Kind = MediaKind.Radio,
                    Source = "shoutcast",
                    SourceId = id,
                    Title = name,
                    StreamUrl = streamUrl,
                    Tags = element.Attribute("genre")?.Value,
                    FaviconUrl = string.IsNullOrWhiteSpace(element.Attribute("logo")?.Value) ? null : element.Attribute("logo")?.Value,
                    Codec = element.Attribute("mt")?.Value,
                    Bitrate = bitrate > 0 ? bitrate : null,
                    ListenerCount = listeners > 0 ? listeners : null,
                });
            }
        }
        catch
        {
            // Malformed XML
        }

        return stations;
    }
}
