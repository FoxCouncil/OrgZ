// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

/// <summary>
/// The RadioState overlay: persisted user state (favorite / plays / rename) for bundled
/// stations, which have no Media rows and reload fresh from the embedded catalogue every
/// launch. Survival across the startup purge is the whole point, so that's tested too.
/// </summary>
[Collection(LibraryDbCollection.Name)]
public class RadioStateTests : IDisposable
{
    private readonly string _tempDbPath;

    public RadioStateTests()
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

    [Fact]
    public void Favorite_round_trips_without_a_media_row()
    {
        MediaCache.SetRadioFavorite("rb:station-1", true);

        var state = MediaCache.LoadRadioState();
        Assert.True(state["rb:station-1"].IsFavorite);

        MediaCache.SetRadioFavorite("rb:station-1", false);
        Assert.False(MediaCache.LoadRadioState()["rb:station-1"].IsFavorite);
    }

    [Fact]
    public void BumpRadioPlay_increments_and_stamps_last_played()
    {
        var first = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var second = new DateTime(2026, 8, 2, 18, 30, 0, DateTimeKind.Utc);

        MediaCache.BumpRadioPlay("rb:station-1", first);
        MediaCache.BumpRadioPlay("rb:station-1", second);

        var state = MediaCache.LoadRadioState()["rb:station-1"];
        Assert.Equal(2, state.PlayCount);
        Assert.Equal(second, state.LastPlayed!.Value.ToUniversalTime());
    }

    [Fact]
    public void Title_override_sets_and_clears()
    {
        MediaCache.SetRadioTitle("rb:station-1", "My Better Name");
        Assert.Equal("My Better Name", MediaCache.LoadRadioState()["rb:station-1"].TitleOverride);

        MediaCache.SetRadioTitle("rb:station-1", "   ");
        Assert.Null(MediaCache.LoadRadioState()["rb:station-1"].TitleOverride);
    }

    [Fact]
    public void Fields_are_independent_across_upserts()
    {
        // Each setter upserts its own column; a later favorite flip must not clobber plays or the rename.
        MediaCache.BumpRadioPlay("rb:station-1", DateTime.UtcNow);
        MediaCache.SetRadioTitle("rb:station-1", "Renamed");
        MediaCache.SetRadioFavorite("rb:station-1", true);

        var state = MediaCache.LoadRadioState()["rb:station-1"];
        Assert.True(state.IsFavorite);
        Assert.Equal(1, state.PlayCount);
        Assert.Equal("Renamed", state.TitleOverride);
    }

    [Fact]
    public void State_survives_the_legacy_radio_purge()
    {
        MediaCache.SetRadioFavorite("rb:station-1", true);

        // The startup purge that deletes stray non-user radio Media rows must never
        // touch the overlay - that was the original favorites-vanish bug.
        MediaCache.RemoveLegacyRadioSources();

        Assert.True(MediaCache.LoadRadioState()["rb:station-1"].IsFavorite);
    }

    [Fact]
    public void Empty_table_loads_empty()
    {
        Assert.Empty(MediaCache.LoadRadioState());
    }
}
