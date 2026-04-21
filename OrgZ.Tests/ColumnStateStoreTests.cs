// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Helpers;

namespace OrgZ.Tests;

/// <summary>
/// Each test redirects Settings to an isolated temp dir so we don't poison the user's
/// real settings.json (ColumnStateStore persists via Settings). Shares the "Settings"
/// collection with SettingsTests so xUnit serializes them — both mutate the global
/// Settings override and would race under the default class-parallel scheduler.
/// </summary>
[Collection("Settings")]
public class ColumnStateStoreTests : IDisposable
{
    private readonly string _tempDir;

    public ColumnStateStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OrgZ-ColumnState-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Settings.OverrideSettingsDirectory(_tempDir);
        Settings.Clear();
    }

    public void Dispose()
    {
        Settings.OverrideSettingsDirectory(null);
        Settings.Clear();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ===== Load (empty state) =====

    [Fact]
    public void Load_returns_empty_list_when_no_state_saved()
    {
        Assert.Empty(ColumnStateStore.Load("Music"));
    }

    [Fact]
    public void LoadOrder_returns_empty_list_when_no_state_saved()
    {
        Assert.Empty(ColumnStateStore.LoadOrder("Music"));
    }

    [Fact]
    public void GetVisibility_returns_null_when_no_state_saved()
    {
        Assert.Null(ColumnStateStore.GetVisibility("Music", "Title"));
    }

    // ===== Save + Load round-trip =====

    [Fact]
    public void Save_then_Load_round_trips_visibility_and_order()
    {
        var original = new List<ColumnStateStore.ColumnState>
        {
            new() { Key = "Title",  IsVisible = true },
            new() { Key = "Artist", IsVisible = true },
            new() { Key = "Year",   IsVisible = false },
        };

        ColumnStateStore.Save("Music", original);
        var loaded = ColumnStateStore.Load("Music");

        Assert.Equal(3, loaded.Count);
        Assert.Equal("Title",  loaded[0].Key);
        Assert.True(loaded[0].IsVisible);
        Assert.Equal("Artist", loaded[1].Key);
        Assert.Equal("Year",   loaded[2].Key);
        Assert.False(loaded[2].IsVisible);
    }

    [Fact]
    public void LoadOrder_returns_keys_in_saved_order()
    {
        ColumnStateStore.Save("Music",
        [
            new() { Key = "Album" },
            new() { Key = "Title" },
            new() { Key = "Artist" },
        ]);

        Assert.Equal(["Album", "Title", "Artist"], ColumnStateStore.LoadOrder("Music"));
    }

    [Fact]
    public void GetVisibility_returns_saved_override()
    {
        ColumnStateStore.Save("Music",
        [
            new() { Key = "Title", IsVisible = true },
            new() { Key = "Year",  IsVisible = false },
        ]);

        Assert.True(ColumnStateStore.GetVisibility("Music", "Title"));
        Assert.False(ColumnStateStore.GetVisibility("Music", "Year"));
    }

    [Fact]
    public void GetVisibility_returns_null_for_key_not_in_saved_state()
    {
        ColumnStateStore.Save("Music", [new() { Key = "Title", IsVisible = true }]);
        Assert.Null(ColumnStateStore.GetVisibility("Music", "ThisKeyWasNotSaved"));
    }

    // ===== Per-view isolation =====

    [Fact]
    public void Saves_are_scoped_per_view()
    {
        ColumnStateStore.Save("Music", [new() { Key = "Title", IsVisible = true }]);
        ColumnStateStore.Save("Radio", [new() { Key = "Stream", IsVisible = false }]);

        // Music's state doesn't leak into Radio
        Assert.True(ColumnStateStore.GetVisibility("Music", "Title"));
        Assert.Null(ColumnStateStore.GetVisibility("Music", "Stream"));
        Assert.False(ColumnStateStore.GetVisibility("Radio", "Stream"));
        Assert.Null(ColumnStateStore.GetVisibility("Radio", "Title"));
    }

    [Fact]
    public void View_keys_with_colons_and_backslashes_work()
    {
        // Device views look like "Device:L:\" — make sure that doesn't break persistence
        const string deviceView = @"Device:L:\";
        ColumnStateStore.Save(deviceView, [new() { Key = "Title", IsVisible = false }]);

        Assert.False(ColumnStateStore.GetVisibility(deviceView, "Title"));
    }

    // ===== Replace behavior =====

    [Fact]
    public void Save_replaces_prior_state_wholesale()
    {
        ColumnStateStore.Save("Music",
        [
            new() { Key = "Title",  IsVisible = true },
            new() { Key = "Artist", IsVisible = true },
        ]);
        ColumnStateStore.Save("Music",
        [
            new() { Key = "Year", IsVisible = false },
        ]);

        var loaded = ColumnStateStore.Load("Music");
        Assert.Single(loaded);
        Assert.Equal("Year", loaded[0].Key);
    }

    [Fact]
    public void Save_empty_removes_saved_state_entirely()
    {
        ColumnStateStore.Save("Music", [new() { Key = "Title", IsVisible = true }]);
        ColumnStateStore.Save("Music", []);

        Assert.Empty(ColumnStateStore.Load("Music"));
        Assert.Null(ColumnStateStore.GetVisibility("Music", "Title"));
    }

    // ===== Malformed storage recovery =====

    [Fact]
    public void Malformed_json_is_treated_as_empty_state()
    {
        Settings.Set("OrgZ.Columns.Music", "{{ not json ]");
        Settings.Save();

        // Should fall through to "no saved state" rather than throwing
        Assert.Empty(ColumnStateStore.Load("Music"));
        Assert.Null(ColumnStateStore.GetVisibility("Music", "Title"));
    }

    [Fact]
    public void Empty_settings_value_is_treated_as_no_state()
    {
        Settings.Set("OrgZ.Columns.Music", string.Empty);
        Assert.Empty(ColumnStateStore.Load("Music"));
    }
}
