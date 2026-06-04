// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Platform;
using OrgZ.Models;

namespace OrgZ.Services;

/// <summary>
/// Loads the curated <c>Assets/stations.json</c> shipped with the app and maps
/// each entry to a <see cref="MediaItem"/>. The bundled list lives in memory only;
/// SQLite is reserved for user-added personal streams.
/// </summary>
public static class BundledStationsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static List<MediaItem> LoadAll()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Orgz/Assets/stations.json"));
        var bundle = JsonSerializer.Deserialize<Bundle>(stream, JsonOptions);
        if (bundle?.Stations == null)
        {
            return [];
        }

        var items = new List<MediaItem>(bundle.Stations.Count);
        foreach (var s in bundle.Stations)
        {
            if (string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.StreamUrl))
            {
                continue;
            }

            var genre = (RadioGenre)s.GenreId;

            items.Add(new MediaItem
            {
                // Use the JSON id verbatim. Radio-browser-sourced stations
                // ("rb:UUID") collide intentionally with any legacy rows from
                // the old runtime sync - the upsert merges them, refreshing
                // metadata while keeping favorite / last-played / play-count.
                Id            = s.Id,
                Kind          = MediaKind.Radio,
                Title         = s.Name,
                StreamUrl     = s.StreamUrl,
                HomepageUrl   = string.IsNullOrWhiteSpace(s.Homepage) ? null : s.Homepage,
                FaviconUrl    = string.IsNullOrWhiteSpace(s.LogoUrl)  ? null : s.LogoUrl,
                Country       = s.Country,
                CountryCode   = s.CountryCode,
                Tags          = genre.DisplayName(),
                Codec         = FormatToMime(s.StreamFormat),
                Bitrate       = s.Bitrate > 0 ? s.Bitrate : null,
                Source        = "bundled",
                SourceId      = s.Id,
                IsHls         = string.Equals(s.StreamFormat, "hls", StringComparison.OrdinalIgnoreCase),
            });
        }

        return items;
    }

    private static string FormatToMime(string streamFormat) => streamFormat?.ToLowerInvariant() switch
    {
        "mp3"  => "audio/mpeg",
        "aac"  => "audio/aacp",
        "ogg"  => "audio/ogg",
        "flac" => "audio/flac",
        "hls"  => "application/vnd.apple.mpegurl",
        _      => streamFormat ?? "",
    };

    private sealed record Bundle(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("taxonomy")]      Taxonomy? Taxonomy,
        [property: JsonPropertyName("stations")]      List<Station>? Stations);

    private sealed record Taxonomy(
        [property: JsonPropertyName("genres")] List<GenreEntry>? Genres);

    private sealed record GenreEntry(
        [property: JsonPropertyName("id")]   int Id,
        [property: JsonPropertyName("name")] string Name);

    private sealed record Station(
        [property: JsonPropertyName("id")]            string Id,
        [property: JsonPropertyName("name")]          string Name,
        [property: JsonPropertyName("streamUrl")]     string StreamUrl,
        [property: JsonPropertyName("streamFormat")]  string StreamFormat,
        [property: JsonPropertyName("bitrate")]       int Bitrate,
        [property: JsonPropertyName("genreId")]       int GenreId,
        [property: JsonPropertyName("country")]       string Country,
        [property: JsonPropertyName("countryCode")]   string CountryCode,
        [property: JsonPropertyName("homepage")]      string? Homepage,
        [property: JsonPropertyName("logoUrl")]       string? LogoUrl,
        [property: JsonPropertyName("description")]   string? Description);
}
