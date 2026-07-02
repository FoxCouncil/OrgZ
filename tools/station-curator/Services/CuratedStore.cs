// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using OrgZ.StationCurator.Models;

namespace OrgZ.StationCurator.Services;

/// <summary>Load/save for tools/station-curator/curated.json.</summary>
public static class CuratedStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static CuratedDb Load()
    {
        var path = RepoPaths.CuratedJson;
        if (!File.Exists(path))
        {
            return new CuratedDb();
        }
        return JsonSerializer.Deserialize<CuratedDb>(File.ReadAllText(path), JsonOptions) ?? new CuratedDb();
    }

    public static void Save(CuratedDb db)
    {
        db.Stations = db.Stations
            .OrderBy(s => s.GenreId == 0 ? int.MaxValue : s.GenreId)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        File.WriteAllText(RepoPaths.CuratedJson, JsonSerializer.Serialize(db, JsonOptions) + Environment.NewLine);
    }
}
