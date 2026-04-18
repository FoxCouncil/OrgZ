// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using static OrgZ.Tests.TestHelpers;

namespace OrgZ.Tests;

/// <summary>
/// Coverage-gap tests for MediaCache, complementing the playlist-focused MediaCacheTests.
/// Targets: SetFavorite, SetRating, IncrementPlayCount, SetLastPlayed, RemoveMusic,
/// LoadAllRadio + UpsertRadioStations, RemoveRadioBySource, GetLastSync + RecordSync,
/// GetCdMetadata + SaveCdMetadata.
/// </summary>
public class MediaCacheGapTests : IDisposable
{
    private readonly string _tempDbPath;

    public MediaCacheGapTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"orgz-test-{Guid.NewGuid():N}.db");
        MediaCache.OverrideCachePath(_tempDbPath);
        MediaCache.EnsureCreated();
    }

    public void Dispose()
    {
        MediaCache.OverrideCachePath(null);
        try { if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath); } catch { }
    }

    // ===== SetFavorite =====

    [Fact]
    public void SetFavorite_toggles_flag_round_trip()
    {
        MediaCache.UpsertMusic(Music("track-1"));
        MediaCache.SetFavorite("track-1", true);

        var loaded = MediaCache.LoadAll().Single(i => i.Id == "track-1");
        Assert.True(loaded.IsFavorite);

        MediaCache.SetFavorite("track-1", false);
        loaded = MediaCache.LoadAll().Single(i => i.Id == "track-1");
        Assert.False(loaded.IsFavorite);
    }

    [Fact]
    public void SetFavorite_on_nonexistent_id_is_noop()
    {
        // Should not throw — just silently affects zero rows
        MediaCache.SetFavorite("nope", true);
    }

    // ===== SetRating =====

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void SetRating_persists_value(int rating)
    {
        MediaCache.UpsertMusic(Music("track-1"));
        MediaCache.SetRating("track-1", rating);

        var loaded = MediaCache.LoadAll().Single(i => i.Id == "track-1");
        Assert.Equal(rating, loaded.Rating);
    }

    [Fact]
    public void SetRating_null_clears_rating()
    {
        MediaCache.UpsertMusic(Music("track-1"));
        MediaCache.SetRating("track-1", 5);
        MediaCache.SetRating("track-1", null);

        var loaded = MediaCache.LoadAll().Single(i => i.Id == "track-1");
        Assert.Null(loaded.Rating);
    }

    // ===== IncrementPlayCount =====

    [Fact]
    public void IncrementPlayCount_increments_from_zero()
    {
        MediaCache.UpsertMusic(Music("track-1"));
        MediaCache.IncrementPlayCount("track-1");

        var loaded = MediaCache.LoadAll().Single(i => i.Id == "track-1");
        Assert.Equal(1, loaded.PlayCount);
    }

    [Fact]
    public void IncrementPlayCount_called_repeatedly_accumulates()
    {
        MediaCache.UpsertMusic(Music("track-1"));
        for (int i = 0; i < 5; i++) MediaCache.IncrementPlayCount("track-1");

        var loaded = MediaCache.LoadAll().Single(i => i.Id == "track-1");
        Assert.Equal(5, loaded.PlayCount);
    }

    // ===== SetLastPlayed =====

    [Fact]
    public void SetLastPlayed_round_trips_round_trip_kind()
    {
        MediaCache.UpsertMusic(Music("track-1"));
        var ts = new DateTime(2026, 4, 17, 23, 30, 0, DateTimeKind.Utc);
        MediaCache.SetLastPlayed("track-1", ts);

        var loaded = MediaCache.LoadAll().Single(i => i.Id == "track-1");
        Assert.NotNull(loaded.LastPlayed);
        Assert.Equal(ts, loaded.LastPlayed!.Value.ToUniversalTime());
    }

    // ===== RemoveMusic =====

    [Fact]
    public void RemoveMusic_deletes_listed_ids_only()
    {
        MediaCache.UpsertMusic(Music("a"));
        MediaCache.UpsertMusic(Music("b"));
        MediaCache.UpsertMusic(Music("c"));

        MediaCache.RemoveMusic(["a", "c"]);

        var ids = MediaCache.LoadAll().Where(i => i.Kind == MediaKind.Music).Select(i => i.Id).ToHashSet();
        Assert.DoesNotContain("a", ids);
        Assert.Contains("b", ids);
        Assert.DoesNotContain("c", ids);
    }

    [Fact]
    public void RemoveMusic_empty_list_is_noop()
    {
        MediaCache.UpsertMusic(Music("a"));
        MediaCache.RemoveMusic([]);
        Assert.Single(MediaCache.LoadAll().Where(i => i.Kind == MediaKind.Music));
    }

    [Fact]
    public void RemoveMusic_does_not_delete_radio_rows()
    {
        // The Media table has Id as PK, so music and radio IDs are distinct namespaces.
        // RemoveMusic must filter on Kind='Music' so it can't accidentally drop a radio
        // row even if a caller passes a radio ID by mistake.
        MediaCache.UpsertMusic(Music("track-1"));
        MediaCache.UpsertRadioStations([Radio("station-1", title: "BBC Radio 1")]);

        MediaCache.RemoveMusic(["track-1", "station-1"]);   // try removing both as music

        Assert.DoesNotContain(MediaCache.LoadAll().Where(i => i.Kind == MediaKind.Music), i => i.Id == "track-1");
        // station-1 is in the Radio kind, so the music DELETE should not have touched it
        Assert.Contains(MediaCache.LoadAllRadio(), i => i.Id == "station-1");
    }

    // ===== LoadAllRadio + UpsertRadioStations =====

    [Fact]
    public void UpsertRadioStations_then_LoadAllRadio_round_trips()
    {
        var stations = new List<MediaItem>
        {
            Radio("rb-1", title: "BBC Radio 1", country: "United Kingdom", source: "radiobrowser", codec: "mp3", bitrate: 128),
            Radio("rb-2", title: "SomaFM Drone Zone", country: "United States",   source: "shoutcast", codec: "aac", bitrate: 64),
        };

        MediaCache.UpsertRadioStations(stations);
        var loaded = MediaCache.LoadAllRadio();

        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, s => s.Id == "rb-1" && s.Title == "BBC Radio 1");
        Assert.Contains(loaded, s => s.Id == "rb-2" && s.Codec == "aac");
    }

    [Fact]
    public void UpsertRadioStations_replaces_existing_by_id()
    {
        MediaCache.UpsertRadioStations([Radio("rb-1", title: "Original")]);
        MediaCache.UpsertRadioStations([Radio("rb-1", title: "Updated")]);

        var loaded = MediaCache.LoadAllRadio();
        Assert.Single(loaded);
        Assert.Equal("Updated", loaded[0].Title);
    }

    // ===== RemoveRadioBySource =====

    [Fact]
    public void RemoveRadioBySource_deletes_only_matching_source_non_favorites()
    {
        MediaCache.UpsertRadioStations([
            Radio("rb-1", source: "radiobrowser"),
            Radio("rb-2", source: "radiobrowser", isFavorite: true),
            Radio("sc-1", source: "shoutcast"),
        ]);

        MediaCache.RemoveRadioBySource("radiobrowser");

        var loaded = MediaCache.LoadAllRadio();
        Assert.DoesNotContain(loaded, s => s.Id == "rb-1");        // non-favorite, removed
        Assert.Contains(loaded, s => s.Id == "rb-2");              // favorite, retained
        Assert.Contains(loaded, s => s.Id == "sc-1");              // different source, retained
    }

    // ===== GetLastSync + RecordSync =====

    [Fact]
    public void GetLastSync_returns_null_when_no_history()
    {
        Assert.Null(MediaCache.GetLastSync("never-synced"));
    }

    [Fact]
    public void RecordSync_then_GetLastSync_round_trips()
    {
        MediaCache.RecordSync("radiobrowser", count: 4500, durationMs: 12_345);
        var entry = MediaCache.GetLastSync("radiobrowser");

        Assert.NotNull(entry);
        Assert.Equal(4500, entry!.Value.StationCount);
        Assert.Equal(12_345, entry.Value.DurationMs);
        // LastSync should be very recent (within the last minute)
        Assert.True((DateTime.UtcNow - entry.Value.LastSync).TotalMinutes < 1);
    }

    [Fact]
    public void RecordSync_overwrites_previous_for_same_source()
    {
        MediaCache.RecordSync("radiobrowser", count: 100, durationMs: 1000);
        MediaCache.RecordSync("radiobrowser", count: 200, durationMs: 2000);

        var entry = MediaCache.GetLastSync("radiobrowser");
        Assert.Equal(200, entry!.Value.StationCount);
        Assert.Equal(2000, entry.Value.DurationMs);
    }

    [Fact]
    public void RecordSync_keeps_independent_rows_per_source()
    {
        MediaCache.RecordSync("radiobrowser", count: 100, durationMs: 1000);
        MediaCache.RecordSync("shoutcast",    count: 50,  durationMs: 500);

        Assert.Equal(100, MediaCache.GetLastSync("radiobrowser")!.Value.StationCount);
        Assert.Equal(50,  MediaCache.GetLastSync("shoutcast")!.Value.StationCount);
    }

    // ===== CD metadata cache =====

    [Fact]
    public void GetCdMetadata_returns_null_when_no_entry()
    {
        Assert.Null(MediaCache.GetCdMetadata("nonexistent-disc-id"));
    }

    [Fact]
    public void SaveCdMetadata_then_GetCdMetadata_round_trips_all_fields()
    {
        var meta = new CachedCdMetadata
        {
            DiscId = "disc-abc",
            ReleaseMbid = "00000000-0000-0000-0000-000000000001",
            Artist = "Pink Floyd",
            Album = "The Dark Side of the Moon",
            Year = 1973,
            TracksJson = "[{\"n\":1,\"title\":\"Speak to Me\"}]",
            CoverArt = [0xFF, 0xD8, 0xFF, 0xE0],   // JPEG SOI bytes
        };

        MediaCache.SaveCdMetadata(meta);
        var loaded = MediaCache.GetCdMetadata("disc-abc");

        Assert.NotNull(loaded);
        Assert.Equal(meta.DiscId, loaded!.DiscId);
        Assert.Equal(meta.ReleaseMbid, loaded.ReleaseMbid);
        Assert.Equal(meta.Artist, loaded.Artist);
        Assert.Equal(meta.Album, loaded.Album);
        Assert.Equal(meta.Year, loaded.Year);
        Assert.Equal(meta.TracksJson, loaded.TracksJson);
        Assert.Equal(meta.CoverArt, loaded.CoverArt);
    }

    [Fact]
    public void SaveCdMetadata_handles_null_optional_fields()
    {
        var meta = new CachedCdMetadata
        {
            DiscId = "disc-bare",
            // Everything else null
        };

        MediaCache.SaveCdMetadata(meta);
        var loaded = MediaCache.GetCdMetadata("disc-bare");

        Assert.NotNull(loaded);
        Assert.Equal("disc-bare", loaded!.DiscId);
        Assert.Null(loaded.ReleaseMbid);
        Assert.Null(loaded.Artist);
        Assert.Null(loaded.Album);
        Assert.Null(loaded.Year);
        Assert.Null(loaded.TracksJson);
        Assert.Null(loaded.CoverArt);
    }

    [Fact]
    public void SaveCdMetadata_overwrites_existing_disc_id()
    {
        MediaCache.SaveCdMetadata(new CachedCdMetadata { DiscId = "d1", Artist = "Original" });
        MediaCache.SaveCdMetadata(new CachedCdMetadata { DiscId = "d1", Artist = "Updated" });

        Assert.Equal("Updated", MediaCache.GetCdMetadata("d1")!.Artist);
    }
}
