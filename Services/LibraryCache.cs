// Copyright (c) 2025 Fox Diller

using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OrgZ.Services;

/// <summary>
/// SQLite-backed cache for analyzed audio file metadata
/// </summary>
public static class LibraryCache
{
    private static readonly string CacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrgZ");
    private static readonly string CacheFilePath = Path.Combine(CacheDirectory, "library.db");
    private static string ConnectionString => $"Data Source={CacheFilePath}";

    /// <summary>
    /// Creates the database and table if they don't exist
    /// </summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(CacheDirectory);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AudioFiles (
                FilePath        TEXT PRIMARY KEY,
                FileName        TEXT NOT NULL,
                Extension       TEXT NOT NULL,
                FileSize        INTEGER NOT NULL,
                LastModified    TEXT NOT NULL,
                Artist          TEXT,
                Album           TEXT,
                Title           TEXT,
                Year            INTEGER,
                Duration        INTEGER,
                Track           INTEGER,
                TotalTracks     INTEGER,
                Disc            INTEGER,
                TotalDiscs      INTEGER,
                MimeType        TEXT,
                HasAlbumArt     INTEGER,
                FileNameMatchesHeaders INTEGER,
                Issues          TEXT
            )
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Loads all cached audio file entries
    /// </summary>
    public static Dictionary<string, (AudioFileInfo File, DateTime LastModified)> LoadAll()
    {
        var result = new Dictionary<string, (AudioFileInfo, DateTime)>(StringComparer.OrdinalIgnoreCase);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM AudioFiles";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var filePath = reader.GetString(0);
            var fileName = reader.GetString(1);
            var extension = reader.GetString(2);
            var fileSize = reader.GetInt64(3);
            var lastModified = DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind);

            var file = new AudioFileInfo
            {
                FilePath = filePath,
                FileName = fileName,
                Extension = extension,
                FileSize = fileSize,
                LastModified = lastModified
            };

            file.Artist = reader.IsDBNull(5) ? null : reader.GetString(5);
            file.Album = reader.IsDBNull(6) ? null : reader.GetString(6);
            file.Title = reader.IsDBNull(7) ? null : reader.GetString(7);
            file.Year = reader.IsDBNull(8) ? null : (uint)reader.GetInt64(8);
            file.Duration = reader.IsDBNull(9) ? null : TimeSpan.FromTicks(reader.GetInt64(9));
            file.Track = reader.IsDBNull(10) ? null : (uint)reader.GetInt64(10);
            file.TotalTracks = reader.IsDBNull(11) ? null : (uint)reader.GetInt64(11);
            file.Disc = reader.IsDBNull(12) ? null : (uint)reader.GetInt64(12);
            file.TotalDiscs = reader.IsDBNull(13) ? null : (uint)reader.GetInt64(13);
            file.MimeType = reader.IsDBNull(14) ? null : reader.GetString(14);
            file.HasAlbumArt = reader.IsDBNull(15) ? null : reader.GetInt32(15) != 0;
            file.FileNameMatchesHeaders = reader.IsDBNull(16) ? null : reader.GetInt32(16) != 0;

            if (!reader.IsDBNull(17))
            {
                var issuesJson = reader.GetString(17);
                var issues = JsonSerializer.Deserialize<List<string>>(issuesJson);
                if (issues != null)
                {
                    foreach (var issue in issues)
                    {
                        file.Issues.Add(issue);
                    }
                }
            }

            file.IsAnalyzed = true;

            result[filePath] = (file, lastModified);
        }

        return result;
    }

    /// <summary>
    /// Inserts or replaces a single audio file entry in the cache
    /// </summary>
    public static void UpsertFile(AudioFileInfo file, DateTime lastModified)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO AudioFiles
                (FilePath, FileName, Extension, FileSize, LastModified,
                 Artist, Album, Title, Year, Duration,
                 Track, TotalTracks, Disc, TotalDiscs,
                 MimeType, HasAlbumArt, FileNameMatchesHeaders, Issues)
            VALUES
                (@FilePath, @FileName, @Extension, @FileSize, @LastModified,
                 @Artist, @Album, @Title, @Year, @Duration,
                 @Track, @TotalTracks, @Disc, @TotalDiscs,
                 @MimeType, @HasAlbumArt, @FileNameMatchesHeaders, @Issues)
            """;

        command.Parameters.AddWithValue("@FilePath", file.FilePath);
        command.Parameters.AddWithValue("@FileName", file.FileName);
        command.Parameters.AddWithValue("@Extension", file.Extension);
        command.Parameters.AddWithValue("@FileSize", file.FileSize);
        command.Parameters.AddWithValue("@LastModified", lastModified.ToString("O"));

        command.Parameters.AddWithValue("@Artist", (object?)file.Artist ?? DBNull.Value);
        command.Parameters.AddWithValue("@Album", (object?)file.Album ?? DBNull.Value);
        command.Parameters.AddWithValue("@Title", (object?)file.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("@Year", file.Year.HasValue ? (object)(long)file.Year.Value : DBNull.Value);
        command.Parameters.AddWithValue("@Duration", file.Duration.HasValue ? (object)file.Duration.Value.Ticks : DBNull.Value);
        command.Parameters.AddWithValue("@Track", file.Track.HasValue ? (object)(long)file.Track.Value : DBNull.Value);
        command.Parameters.AddWithValue("@TotalTracks", file.TotalTracks.HasValue ? (object)(long)file.TotalTracks.Value : DBNull.Value);
        command.Parameters.AddWithValue("@Disc", file.Disc.HasValue ? (object)(long)file.Disc.Value : DBNull.Value);
        command.Parameters.AddWithValue("@TotalDiscs", file.TotalDiscs.HasValue ? (object)(long)file.TotalDiscs.Value : DBNull.Value);
        command.Parameters.AddWithValue("@MimeType", (object?)file.MimeType ?? DBNull.Value);
        command.Parameters.AddWithValue("@HasAlbumArt", file.HasAlbumArt.HasValue ? (object)(file.HasAlbumArt.Value ? 1 : 0) : DBNull.Value);
        command.Parameters.AddWithValue("@FileNameMatchesHeaders", file.FileNameMatchesHeaders.HasValue ? (object)(file.FileNameMatchesHeaders.Value ? 1 : 0) : DBNull.Value);
        command.Parameters.AddWithValue("@Issues", file.Issues.Count > 0 ? JsonSerializer.Serialize(file.Issues) : DBNull.Value);

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Removes cached entries for files that no longer exist on disk
    /// </summary>
    public static void RemoveFiles(IEnumerable<string> filePaths)
    {
        var paths = filePaths.ToList();
        if (paths.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();

        foreach (var path in paths)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM AudioFiles WHERE FilePath = @FilePath";
            command.Parameters.AddWithValue("@FilePath", path);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Drops all rows from the cache (for full rescan)
    /// </summary>
    public static void Clear()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AudioFiles";
        command.ExecuteNonQuery();
    }
}
