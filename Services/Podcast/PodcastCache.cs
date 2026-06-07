// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Microsoft.Data.Sqlite;
using OrgZ.Models;

namespace OrgZ.Services.Podcast;

/// <summary>
/// SQLite-backed cache for user podcast state: subscriptions and listen history.
/// Lives in the same library.db as <see cref="MediaCache"/>, on its own tables.
/// Download presence is no longer tracked here - it's a function of whether the
/// downloaded file exists on disk; see <see cref="PodcastDownloadService.GetState"/>.
/// </summary>
public static class PodcastCache
{
    private static readonly string DefaultCacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrgZ");
    private static string ConnectionString =>
        $"Data Source={Path.Combine(DefaultCacheDirectory, "library.db")}";

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DefaultCacheDirectory);
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS PodcastSubscription (
                    FeedId          INTEGER PRIMARY KEY,
                    PodcastGuid     TEXT,
                    Title           TEXT,
                    Author          TEXT,
                    Description     TEXT,
                    HomepageUrl     TEXT,
                    FeedUrl         TEXT,
                    ImageUrl        TEXT,
                    SubscribedAt    TEXT NOT NULL,
                    LastCheckedAt   TEXT
                );

                -- Legacy table — kept as a DROP for upgrades from older installs.
                -- Download tracking now lives on disk (PodcastDownloadService.GetState).
                DROP TABLE IF EXISTS PodcastDownload;

                -- Listen history: one row per episode the user has ever played,
                -- whether they downloaded it or just streamed. Upserted on play
                -- and updated with progress as they listen.
                CREATE TABLE IF NOT EXISTS PodcastListen (
                    EpisodeId           INTEGER PRIMARY KEY,
                    FeedId              INTEGER NOT NULL,
                    Title               TEXT,
                    FeedTitle           TEXT,
                    EnclosureUrl        TEXT,
                    ImageUrl            TEXT,
                    DurationSec         INTEGER NOT NULL DEFAULT 0,
                    DatePublishedEpoch  INTEGER NOT NULL DEFAULT 0,
                    FirstPlayedAt       TEXT NOT NULL,
                    LastPlayedAt        TEXT NOT NULL,
                    LastPositionMs      INTEGER,
                    PlayCount           INTEGER NOT NULL DEFAULT 1,
                    Completed           INTEGER NOT NULL DEFAULT 0
                );

                CREATE INDEX IF NOT EXISTS IX_PodcastListen_FeedId ON PodcastListen(FeedId);
                CREATE INDEX IF NOT EXISTS IX_PodcastListen_LastPlayedAt ON PodcastListen(LastPlayedAt);
                """;
            cmd.ExecuteNonQuery();
        }
    }

    // -- Listen history --------------------------------------------------

    /// <summary>
    /// Records or refreshes a play event for an episode. Inserts a new row on
    /// first play; subsequent calls bump PlayCount and update LastPlayedAt.
    /// </summary>
    public static void RecordPlay(PodcastFeed feed, PodcastEpisode episode)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO PodcastListen (EpisodeId, FeedId, Title, FeedTitle, EnclosureUrl, ImageUrl, DurationSec, DatePublishedEpoch, FirstPlayedAt, LastPlayedAt, PlayCount)
            VALUES (@EpisodeId, @FeedId, @Title, @FeedTitle, @EnclosureUrl, @ImageUrl, @DurationSec, @DatePublishedEpoch, @Now, @Now, 1)
            ON CONFLICT(EpisodeId) DO UPDATE SET
                LastPlayedAt = @Now,
                PlayCount    = PlayCount + 1,
                Title        = COALESCE(excluded.Title,        PodcastListen.Title),
                FeedTitle    = COALESCE(excluded.FeedTitle,    PodcastListen.FeedTitle),
                EnclosureUrl = COALESCE(excluded.EnclosureUrl, PodcastListen.EnclosureUrl),
                ImageUrl     = COALESCE(excluded.ImageUrl,     PodcastListen.ImageUrl)
            """;
        cmd.Parameters.AddWithValue("@EpisodeId",          episode.Id);
        cmd.Parameters.AddWithValue("@FeedId",             feed.Id);
        cmd.Parameters.AddWithValue("@Title",              (object?)episode.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FeedTitle",          (object?)feed.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@EnclosureUrl",       (object?)episode.EnclosureUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ImageUrl",           (object?)(episode.Image ?? feed.DisplayImage) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@DurationSec",        episode.DurationSec);
        cmd.Parameters.AddWithValue("@DatePublishedEpoch", episode.DatePublishedEpoch);
        cmd.Parameters.AddWithValue("@Now",                DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public static void UpdateListenPosition(long episodeId, long positionMs, bool completed)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE PodcastListen SET LastPositionMs = @Pos, Completed = @Done, LastPlayedAt = @Now WHERE EpisodeId = @Id";
        cmd.Parameters.AddWithValue("@Pos",  positionMs);
        cmd.Parameters.AddWithValue("@Done", completed ? 1 : 0);
        cmd.Parameters.AddWithValue("@Now",  DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@Id",   episodeId);
        cmd.ExecuteNonQuery();
    }

    public static List<PodcastListenEntry> GetRecentListens(int max = 50)
    {
        var list = new List<PodcastListenEntry>();
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT EpisodeId, FeedId, Title, FeedTitle, EnclosureUrl, ImageUrl, DurationSec, DatePublishedEpoch, FirstPlayedAt, LastPlayedAt, LastPositionMs, PlayCount, Completed FROM PodcastListen ORDER BY LastPlayedAt DESC LIMIT {max}";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PodcastListenEntry
            {
                EpisodeId          = r.GetInt64(0),
                FeedId             = r.GetInt64(1),
                Title              = GetString(r, 2),
                FeedTitle          = GetString(r, 3),
                EnclosureUrl       = GetString(r, 4),
                ImageUrl           = GetString(r, 5),
                DurationSec        = r.GetInt32(6),
                DatePublishedEpoch = r.GetInt64(7),
                FirstPlayedAt      = ParseDate(r.GetString(8)) ?? DateTime.UtcNow,
                LastPlayedAt       = ParseDate(r.GetString(9)) ?? DateTime.UtcNow,
                LastPositionMs     = r.IsDBNull(10) ? null : r.GetInt64(10),
                PlayCount          = r.GetInt32(11),
                Completed          = r.GetInt32(12) != 0,
            });
        }
        return list;
    }

    // -- Subscriptions ---------------------------------------------------

    public static List<PodcastSubscription> GetSubscriptions()
    {
        var list = new List<PodcastSubscription>();
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT FeedId, PodcastGuid, Title, Author, Description, HomepageUrl, FeedUrl, ImageUrl, SubscribedAt, LastCheckedAt FROM PodcastSubscription ORDER BY Title COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PodcastSubscription
            {
                FeedId        = r.GetInt64(0),
                PodcastGuid   = GetString(r, 1),
                Title         = GetString(r, 2),
                Author        = GetString(r, 3),
                Description   = GetString(r, 4),
                HomepageUrl   = GetString(r, 5),
                FeedUrl       = GetString(r, 6),
                ImageUrl      = GetString(r, 7),
                SubscribedAt  = ParseDate(r.GetString(8)) ?? DateTime.UtcNow,
                LastCheckedAt = GetString(r, 9) is { } s ? ParseDate(s) : null,
            });
        }
        return list;
    }

    public static bool IsSubscribed(long feedId)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM PodcastSubscription WHERE FeedId = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", feedId);
        return cmd.ExecuteScalar() is not null;
    }

    public static void AddSubscription(PodcastFeed feed)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO PodcastSubscription (FeedId, PodcastGuid, Title, Author, Description, HomepageUrl, FeedUrl, ImageUrl, SubscribedAt)
            VALUES (@FeedId, @PodcastGuid, @Title, @Author, @Description, @HomepageUrl, @FeedUrl, @ImageUrl, @SubscribedAt)
            ON CONFLICT(FeedId) DO UPDATE SET
                PodcastGuid = excluded.PodcastGuid,
                Title       = excluded.Title,
                Author      = excluded.Author,
                Description = excluded.Description,
                HomepageUrl = excluded.HomepageUrl,
                FeedUrl     = excluded.FeedUrl,
                ImageUrl    = excluded.ImageUrl
            """;
        cmd.Parameters.AddWithValue("@FeedId",       feed.Id);
        cmd.Parameters.AddWithValue("@PodcastGuid",  (object?)feed.PodcastGuid  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Title",        (object?)feed.Title        ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Author",       (object?)feed.Author       ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Description",  (object?)feed.Description  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@HomepageUrl",  (object?)feed.HomepageUrl  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FeedUrl",      (object?)feed.FeedUrl      ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ImageUrl",     (object?)feed.DisplayImage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SubscribedAt", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public static void RemoveSubscription(long feedId)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM PodcastSubscription WHERE FeedId = @id";
        cmd.Parameters.AddWithValue("@id", feedId);
        cmd.ExecuteNonQuery();
    }

    private static string? GetString(SqliteDataReader r, int ord) => r.IsDBNull(ord) ? null : r.GetString(ord);
    private static DateTime? ParseDate(string s) => DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
}
