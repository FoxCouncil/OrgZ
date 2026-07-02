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
/// Used by Rockbox-mode devices only. Stock iPods go through iTunesDB which is already
/// fast to parse (and we never write to stock iPods as policy).
///
/// Schema is a narrow single-table subset of <see cref="MediaCache"/> - we only persist
/// the fields that are expensive to recompute (tag metadata + audio properties) plus the
/// stat fields used for staleness detection.
/// </summary>
public static class DeviceLibraryCache
{
    private const string Subfolder = ".orgz";
    private const string Filename = "library.db";
    private const int CurrentSchemaVersion = 2;

    private static readonly ILogger _log = Logging.For("DeviceLibraryCache");

    private static string GetDbPath(string mountPath)
        => Path.Combine(mountPath, Subfolder, Filename);

    private static string ConnectionString(string mountPath)
        => $"Data Source={GetDbPath(mountPath)}";

    /// <summary>
    /// Opens (or creates) the device DB and reads every row into <see cref="MediaItem"/>s
    /// tagged with the given <paramref name="source"/>. Returns empty on missing file or
    /// any read error - the scan falls through to a clean full walk in that case.
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
            using var connection = OpenAndEnsureSchema(mountPath);
            var items = new List<MediaItem>();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT " + ColumnList + " FROM Media";
            using var reader = cmd.ExecuteReader();

