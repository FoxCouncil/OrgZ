// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Serilog;

namespace OrgZ.Services;

public static class MediaCache
{
    private static readonly ILogger _log = Logging.For("MediaCache");

    // All three library.db consumers resolve through LibraryDb, so redirecting one
    // (the service adopting its owner's library) redirects all of them.
    private static string CacheDirectory => LibraryDb.Directory;
    private static string CacheFilePath => LibraryDb.FilePath;
    private static string ConnectionString => LibraryDb.ConnectionString;

    /// <summary>
    /// Where the library database lives right now. A GUI hands this to the background
    /// service with share-start / sync-run: the service runs as LocalSystem, whose
    /// %APPDATA% is the (empty) systemprofile - "the service shares the library DB" is
    /// only true when it's told WHICH one.
    /// </summary>
    public static string CurrentDatabasePath => CacheFilePath;

    /// <summary>
    /// Points the cache at a client-supplied database - the service process adopting its
    /// owner's library. Rooted, existing files only: a missing or relative path is
    /// ignored (returns false) rather than aiming the service at a file that isn't a
    /// library and silently serving nothing.
    /// </summary>
    internal static bool AdoptClientDatabase(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !File.Exists(path))
        {
            return false;
        }

        OverrideCachePath(path);
        return true;
    }

    /// <summary>
    /// Test hook: redirect the cache to a custom file path. Pass null to restore the default location.
    /// </summary>
    internal static void OverrideCachePath(string? path) => LibraryDb.OverrideFilePath(path);

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(CacheDirectory);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS Media (
                    Id                      TEXT PRIMARY KEY,
                    Kind                    TEXT NOT NULL,

                    Title                   TEXT,
                    Artist                  TEXT,
                    Album                   TEXT,
                    Duration                INTEGER,
                    IsFavorite              INTEGER NOT NULL DEFAULT 0,
                    LastPlayed              TEXT,
                    DateAdded               TEXT NOT NULL,

                    FilePath                TEXT,
                    FileName                TEXT,
                    Extension               TEXT,
                    FileSize                INTEGER,
                    LastModified            TEXT,
                    Year                    INTEGER,
                    Track                   INTEGER,
                    TotalTracks             INTEGER,
                    Disc                    INTEGER,
                    TotalDiscs              INTEGER,
                    HasAlbumArt             INTEGER,
                    FileNameMatchesHeaders  INTEGER,
                    MimeType                TEXT,
                    Genre                   TEXT,
                    Composer                TEXT,
                    Comment                 TEXT,
                    BPM                     INTEGER,
                    AudioBitrate            INTEGER,
                    SampleRate              INTEGER,
                    BitDepth                INTEGER,
                    AudioChannels           INTEGER,
                    EncoderSettings         TEXT,
                    CodecDescription        TEXT,
                    Issues                  TEXT,

                    StreamUrl               TEXT,
                    Source                   TEXT,
                    SourceId                TEXT,
                    HomepageUrl             TEXT,
                    FaviconUrl              TEXT,
                    Country                 TEXT,
                    CountryCode             TEXT,
                    Tags                    TEXT,
                    Codec                   TEXT,
                    Bitrate                 INTEGER,
                    Votes                   INTEGER,
                    ClickCount              INTEGER,
                    IsHls                   INTEGER NOT NULL DEFAULT 0,

                    Rating                  INTEGER,
                    PlayCount               INTEGER NOT NULL DEFAULT 0,
                    IsIgnored               INTEGER NOT NULL DEFAULT 0,

                    VolumeAdjustment        INTEGER NOT NULL DEFAULT 0,
                    EqPreset                TEXT,
                    StartTime               INTEGER,
                    StopTime                INTEGER,
                    UseStartTime            INTEGER NOT NULL DEFAULT 0,
                    UseStopTime             INTEGER NOT NULL DEFAULT 0,

                    DiscId                  TEXT,
                    LastPositionMs          INTEGER NOT NULL DEFAULT 0,
                    ReplayGainTrackGain     REAL
                )
                """;
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE INDEX IF NOT EXISTS IX_Media_Kind ON Media(Kind);
                CREATE INDEX IF NOT EXISTS IX_Media_Kind_Source ON Media(Kind, Source);
                CREATE INDEX IF NOT EXISTS IX_Media_Title ON Media(Title);
                CREATE INDEX IF NOT EXISTS IX_Media_Artist ON Media(Artist);
                """;
            cmd.ExecuteNonQuery();
        }


        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS Playlists (
                    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name        TEXT NOT NULL,
                    Source      TEXT NOT NULL DEFAULT 'Library',
                    CreatedAt   TEXT NOT NULL,
                    UpdatedAt   TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS PlaylistTracks (
                    PlaylistId  INTEGER NOT NULL,
                    MediaId     TEXT NOT NULL,
                    SortOrder   INTEGER NOT NULL,
                    AddedAt     TEXT NOT NULL,
                    PRIMARY KEY (PlaylistId, MediaId),
                    FOREIGN KEY (PlaylistId) REFERENCES Playlists(Id) ON DELETE CASCADE,
                    FOREIGN KEY (MediaId) REFERENCES Media(Id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS IX_PlaylistTracks_PlaylistId ON PlaylistTracks(PlaylistId);

                CREATE TABLE IF NOT EXISTS CdMetadataCache (
                    DiscId      TEXT PRIMARY KEY,
                    ReleaseMbid TEXT,
                    Artist      TEXT,
                    Album       TEXT,
                    Year        INTEGER,
                    TracksJson  TEXT,
                    CoverArt    BLOB,
                    Genre       TEXT,
                    CachedAt    TEXT NOT NULL
                );

                -- User state for BUNDLED radio stations. The catalogue itself reloads fresh
                -- from the embedded JSON every launch (never persisted; the startup purge
                -- deletes any stray non-user radio rows), so favorites / play counts /
                -- renames live here, keyed by the station's stable catalogue id, and are
                -- re-applied over the freshly loaded list. User-added stations ('user'
                -- Source) keep their real Media rows and never touch this table.
                CREATE TABLE IF NOT EXISTS RadioState (
                    Id            TEXT PRIMARY KEY,
                    IsFavorite    INTEGER NOT NULL DEFAULT 0,
                    PlayCount     INTEGER NOT NULL DEFAULT 0,
                    LastPlayed    TEXT,
                    TitleOverride TEXT
                );

                """;
            cmd.ExecuteNonQuery();
        }

        MigrateOldTables(connection);
        MigrateAddColumns(connection);
        MigrateAddCdCacheColumns(connection);
        MigrateAddPlaylistColumns(connection);
        PurgeOrphanedPlaylistTracks(connection);
    }

    /// <summary>
    /// PlaylistTracks declares ON DELETE CASCADE, but SQLite only
    /// honors it when <c>PRAGMA foreign_keys</c> is on for the deleting connection - which it
    /// historically wasn't, so every RemoveLibraryFiles left ghost membership rows behind
    /// forever. The delete paths now clean up in-transaction; this sweeps the ghosts already
    /// accumulated (and any a future path forgets). Cheap: the table is small and the subquery
    /// hits Media's primary key.
    /// </summary>
    private static void PurgeOrphanedPlaylistTracks(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM PlaylistTracks WHERE MediaId NOT IN (SELECT Id FROM Media)";
        var purged = cmd.ExecuteNonQuery();
        if (purged > 0)
        {
            _log.Information("Purged {Count} orphaned playlist membership row(s)", purged);
        }
    }

    /// <summary>
    /// Idempotent column additions for the <c>Playlists</c> table. Existing rows pick up
    /// the default 'Library' source.
    /// </summary>
    private static void MigrateAddPlaylistColumns(SqliteConnection connection)
    {
        var columns = new[]
        {
            "Source TEXT NOT NULL DEFAULT 'Library'",
        };

        AddMissingColumns(connection, "Playlists", columns);
    }

    private static void MigrateOldTables(SqliteConnection connection)
    {
        var hasAudioFiles = TableExists(connection, "AudioFiles");
        var hasRadioStations = TableExists(connection, "RadioStations");

        if (!hasAudioFiles && !hasRadioStations)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();

        if (hasAudioFiles)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO Media
                    (Id, Kind, Title, Artist, Album, Duration, DateAdded,
                     FilePath, FileName, Extension, FileSize, LastModified,
                     Year, Track, TotalTracks, Disc, TotalDiscs,
                     HasAlbumArt, FileNameMatchesHeaders, MimeType, Issues)
                SELECT
                    FilePath, 'Music', Title, Artist, Album, Duration, COALESCE(LastModified, datetime('now')),
                    FilePath, FileName, Extension, FileSize, LastModified,
                    Year, Track, TotalTracks, Disc, TotalDiscs,
                    HasAlbumArt, FileNameMatchesHeaders, MimeType, Issues
                FROM AudioFiles
                """;
            cmd.ExecuteNonQuery();

            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE AudioFiles";
            drop.ExecuteNonQuery();
        }

        if (hasRadioStations)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO Media
                    (Id, Kind, Title, DateAdded, IsFavorite, LastPlayed,
                     StreamUrl, Source, SourceId, HomepageUrl, FaviconUrl,
                     Country, CountryCode, Tags, Codec, Bitrate,
                     Votes, ClickCount, IsHls)
                SELECT
                    CASE
                        WHEN Source = 'radiobrowser' THEN 'rb:' || SourceId
                        ELSE 'user:' || Id
                    END,
                    'Radio', Name, DateAdded, IsFavorite, LastPlayed,
                    StreamUrl, Source, SourceId, HomepageUrl, FaviconUrl,
                    Country, CountryCode, Tags, Codec, Bitrate,
                    Votes, ClickCount, IsHls
                FROM RadioStations
                """;
            cmd.ExecuteNonQuery();

            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE RadioStations";
            drop.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>The column names a table actually has, per PRAGMA table_info.</summary>
    private static HashSet<string> ExistingColumns(SqliteConnection connection, string table)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(1));
        }
        return names;
    }

    /// <summary>
    /// Adds only the columns a table is actually missing. The old shape - attempt every
    /// historical ALTER and swallow the SqliteException - also swallowed real failures
    /// (locked file, read-only db, corruption), which then surfaced as a crash deep in
    /// ReadMediaItem instead of a clear one here.
    /// </summary>
    private static void AddMissingColumns(SqliteConnection connection, string table, string[] columns)
    {
        var existing = ExistingColumns(connection, table);
        foreach (var col in columns)
        {
            var name = col[..col.IndexOf(' ')];
            if (existing.Contains(name))
            {
                continue;
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {col}";
            cmd.ExecuteNonQuery();
        }
    }

    private static void MigrateAddColumns(SqliteConnection connection)
    {
        var columns = new[]
        {
            "Rating INTEGER",
            "PlayCount INTEGER NOT NULL DEFAULT 0",
            "Genre TEXT",
            "Composer TEXT",
            "Comment TEXT",
            "BPM INTEGER",
            "AudioBitrate INTEGER",
            "SampleRate INTEGER",
            "BitDepth INTEGER",
            "AudioChannels INTEGER",
            "EncoderSettings TEXT",
            "CodecDescription TEXT",
            "VolumeAdjustment INTEGER NOT NULL DEFAULT 0",
            "EqPreset TEXT",
            "StartTime INTEGER",
            "StopTime INTEGER",
            "UseStartTime INTEGER NOT NULL DEFAULT 0",
            "UseStopTime INTEGER NOT NULL DEFAULT 0",
            "IsIgnored INTEGER NOT NULL DEFAULT 0",
            "DiscId TEXT",
            "LastPositionMs INTEGER NOT NULL DEFAULT 0",
            "ReplayGainTrackGain REAL",
        };

        AddMissingColumns(connection, "Media", columns);
    }

    /// <summary>
    /// Idempotent column additions for the <c>CdMetadataCache</c> table.
    /// New fields land here so older rows pick up nulls and get backfilled on
    /// the next disc scan (CdAudioService treats null Genre as "go re-fetch").
    /// </summary>
    private static void MigrateAddCdCacheColumns(SqliteConnection connection)
    {
        var columns = new[]
        {
            "Genre TEXT",
            // 0 = pre-versioned row: re-fetches once, then records the version it settled at.
            "LookupVersion INTEGER NOT NULL DEFAULT 0",
        };

        AddMissingColumns(connection, "CdMetadataCache", columns);
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@Name";
        cmd.Parameters.AddWithValue("@Name", tableName);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    // -- Load everything once at startup --

    public static List<MediaItem> LoadAll()
    {
        var result = new List<MediaItem>();

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Media";

        using var reader = cmd.ExecuteReader();

        // Name→ordinal resolved ONCE per reader: SqliteDataReader.GetOrdinal scans column
        // names linearly per call, and ~58 columns × ~58 reads × every row made a big
        // library's load quadratic in string compares.
        var ordinals = new Dictionary<string, int>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            ordinals[reader.GetName(i)] = i;
        }

        while (reader.Read())
        {
            result.Add(ReadMediaItem(reader, ordinals));
        }

        return result;
    }

    // -- Music operations --

    public static void UpsertMusic(MediaItem item)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        ExecuteUpsertMedia(connection, item);
    }

    /// <summary>
    /// Batch upsert: one connection, one transaction, one journal fsync for the whole
    /// set. The per-item overload autocommits, which is fine for a dialog save but costs
    /// 25k journal flushes on a 25k-track first scan. Same shape as UpsertRadioStations.
    /// </summary>
    public static void UpsertMusicBatch(IReadOnlyList<MediaItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();
        foreach (var item in items)
        {
            ExecuteUpsertMedia(connection, item);
        }
        transaction.Commit();
    }

    /// <summary>
    /// Persists an audiobook's live playback position - the resume point. Owned SOLELY by the
    /// playback throttle: the general upsert never touches this column, so a re-analysis writing
    /// a fresh item can't clobber a live position with zero.
    /// </summary>
    public static void UpdatePlaybackPosition(string id, long positionMs)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Media SET LastPositionMs = @Pos WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Pos", positionMs);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Caches a track's computed ReplayGain (dB) after the background pass tags the file - so the
    /// cache row agrees with the file's new tag without a re-scan. Owned by the analysis pass, like
    /// the playback position above.
    /// </summary>
    public static void UpdateReplayGain(string id, double gainDb)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Media SET ReplayGainTrackGain = @Gain WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Gain", gainDb);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Removes local library rows (music + audiobooks) by id. The Kind guard keeps an id
    /// collision from ever deleting a Radio row - only local-file kinds are eligible.
    /// </summary>
    public static void RemoveLibraryFiles(IEnumerable<string> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();

        foreach (var id in idList)
        {
            // The schema's ON DELETE CASCADE never fires (foreign_keys is off on this
            // connection), so playlist memberships are cleaned up explicitly - same
            // pattern as IgnoreMedia. Without this, deleted tracks haunt playlists as
            // invisible ghost rows forever. The EXISTS mirrors the Kind guard below: an
            // id that won't be deleted must not lose its membership either.
            using var cleanup = connection.CreateCommand();
            cleanup.CommandText = """
                DELETE FROM PlaylistTracks WHERE MediaId = @Id
                    AND EXISTS (SELECT 1 FROM Media WHERE Id = @Id AND Kind IN ('Music', 'Audiobook'))
                """;
            cleanup.Parameters.AddWithValue("@Id", id);
            cleanup.ExecuteNonQuery();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Media WHERE Id = @Id AND Kind IN ('Music', 'Audiobook')";
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    // -- Radio operations --

    public static void UpsertRadioStations(IEnumerable<MediaItem> stations)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();

        foreach (var station in stations)
        {
            ExecuteUpsertMedia(connection, station);
        }

        transaction.Commit();
    }

    public static void SetFavorite(string id, bool isFavorite)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Media SET IsFavorite = @IsFavorite WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@IsFavorite", isFavorite ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public static void IncrementPlayCount(string id)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Media SET PlayCount = PlayCount + 1 WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.ExecuteNonQuery();
    }

    public static void SetRating(string id, int? rating)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Media SET Rating = @Rating WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Rating", rating.HasValue ? (object)rating.Value : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Sets the iTunes-style checked flag (stored inverted in the legacy IsIgnored column).
    /// Flag only: playlist memberships are untouched, because unticking a song means
    /// "skip it for now", not "remove it from my playlists". Survives rescans, since
    /// the scanner's UPSERT preserves the column.
    /// </summary>
    public static void SetIgnored(string id, bool ignored)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Media SET IsIgnored = @Ignored WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Ignored", ignored ? 1 : 0);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.ExecuteNonQuery();
    }

    // IgnoreMedia / RestoreMedia lived here until the Ignored view was replaced by the
    // iTunes-style row tick (0.9.16). Nothing had called them since, and their semantics
    // contradicted the live SetIgnored above: ignoring purged the track from every playlist
    // it belonged to, and restoring couldn't put it back.

    public static void SetLastPlayed(string id, DateTime lastPlayed)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Media SET LastPlayed = @LastPlayed WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@LastPlayed", lastPlayed.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Deletes a user-added station's row (bundled stations have no row to delete -
    /// their scope guard is the Source clause). True when a row actually went.
    /// </summary>
    public static bool RemoveUserStation(string id)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Media WHERE Id = @Id AND Kind = 'Radio' AND Source = 'user'";
        cmd.Parameters.AddWithValue("@Id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ── Bundled-station user state ────────────────────────────

    /// <summary>One bundled station's persisted user state - see the RadioState DDL comment.</summary>
    public sealed record RadioUserState(bool IsFavorite, int PlayCount, DateTime? LastPlayed, string? TitleOverride);

    public static Dictionary<string, RadioUserState> LoadRadioState()
    {
        var result = new Dictionary<string, RadioUserState>(StringComparer.Ordinal);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, IsFavorite, PlayCount, LastPlayed, TitleOverride FROM RadioState";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var lastPlayed = reader.IsDBNull(3) ? (DateTime?)null
                : DateTime.TryParse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind, out var lp) ? lp : null;
            result[reader.GetString(0)] = new RadioUserState(
                reader.GetInt64(1) != 0,
                (int)reader.GetInt64(2),
                lastPlayed,
                reader.IsDBNull(4) ? null : reader.GetString(4));
        }

        return result;
    }

    public static void SetRadioFavorite(string id, bool isFavorite)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO RadioState (Id, IsFavorite) VALUES (@Id, @Fav)
            ON CONFLICT(Id) DO UPDATE SET IsFavorite = @Fav
            """;
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Fav", isFavorite ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public static void BumpRadioPlay(string id, DateTime lastPlayed)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO RadioState (Id, PlayCount, LastPlayed) VALUES (@Id, 1, @Lp)
            ON CONFLICT(Id) DO UPDATE SET PlayCount = RadioState.PlayCount + 1, LastPlayed = @Lp
            """;
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Lp", lastPlayed.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Null or blank clears the override, falling back to the catalogue name.</summary>
    public static void SetRadioTitle(string id, string? titleOverride)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO RadioState (Id, TitleOverride) VALUES (@Id, @Title)
            ON CONFLICT(Id) DO UPDATE SET TitleOverride = @Title
            """;
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Title", string.IsNullOrWhiteSpace(titleOverride) ? DBNull.Value : titleOverride);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Drops every non-user radio row. Bundled stations live in memory only -
    /// anything in SQLite that isn't <c>Source = 'user'</c> is stale (legacy
    /// radio-browser/SHOUTcast sync rows, or bundled rows from when we
    /// briefly persisted the curated list). NULL Source counts as legacy too.
    /// User state for bundled stations survives in RadioState, which this
    /// purge never touches.
    /// </summary>
    public static int RemoveLegacyRadioSources()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Media WHERE Kind = 'Radio' AND (Source IS NULL OR Source <> 'user')";
        return cmd.ExecuteNonQuery();
    }

    // -- Internal helpers --

    private static void ExecuteUpsertMedia(SqliteConnection connection, MediaItem item)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Media
                (Id, Kind, Title, Artist, Album, Duration, IsFavorite, LastPlayed, DateAdded,
                 FilePath, FileName, Extension, FileSize, LastModified,
                 Year, Track, TotalTracks, Disc, TotalDiscs, DiscId,
                 HasAlbumArt, FileNameMatchesHeaders, MimeType,
                 Genre, Composer, Comment, BPM, AudioBitrate, SampleRate, BitDepth, AudioChannels,
                 EncoderSettings, CodecDescription, ReplayGainTrackGain,
                 Issues,
                 StreamUrl, Source, SourceId, HomepageUrl, FaviconUrl,
                 Country, CountryCode, Tags, Codec, Bitrate,
                 Votes, ClickCount, IsHls,
                 Rating, PlayCount,
                 VolumeAdjustment, EqPreset, StartTime, StopTime, UseStartTime, UseStopTime)
            VALUES
                (@Id, @Kind, @Title, @Artist, @Album, @Duration, @IsFavorite, @LastPlayed, @DateAdded,
                 @FilePath, @FileName, @Extension, @FileSize, @LastModified,
                 @Year, @Track, @TotalTracks, @Disc, @TotalDiscs, @DiscId,
                 @HasAlbumArt, @FileNameMatchesHeaders, @MimeType,
                 @Genre, @Composer, @Comment, @BPM, @AudioBitrate, @SampleRate, @BitDepth, @AudioChannels,
                 @EncoderSettings, @CodecDescription, @ReplayGainTrackGain,
                 @Issues,
                 @StreamUrl, @Source, @SourceId, @HomepageUrl, @FaviconUrl,
                 @Country, @CountryCode, @Tags, @Codec, @Bitrate,
                 @Votes, @ClickCount, @IsHls,
                 @Rating, @PlayCount,
                 @VolumeAdjustment, @EqPreset, @StartTime, @StopTime, @UseStartTime, @UseStopTime)
            ON CONFLICT(Id) DO UPDATE SET
                -- Deliberately NOT updated on conflict: IsFavorite, LastPlayed, DateAdded,
                -- IsIgnored, and PlayCount. Those are the user's history, owned by their
                -- dedicated setters (SetFavorite / SetLastPlayed / SetIgnored /
                -- IncrementPlayCount) - a rescan's upsert carrying stale copies must never
                -- win a race against them. Rating and the Options-tab columns DO update:
                -- the MediaInfo dialog persists them through this upsert, and the scan path
                -- adopts the user's values onto its items first (MediaItem.AdoptUserStateFrom).
                Title = excluded.Title,
                Artist = excluded.Artist,
                Album = excluded.Album,
                Duration = excluded.Duration,
                FilePath = excluded.FilePath,
                FileName = excluded.FileName,
                Extension = excluded.Extension,
                FileSize = excluded.FileSize,
                LastModified = excluded.LastModified,
                Year = excluded.Year,
                Track = excluded.Track,
                TotalTracks = excluded.TotalTracks,
                Disc = excluded.Disc,
                TotalDiscs = excluded.TotalDiscs,
                DiscId = excluded.DiscId,
                HasAlbumArt = excluded.HasAlbumArt,
                FileNameMatchesHeaders = excluded.FileNameMatchesHeaders,
                MimeType = excluded.MimeType,
                Genre = excluded.Genre,
                Composer = excluded.Composer,
                Comment = excluded.Comment,
                BPM = excluded.BPM,
                AudioBitrate = excluded.AudioBitrate,
                SampleRate = excluded.SampleRate,
                BitDepth = excluded.BitDepth,
                AudioChannels = excluded.AudioChannels,
                EncoderSettings = excluded.EncoderSettings,
                CodecDescription = excluded.CodecDescription,
                ReplayGainTrackGain = excluded.ReplayGainTrackGain,
                Issues = excluded.Issues,
                StreamUrl = excluded.StreamUrl,
                Source = excluded.Source,
                SourceId = excluded.SourceId,
                HomepageUrl = excluded.HomepageUrl,
                FaviconUrl = excluded.FaviconUrl,
                Country = excluded.Country,
                CountryCode = excluded.CountryCode,
                Tags = excluded.Tags,
                Codec = excluded.Codec,
                Bitrate = excluded.Bitrate,
                Votes = excluded.Votes,
                ClickCount = excluded.ClickCount,
                IsHls = excluded.IsHls,
                Rating = excluded.Rating,
                VolumeAdjustment = excluded.VolumeAdjustment,
                EqPreset = excluded.EqPreset,
                StartTime = excluded.StartTime,
                StopTime = excluded.StopTime,
                UseStartTime = excluded.UseStartTime,
                UseStopTime = excluded.UseStopTime
            """;

        cmd.Parameters.AddWithValue("@Id", item.Id);
        cmd.Parameters.AddWithValue("@Kind", item.Kind.ToString());
        cmd.Parameters.AddWithValue("@Title", (object?)item.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Artist", (object?)item.Artist ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Album", (object?)item.Album ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Duration", item.Duration.HasValue ? (object)item.Duration.Value.Ticks : DBNull.Value);
        cmd.Parameters.AddWithValue("@IsFavorite", item.IsFavorite ? 1 : 0);
        cmd.Parameters.AddWithValue("@LastPlayed", item.LastPlayed.HasValue ? (object)item.LastPlayed.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("@DateAdded", item.DateAdded.ToString("O"));

        cmd.Parameters.AddWithValue("@FilePath", (object?)item.FilePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FileName", (object?)item.FileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Extension", (object?)item.Extension ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FileSize", item.FileSize.HasValue ? (object)item.FileSize.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@LastModified", item.LastModified.HasValue ? (object)item.LastModified.Value.ToString("O") : DBNull.Value);

        cmd.Parameters.AddWithValue("@Year", item.Year.HasValue ? (object)(long)item.Year.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@Track", item.Track.HasValue ? (object)(long)item.Track.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@TotalTracks", item.TotalTracks.HasValue ? (object)(long)item.TotalTracks.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@Disc", item.Disc.HasValue ? (object)(long)item.Disc.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@TotalDiscs", item.TotalDiscs.HasValue ? (object)(long)item.TotalDiscs.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@DiscId", (object?)item.DiscId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@HasAlbumArt", item.HasAlbumArt.HasValue ? (object)(item.HasAlbumArt.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("@FileNameMatchesHeaders", item.FileNameMatchesHeaders.HasValue ? (object)(item.FileNameMatchesHeaders.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("@MimeType", (object?)item.MimeType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Genre", (object?)item.Genre ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Composer", (object?)item.Composer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Comment", (object?)item.Comment ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@BPM", item.Bpm.HasValue && item.Bpm.Value > 0 ? (object)(long)item.Bpm.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@AudioBitrate", item.AudioBitrate.HasValue ? (object)item.AudioBitrate.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@SampleRate", item.SampleRate.HasValue ? (object)item.SampleRate.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@BitDepth", item.BitDepth.HasValue ? (object)item.BitDepth.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@AudioChannels", item.AudioChannels.HasValue ? (object)item.AudioChannels.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@EncoderSettings", (object?)item.EncoderSettings ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CodecDescription", (object?)item.CodecDescription ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ReplayGainTrackGain", item.ReplayGainTrackGainDb.HasValue ? (object)item.ReplayGainTrackGainDb.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@Issues", item.Issues.Count > 0 ? JsonSerializer.Serialize(item.Issues) : DBNull.Value);

        cmd.Parameters.AddWithValue("@StreamUrl", (object?)item.StreamUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Source", (object?)item.Source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SourceId", (object?)item.SourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@HomepageUrl", (object?)item.HomepageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FaviconUrl", (object?)item.FaviconUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Country", (object?)item.Country ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CountryCode", (object?)item.CountryCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Tags", (object?)item.Tags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Codec", (object?)item.Codec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Bitrate", item.Bitrate.HasValue ? (object)item.Bitrate.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@Votes", item.Votes.HasValue ? (object)item.Votes.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@ClickCount", item.ClickCount.HasValue ? (object)item.ClickCount.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@IsHls", item.IsHls ? 1 : 0);
        cmd.Parameters.AddWithValue("@Rating", item.Rating.HasValue ? (object)item.Rating.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@PlayCount", item.PlayCount);
        cmd.Parameters.AddWithValue("@VolumeAdjustment", item.VolumeAdjustment);
        cmd.Parameters.AddWithValue("@EqPreset", (object?)item.EqPreset ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@StartTime", item.StartTime.HasValue ? (object)item.StartTime.Value.Ticks : DBNull.Value);
        cmd.Parameters.AddWithValue("@StopTime", item.StopTime.HasValue ? (object)item.StopTime.Value.Ticks : DBNull.Value);
        cmd.Parameters.AddWithValue("@UseStartTime", item.UseStartTime ? 1 : 0);
        cmd.Parameters.AddWithValue("@UseStopTime", item.UseStopTime ? 1 : 0);

        cmd.ExecuteNonQuery();
    }

    private static MediaItem ReadMediaItem(SqliteDataReader reader, Dictionary<string, int> o)
    {
        var kind = Enum.Parse<MediaKind>(reader.GetString(o["Kind"]));

        var item = new MediaItem
        {
            Id = reader.GetString(o["Id"]),
            Kind = kind,
            DateAdded = DateTime.Parse(reader.GetString(o["DateAdded"]), null, System.Globalization.DateTimeStyles.RoundtripKind),

            // Music-only
            FilePath = GetNullableString(reader, o, "FilePath"),
            FileName = GetNullableString(reader, o, "FileName"),
            Extension = GetNullableString(reader, o, "Extension"),
            FileSize = GetNullableLong(reader, o, "FileSize"),
            LastModified = GetNullableDateTime(reader, o, "LastModified"),

            // Radio-only
            StreamUrl = GetNullableString(reader, o, "StreamUrl"),
            Source = GetNullableString(reader, o, "Source"),
            SourceId = GetNullableString(reader, o, "SourceId"),
            HomepageUrl = GetNullableString(reader, o, "HomepageUrl"),
            FaviconUrl = GetNullableString(reader, o, "FaviconUrl"),
            Country = GetNullableString(reader, o, "Country"),
            CountryCode = GetNullableString(reader, o, "CountryCode"),
            Tags = GetNullableString(reader, o, "Tags"),
            Codec = GetNullableString(reader, o, "Codec"),
            Bitrate = GetNullableInt(reader, o, "Bitrate"),
            Votes = GetNullableInt(reader, o, "Votes"),
            ClickCount = GetNullableInt(reader, o, "ClickCount"),
            IsHls = reader.GetInt32(o["IsHls"]) != 0,
        };

        // Mutable shared properties
        item.Title = GetNullableString(reader, o, "Title");
        item.Artist = GetNullableString(reader, o, "Artist");
        item.Album = GetNullableString(reader, o, "Album");
        item.IsFavorite = reader.GetInt32(o["IsFavorite"]) != 0;

        var durationOrd = o["Duration"];
        item.Duration = reader.IsDBNull(durationOrd) ? null : TimeSpan.FromTicks(reader.GetInt64(durationOrd));

        var lastPlayedOrd = o["LastPlayed"];
        item.LastPlayed = reader.IsDBNull(lastPlayedOrd) ? null : DateTime.Parse(reader.GetString(lastPlayedOrd), null, System.Globalization.DateTimeStyles.RoundtripKind);

        // Music-only mutable
        item.Year = GetNullableUint(reader, o, "Year");
        item.Track = GetNullableUint(reader, o, "Track");
        item.TotalTracks = GetNullableUint(reader, o, "TotalTracks");
        item.Disc = GetNullableUint(reader, o, "Disc");
        item.TotalDiscs = GetNullableUint(reader, o, "TotalDiscs");
        item.DiscId = GetNullableString(reader, o, "DiscId");
        item.HasAlbumArt = GetNullableBool(reader, o, "HasAlbumArt");
        item.FileNameMatchesHeaders = GetNullableBool(reader, o, "FileNameMatchesHeaders");
        item.MimeType = GetNullableString(reader, o, "MimeType");
        item.Genre = GetNullableString(reader, o, "Genre");
        item.Composer = GetNullableString(reader, o, "Composer");
        item.Comment = GetNullableString(reader, o, "Comment");
        item.Bpm = GetNullableUint(reader, o, "BPM");
        item.AudioBitrate = GetNullableInt(reader, o, "AudioBitrate");
        item.SampleRate = GetNullableInt(reader, o, "SampleRate");
        item.BitDepth = GetNullableInt(reader, o, "BitDepth");
        item.AudioChannels = GetNullableInt(reader, o, "AudioChannels");
        item.EncoderSettings = GetNullableString(reader, o, "EncoderSettings");
        item.CodecDescription = GetNullableString(reader, o, "CodecDescription");
        var rgOrd = o["ReplayGainTrackGain"];
        item.ReplayGainTrackGainDb = reader.IsDBNull(rgOrd) ? null : reader.GetDouble(rgOrd);

        var issuesOrd = o["Issues"];
        if (!reader.IsDBNull(issuesOrd))
        {
            var issues = JsonSerializer.Deserialize<List<string>>(reader.GetString(issuesOrd));
            if (issues != null)
            {
                foreach (var issue in issues)
                {
                    item.Issues.Add(issue);
                }
            }
        }

        item.Rating = GetNullableInt(reader, o, "Rating");
        item.PlayCount = reader.GetInt32(o["PlayCount"]);
        item.IsIgnored = (GetNullableInt(reader, o, "IsIgnored") ?? 0) != 0;

        item.VolumeAdjustment = GetNullableInt(reader, o, "VolumeAdjustment") ?? 0;
        item.EqPreset = GetNullableString(reader, o, "EqPreset");

        var startTimeOrd = o["StartTime"];
        item.StartTime = reader.IsDBNull(startTimeOrd) ? null : TimeSpan.FromTicks(reader.GetInt64(startTimeOrd));

        var stopTimeOrd = o["StopTime"];
        item.StopTime = reader.IsDBNull(stopTimeOrd) ? null : TimeSpan.FromTicks(reader.GetInt64(stopTimeOrd));

        item.UseStartTime = (GetNullableInt(reader, o, "UseStartTime") ?? 0) != 0;
        item.UseStopTime = (GetNullableInt(reader, o, "UseStopTime") ?? 0) != 0;
        item.LastPositionMs = GetNullableLong(reader, o, "LastPositionMs") ?? 0;

        // A cached local file (music or audiobook) was analyzed before it was saved - mark it so
        // the scan's delta logic doesn't re-run TagLib on unchanged files.
        if (kind is MediaKind.Music or MediaKind.Audiobook)
        {
            item.IsAnalyzed = true;
        }

        return item;
    }

    private static string? GetNullableString(SqliteDataReader reader, Dictionary<string, int> o, string column)
    {
        var ord = o[column];
        return reader.IsDBNull(ord) ? null : reader.GetString(ord);
    }

    private static long? GetNullableLong(SqliteDataReader reader, Dictionary<string, int> o, string column)
    {
        var ord = o[column];
        return reader.IsDBNull(ord) ? null : reader.GetInt64(ord);
    }

    private static int? GetNullableInt(SqliteDataReader reader, Dictionary<string, int> o, string column)
    {
        var ord = o[column];
        return reader.IsDBNull(ord) ? null : reader.GetInt32(ord);
    }

    private static uint? GetNullableUint(SqliteDataReader reader, Dictionary<string, int> o, string column)
    {
        var ord = o[column];
        return reader.IsDBNull(ord) ? null : (uint)reader.GetInt64(ord);
    }

    private static bool? GetNullableBool(SqliteDataReader reader, Dictionary<string, int> o, string column)
    {
        var ord = o[column];
        return reader.IsDBNull(ord) ? null : reader.GetInt32(ord) != 0;
    }

    private static DateTime? GetNullableDateTime(SqliteDataReader reader, Dictionary<string, int> o, string column)
    {
        var ord = o[column];
        return reader.IsDBNull(ord) ? null : DateTime.Parse(reader.GetString(ord), null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    #region Playlists

    public static int CreatePlaylist(string name, string source = "Library")
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        var now = DateTime.UtcNow.ToString("o");
        cmd.CommandText = "INSERT INTO Playlists (Name, Source, CreatedAt, UpdatedAt) VALUES (@name, @source, @now, @now); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@source", string.IsNullOrWhiteSpace(source) ? "Library" : source);
        cmd.Parameters.AddWithValue("@now", now);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Returns a playlist's <c>Source</c> ("Library", "M3U8", ...), or "Library" if unknown.</summary>
    public static string GetPlaylistSource(int id)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Source FROM Playlists WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() is string s && !string.IsNullOrWhiteSpace(s) ? s : "Library";
    }

    public static void RenamePlaylist(int id, string newName)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Playlists SET Name = @name, UpdatedAt = @now WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", newName);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public static void DeletePlaylist(int id)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        // Enable foreign keys for CASCADE
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Playlists WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public static List<Playlist> LoadAllPlaylists()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Source, CreatedAt, UpdatedAt FROM Playlists ORDER BY Name";

        var playlists = new List<Playlist>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            playlists.Add(new Playlist
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Source = reader.IsDBNull(2) ? "Library" : reader.GetString(2),
                CreatedAt = DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
                UpdatedAt = DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
            });
        }

        return playlists;
    }

    public static void AddTrackToPlaylist(int playlistId, string mediaId)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        // Get next sort order
        using var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM PlaylistTracks WHERE PlaylistId = @pid";
        countCmd.Parameters.AddWithValue("@pid", playlistId);
        var nextSort = Convert.ToInt32(countCmd.ExecuteScalar());

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO PlaylistTracks (PlaylistId, MediaId, SortOrder, AddedAt) VALUES (@pid, @mid, @sort, @now)";
        cmd.Parameters.AddWithValue("@pid", playlistId);
        cmd.Parameters.AddWithValue("@mid", mediaId);
        cmd.Parameters.AddWithValue("@sort", nextSort);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();

        // Update playlist timestamp
        using var upd = connection.CreateCommand();
        upd.CommandText = "UPDATE Playlists SET UpdatedAt = @now WHERE Id = @pid";
        upd.Parameters.AddWithValue("@pid", playlistId);
        upd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        upd.ExecuteNonQuery();
    }

    public static void RemoveTrackFromPlaylist(int playlistId, string mediaId)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM PlaylistTracks WHERE PlaylistId = @pid AND MediaId = @mid";
        cmd.Parameters.AddWithValue("@pid", playlistId);
        cmd.Parameters.AddWithValue("@mid", mediaId);
        cmd.ExecuteNonQuery();
    }

    public static List<string> GetPlaylistTrackIds(int playlistId)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MediaId FROM PlaylistTracks WHERE PlaylistId = @pid ORDER BY SortOrder";
        cmd.Parameters.AddWithValue("@pid", playlistId);

        var ids = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    public static void ReorderPlaylistTracks(int playlistId, List<string> orderedMediaIds)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var tx = connection.BeginTransaction();

        for (int i = 0; i < orderedMediaIds.Count; i++)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE PlaylistTracks SET SortOrder = @sort WHERE PlaylistId = @pid AND MediaId = @mid";
            cmd.Parameters.AddWithValue("@sort", i);
            cmd.Parameters.AddWithValue("@pid", playlistId);
            cmd.Parameters.AddWithValue("@mid", orderedMediaIds[i]);
            cmd.ExecuteNonQuery();
        }

        using var upd = connection.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE Playlists SET UpdatedAt = @now WHERE Id = @pid";
        upd.Parameters.AddWithValue("@pid", playlistId);
        upd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        upd.ExecuteNonQuery();

        tx.Commit();
    }

    #endregion

    #region CD Metadata Cache

    public static CachedCdMetadata? GetCdMetadata(string discId)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ReleaseMbid, Artist, Album, Year, TracksJson, CoverArt, Genre, LookupVersion FROM CdMetadataCache WHERE DiscId = @id";
        cmd.Parameters.AddWithValue("@id", discId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new CachedCdMetadata
        {
            DiscId = discId,
            ReleaseMbid = reader.IsDBNull(0) ? null : reader.GetString(0),
            Artist = reader.IsDBNull(1) ? null : reader.GetString(1),
            Album = reader.IsDBNull(2) ? null : reader.GetString(2),
            Year = reader.IsDBNull(3) ? null : (uint?)reader.GetInt32(3),
            TracksJson = reader.IsDBNull(4) ? null : reader.GetString(4),
            CoverArt = reader.IsDBNull(5) ? null : (byte[])reader[5],
            Genre = reader.IsDBNull(6) ? null : reader.GetString(6),
            LookupVersion = reader.IsDBNull(7) ? 0 : (int)reader.GetInt64(7),
        };
    }

    public static void SaveCdMetadata(CachedCdMetadata meta)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO CdMetadataCache
                (DiscId, ReleaseMbid, Artist, Album, Year, TracksJson, CoverArt, Genre, LookupVersion, CachedAt)
            VALUES
                (@id, @mbid, @artist, @album, @year, @tracks, @art, @genre, @lookupVersion, @now)
            """;
        cmd.Parameters.AddWithValue("@id", meta.DiscId);
        cmd.Parameters.AddWithValue("@mbid", (object?)meta.ReleaseMbid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@artist", (object?)meta.Artist ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@album", (object?)meta.Album ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@year", meta.Year.HasValue ? (object)(int)meta.Year.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@tracks", (object?)meta.TracksJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@art", (object?)meta.CoverArt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@genre", (object?)meta.Genre ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lookupVersion", meta.LookupVersion);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    #endregion
}

public class CachedCdMetadata
{
    /// <summary>
    /// The lookup pipeline that produced current rows. A row saved at this version is the
    /// settled answer even when art/genre are null (the archive genuinely has none) - only
    /// rows from an OLDER pipeline re-fetch. Bump when the lookup learns to fetch more.
    /// </summary>
    public const int CurrentLookupVersion = 2;

    public string DiscId { get; set; } = "";
    public string? ReleaseMbid { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Genre { get; set; }
    public uint? Year { get; set; }
    public string? TracksJson { get; set; }
    public byte[]? CoverArt { get; set; }
    public int LookupVersion { get; set; }
}
