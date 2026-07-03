// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.ComponentModel;
using System.Text.Json.Serialization;
using OrgZ.Models;
using OrgZ.Services;

namespace OrgZ.StationCurator.Models;

/// <summary>Root of tools/station-curator/curated.json - the source of truth the tool maintains.</summary>
public sealed class CuratedDb
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("stations")] public List<CuratedStation> Stations { get; set; } = [];
}

/// <summary>
/// One logical station. The same broadcast frequently appears in directories several times
/// (per bitrate, per codec, per mirror) - those all merge into <see cref="Streams"/> variants
/// under a single station, and export picks one.
/// </summary>
public sealed class CuratedStation : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The grids bind computed getters (ProbeSummary, BestBitrate, ...) on plain POCOs; after a
    /// mutation (probe, edit, prefer) the SAME instance goes back into the collection, so row
    /// recycling keeps the stale cell text. Empty property name = "re-read everything".
    /// </summary>
    public void NotifyChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));

    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("genreId")] public int GenreId { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("countryCode")] public string? CountryCode { get; set; }
    [JsonPropertyName("homepage")] public string? Homepage { get; set; }
    [JsonPropertyName("logoUrl")] public string? LogoUrl { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("preferredStreamId")] public string? PreferredStreamId { get; set; }
    [JsonPropertyName("streams")] public List<StreamVariant> Streams { get; set; } = [];

    [JsonIgnore] public string GenreName => GenreId == 0 ? "—" : RadioGenres.DisplayName(GenreId);
    [JsonIgnore] public int StreamCount => Streams.Count;
    [JsonIgnore] public int BestBitrate => Streams.Count == 0 ? 0 : Streams.Max(s => Math.Max(s.Bitrate, s.ProbeBitrate ?? 0));

    /// <summary>The codec the export would ship - the best variant's effective format, uppercased like the main app's Codec badge.</summary>
    [JsonIgnore] public string BestFormat
    {
        get
        {
            var format = BestVariant()?.EffectiveFormat;
            return string.IsNullOrEmpty(format) ? "—" : format!.ToUpperInvariant();
        }
    }

    [JsonIgnore] public string ProbeSummary
    {
        get
        {
            if (Streams.Count == 0)
            {
                return "—";
            }
            var ok = Streams.Count(s => s.ProbeStatus == ProbeStatus.Ok);
            var dead = Streams.Count(s => s.ProbeStatus == ProbeStatus.Dead);
            var geo = Streams.Count(s => s.ProbeStatus == ProbeStatus.Geo);
            var untested = Streams.Count(s => s.ProbeStatus is null or ProbeStatus.Untested);
            var parts = new List<string>();
            if (ok > 0) { parts.Add($"{ok} ok"); }
            if (dead > 0) { parts.Add($"{dead} dead"); }
            if (geo > 0) { parts.Add($"{geo} geo"); }
            if (untested > 0) { parts.Add($"{untested} ?"); }
            return string.Join(", ", parts);
        }
    }

    [JsonIgnore] public bool AnyGeoRisk => Streams.Any(s => s.GeoRisk);

    [JsonIgnore] public string GeoLabel
    {
        get
        {
            var code = BestVariant()?.ServerCountryCode ?? "";
            return AnyGeoRisk ? (code.Length > 0 ? $"{code} ⚠" : "⚠") : code.Length > 0 ? code : "—";
        }
    }

    /// <summary>✓ when the variant the export would ship supports in-stream metadata.</summary>
    [JsonIgnore] public string MetaLabel => BestVariant()?.HasMetadata == true ? "✓" : "—";

    /// <summary>✓ when the station has a logo to show.</summary>
    [JsonIgnore] public string IconLabel => string.IsNullOrEmpty(LogoUrl) ? "—" : "✓";

    /// <summary>
    /// The variant export ships: the explicit preference when set, otherwise probe-ok first,
    /// then direct streams over HLS, then bitrate descending, then codec preference.
    /// </summary>
    public StreamVariant? BestVariant()
    {
        if (Streams.Count == 0)
        {
            return null;
        }

        if (PreferredStreamId != null)
        {
            var preferred = Streams.FirstOrDefault(s => s.Id == PreferredStreamId);
            if (preferred != null)
            {
                return preferred;
            }
        }

        return Streams
            .OrderBy(s => s.ProbeStatus switch { ProbeStatus.Ok => 0, null or ProbeStatus.Untested => 1, ProbeStatus.Geo => 2, _ => 3 })
            .ThenBy(s => s.EffectiveFormat == "hls" ? 1 : 0)
            .ThenByDescending(s => s.ProbeBitrate ?? s.Bitrate)
            .ThenByDescending(s => s.HasMetadata)
            .ThenBy(s => s.EffectiveFormat switch { "aac" => 0, "mp3" => 1, "ogg" => 2, "flac" => 3, _ => 4 })
            .ThenBy(s => s.ProbeRedirects ?? 9)
            .First();
    }
}

public static class ProbeStatus
{
    public const string Untested = "untested";
    public const string Ok = "ok";
    public const string Dead = "dead";
    public const string Geo = "geo";
}

