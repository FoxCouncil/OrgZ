// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OrgZ.Services;

public static class MediaCache
{
    private static readonly string DefaultCacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrgZ");
    private static string CacheDirectory { get; set; } = DefaultCacheDirectory;
    private static string CacheFilePath { get; set; } = Path.Combine(DefaultCacheDirectory, "library.db");
    private static string ConnectionString => $"Data Source={CacheFilePath}";

    /// <summary>
    /// Test hook: redirect the cache to a custom file path. Pass null to restore the default location.
    /// </summary>
    internal static void OverrideCachePath(string? path)
    {
        if (path == null)
        {
            CacheDirectory = DefaultCacheDirectory;
            CacheFilePath = Path.Combine(DefaultCacheDirectory, "library.db");
        }
        else
        {
            CacheFilePath = path;
            CacheDirectory = Path.GetDirectoryName(path) ?? DefaultCacheDirectory;
        }
    }

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
                    UseStopTime             INTEGER NOT NULL DEFAULT 0
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
                CREATE TABLE IF NOT EXISTS SyncHistory (
                    Source          TEXT PRIMARY KEY,
                    LastSyncUtc     TEXT NOT NULL,
                    StationCount    INTEGER NOT NULL,
                    DurationMs      INTEGER NOT NULL
                )
                """;
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS Playlists (
                    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name        TEXT NOT NULL,
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
                    CachedAt    TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        MigrateOldTables(connection);
        MigrateAddColumns(connection);
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

    private static void MigrateAddColumns(SqliteConnection connection)
    {
        // Idempotent: ADD COLUMN throws if column already exists, which we catch
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
        };

        foreach (var col in columns)
        {
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"ALTER TABLE Media ADD COLUMN {col}";
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Column already exists
            }
        }
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
        while (reader.Read())
        {
            result.Add(ReadMediaItem(reader));
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

    public static void RemoveMusic(IEnumerable<string> ids)
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
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Media WHERE Id = @Id AND Kind = 'Music'";
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    // -- Radio operations --

    public static List<MediaItem> LoadAllRadio()
    {
        var result = new List<MediaItem>();

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Title, StreamUrl, Source, SourceId, HomepageUrl, FaviconUrl,
                   Country, CountryCode, Tags, Codec, Bitrate, Votes, ClickCount,
                   IsHls, IsFavorite, LastPlayed, DateAdded,
                   Rating, PlayCount
            FROM Media WHERE Kind = 'Radio'
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadRadioItem(reader));
        }

        return result;
    }

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
    /// Marks a media item as ignored: it disappears from normal views, is removed from every
    /// playlist it was in, and will NOT be re-added by the scanner (UPSERT preserves IsIgnored).
    /// The file itself is never touched.
    /// </summary>
    public static void IgnoreMedia(string id)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var tx = connection.BeginTransaction();

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE Media SET IsIgnored = 1 WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM PlaylistTracks WHERE MediaId = @Id";
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// Clears the ignored flag. The item will reappear in its normal views. Playlist memberships
    /// are NOT automatically restored (they were destroyed when the item was ignored).
    /// </summary>
    public static void RestoreMedia(string id)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Media SET IsIgnored = 0 WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.ExecuteNonQuery();
    }

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

    public static void RemoveRadioBySource(string source)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Media WHERE Kind = 'Radio' AND Source = @Source AND IsFavorite = 0";
        cmd.Parameters.AddWithValue("@Source", source);
        cmd.ExecuteNonQuery();
    }

    // -- Sync history --

    public static (DateTime LastSync, int StationCount, long DurationMs)? GetLastSync(string source)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT LastSyncUtc, StationCount, DurationMs FROM SyncHistory WHERE Source = @Source";
        cmd.Parameters.AddWithValue("@Source", source);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return (
                DateTime.Parse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.GetInt32(1),
                reader.GetInt64(2)
            );
        }

        return null;
    }

    public static void RecordSync(string source, int count, long durationMs)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SyncHistory (Source, LastSyncUtc, StationCount, DurationMs)
            VALUES (@Source, @LastSync, @Count, @Duration)
            ON CONFLICT(Source) DO UPDATE SET
                LastSyncUtc = excluded.LastSyncUtc,
                StationCount = excluded.StationCount,
                DurationMs = excluded.DurationMs
            """;
        cmd.Parameters.AddWithValue("@Source", source);
        cmd.Parameters.AddWithValue("@LastSync", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@Count", count);
        cmd.Parameters.AddWithValue("@Duration", durationMs);
        cmd.ExecuteNonQuery();
    }

