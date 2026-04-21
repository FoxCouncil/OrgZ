// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

/// <summary>
/// Each test creates a fresh fake "mount" directory; DeviceLibraryCache stores the DB
/// under {mount}/.orgz/library.db so pointing the API at a temp dir gives us isolation.
/// </summary>
public class DeviceLibraryCacheTests : IDisposable
{
    private readonly string _mountPath;

    public DeviceLibraryCacheTests()
    {
        _mountPath = Path.Combine(Path.GetTempPath(), "OrgZ-DeviceLibraryCache-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_mountPath);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_mountPath)) Directory.Delete(_mountPath, recursive: true); } catch { }
    }

    private static MediaItem MakeItem(string id, string filePath, string? title = null, string? artist = null, long? size = null, DateTime? lastModified = null)
    {
        return new MediaItem
        {
            Id = id,
            Kind = MediaKind.Music,
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            Extension = Path.GetExtension(filePath),
            FileSize = size ?? 1000,
            LastModified = lastModified ?? new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc),
            Title = title ?? Path.GetFileNameWithoutExtension(filePath),
            Artist = artist,
        };
    }

    // ===== TryLoad =====

    [Fact]
    public void TryLoad_returns_empty_when_db_missing()
    {
        Assert.Empty(DeviceLibraryCache.TryLoad(_mountPath, "device:test"));
    }

    [Fact]
    public void TryLoad_returns_empty_when_orgz_folder_missing()
    {
        // No .orgz folder at all — same behavior as missing DB
        Assert.Empty(DeviceLibraryCache.TryLoad(_mountPath, "device:test"));
        Assert.False(Directory.Exists(Path.Combine(_mountPath, ".orgz")));
    }

    // ===== Upsert + TryLoad round-trip =====

    [Fact]
    public void Upsert_then_TryLoad_round_trips_single_item()
    {
        var source = $"device:{_mountPath}";
        var original = new MediaItem
        {
            Id = "track-1",
            Kind = MediaKind.Music,
            FilePath = "/music/a.mp3",
            FileName = "a.mp3",
            Extension = ".mp3",
            FileSize = 5_120_000,
            LastModified = new DateTime(2026, 4, 1, 10, 30, 0, DateTimeKind.Utc),
            Title = "Subdivisions",
            Artist = "Rush",
            Album = "Signals",
            Genre = "Progressive Rock",
            Composer = "Geddy Lee",
            Year = 1982,
            Track = 1,
            TotalTracks = 8,
            Disc = 1,
            TotalDiscs = 1,
            Duration = TimeSpan.FromSeconds(213),
            AudioBitrate = 192,
            SampleRate = 44_100,
            AudioChannels = 2,
            CodecDescription = "MP3 (MPEG-1 Layer 3)",
            HasAlbumArt = true,
        };

        DeviceLibraryCache.Upsert(_mountPath, [original]);
        var loaded = DeviceLibraryCache.TryLoad(_mountPath, source);

        var x = Assert.Single(loaded);
        Assert.Equal("track-1", x.Id);
        Assert.Equal(MediaKind.Music, x.Kind);
        Assert.Equal(source, x.Source);
        Assert.Equal("/music/a.mp3", x.FilePath);
        Assert.Equal("a.mp3", x.FileName);
        Assert.Equal(".mp3", x.Extension);
        Assert.Equal(5_120_000, x.FileSize);
        Assert.Equal(original.LastModified!.Value.ToUniversalTime(), x.LastModified!.Value.ToUniversalTime());
        Assert.Equal("Subdivisions", x.Title);
        Assert.Equal("Rush", x.Artist);
        Assert.Equal("Signals", x.Album);
        Assert.Equal("Progressive Rock", x.Genre);
        Assert.Equal("Geddy Lee", x.Composer);
        Assert.Equal(1982u, x.Year);
        Assert.Equal(1u, x.Track);
        Assert.Equal(8u, x.TotalTracks);
        Assert.Equal(1u, x.Disc);
        Assert.Equal(1u, x.TotalDiscs);
        Assert.Equal(TimeSpan.FromSeconds(213), x.Duration);
        Assert.Equal(192, x.AudioBitrate);
        Assert.Equal(44_100, x.SampleRate);
        Assert.Equal(2, x.AudioChannels);
        Assert.Equal("MP3 (MPEG-1 Layer 3)", x.CodecDescription);
        Assert.True(x.HasAlbumArt);
        Assert.True(x.IsAnalyzed);    // always true on load — the cache only holds analyzed data
        Assert.Equal("/music/a.mp3", x.StreamUrl);
    }

