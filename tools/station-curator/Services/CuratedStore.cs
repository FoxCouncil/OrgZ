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

    /// <summary>
    /// Save, but first fold back in any stations another process appended to disk since we loaded,
    /// so a concurrent external edit (seed script, batch import, a second curator) is never clobbered.
    /// <paramref name="knownIds"/> is the set of ids we last loaded/saved: a station present on disk
    /// but absent from memory is an external append to KEEP only if it is not known - a known id that
    /// is gone from memory was deleted in this session and must NOT be resurrected. Returns the ids
    /// that were merged in (empty when nothing external showed up).
    /// </summary>
    public static List<string> SaveMerging(CuratedDb db, ISet<string> knownIds)
    {
        var merged = new List<string>();
        if (File.Exists(RepoPaths.CuratedJson))
        {
            var memIds = db.Stations.Select(s => s.Id).ToHashSet();
            foreach (var s in Load().Stations)
            {
                if (!memIds.Contains(s.Id) && !knownIds.Contains(s.Id))
                {
                    db.Stations.Add(s);
                    merged.Add(s.Id);
                }
            }
        }
        Save(db);
        return merged;
    }
}
