// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Microsoft.Data.Sqlite;
using OrgZ.Models;

namespace OrgZ.Services;

/// <summary>
/// Reads an iPod Nano 5G "iTunes Library.itlp" SQLite library (Library.itdb) into the same
/// <see cref="ITunesDbReader.ITunesTrack"/> + <see cref="DevicePlaylist"/> shapes the binary
/// iTunesDB reader produces - so the device scan can represent the Nano 5G's actual content
/// (music, podcasts via <c>media_kind=4</c>, audiobooks via <c>media_kind=8</c>) and its user
/// playlists, even though there's no binary iTunesDB to parse. Read-only: never touches the device.
/// </summary>
public static class Nano5gLibraryReader
{
    private static readonly DateTime Cf2001Epoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static void ReadAll(string itlpDir, string mountPath, out List<ITunesDbReader.ITunesTrack> tracks, out List<DevicePlaylist> playlists)
    {
        tracks = new List<ITunesDbReader.ITunesTrack>();
        playlists = new List<DevicePlaylist>();

        var libPath = Path.Combine(itlpDir, "Library.itdb");
        if (!File.Exists(libPath))
        {
            return;
        }

        var musicDir = Path.Combine(mountPath, "iPod_Control", "Music");

        // The location table lives in a SEPARATE database file (Locations.itdb), not Library.itdb -
        // read it first, keyed by item pid, then merge into the items below.
        var locById = new Dictionary<long, (string? Loc, long Size)>();
        var locPath = Path.Combine(itlpDir, "Locations.itdb");
        if (File.Exists(locPath))
        {
            using var lcon = new SqliteConnection($"Data Source={locPath};Mode=ReadOnly;Pooling=False");
            lcon.Open();
            using var lcmd = lcon.CreateCommand();
            lcmd.CommandText = "SELECT item_pid, location, file_size FROM location WHERE sub_id=0";
            using var lr = lcmd.ExecuteReader();
            while (lr.Read())
            {
                locById[lr.GetInt64(0)] = (lr.IsDBNull(1) ? null : lr.GetString(1), lr.IsDBNull(2) ? 0 : lr.GetInt64(2));
            }
        }

        using var con = new SqliteConnection($"Data Source={libPath};Mode=ReadOnly;Pooling=False");
        con.Open();

        // ── tracks: item joined to its format / genre (location merged from above) ──
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = """
                SELECT i.pid, i.media_kind, i.title, i.artist, i.album, i.album_artist,
                       i.year, i.track_number, i.track_count, i.disc_number, i.disc_count,
                       i.total_time_ms, g.genre, a.bit_rate, a.sample_rate, i.date_released
                FROM item i
                LEFT JOIN avformat_info a ON a.item_pid = i.pid AND a.sub_id = 0
                LEFT JOIN genre_map g ON g.id = i.genre_id
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                long pid = r.GetInt64(0);
                locById.TryGetValue(pid, out var loc);
                tracks.Add(new ITunesDbReader.ITunesTrack
                {
                    Pid = pid,
                    Dbid = unchecked((ulong)pid),
                    MediaType = r.IsDBNull(1) ? 1 : (int)r.GetInt64(1),
                    Title = Str(r, 2),
                    Artist = Str(r, 3),
                    Album = Str(r, 4),
                    Genre = Str(r, 12),
                    Year = Int(r, 6),
                    TrackNumber = Int(r, 7),
                    TotalTracks = Int(r, 8),
                    DiscNumber = Int(r, 9),
                    TotalDiscs = Int(r, 10),
                    DurationMs = Int(r, 11),
                    FilePath = loc.Loc is null ? null : Path.Combine(musicDir, loc.Loc.Replace('/', Path.DirectorySeparatorChar)),
                    FileSize = loc.Size,
                    Bitrate = Int(r, 13),
                    SampleRate = Int(r, 14),
                    // date_released is CFAbsoluteTime (seconds since 2001-01-01 UTC); 0 = unset.
                    DateReleased = r.IsDBNull(15) || r.GetInt64(15) == 0 ? null : Cf2001Epoch.AddSeconds(r.GetInt64(15)),
                });
            }
        }

        // ── user playlists: ordinary, visible containers (not the primary library, not the
        //    Podcasts menu which is distinguished_kind=11 / hidden) ──
        var containers = new List<(long Pid, string Name)>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT pid, name FROM container WHERE COALESCE(distinguished_kind,0)=0 AND COALESCE(is_hidden,0)=0 AND name IS NOT NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                containers.Add((r.GetInt64(0), r.GetString(1)));
            }
        }

        foreach (var (cpid, name) in containers)
        {
            var ids = new List<string>();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT item_pid FROM item_to_container WHERE container_pid=$c ORDER BY physical_order";
            cmd.Parameters.AddWithValue("$c", cpid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                ids.Add($"device:{mountPath}:{r.GetInt64(0)}");
            }
            playlists.Add(new DevicePlaylist { Name = name, Key = $"CNT:{cpid}", TrackIds = ids });
        }
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static int Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : (int)r.GetInt64(i);
}
