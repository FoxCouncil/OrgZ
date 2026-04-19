// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Microsoft.Data.Sqlite;
using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Per-device music library cache stored at <c>{mount}/.orgz/library.db</c> as a small
/// SQLite file that travels with the iPod. Preserves scan results so the next connect
/// skips TagLib analysis on files whose size + mtime are unchanged; only new or modified
/// files are re-analyzed; vanished files are pruned.
///
/// Schema is a narrow single-table subset of <see cref="MediaCache"/> — we only persist
/// the fields that are expensive to recompute (tag metadata + audio properties) plus the
/// stat fields used for staleness detection.
/// </summary>
public static class DeviceLibraryCache
{
    private const string Subfolder = ".orgz";
    private const string Filename = "library.db";
    private const int SchemaVersion = 1;

    private static readonly ILogger _log = Logging.For("DeviceLibraryCache");

    private static string GetDbPath(string mountPath)
        => Path.Combine(mountPath, Subfolder, Filename);

    private static string ConnectionString(string mountPath)
        => $"Data Source={GetDbPath(mountPath)}";

    /// <summary>
    /// Opens (or creates) the device DB and reads every row into <see cref="MediaItem"/>s
    /// tagged with the given <paramref name="source"/>. Returns empty on missing file or
    /// any read error — the scan falls through to a clean full walk in that case.
    /// </summary>
    public static List<MediaItem> TryLoad(string mountPath, string source)
    {
        var dbPath = GetDbPath(mountPath);
        if (!File.Exists(dbPath))
        {
            return [];
        }

        try
        {
            EnsureSchema(mountPath);

            var items = new List<MediaItem>();
            using var connection = new SqliteConnection(ConnectionString(mountPath));
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM Media";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadRow(reader, source));
            }

            _log.Information("Device library cache loaded: {Path} tracks={Count}", dbPath, items.Count);
            return items;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Device library cache read failed at {Path} — scan will run fresh", dbPath);
            return [];
        }
    }

    /// <summary>
    /// Inserts or replaces the given items. Run from the scanner every batch so an
    /// interrupted scan (user closes the app, device unplugged mid-analysis, crash)
    /// resumes from exactly where it stopped on the next connect — everything already
    /// upserted is still in the DB and the delta logic reuses those rows.
    /// </summary>
    public static void Upsert(string mountPath, IReadOnlyList<MediaItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        try
        {
            EnsureSchema(mountPath);

            using var connection = new SqliteConnection(ConnectionString(mountPath));
            connection.Open();

            using var tx = connection.BeginTransaction();

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = InsertSql;
                var pId       = insert.Parameters.Add("@Id",       SqliteType.Text);
                var pPath     = insert.Parameters.Add("@FilePath", SqliteType.Text);
                var pFileName = insert.Parameters.Add("@FileName", SqliteType.Text);
                var pExt      = insert.Parameters.Add("@Extension", SqliteType.Text);
                var pSize     = insert.Parameters.Add("@FileSize",  SqliteType.Integer);
                var pMod      = insert.Parameters.Add("@LastModified", SqliteType.Text);
                var pTitle    = insert.Parameters.Add("@Title",     SqliteType.Text);
                var pArtist   = insert.Parameters.Add("@Artist",    SqliteType.Text);
                var pAlbum    = insert.Parameters.Add("@Album",     SqliteType.Text);
                var pGenre    = insert.Parameters.Add("@Genre",     SqliteType.Text);
                var pComp     = insert.Parameters.Add("@Composer",  SqliteType.Text);
                var pYear     = insert.Parameters.Add("@Year",      SqliteType.Integer);
                var pTrack    = insert.Parameters.Add("@Track",     SqliteType.Integer);
                var pTotTrack = insert.Parameters.Add("@TotalTracks", SqliteType.Integer);
                var pDisc     = insert.Parameters.Add("@Disc",      SqliteType.Integer);
                var pTotDisc  = insert.Parameters.Add("@TotalDiscs", SqliteType.Integer);
                var pDur      = insert.Parameters.Add("@DurationTicks", SqliteType.Integer);
                var pBitrate  = insert.Parameters.Add("@AudioBitrate", SqliteType.Integer);
                var pSample   = insert.Parameters.Add("@SampleRate", SqliteType.Integer);
                var pChannels = insert.Parameters.Add("@AudioChannels", SqliteType.Integer);
                var pCodec    = insert.Parameters.Add("@CodecDescription", SqliteType.Text);
                var pArt      = insert.Parameters.Add("@HasAlbumArt", SqliteType.Integer);

                int count = 0;
                foreach (var m in items)
                {
                    pId.Value       = (object?)m.Id ?? DBNull.Value;
                    pPath.Value     = (object?)m.FilePath ?? DBNull.Value;
                    pFileName.Value = (object?)m.FileName ?? DBNull.Value;
                    pExt.Value      = (object?)m.Extension ?? DBNull.Value;
                    pSize.Value     = (object?)m.FileSize ?? DBNull.Value;
                    pMod.Value      = m.LastModified?.ToString("o") ?? (object)DBNull.Value;
                    pTitle.Value    = (object?)m.Title ?? DBNull.Value;
                    pArtist.Value   = (object?)m.Artist ?? DBNull.Value;
                    pAlbum.Value    = (object?)m.Album ?? DBNull.Value;
                    pGenre.Value    = (object?)m.Genre ?? DBNull.Value;
                    pComp.Value     = (object?)m.Composer ?? DBNull.Value;
                    pYear.Value     = (object?)m.Year ?? DBNull.Value;
                    pTrack.Value    = (object?)m.Track ?? DBNull.Value;
                    pTotTrack.Value = (object?)m.TotalTracks ?? DBNull.Value;
                    pDisc.Value     = (object?)m.Disc ?? DBNull.Value;
                    pTotDisc.Value  = (object?)m.TotalDiscs ?? DBNull.Value;
                    pDur.Value      = (object?)m.Duration?.Ticks ?? DBNull.Value;
                    pBitrate.Value  = (object?)m.AudioBitrate ?? DBNull.Value;
                    pSample.Value   = (object?)m.SampleRate ?? DBNull.Value;
                    pChannels.Value = (object?)m.AudioChannels ?? DBNull.Value;
                    pCodec.Value    = (object?)m.CodecDescription ?? DBNull.Value;
                    pArt.Value      = m.HasAlbumArt.HasValue ? (m.HasAlbumArt.Value ? 1 : 0) : (object)DBNull.Value;

                    insert.ExecuteNonQuery();
                    count++;
                }

                tx.Commit();
                _log.Debug("Device library cache upserted: {Path} batch={Count}", GetDbPath(mountPath), count);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Device library cache upsert failed for {MountPath}", mountPath);
        }
    }

    /// <summary>
    /// Removes rows whose FilePath isn't in <paramref name="keepFilePaths"/>. Called at
    /// the end of a complete scan to prune rows for files that have been deleted from
    /// the device since the last scan. Interrupted scans should skip this — partial
    /// state shouldn't delete otherwise-valid rows.
    /// </summary>
    public static void PruneMissing(string mountPath, IEnumerable<string> keepFilePaths)
    {
        try
        {
            EnsureSchema(mountPath);

            using var connection = new SqliteConnection(ConnectionString(mountPath));
            connection.Open();

            using var tx = connection.BeginTransaction();

            // Build a temp table of paths to keep so we can DELETE … NOT IN (…) without
            // blowing out the SQL parser on libraries with thousands of tracks.
            using (var tmp = connection.CreateCommand())
            {
                tmp.Transaction = tx;
                tmp.CommandText = "CREATE TEMP TABLE KeepPaths (FilePath TEXT PRIMARY KEY)";
                tmp.ExecuteNonQuery();
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = "INSERT OR IGNORE INTO KeepPaths (FilePath) VALUES (@p)";
                var p = insert.Parameters.Add("@p", SqliteType.Text);
                foreach (var path in keepFilePaths)
                {
                    p.Value = path;
                    insert.ExecuteNonQuery();
                }
            }

            int deleted;
            using (var del = connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM Media WHERE FilePath NOT IN (SELECT FilePath FROM KeepPaths)";
                deleted = del.ExecuteNonQuery();
            }

            tx.Commit();
            if (deleted > 0)
            {
                _log.Information("Device library cache pruned: {Path} removed={Count}", GetDbPath(mountPath), deleted);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Device library cache prune failed for {MountPath}", mountPath);
        }
    }

    private static void EnsureSchema(string mountPath)
    {
        Directory.CreateDirectory(Path.Combine(mountPath, Subfolder));

        using var connection = new SqliteConnection(ConnectionString(mountPath));
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            PRAGMA user_version = {SchemaVersion};
            CREATE TABLE IF NOT EXISTS Media (
                Id               TEXT PRIMARY KEY,
                FilePath         TEXT NOT NULL,
                FileName         TEXT,
                Extension        TEXT,
                FileSize         INTEGER,
                LastModified     TEXT,
                Title            TEXT,
                Artist           TEXT,
                Album            TEXT,
                Genre            TEXT,
                Composer         TEXT,
                Year             INTEGER,
                Track            INTEGER,
                TotalTracks      INTEGER,
                Disc             INTEGER,
                TotalDiscs       INTEGER,
                DurationTicks    INTEGER,
                AudioBitrate     INTEGER,
                SampleRate       INTEGER,
                AudioChannels    INTEGER,
                CodecDescription TEXT,
                HasAlbumArt      INTEGER
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private const string InsertSql = """
        INSERT OR REPLACE INTO Media (
            Id, FilePath, FileName, Extension, FileSize, LastModified,
            Title, Artist, Album, Genre, Composer,
            Year, Track, TotalTracks, Disc, TotalDiscs,
            DurationTicks, AudioBitrate, SampleRate, AudioChannels,
            CodecDescription, HasAlbumArt
        ) VALUES (
            @Id, @FilePath, @FileName, @Extension, @FileSize, @LastModified,
            @Title, @Artist, @Album, @Genre, @Composer,
            @Year, @Track, @TotalTracks, @Disc, @TotalDiscs,
            @DurationTicks, @AudioBitrate, @SampleRate, @AudioChannels,
            @CodecDescription, @HasAlbumArt
        );
        """;

    private static MediaItem ReadRow(SqliteDataReader r, string source)
    {
        var item = new MediaItem
        {
            Id           = r.GetString(r.GetOrdinal("Id")),
            Kind         = MediaKind.Music,
            FilePath     = GetString(r, "FilePath"),
            FileName     = GetString(r, "FileName"),
            Extension    = GetString(r, "Extension"),
            FileSize     = GetNullableLong(r, "FileSize"),
            LastModified = GetNullableDate(r, "LastModified"),
            Source       = source,
            StreamUrl    = GetString(r, "FilePath"),
            IsAnalyzed   = true,
        };
        item.Title            = GetString(r, "Title");
        item.Artist           = GetString(r, "Artist");
        item.Album            = GetString(r, "Album");
        item.Genre            = GetString(r, "Genre");
        item.Composer         = GetString(r, "Composer");
        item.Year             = GetNullableUint(r, "Year");
        item.Track            = GetNullableUint(r, "Track");
        item.TotalTracks      = GetNullableUint(r, "TotalTracks");
        item.Disc             = GetNullableUint(r, "Disc");
        item.TotalDiscs       = GetNullableUint(r, "TotalDiscs");
        item.Duration         = GetNullableTimeSpan(r, "DurationTicks");
        item.AudioBitrate     = GetNullableInt(r, "AudioBitrate");
        item.SampleRate       = GetNullableInt(r, "SampleRate");
        item.AudioChannels    = GetNullableInt(r, "AudioChannels");
        item.CodecDescription = GetString(r, "CodecDescription");
        item.HasAlbumArt      = GetNullableBool(r, "HasAlbumArt");
        return item;
    }

    private static string? GetString(SqliteDataReader r, string col)
    {
        var o = r.GetOrdinal(col);
        return r.IsDBNull(o) ? null : r.GetString(o);
    }

    private static long? GetNullableLong(SqliteDataReader r, string col)
    {
        var o = r.GetOrdinal(col);
        return r.IsDBNull(o) ? null : r.GetInt64(o);
    }

    private static int? GetNullableInt(SqliteDataReader r, string col)
    {
        var o = r.GetOrdinal(col);
        return r.IsDBNull(o) ? null : r.GetInt32(o);
    }

    private static uint? GetNullableUint(SqliteDataReader r, string col)
    {
        var o = r.GetOrdinal(col);
        return r.IsDBNull(o) ? null : (uint)r.GetInt64(o);
    }

    private static bool? GetNullableBool(SqliteDataReader r, string col)
    {
        var o = r.GetOrdinal(col);
        return r.IsDBNull(o) ? null : r.GetInt32(o) != 0;
    }

    private static DateTime? GetNullableDate(SqliteDataReader r, string col)
    {
        var o = r.GetOrdinal(col);
        return r.IsDBNull(o) ? null : DateTime.Parse(r.GetString(o), null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    private static TimeSpan? GetNullableTimeSpan(SqliteDataReader r, string col)
    {
        var ticks = GetNullableLong(r, col);
        return ticks.HasValue ? new TimeSpan(ticks.Value) : null;
    }
}
