// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Helpers;

namespace OrgZ.Tests;

/// <summary>
/// Shares the "Settings" collection so it serializes with SettingsTests and
/// ColumnStateStoreTests — all three mutate the global OverrideSettingsDirectory.
/// </summary>
[Collection("Settings")]
public class GroupExpansionStateTests : IDisposable
{
    private readonly string _tempDir;

    public GroupExpansionStateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OrgZ-GroupExpansion-" + Path.GetRandomFileName());
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

    [Fact]
    public void Load_returns_empty_dict_when_no_state_saved()
    {
        var loaded = GroupExpansionState.Load("Radio");
        Assert.Empty(loaded);
    }

    [Fact]
    public void Save_then_Load_round_trips_true_and_false_values()
    {
        var original = new Dictionary<string, bool>
        {
            ["Rock"]  = true,
            ["Jazz"]  = false,
            ["Blues"] = true,
        };

        GroupExpansionState.Save("Radio", original);
        var loaded = GroupExpansionState.Load("Radio");

        Assert.Equal(3, loaded.Count);
        Assert.True(loaded["Rock"]);
        Assert.False(loaded["Jazz"]);
        Assert.True(loaded["Blues"]);
    }

    [Fact]
    public void Save_replaces_prior_state_wholesale()
    {
        GroupExpansionState.Save("Radio", new Dictionary<string, bool>
        {
            ["Rock"] = true,
            ["Jazz"] = true,
        });
        GroupExpansionState.Save("Radio", new Dictionary<string, bool>
        {
            ["Pop"] = false,
        });

        var loaded = GroupExpansionState.Load("Radio");
        Assert.Single(loaded);
        Assert.False(loaded["Pop"]);
    }

    [Fact]
    public void Save_empty_removes_saved_state()
    {
        GroupExpansionState.Save("Radio", new Dictionary<string, bool> { ["Rock"] = true });
        GroupExpansionState.Save("Radio", new Dictionary<string, bool>());

        Assert.Empty(GroupExpansionState.Load("Radio"));
    }

    [Fact]
    public void Views_are_isolated()
    {
        GroupExpansionState.Save("Radio", new Dictionary<string, bool> { ["Rock"] = true });
        GroupExpansionState.Save("Artists", new Dictionary<string, bool> { ["Rush"] = false });

        var radio = GroupExpansionState.Load("Radio");
        var artists = GroupExpansionState.Load("Artists");

        Assert.True(radio["Rock"]);
        Assert.False(radio.ContainsKey("Rush"));
        Assert.False(artists["Rush"]);
        Assert.False(artists.ContainsKey("Rock"));
    }

    [Fact]
    public void Malformed_JSON_is_treated_as_empty_state()
    {
        Settings.Set("OrgZ.GroupExpansion.Radio", "{{ not json");
        Settings.Save();

        // Must not throw — scanner-side code calls this and expects a safe fallback
        var loaded = GroupExpansionState.Load("Radio");
        Assert.Empty(loaded);
    }

    [Fact]
    public void Group_keys_with_slashes_and_special_chars_round_trip()
    {
        var original = new Dictionary<string, bool>
        {
            ["R&B / Soul"] = true,
            ["Country / Folk"] = false,
            ["Children's"] = true,
        };
        GroupExpansionState.Save("Radio", original);
        var loaded = GroupExpansionState.Load("Radio");

        Assert.True(loaded["R&B / Soul"]);
        Assert.False(loaded["Country / Folk"]);
        Assert.True(loaded["Children's"]);
    }

    [Fact]
    public void Empty_group_key_round_trips()
    {
        // Fallback bucket for items with null/missing group-by value
        var original = new Dictionary<string, bool> { [""] = true };
        GroupExpansionState.Save("Radio", original);

        var loaded = GroupExpansionState.Load("Radio");
        Assert.True(loaded[""]);
    }
}