            var ordinals = new ColumnOrdinals(reader);
            while (reader.Read())
            {
                items.Add(ReadRow(reader, ordinals, source));
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
    /// resumes from exactly where it stopped on the next connect - everything already
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
            using var connection = OpenAndEnsureSchema(mountPath);
            using var tx = connection.BeginTransaction();

            using var insert = connection.CreateCommand();
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
            var pKind     = insert.Parameters.Add("@Kind",      SqliteType.Text);

            int count = 0;
            int skipped = 0;
            foreach (var m in items)
            {
                // FilePath is NOT NULL in the schema - skip any item without one rather
                // than failing the whole batch with a constraint violation.
                if (string.IsNullOrEmpty(m.Id) || string.IsNullOrEmpty(m.FilePath))
                {
                    skipped++;
                    continue;
                }

                pId.Value       = m.Id;
                pPath.Value     = m.FilePath;
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
                pKind.Value     = m.Kind.ToString();

                insert.ExecuteNonQuery();
                count++;
            }

            tx.Commit();
            if (skipped > 0)
            {
                _log.Warning("Device library cache upsert skipped {Count} items missing Id or FilePath", skipped);
            }
            _log.Debug("Device library cache upserted: {Path} batch={Count}", GetDbPath(mountPath), count);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Device library cache upsert failed for {MountPath}", mountPath);
        }
    }

    /// <summary>
    /// Removes rows whose FilePath isn't in <paramref name="keepFilePaths"/>. Called at
    /// the end of a complete scan to prune rows for files that have been deleted from
    /// the device since the last scan. Interrupted scans should skip this - partial
    /// state shouldn't delete otherwise-valid rows.
    /// </summary>
    public static void PruneMissing(string mountPath, IEnumerable<string> keepFilePaths)
    {
        try
        {
            using var connection = OpenAndEnsureSchema(mountPath);
            using var tx = connection.BeginTransaction();

            // Build a temp table of paths to keep so we can DELETE ... NOT IN (...) without
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

    /// <summary>
    /// Reads a metadata value stored alongside the media cache. Used by scanners to
    /// cache arbitrary signatures (e.g. source-file mtime/size) so we can skip reparsing
    /// when the source hasn't changed. Returns null when the key doesn't exist or the
    /// DB is unreadable.
    /// </summary>
    public static string? GetMetadata(string mountPath, string key)
    {
        var dbPath = GetDbPath(mountPath);
        if (!File.Exists(dbPath))
        {
            return null;
        }

        try
        {
            using var connection = OpenAndEnsureSchema(mountPath);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Metadata WHERE Key = @k";
            cmd.Parameters.Add("@k", SqliteType.Text).Value = key;
            var result = cmd.ExecuteScalar();
            return result is string s ? s : null;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Device library cache GetMetadata failed for {Key} at {Path}", key, dbPath);
            return null;
        }
    }

    /// <summary>
    /// Writes or replaces a metadata value. Paired with <see cref="GetMetadata"/> so
    /// scanners can persist a staleness signature next to the cached tracks.
    /// </summary>
    public static void SetMetadata(string mountPath, string key, string value)
    {
        try
        {
            using var connection = OpenAndEnsureSchema(mountPath);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO Metadata (Key, Value) VALUES (@k, @v)";
            cmd.Parameters.Add("@k", SqliteType.Text).Value = key;
            cmd.Parameters.Add("@v", SqliteType.Text).Value = value;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Device library cache SetMetadata failed for {Key} at {MountPath}", key, mountPath);
        }
    }

    /// <summary>
    /// Opens a connection, ensures <c>.orgz</c> exists, applies any pending schema
    /// migrations, and enables WAL so concurrent reads (e.g. playback reading a track
    /// while a background scan writes) don't block each other. Callers must dispose
    /// the returned connection - usual using pattern applies.
    /// </summary>
    private static SqliteConnection OpenAndEnsureSchema(string mountPath)
    {
        Directory.CreateDirectory(Path.Combine(mountPath, Subfolder));

        var connection = new SqliteConnection(ConnectionString(mountPath));
        connection.Open();

        // WAL gives us concurrent reader + writer on the same DB. For a library of a
        // few thousand tracks the overhead is negligible but the unblocked playback
        // path is worth it - otherwise a track playing from the iPod could stall
        // mid-play while a bg rescan is upserting.
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL";
            pragma.ExecuteNonQuery();
        }

        // Read the current schema version. 0 = brand new DB; bump past 0 as we create
        // tables so the next open sees the right version.
        int currentVersion;
        using (var vcmd = connection.CreateCommand())
        {
            vcmd.CommandText = "PRAGMA user_version";
            currentVersion = Convert.ToInt32(vcmd.ExecuteScalar() ?? 0);
        }

        if (currentVersion < CurrentSchemaVersion)
        {
            ApplyMigrations(connection, currentVersion);
        }
        else if (currentVersion > CurrentSchemaVersion)
        {
            // DB was written by a newer OrgZ. Reads may still work if new columns are
            // only additive, but we log a warning so the user knows why something might
            // be missing after a downgrade.
            _log.Warning("Device library cache schema version {Current} is newer than supported {Supported} at {Path}",
                currentVersion, CurrentSchemaVersion, GetDbPath(mountPath));
        }

        return connection;
    }

    /// <summary>
    /// Applies pending migrations incrementally from <paramref name="fromVersion"/> up
    /// to <see cref="CurrentSchemaVersion"/>. Each case creates tables / alters schema
    /// to bring the DB from version N-1 to N; bumps user_version at the end of each
    /// step so a crashed migration resumes cleanly on next open.
    /// </summary>
    private static void ApplyMigrations(SqliteConnection connection, int fromVersion)
    {
        using var tx = connection.BeginTransaction();

        if (fromVersion < 1)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
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
                    HasAlbumArt      INTEGER,
                    Kind             TEXT
                );
                CREATE TABLE IF NOT EXISTS Metadata (
                    Key    TEXT PRIMARY KEY,
                    Value  TEXT
                );
                """;
            cmd.ExecuteNonQuery();
        }

        if (fromVersion is >= 1 and < 2)
        {
            // v2: the row's MediaKind (Audiobook vs Music). Only v1 DBs need the ALTER - a fresh
            // DB gets the column in the v1 CREATE TABLE above. NULL on pre-v2 rows reads as
            // Music, matching what those scans assumed.
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "ALTER TABLE Media ADD COLUMN Kind TEXT";
            cmd.ExecuteNonQuery();
        }

        using (var bump = connection.CreateCommand())
        {
            bump.Transaction = tx;
            bump.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion}";
            bump.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private const string ColumnList = """
        Id, FilePath, FileName, Extension, FileSize, LastModified,
        Title, Artist, Album, Genre, Composer,
        Year, Track, TotalTracks, Disc, TotalDiscs,
        DurationTicks, AudioBitrate, SampleRate, AudioChannels,
        CodecDescription, HasAlbumArt, Kind
        """;

    private const string InsertSql = """
        INSERT OR REPLACE INTO Media (
            Id, FilePath, FileName, Extension, FileSize, LastModified,
            Title, Artist, Album, Genre, Composer,
            Year, Track, TotalTracks, Disc, TotalDiscs,
            DurationTicks, AudioBitrate, SampleRate, AudioChannels,
            CodecDescription, HasAlbumArt, Kind
        ) VALUES (
            @Id, @FilePath, @FileName, @Extension, @FileSize, @LastModified,
            @Title, @Artist, @Album, @Genre, @Composer,
            @Year, @Track, @TotalTracks, @Disc, @TotalDiscs,
            @DurationTicks, @AudioBitrate, @SampleRate, @AudioChannels,
            @CodecDescription, @HasAlbumArt, @Kind
        );
        """;

    // Ordinal cache - looked up once per reader instead of per column per row.
    // For a 14k-track library this goes from 300k ordinal lookups to ~22.
    private readonly struct ColumnOrdinals
    {
        public readonly int Id, FilePath, FileName, Extension, FileSize, LastModified;
        public readonly int Title, Artist, Album, Genre, Composer;
        public readonly int Year, Track, TotalTracks, Disc, TotalDiscs;
        public readonly int DurationTicks, AudioBitrate, SampleRate, AudioChannels;
        public readonly int CodecDescription, HasAlbumArt, Kind;

        public ColumnOrdinals(SqliteDataReader r)
        {
            Id               = r.GetOrdinal("Id");
            FilePath         = r.GetOrdinal("FilePath");
            FileName         = r.GetOrdinal("FileName");
            Extension        = r.GetOrdinal("Extension");
            FileSize         = r.GetOrdinal("FileSize");
            LastModified     = r.GetOrdinal("LastModified");
            Title            = r.GetOrdinal("Title");
            Artist           = r.GetOrdinal("Artist");
            Album            = r.GetOrdinal("Album");
            Genre            = r.GetOrdinal("Genre");
            Composer         = r.GetOrdinal("Composer");
            Year             = r.GetOrdinal("Year");
            Track            = r.GetOrdinal("Track");
            TotalTracks      = r.GetOrdinal("TotalTracks");
            Disc             = r.GetOrdinal("Disc");
            TotalDiscs       = r.GetOrdinal("TotalDiscs");
            DurationTicks    = r.GetOrdinal("DurationTicks");
            AudioBitrate     = r.GetOrdinal("AudioBitrate");
            SampleRate       = r.GetOrdinal("SampleRate");
            AudioChannels    = r.GetOrdinal("AudioChannels");
            CodecDescription = r.GetOrdinal("CodecDescription");
            HasAlbumArt      = r.GetOrdinal("HasAlbumArt");
            Kind             = r.GetOrdinal("Kind");
        }
    }

    private static MediaItem ReadRow(SqliteDataReader r, ColumnOrdinals o, string source)
    {
        var filePath = GetString(r, o.FilePath);
        var item = new MediaItem
        {
            Id           = r.GetString(o.Id),
            // Pre-v2 rows have no Kind - those scans only ever produced Music, so that's the default.
            Kind         = GetString(r, o.Kind) is { } k && Enum.TryParse<MediaKind>(k, out var kind) ? kind : MediaKind.Music,
            FilePath     = filePath,
            FileName     = GetString(r, o.FileName),
            Extension    = GetString(r, o.Extension),
            FileSize     = GetNullableLong(r, o.FileSize),
            LastModified = GetNullableDate(r, o.LastModified),
            Source       = source,
            StreamUrl    = filePath,
            IsAnalyzed   = true,
        };
        item.Title            = GetString(r, o.Title);
        item.Artist           = GetString(r, o.Artist);
        item.Album            = GetString(r, o.Album);
        item.Genre            = GetString(r, o.Genre);
        item.Composer         = GetString(r, o.Composer);
        item.Year             = GetNullableUint(r, o.Year);
        item.Track            = GetNullableUint(r, o.Track);
        item.TotalTracks      = GetNullableUint(r, o.TotalTracks);
        item.Disc             = GetNullableUint(r, o.Disc);
        item.TotalDiscs       = GetNullableUint(r, o.TotalDiscs);
        item.Duration         = GetNullableTimeSpan(r, o.DurationTicks);
        item.AudioBitrate     = GetNullableInt(r, o.AudioBitrate);
        item.SampleRate       = GetNullableInt(r, o.SampleRate);
        item.AudioChannels    = GetNullableInt(r, o.AudioChannels);
        item.CodecDescription = GetString(r, o.CodecDescription);
        item.HasAlbumArt      = GetNullableBool(r, o.HasAlbumArt);
        return item;
    }

    private static string? GetString(SqliteDataReader r, int ord)
        => r.IsDBNull(ord) ? null : r.GetString(ord);

    private static long? GetNullableLong(SqliteDataReader r, int ord)
        => r.IsDBNull(ord) ? null : r.GetInt64(ord);

    private static int? GetNullableInt(SqliteDataReader r, int ord)
        => r.IsDBNull(ord) ? null : r.GetInt32(ord);

    private static uint? GetNullableUint(SqliteDataReader r, int ord)
        => r.IsDBNull(ord) ? null : (uint)r.GetInt64(ord);

    private static bool? GetNullableBool(SqliteDataReader r, int ord)
        => r.IsDBNull(ord) ? null : r.GetInt32(ord) != 0;

    private static DateTime? GetNullableDate(SqliteDataReader r, int ord)
        => r.IsDBNull(ord) ? null : DateTime.Parse(r.GetString(ord), null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static TimeSpan? GetNullableTimeSpan(SqliteDataReader r, int ord)
    {
        var ticks = GetNullableLong(r, ord);
        return ticks.HasValue ? new TimeSpan(ticks.Value) : null;
    }
}
