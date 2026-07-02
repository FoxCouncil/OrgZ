// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Microsoft.Data.Sqlite;
using OrgZ.Models;

namespace OrgZ.Services.Media;

/// <summary>
/// SQLite-backed record of everything the user has acquired across the downloadable verticals -
/// podcast subscriptions and audiobook acquisitions - on one <c>AcquiredMedia</c> table in the
/// same library.db as <see cref="MediaCache"/> and <see cref="Podcast.PodcastCache"/>. This is the
/// durable "I got this" layer; whether the bytes are on disk is a separate runtime probe
/// (<see cref="MediaDownloadState"/>). Identity is (Kind, SourceKey), so a podcast feed id and an
/// audiobook identifier never collide.
/// </summary>
public static class AcquisitionStore
{
    private static readonly string DefaultCacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrgZ");
    private static string CacheDirectory { get; set; } = DefaultCacheDirectory;
    private static string ConnectionString =>
        $"Data Source={Path.Combine(CacheDirectory, "library.db")}";

    /// <summary>
    /// Test hook: redirect the DB to a custom directory (pass null to restore the default
    /// %APPDATA%/OrgZ location). Call <see cref="EnsureCreated"/> afterward to build the schema.
    /// </summary>
    internal static void OverrideCacheDirectory(string? directory) => CacheDirectory = directory ?? DefaultCacheDirectory;

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(CacheDirectory);
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS AcquiredMedia (
                Kind            INTEGER NOT NULL,
                SourceKey       TEXT    NOT NULL,
                Title           TEXT,
                Creator         TEXT,
                ImageUrl        TEXT,
                HomepageUrl     TEXT,
                SourceRefJson   TEXT,
                IsUserProvided  INTEGER NOT NULL DEFAULT 0,
                AcquiredAt      TEXT    NOT NULL,
                PRIMARY KEY (Kind, SourceKey)
            );

            CREATE INDEX IF NOT EXISTS IX_AcquiredMedia_Kind ON AcquiredMedia(Kind);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Records an acquisition, or refreshes the mutable fields of one already present. The original
    /// <see cref="AcquiredMedia.AcquiredAt"/> is preserved on conflict - re-acquiring doesn't reset
    /// when you first got it.
    /// </summary>
    public static void Acquire(AcquiredMedia media)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AcquiredMedia (Kind, SourceKey, Title, Creator, ImageUrl, HomepageUrl, SourceRefJson, IsUserProvided, AcquiredAt)
            VALUES (@Kind, @SourceKey, @Title, @Creator, @ImageUrl, @HomepageUrl, @SourceRefJson, @IsUserProvided, @AcquiredAt)
            ON CONFLICT(Kind, SourceKey) DO UPDATE SET
                Title          = excluded.Title,
                Creator        = excluded.Creator,
                ImageUrl       = excluded.ImageUrl,
                HomepageUrl    = excluded.HomepageUrl,
                SourceRefJson  = excluded.SourceRefJson,
                IsUserProvided = excluded.IsUserProvided
            """;
        cmd.Parameters.AddWithValue("@Kind",           (int)media.Kind);
        cmd.Parameters.AddWithValue("@SourceKey",      media.SourceKey);
        cmd.Parameters.AddWithValue("@Title",          (object?)media.Title         ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Creator",        (object?)media.Creator       ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ImageUrl",       (object?)media.ImageUrl      ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@HomepageUrl",    (object?)media.HomepageUrl   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SourceRefJson",  (object?)media.SourceRefJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsUserProvided", media.IsUserProvided ? 1 : 0);
        cmd.Parameters.AddWithValue("@AcquiredAt",     (media.AcquiredAt == default ? DateTime.UtcNow : media.AcquiredAt).ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Forgets an acquisition entirely (unsubscribe / remove). Downloaded bytes are the caller's concern.</summary>
    public static void Release(AcquiredMediaKind kind, string sourceKey)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM AcquiredMedia WHERE Kind = @Kind AND SourceKey = @SourceKey";
        cmd.Parameters.AddWithValue("@Kind", (int)kind);
        cmd.Parameters.AddWithValue("@SourceKey", sourceKey);
        cmd.ExecuteNonQuery();
    }

    public static bool IsAcquired(AcquiredMediaKind kind, string sourceKey)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM AcquiredMedia WHERE Kind = @Kind AND SourceKey = @SourceKey LIMIT 1";
        cmd.Parameters.AddWithValue("@Kind", (int)kind);
        cmd.Parameters.AddWithValue("@SourceKey", sourceKey);
        return cmd.ExecuteScalar() is not null;
    }

    public static AcquiredMedia? Get(AcquiredMediaKind kind, string sourceKey)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Kind, SourceKey, Title, Creator, ImageUrl, HomepageUrl, SourceRefJson, IsUserProvided, AcquiredAt FROM AcquiredMedia WHERE Kind = @Kind AND SourceKey = @SourceKey";
        cmd.Parameters.AddWithValue("@Kind", (int)kind);
        cmd.Parameters.AddWithValue("@SourceKey", sourceKey);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadRow(r) : null;
    }

    /// <summary>Every acquisition of a kind, newest first.</summary>
    public static List<AcquiredMedia> GetAll(AcquiredMediaKind kind)
    {
        var list = new List<AcquiredMedia>();
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Kind, SourceKey, Title, Creator, ImageUrl, HomepageUrl, SourceRefJson, IsUserProvided, AcquiredAt FROM AcquiredMedia WHERE Kind = @Kind ORDER BY AcquiredAt DESC";
        cmd.Parameters.AddWithValue("@Kind", (int)kind);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(ReadRow(r));
        }
        return list;
    }

    private static AcquiredMedia ReadRow(SqliteDataReader r) => new()
    {
        Kind           = (AcquiredMediaKind)r.GetInt32(0),
        SourceKey      = r.GetString(1),
        Title          = GetString(r, 2),
        Creator        = GetString(r, 3),
        ImageUrl       = GetString(r, 4),
        HomepageUrl    = GetString(r, 5),
        SourceRefJson  = GetString(r, 6),
        IsUserProvided = r.GetInt32(7) != 0,
        AcquiredAt     = ParseDate(r.GetString(8)) ?? DateTime.UtcNow,
    };

    private static string? GetString(SqliteDataReader r, int ord) => r.IsDBNull(ord) ? null : r.GetString(ord);
    private static DateTime? ParseDate(string s) => DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
}