    [Fact]
    public void Upsert_preserves_null_optional_fields()
    {
        // Only the required bits — verify nulls come back as nulls, not "" or 0
        var minimal = new MediaItem
        {
            Id = "track-min",
            Kind = MediaKind.Music,
            FilePath = "/music/bare.flac",
            FileSize = 100,
            LastModified = DateTime.UtcNow,
        };

        DeviceLibraryCache.Upsert(_mountPath, [minimal]);
        var loaded = DeviceLibraryCache.TryLoad(_mountPath, "src").Single();

        Assert.Null(loaded.Title);
        Assert.Null(loaded.Artist);
        Assert.Null(loaded.Album);
        Assert.Null(loaded.Year);
        Assert.Null(loaded.Track);
        Assert.Null(loaded.Duration);
        Assert.Null(loaded.AudioBitrate);
        Assert.Null(loaded.HasAlbumArt);
    }

    [Fact]
    public void Upsert_replaces_existing_by_id()
    {
        DeviceLibraryCache.Upsert(_mountPath, [MakeItem("t1", "/m/a.mp3", title: "Original")]);
        DeviceLibraryCache.Upsert(_mountPath, [MakeItem("t1", "/m/a.mp3", title: "Updated")]);

        var loaded = DeviceLibraryCache.TryLoad(_mountPath, "src");
        Assert.Single(loaded);
        Assert.Equal("Updated", loaded[0].Title);
    }

    [Fact]
    public void Upsert_with_empty_list_is_noop()
    {
        DeviceLibraryCache.Upsert(_mountPath, []);
        // No .orgz folder should exist — nothing was written
        Assert.False(File.Exists(Path.Combine(_mountPath, ".orgz", "library.db")));
    }

    [Fact]
    public void Upsert_skips_items_with_null_or_empty_Id()
    {
        // MediaItem.Id is required but the scanner might build invalid ones; the cache
        // should skip them rather than corrupting the whole batch with a constraint fail
        var good = MakeItem("good", "/m/good.mp3");
        var badId = new MediaItem
        {
            Id = "",
            Kind = MediaKind.Music,
            FilePath = "/m/bad.mp3",
            FileSize = 100,
            LastModified = DateTime.UtcNow,
        };

        DeviceLibraryCache.Upsert(_mountPath, [good, badId]);

        var loaded = DeviceLibraryCache.TryLoad(_mountPath, "src");
        Assert.Single(loaded);
        Assert.Equal("good", loaded[0].Id);
    }

    [Fact]
    public void Upsert_skips_items_with_null_FilePath()
    {
        var good = MakeItem("good", "/m/good.mp3");
        var badPath = new MediaItem
        {
            Id = "bad",
            Kind = MediaKind.Music,
            FilePath = null,   // FilePath is NOT NULL in the schema
            FileSize = 100,
            LastModified = DateTime.UtcNow,
        };

        DeviceLibraryCache.Upsert(_mountPath, [good, badPath]);

        var loaded = DeviceLibraryCache.TryLoad(_mountPath, "src");
        Assert.Single(loaded);
    }

    [Fact]
    public void Upsert_handles_large_batch()
    {
        // Scanner batches are typically 32-64; make sure 500 in one transaction works
        var items = Enumerable.Range(0, 500)
            .Select(i => MakeItem($"t{i}", $"/m/file-{i}.mp3", title: $"Track {i}"))
            .ToList();

        DeviceLibraryCache.Upsert(_mountPath, items);
        var loaded = DeviceLibraryCache.TryLoad(_mountPath, "src");

        Assert.Equal(500, loaded.Count);
    }

    // ===== PruneMissing =====

    [Fact]
    public void PruneMissing_removes_rows_not_in_keep_list()
    {
        DeviceLibraryCache.Upsert(_mountPath, [
            MakeItem("a", "/m/a.mp3"),
            MakeItem("b", "/m/b.mp3"),
            MakeItem("c", "/m/c.mp3"),
        ]);

        DeviceLibraryCache.PruneMissing(_mountPath, ["/m/a.mp3", "/m/c.mp3"]);

        var loaded = DeviceLibraryCache.TryLoad(_mountPath, "src").OrderBy(i => i.Id).ToList();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("a", loaded[0].Id);
        Assert.Equal("c", loaded[1].Id);
    }

    [Fact]
    public void PruneMissing_empty_keep_list_deletes_everything()
    {
        DeviceLibraryCache.Upsert(_mountPath, [MakeItem("a", "/m/a.mp3")]);
        DeviceLibraryCache.PruneMissing(_mountPath, []);

        Assert.Empty(DeviceLibraryCache.TryLoad(_mountPath, "src"));
    }

    [Fact]
    public void PruneMissing_keep_list_with_unseen_paths_is_noop()
    {
        // Keep list contains paths that aren't in the DB — all real rows should stay
        DeviceLibraryCache.Upsert(_mountPath, [
            MakeItem("a", "/m/a.mp3"),
            MakeItem("b", "/m/b.mp3"),
        ]);

        DeviceLibraryCache.PruneMissing(_mountPath, ["/m/a.mp3", "/m/b.mp3", "/m/c.mp3", "/m/d.mp3"]);

        Assert.Equal(2, DeviceLibraryCache.TryLoad(_mountPath, "src").Count);
    }