    // -- Internal helpers --

    private static void ExecuteUpsertMedia(SqliteConnection connection, MediaItem item)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Media
                (Id, Kind, Title, Artist, Album, Duration, IsFavorite, LastPlayed, DateAdded,
                 FilePath, FileName, Extension, FileSize, LastModified,
                 Year, Track, TotalTracks, Disc, TotalDiscs,
                 HasAlbumArt, FileNameMatchesHeaders, MimeType,
                 Genre, Composer, Comment, BPM, AudioBitrate, SampleRate, AudioChannels,
                 EncoderSettings, CodecDescription,
                 Issues,
                 StreamUrl, Source, SourceId, HomepageUrl, FaviconUrl,
                 Country, CountryCode, Tags, Codec, Bitrate,
                 Votes, ClickCount, IsHls,
                 Rating, PlayCount,
                 VolumeAdjustment, EqPreset, StartTime, StopTime, UseStartTime, UseStopTime)
            VALUES
                (@Id, @Kind, @Title, @Artist, @Album, @Duration, @IsFavorite, @LastPlayed, @DateAdded,
                 @FilePath, @FileName, @Extension, @FileSize, @LastModified,
                 @Year, @Track, @TotalTracks, @Disc, @TotalDiscs,
                 @HasAlbumArt, @FileNameMatchesHeaders, @MimeType,
                 @Genre, @Composer, @Comment, @BPM, @AudioBitrate, @SampleRate, @AudioChannels,
                 @EncoderSettings, @CodecDescription,
                 @Issues,
                 @StreamUrl, @Source, @SourceId, @HomepageUrl, @FaviconUrl,
                 @Country, @CountryCode, @Tags, @Codec, @Bitrate,
                 @Votes, @ClickCount, @IsHls,
                 @Rating, @PlayCount,
                 @VolumeAdjustment, @EqPreset, @StartTime, @StopTime, @UseStartTime, @UseStopTime)
            ON CONFLICT(Id) DO UPDATE SET
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
                HasAlbumArt = excluded.HasAlbumArt,
                FileNameMatchesHeaders = excluded.FileNameMatchesHeaders,
                MimeType = excluded.MimeType,
                Genre = excluded.Genre,
                Composer = excluded.Composer,
                Comment = excluded.Comment,
                BPM = excluded.BPM,
                AudioBitrate = excluded.AudioBitrate,
                SampleRate = excluded.SampleRate,
                AudioChannels = excluded.AudioChannels,
                EncoderSettings = excluded.EncoderSettings,
                CodecDescription = excluded.CodecDescription,
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
                PlayCount = excluded.PlayCount,
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
        cmd.Parameters.AddWithValue("@HasAlbumArt", item.HasAlbumArt.HasValue ? (object)(item.HasAlbumArt.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("@FileNameMatchesHeaders", item.FileNameMatchesHeaders.HasValue ? (object)(item.FileNameMatchesHeaders.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("@MimeType", (object?)item.MimeType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Genre", (object?)item.Genre ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Composer", (object?)item.Composer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Comment", (object?)item.Comment ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@BPM", item.Bpm.HasValue && item.Bpm.Value > 0 ? (object)(long)item.Bpm.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@AudioBitrate", item.AudioBitrate.HasValue ? (object)item.AudioBitrate.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@SampleRate", item.SampleRate.HasValue ? (object)item.SampleRate.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@AudioChannels", item.AudioChannels.HasValue ? (object)item.AudioChannels.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@EncoderSettings", (object?)item.EncoderSettings ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CodecDescription", (object?)item.CodecDescription ?? DBNull.Value);
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

    private static MediaItem ReadRadioItem(SqliteDataReader reader)
    {
        var item = new MediaItem
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            Kind = MediaKind.Radio,
            DateAdded = DateTime.Parse(reader.GetString(reader.GetOrdinal("DateAdded")), null, System.Globalization.DateTimeStyles.RoundtripKind),
            StreamUrl = GetNullableString(reader, "StreamUrl"),
            Source = GetNullableString(reader, "Source"),
            SourceId = GetNullableString(reader, "SourceId"),
            HomepageUrl = GetNullableString(reader, "HomepageUrl"),
            FaviconUrl = GetNullableString(reader, "FaviconUrl"),
            Country = GetNullableString(reader, "Country"),
            CountryCode = GetNullableString(reader, "CountryCode"),
            Tags = GetNullableString(reader, "Tags"),
            Codec = GetNullableString(reader, "Codec"),
            Bitrate = GetNullableInt(reader, "Bitrate"),
            Votes = GetNullableInt(reader, "Votes"),
            ClickCount = GetNullableInt(reader, "ClickCount"),
            IsHls = reader.GetInt32(reader.GetOrdinal("IsHls")) != 0,
        };

        item.Title = GetNullableString(reader, "Title");
        item.IsFavorite = reader.GetInt32(reader.GetOrdinal("IsFavorite")) != 0;
        item.Rating = GetNullableInt(reader, "Rating");
        item.PlayCount = reader.GetInt32(reader.GetOrdinal("PlayCount"));
        item.IsIgnored = (GetNullableInt(reader, "IsIgnored") ?? 0) != 0;

        var lastPlayedOrd = reader.GetOrdinal("LastPlayed");
        item.LastPlayed = reader.IsDBNull(lastPlayedOrd) ? null : DateTime.Parse(reader.GetString(lastPlayedOrd), null, System.Globalization.DateTimeStyles.RoundtripKind);

        return item;
    }

    private static MediaItem ReadMediaItem(SqliteDataReader reader)
    {
        var kind = Enum.Parse<MediaKind>(reader.GetString(reader.GetOrdinal("Kind")));

        var item = new MediaItem
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            Kind = kind,
            DateAdded = DateTime.Parse(reader.GetString(reader.GetOrdinal("DateAdded")), null, System.Globalization.DateTimeStyles.RoundtripKind),

            // Music-only
            FilePath = GetNullableString(reader, "FilePath"),
            FileName = GetNullableString(reader, "FileName"),
            Extension = GetNullableString(reader, "Extension"),
            FileSize = GetNullableLong(reader, "FileSize"),
            LastModified = GetNullableDateTime(reader, "LastModified"),

            // Radio-only
            StreamUrl = GetNullableString(reader, "StreamUrl"),
            Source = GetNullableString(reader, "Source"),
            SourceId = GetNullableString(reader, "SourceId"),
            HomepageUrl = GetNullableString(reader, "HomepageUrl"),
            FaviconUrl = GetNullableString(reader, "FaviconUrl"),
            Country = GetNullableString(reader, "Country"),
            CountryCode = GetNullableString(reader, "CountryCode"),
            Tags = GetNullableString(reader, "Tags"),
            Codec = GetNullableString(reader, "Codec"),
            Bitrate = GetNullableInt(reader, "Bitrate"),
            Votes = GetNullableInt(reader, "Votes"),
            ClickCount = GetNullableInt(reader, "ClickCount"),
            IsHls = reader.GetInt32(reader.GetOrdinal("IsHls")) != 0,
        };

        // Mutable shared properties
        item.Title = GetNullableString(reader, "Title");
        item.Artist = GetNullableString(reader, "Artist");
        item.Album = GetNullableString(reader, "Album");
        item.IsFavorite = reader.GetInt32(reader.GetOrdinal("IsFavorite")) != 0;

        var durationOrd = reader.GetOrdinal("Duration");
        item.Duration = reader.IsDBNull(durationOrd) ? null : TimeSpan.FromTicks(reader.GetInt64(durationOrd));

        var lastPlayedOrd = reader.GetOrdinal("LastPlayed");
        item.LastPlayed = reader.IsDBNull(lastPlayedOrd) ? null : DateTime.Parse(reader.GetString(lastPlayedOrd), null, System.Globalization.DateTimeStyles.RoundtripKind);

        // Music-only mutable
        item.Year = GetNullableUint(reader, "Year");
        item.Track = GetNullableUint(reader, "Track");
        item.TotalTracks = GetNullableUint(reader, "TotalTracks");
        item.Disc = GetNullableUint(reader, "Disc");
        item.TotalDiscs = GetNullableUint(reader, "TotalDiscs");
        item.HasAlbumArt = GetNullableBool(reader, "HasAlbumArt");
        item.FileNameMatchesHeaders = GetNullableBool(reader, "FileNameMatchesHeaders");
        item.MimeType = GetNullableString(reader, "MimeType");
        item.Genre = GetNullableString(reader, "Genre");
        item.Composer = GetNullableString(reader, "Composer");
        item.Comment = GetNullableString(reader, "Comment");
        item.Bpm = GetNullableUint(reader, "BPM");
        item.AudioBitrate = GetNullableInt(reader, "AudioBitrate");
        item.SampleRate = GetNullableInt(reader, "SampleRate");
        item.AudioChannels = GetNullableInt(reader, "AudioChannels");
        item.EncoderSettings = GetNullableString(reader, "EncoderSettings");
        item.CodecDescription = GetNullableString(reader, "CodecDescription");

        var issuesOrd = reader.GetOrdinal("Issues");
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

        item.Rating = GetNullableInt(reader, "Rating");
        item.PlayCount = reader.GetInt32(reader.GetOrdinal("PlayCount"));
        item.IsIgnored = (GetNullableInt(reader, "IsIgnored") ?? 0) != 0;

        item.VolumeAdjustment = GetNullableInt(reader, "VolumeAdjustment") ?? 0;
        item.EqPreset = GetNullableString(reader, "EqPreset");

        var startTimeOrd = reader.GetOrdinal("StartTime");
        item.StartTime = reader.IsDBNull(startTimeOrd) ? null : TimeSpan.FromTicks(reader.GetInt64(startTimeOrd));

        var stopTimeOrd = reader.GetOrdinal("StopTime");
        item.StopTime = reader.IsDBNull(stopTimeOrd) ? null : TimeSpan.FromTicks(reader.GetInt64(stopTimeOrd));

        item.UseStartTime = (GetNullableInt(reader, "UseStartTime") ?? 0) != 0;
        item.UseStopTime = (GetNullableInt(reader, "UseStopTime") ?? 0) != 0;

        if (kind == MediaKind.Music)
        {
            item.IsAnalyzed = true;
        }

        return item;
    }

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : reader.GetString(ord);
    }

    private static long? GetNullableLong(SqliteDataReader reader, string column)
    {
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : reader.GetInt64(ord);
    }

    private static int? GetNullableInt(SqliteDataReader reader, string column)
    {
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : reader.GetInt32(ord);
    }

    private static uint? GetNullableUint(SqliteDataReader reader, string column)
    {
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : (uint)reader.GetInt64(ord);
    }

    private static bool? GetNullableBool(SqliteDataReader reader, string column)
    {
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : reader.GetInt32(ord) != 0;
    }

    private static DateTime? GetNullableDateTime(SqliteDataReader reader, string column)
    {
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? null : DateTime.Parse(reader.GetString(ord), null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    #region Playlists

    public static int CreatePlaylist(string name)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        var now = DateTime.UtcNow.ToString("o");
        cmd.CommandText = "INSERT INTO Playlists (Name, CreatedAt, UpdatedAt) VALUES (@name, @now, @now); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@now", now);
        return Convert.ToInt32(cmd.ExecuteScalar());
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
        cmd.CommandText = "SELECT Id, Name, CreatedAt, UpdatedAt FROM Playlists ORDER BY Name";

        var playlists = new List<Playlist>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            playlists.Add(new Playlist
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                CreatedAt = DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                UpdatedAt = DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
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
        cmd.CommandText = "SELECT ReleaseMbid, Artist, Album, Year, TracksJson, CoverArt FROM CdMetadataCache WHERE DiscId = @id";
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
        };
    }

    public static void SaveCdMetadata(CachedCdMetadata meta)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO CdMetadataCache
                (DiscId, ReleaseMbid, Artist, Album, Year, TracksJson, CoverArt, CachedAt)
            VALUES
                (@id, @mbid, @artist, @album, @year, @tracks, @art, @now)
            """;
        cmd.Parameters.AddWithValue("@id", meta.DiscId);
        cmd.Parameters.AddWithValue("@mbid", (object?)meta.ReleaseMbid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@artist", (object?)meta.Artist ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@album", (object?)meta.Album ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@year", meta.Year.HasValue ? (object)(int)meta.Year.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@tracks", (object?)meta.TracksJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@art", (object?)meta.CoverArt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    #endregion
}

public class CachedCdMetadata
{
    public string DiscId { get; set; } = "";
    public string? ReleaseMbid { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public uint? Year { get; set; }
    public string? TracksJson { get; set; }
    public byte[]? CoverArt { get; set; }
}
