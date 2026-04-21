// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Helpers;

namespace OrgZ.Tests;

public class PerViewSearchStateTests
{
    // ===== Save =====

    [Fact]
    public void Save_stores_non_empty_text_under_key()
    {
        var store = new Dictionary<string, string>();
        PerViewSearchState.Save(store, "Music", "rush");

        Assert.Equal("rush", store["Music"]);
    }

    [Fact]
    public void Save_removes_entry_when_text_empty()
    {
        var store = new Dictionary<string, string> { ["Music"] = "rush" };
        PerViewSearchState.Save(store, "Music", "");

        Assert.False(store.ContainsKey("Music"));
    }

    [Fact]
    public void Save_removes_entry_when_text_null()
    {
        var store = new Dictionary<string, string> { ["Music"] = "rush" };
        PerViewSearchState.Save(store, "Music", null);

        Assert.False(store.ContainsKey("Music"));
    }

    [Fact]
    public void Save_with_null_key_is_noop()
    {
        var store = new Dictionary<string, string> { ["Music"] = "rush" };
        PerViewSearchState.Save(store, null, "pink floyd");

        Assert.Single(store);
        Assert.Equal("rush", store["Music"]);
    }

    [Fact]
    public void Save_with_empty_key_is_noop()
    {
        var store = new Dictionary<string, string>();
        PerViewSearchState.Save(store, "", "anything");

        Assert.Empty(store);
    }

    [Fact]
    public void Save_overwrites_existing_value_for_same_key()
    {
        var store = new Dictionary<string, string>();
        PerViewSearchState.Save(store, "Music", "first");
        PerViewSearchState.Save(store, "Music", "second");

        Assert.Equal("second", store["Music"]);
    }

    // ===== Restore =====

    [Fact]
    public void Restore_returns_saved_value_for_known_key()
    {
        var store = new Dictionary<string, string> { ["Music"] = "rush" };
        Assert.Equal("rush", PerViewSearchState.Restore(store, "Music"));
    }

    [Fact]
    public void Restore_returns_empty_for_unknown_key()
    {
        var store = new Dictionary<string, string> { ["Music"] = "rush" };
        Assert.Equal(string.Empty, PerViewSearchState.Restore(store, "Radio"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Restore_returns_empty_for_null_or_empty_key(string? key)
    {
        var store = new Dictionary<string, string> { ["Music"] = "rush" };
        Assert.Equal(string.Empty, PerViewSearchState.Restore(store, key));
    }

    // ===== Save + Restore round-trip =====

    [Fact]
    public void Save_then_Restore_round_trips()
    {
        var store = new Dictionary<string, string>();
        PerViewSearchState.Save(store, "Music", "rush");
        PerViewSearchState.Save(store, "Radio", "bbc");
        PerViewSearchState.Save(store, "Device:L:\\", "");   // empty text, nothing to save

        Assert.Equal("rush", PerViewSearchState.Restore(store, "Music"));
        Assert.Equal("bbc",  PerViewSearchState.Restore(store, "Radio"));
        Assert.Equal("",     PerViewSearchState.Restore(store, "Device:L:\\"));
    }

    [Fact]
    public void Save_then_Save_empty_then_Restore_returns_empty()
    {
        // Scenario: user types "rush" in Music, then clears it - Restore should return ""
        // and the dict entry should be gone.
        var store = new Dictionary<string, string>();
        PerViewSearchState.Save(store, "Music", "rush");
        PerViewSearchState.Save(store, "Music", "");

        Assert.Equal(string.Empty, PerViewSearchState.Restore(store, "Music"));
        Assert.False(store.ContainsKey("Music"));
    }

    [Fact]
    public void Multiple_views_store_independently()
    {
        // The whole point: typing in Music doesn't leak into other views
        var store = new Dictionary<string, string>();
        PerViewSearchState.Save(store, "Music", "rush");
        PerViewSearchState.Save(store, "Radio", "bbc");
        PerViewSearchState.Save(store, "Device:L:\\", "led zep");

        Assert.Equal("rush",    PerViewSearchState.Restore(store, "Music"));
        Assert.Equal("bbc",     PerViewSearchState.Restore(store, "Radio"));
        Assert.Equal("led zep", PerViewSearchState.Restore(store, "Device:L:\\"));
    }
}
