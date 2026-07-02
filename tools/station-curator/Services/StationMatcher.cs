// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.RegularExpressions;
using OrgZ.StationCurator.Models;

namespace OrgZ.StationCurator.Services;

/// <summary>
/// The aggressive same-station tie. Directories list one broadcast many times - "KEXP 128k",
/// "KEXP (AAC)", "KEXP 90.3 FM HD" - so imports match against existing curated stations by
/// source identity, by exact stream URL, and by quality-token-stripped name, and land as an
/// extra stream variant instead of a duplicate station.
/// </summary>
public static partial class StationMatcher
{
    [GeneratedRegex(@"\((?:[^)]*)\)|\[(?:[^\]]*)\]")]
    private static partial Regex BracketedRegex();

    [GeneratedRegex(@"\b\d{2,4}\s?(?:k|kb|kbps|kbit|kbits)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BitrateTokenRegex();

    [GeneratedRegex(@"\b(?:mp3|aac|aac\+|aacp|he-aac|heaac|ogg|opus|vorbis|flac|hls|hd|hq|sq|lq|lo-fi\s+stream|low|high|stereo|mono|stream)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualityTokenRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlnumRegex();

    /// <summary>Strips bitrate/codec/quality noise so "KEXP 128k AAC" and "KEXP" collide.</summary>
    public static string NormalizeName(string name)
    {
        var s = name.ToLowerInvariant();
        s = BracketedRegex().Replace(s, " ");
        s = BitrateTokenRegex().Replace(s, " ");
        s = QualityTokenRegex().Replace(s, " ");
        s = NonAlnumRegex().Replace(s, " ");
        return s.Trim();
    }

    /// <summary>Finds the curated station a source row belongs to, if any.</summary>
    public static CuratedStation? FindMatch(IEnumerable<CuratedStation> stations, SourceStation source)
    {
        var normalized = NormalizeName(source.Name);
        foreach (var station in stations)
        {
            if (source.SourceId != null && station.Streams.Any(v => v.SourceId == source.SourceId && v.Source == source.Source))
            {
                return station;
            }
            if (source.StreamUrl.Length > 0 && station.Streams.Any(v => UrlsEqual(v.Url, source.StreamUrl)))
            {
                return station;
            }
            if (normalized.Length > 0 && NormalizeName(station.Name) == normalized)
            {
                return station;
            }
        }
        return null;
    }

    public static bool UrlsEqual(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Stable curated-station id derived from the first variant's provenance.</summary>
    public static string MakeStationId(SourceStation source)
    {
        return source.Source switch
        {
            "radio-browser" => $"rb:{source.SourceId}",
            "shoutcast" => $"sc:{source.SourceId}",
            _ when source.Source.StartsWith("icecast:", StringComparison.Ordinal) => $"ic:{Slug(source.Source[8..])}.{Slug(source.Name)}",
            _ => $"man:{Slug(source.Name)}",
        };
    }

    private static string Slug(string s)
    {
        var slug = NonAlnumRegex().Replace(s.ToLowerInvariant(), "-").Trim('-');
        return slug.Length > 40 ? slug[..40] : slug;
    }
}