/// <summary>One concrete stream URL belonging to a station, with provenance and probe results.</summary>
public sealed class StreamVariant : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>See <see cref="CuratedStation.NotifyChanged"/> - same row-recycling story.</summary>
    public void NotifyChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));

    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("format")] public string Format { get; set; } = "";
    [JsonPropertyName("bitrate")] public int Bitrate { get; set; }

    // Provenance - which directory this variant came from, and its id over there.
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("sourceId")] public string? SourceId { get; set; }

    // Probe results.
    [JsonPropertyName("probeStatus")] public string? ProbeStatus { get; set; }
    [JsonPropertyName("probeDetail")] public string? ProbeDetail { get; set; }
    [JsonPropertyName("probeFormat")] public string? ProbeFormat { get; set; }
    [JsonPropertyName("probeBitrate")] public int? ProbeBitrate { get; set; }
    [JsonPropertyName("resolvedUrl")] public string? ResolvedUrl { get; set; }
    [JsonPropertyName("probedAtUtc")] public DateTimeOffset? ProbedAtUtc { get; set; }
    [JsonPropertyName("geoRisk")] public bool GeoRisk { get; set; }
    [JsonPropertyName("probeRedirects")] public int? ProbeRedirects { get; set; }
    [JsonPropertyName("probeMetaint")] public int? ProbeMetaint { get; set; }
    [JsonPropertyName("probeTitle")] public string? ProbeTitle { get; set; }
    [JsonPropertyName("probeMeasuredFormat")] public string? ProbeMeasuredFormat { get; set; }
    [JsonPropertyName("probeMeasuredBitrate")] public int? ProbeMeasuredBitrate { get; set; }
    [JsonPropertyName("serverIp")] public string? ServerIp { get; set; }
    [JsonPropertyName("serverCountry")] public string? ServerCountry { get; set; }
    [JsonPropertyName("serverCountryCode")] public string? ServerCountryCode { get; set; }

    /// <summary>Set by the view model when the variant list is rebuilt: ★ explicit preference, • computed best.</summary>
    [JsonIgnore] public string PreferredMark { get; set; } = "";

    // Measured (frame-parsed) values outrank server claims, which outrank directory data.
    [JsonIgnore] public string EffectiveFormat => !string.IsNullOrEmpty(ProbeMeasuredFormat) ? ProbeMeasuredFormat! : string.IsNullOrEmpty(ProbeFormat) ? Format : ProbeFormat!;
    [JsonIgnore] public int EffectiveBitrate => ProbeMeasuredBitrate ?? ProbeBitrate ?? Bitrate;
    [JsonIgnore] public string ProbeLabel => ProbeStatus ?? "untested";
    [JsonIgnore] public string PlayUrl => string.IsNullOrEmpty(ResolvedUrl) ? Url : ResolvedUrl!;

    [JsonIgnore] public bool HasMetadata => ProbeMetaint is > 0;
    [JsonIgnore] public string HopsLabel => ProbeRedirects?.ToString() ?? "—";
    [JsonIgnore] public string MetadataLabel => ProbeMetaint is int m and > 0 ? $"icy {m}" : ProbedAtUtc == null ? "—" : "none";

    [JsonIgnore] public string GeoLabel
    {
        get
        {
            var code = ServerCountryCode ?? "";
            return GeoRisk ? (code.Length > 0 ? $"{code} ⚠" : "⚠") : code.Length > 0 ? code : "—";
        }
    }

    /// <summary>Measured codec/bitrate vs what the directory/server advertised: ✓ agrees, ≠ lies, - nothing to compare yet.</summary>
    [JsonIgnore] public string MatchLabel
    {
        get
        {
            if (ProbedAtUtc == null)
            {
                return "—";
            }

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(ProbeMeasuredFormat) && !string.IsNullOrEmpty(Format) && !Format.Equals(ProbeMeasuredFormat, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"{Format}→{ProbeMeasuredFormat}");
            }
            var advertised = Bitrate > 0 ? Bitrate : ProbeBitrate ?? 0;
            if (ProbeMeasuredBitrate is int measured && advertised > 0 && Math.Abs(measured - advertised) > 16)
            {
                parts.Add($"{advertised}→{measured}k");
            }
            if (parts.Count > 0)
            {
                return "≠ " + string.Join(", ", parts);
            }
            var comparable = (!string.IsNullOrEmpty(ProbeMeasuredFormat) && !string.IsNullOrEmpty(Format)) || (ProbeMeasuredBitrate != null && advertised > 0);
            return comparable ? "✓" : "—";
        }
    }
}

/// <summary>A station row as one of the directories reports it, before curation.</summary>
public sealed class SourceStation
{
    public string Source { get; init; } = "";        // "radio-browser" | "shoutcast" | "icecast:<host>"
    public string? SourceId { get; init; }
    public string Name { get; init; } = "";
    public string StreamUrl { get; set; } = "";
    public string Format { get; init; } = "";
    public int Bitrate { get; init; }
    public string? Country { get; init; }
    public string? CountryCode { get; init; }
    public string? Homepage { get; init; }
    public string? LogoUrl { get; init; }
    public string Tags { get; init; } = "";
    public int Popularity { get; init; }              // clicks / listeners, whatever the source counts

    /// <summary>Tag-driven genre suggestion - the curation-time home of GenreNormalizer.</summary>
    public RadioGenre SuggestedGenre => RadioGenres.FromDisplayName(GenreNormalizer.ExtractPrimaryGenre(Tags));
    public string SuggestedGenreName => SuggestedGenre == RadioGenre.Unknown ? "—" : SuggestedGenre.DisplayName();

    /// <summary>Set by the view model when the station already exists in the curated store.</summary>
    public string CuratedMark { get; set; } = "";
}