    // ===== Metadata accessors =====

    [Fact]
    public void GetMetadata_returns_null_when_db_missing()
    {
        Assert.Null(DeviceLibraryCache.GetMetadata(_mountPath, "any-key"));
    }

    [Fact]
    public void SetMetadata_then_GetMetadata_round_trips()
    {
        DeviceLibraryCache.SetMetadata(_mountPath, "sig", "12345:63900000000000");
        Assert.Equal("12345:63900000000000", DeviceLibraryCache.GetMetadata(_mountPath, "sig"));
    }

    [Fact]
    public void SetMetadata_overwrites_existing_key()
    {
        DeviceLibraryCache.SetMetadata(_mountPath, "k", "v1");
        DeviceLibraryCache.SetMetadata(_mountPath, "k", "v2");
        Assert.Equal("v2", DeviceLibraryCache.GetMetadata(_mountPath, "k"));
    }

    [Fact]
    public void GetMetadata_returns_null_for_missing_key_on_existing_db()
    {
        DeviceLibraryCache.SetMetadata(_mountPath, "k1", "v1");
        Assert.Null(DeviceLibraryCache.GetMetadata(_mountPath, "k2"));
    }

    [Fact]
    public void Metadata_and_Media_coexist_in_same_db()
    {
        DeviceLibraryCache.Upsert(_mountPath, [MakeItem("a", "/m/a.mp3", title: "Track A")]);
        DeviceLibraryCache.SetMetadata(_mountPath, "sig", "abc");

        Assert.Equal("abc", DeviceLibraryCache.GetMetadata(_mountPath, "sig"));
        var loaded = DeviceLibraryCache.TryLoad(_mountPath, "src");
        Assert.Single(loaded);
        Assert.Equal("Track A", loaded[0].Title);
    }

    // ===== Schema migration / version handling =====

    [Fact]
    public void Fresh_db_has_schema_at_current_version()
    {
        DeviceLibraryCache.SetMetadata(_mountPath, "force-create", "x");

        var dbPath = Path.Combine(_mountPath, ".orgz", "library.db");
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(cmd.ExecuteScalar());

        Assert.Equal(1, version);   // CurrentSchemaVersion
    }

    [Fact]
    public void Fresh_db_enables_WAL_mode()
    {
        DeviceLibraryCache.SetMetadata(_mountPath, "force-create", "x");

        var dbPath = Path.Combine(_mountPath, ".orgz", "library.db");
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        var mode = cmd.ExecuteScalar()?.ToString();

        Assert.Equal("wal", mode, ignoreCase: true);
    }

    // ===== Malformed / corrupted DB =====

    [Fact]
    public void TryLoad_returns_empty_on_corrupt_db_file()
    {
        var orgzDir = Path.Combine(_mountPath, ".orgz");
        Directory.CreateDirectory(orgzDir);
        File.WriteAllBytes(Path.Combine(orgzDir, "library.db"), [0xDE, 0xAD, 0xBE, 0xEF, 0xDE, 0xAD]);

        var loaded = DeviceLibraryCache.TryLoad(_mountPath, "src");
        Assert.Empty(loaded);
    }

    [Fact]
    public void GetMetadata_returns_null_on_corrupt_db()
    {
        var orgzDir = Path.Combine(_mountPath, ".orgz");
        Directory.CreateDirectory(orgzDir);
        File.WriteAllBytes(Path.Combine(orgzDir, "library.db"), [0xDE, 0xAD, 0xBE, 0xEF]);

        Assert.Null(DeviceLibraryCache.GetMetadata(_mountPath, "any"));
    }

    // ===== Source tagging =====

    [Fact]
    public void TryLoad_tags_every_item_with_the_given_source()
    {
        DeviceLibraryCache.Upsert(_mountPath, [
            MakeItem("a", "/m/a.mp3"),
            MakeItem("b", "/m/b.mp3"),
            MakeItem("c", "/m/c.mp3"),
        ]);

        var source = "device:/run/media/fox/FOXPOD/";
        var loaded = DeviceLibraryCache.TryLoad(_mountPath, source);

        Assert.All(loaded, i => Assert.Equal(source, i.Source));
    }

    [Fact]
    public void Upsert_overwrites_IsAnalyzed_to_true_on_load()
    {
        var item = MakeItem("a", "/m/a.mp3");
        item.IsAnalyzed = false;

        DeviceLibraryCache.Upsert(_mountPath, [item]);
        var loaded = DeviceLibraryCache.TryLoad(_mountPath, "src").Single();

        Assert.True(loaded.IsAnalyzed);
    }
}
