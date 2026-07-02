// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using OrgZ.Models;
using OrgZ.StationCurator.Models;

namespace OrgZ.StationCurator.Services;

/// <summary>
/// Writes Assets/stations.json in the exact shape BundledStationsService consumes: one
/// stream per station (each station's best variant), full 29-genre taxonomy, sorted by
/// genre then name. Stations without a genre assignment are not shipped.
/// </summary>
public static class StationExporter
{
    public sealed record ExportResult(int Exported, int SkippedUnassigned, int SkippedNoStream);

    public static ExportResult Export(CuratedDb db)
    {
        var exported = new List<object>();
        var skippedUnassigned = 0;
        var skippedNoStream = 0;

        foreach (var station in db.Stations.OrderBy(s => s.GenreId).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (station.GenreId is < 1 or > 29)
            {
                skippedUnassigned++;
                continue;
            }

            var variant = station.BestVariant();
            if (variant == null || string.IsNullOrWhiteSpace(variant.PlayUrl))
            {
                skippedNoStream++;
                continue;
            }

            exported.Add(new
            {
                id = station.Id,
                name = station.Name,
                streamUrl = variant.PlayUrl,
                streamFormat = string.IsNullOrEmpty(variant.EffectiveFormat) ? "mp3" : variant.EffectiveFormat,
                bitrate = variant.EffectiveBitrate,
                genreId = station.GenreId,
                country = station.Country ?? "",
                countryCode = station.CountryCode ?? "",
                homepage = station.Homepage,
                logoUrl = station.LogoUrl,
                description = station.Description,
            });
        }

        var bundle = new
        {
            schemaVersion = 1,
            taxonomy = new
            {
                genres = RadioGenres.All.Select(g => new { id = (int)g, name = g.DisplayName() }).ToArray(),
            },
            stations = exported,
        };

        var json = JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(RepoPaths.StationsJson, json + "\n");

        return new ExportResult(exported.Count, skippedUnassigned, skippedNoStream);
    }
}
