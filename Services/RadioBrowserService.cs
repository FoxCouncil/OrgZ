// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrgZ.Services;

public static class RadioBrowserService
{
    private static readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders =
        {
            { "User-Agent", $"OrgZ/{App.Version}" }
        }
    };

    private static string? _serverBase;

    public static string LastServerUsed => _serverBase ?? "(none)";

    private static async Task<string> GetServerBaseAsync()
    {
        if (_serverBase != null)
        {
            return _serverBase;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync("all.api.radio-browser.info");

            // Shuffle to respect round-robin load distribution
            var shuffled = addresses.OrderBy(_ => Random.Shared.Next()).ToArray();

            foreach (var address in shuffled)
            {
                try
                {
                    var hostEntry = await Dns.GetHostEntryAsync(address);
                    if (!string.IsNullOrEmpty(hostEntry.HostName))
                    {
                        _serverBase = $"https://{hostEntry.HostName}";
                        return _serverBase;
                    }
                }
                catch
                {
                    // Try next address
                }
            }
        }
        catch
        {
            // Fallback
        }

        _serverBase = "https://de1.api.radio-browser.info";
        return _serverBase;
    }

    public static async Task<List<MediaItem>> GetTopStationsAsync(int limit = 100)
    {
        var baseUrl = await GetServerBaseAsync();
        var response = await _http.GetAsync($"{baseUrl}/json/stations/topclick/{limit}");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return ParseStations(json);
    }

    public static async Task<List<MediaItem>> SearchStationsAsync(string? name = null, string? countryCode = null, string? tag = null, int limit = 100)
    {
        var baseUrl = await GetServerBaseAsync();

        var parameters = new List<string> { $"limit={limit}", "hidebroken=true" };

        if (!string.IsNullOrWhiteSpace(name))
        {
            parameters.Add($"name={Uri.EscapeDataString(name)}");
        }

        if (!string.IsNullOrWhiteSpace(countryCode))
        {
            parameters.Add($"countrycode={Uri.EscapeDataString(countryCode)}");
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            parameters.Add($"tag={Uri.EscapeDataString(tag)}");
        }

        var url = $"{baseUrl}/json/stations/search?{string.Join("&", parameters)}";
        var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return ParseStations(json);
    }

    public static async IAsyncEnumerable<List<MediaItem>> GetAllStationsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var baseUrl = await GetServerBaseAsync();
        const int pageSize = 1000;
        int offset = 0;
        bool first = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!first)
            {
                await Task.Delay(500, cancellationToken);
            }
            first = false;

            var url = $"{baseUrl}/json/stations?offset={offset}&limit={pageSize}&hidebroken=true";
            var response = await _http.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            var batch = ParseStations(json);
            if (batch.Count == 0)
            {
                break;
            }

            yield return batch;
            offset += pageSize;
        }
    }

    public static async Task<List<CountryInfo>> GetCountriesAsync()
    {
        var baseUrl = await GetServerBaseAsync();
        var json = await _http.GetStringAsync($"{baseUrl}/json/countries");
        return JsonSerializer.Deserialize<List<CountryInfo>>(json, JsonOptions) ?? [];
    }

    public static async Task<List<TagInfo>> GetTagsAsync(int limit = 100)
    {
        var baseUrl = await GetServerBaseAsync();
        var json = await _http.GetStringAsync($"{baseUrl}/json/tags?order=stationcount&reverse=true&limit={limit}");
        return JsonSerializer.Deserialize<List<TagInfo>>(json, JsonOptions) ?? [];
    }

    private static List<MediaItem> ParseStations(string json)
    {
        var raw = JsonSerializer.Deserialize<List<RbStation>>(json, JsonOptions);
        if (raw == null)
        {
            return [];
        }

        var stations = new List<MediaItem>();

        foreach (var r in raw)
        {
            var streamUrl = r.UrlResolved ?? r.Url;
            if (string.IsNullOrWhiteSpace(streamUrl))
            {
                continue;
            }

            var uuid = r.StationUuid != Guid.Empty ? r.StationUuid.ToString() : Guid.NewGuid().ToString();

            stations.Add(new MediaItem
            {
                Id = $"rb:{uuid}",
                Kind = MediaKind.Radio,
                Source = "radiobrowser",
                SourceId = uuid,
                Title = r.Name ?? "Unknown",
                StreamUrl = streamUrl,
                HomepageUrl = string.IsNullOrWhiteSpace(r.Homepage) ? null : r.Homepage,
                FaviconUrl = string.IsNullOrWhiteSpace(r.Favicon) ? null : r.Favicon,
                Country = r.Country,
                CountryCode = r.CountryCode,
                Tags = r.Tags,
                Codec = r.Codec,
                Bitrate = r.Bitrate > 0 ? r.Bitrate : null,
                Votes = r.Votes > 0 ? r.Votes : null,
                ClickCount = r.ClickCount > 0 ? r.ClickCount : null,
                IsHls = r.Hls == 1,
            });
        }

        return stations;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private class RbStation
    {
        [JsonPropertyName("stationuuid")]
        public Guid StationUuid { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("url_resolved")]
        public string? UrlResolved { get; set; }

        [JsonPropertyName("homepage")]
        public string? Homepage { get; set; }

        [JsonPropertyName("favicon")]
        public string? Favicon { get; set; }

        [JsonPropertyName("country")]
        public string? Country { get; set; }

        [JsonPropertyName("countrycode")]
        public string? CountryCode { get; set; }

        [JsonPropertyName("tags")]
        public string? Tags { get; set; }

        [JsonPropertyName("codec")]
        public string? Codec { get; set; }

        [JsonPropertyName("bitrate")]
        public int Bitrate { get; set; }

        [JsonPropertyName("votes")]
        public int Votes { get; set; }

        [JsonPropertyName("clickcount")]
        public int ClickCount { get; set; }

        [JsonPropertyName("hls")]
        public int Hls { get; set; }
    }

    public class CountryInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("iso_3166_1")]
        public string Code { get; set; } = string.Empty;

        [JsonPropertyName("stationcount")]
        public int StationCount { get; set; }
    }

    public class TagInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("stationcount")]
        public int StationCount { get; set; }
    }
}
