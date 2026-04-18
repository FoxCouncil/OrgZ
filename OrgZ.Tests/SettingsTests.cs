// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

/// <summary>
/// Each test runs against an isolated temp directory via OverrideSettingsDirectory,
/// then restores the default before delete so we never poison the user's real settings.
/// </summary>
public class SettingsTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OrgZ-settings-tests-" + Path.GetRandomFileName());
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

    // ===== Get/Set primitives =====

    [Fact]
    public void Get_returns_default_when_key_missing()
    {
        Assert.Equal("fallback", Settings.Get("missing", "fallback"));
        Assert.Equal(42, Settings.Get("missing-int", 42));
        Assert.True(Settings.Get("missing-bool", true));
    }

    [Fact]
    public void Set_then_Get_round_trips_string()
    {
        Settings.Set("key", "hello");
        Assert.Equal("hello", Settings.Get("key", ""));
    }

    [Fact]
    public void Set_then_Get_round_trips_int()
    {
        Settings.Set("volume", 75);
        Assert.Equal(75, Settings.Get("volume", 0));
    }

    [Fact]
    public void Set_then_Get_round_trips_bool()
    {
        Settings.Set("show-ignored", true);
        Assert.True(Settings.Get("show-ignored", false));

        Settings.Set("show-ignored", false);
        Assert.False(Settings.Get("show-ignored", true));
    }

    [Fact]
    public void Set_then_Get_round_trips_double()
    {
        Settings.Set("scroll-pos", 1234.5);
        Assert.Equal(1234.5, Settings.Get("scroll-pos", 0.0));
    }

    [Fact]
    public void Set_overwrites_existing_value()
    {
        Settings.Set("key", "first");
        Settings.Set("key", "second");
        Assert.Equal("second", Settings.Get("key", ""));
    }

    // ===== Save / Load round-trip =====

    [Fact]
    public void Save_creates_settings_file()
    {
        Settings.Set("foo", "bar");
        Settings.Save();

        var path = Settings.GetSettingsFilePath();
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Save_then_reload_persists_values_across_in_memory_reset()
    {
        Settings.Set("user-folder", @"C:\Music");
        Settings.Set("volume", 80);
        Settings.Save();

        // Force re-load by re-pointing at the same dir (resets the in-memory cache)
        Settings.OverrideSettingsDirectory(_tempDir);

        Assert.Equal(@"C:\Music", Settings.Get("user-folder", ""));
        Assert.Equal(80, Settings.Get("volume", 0));
    }

    [Fact]
    public void GetSettingsFilePath_returns_path_inside_override_directory()
    {
        var path = Settings.GetSettingsFilePath();
        Assert.StartsWith(_tempDir, path);
        Assert.EndsWith("settings.json", path);
    }

    // ===== Clear =====

    [Fact]
    public void Clear_removes_all_in_memory_values()
    {
        Settings.Set("a", 1);
        Settings.Set("b", 2);
        Settings.Clear();

        Assert.Equal(0, Settings.Get("a", 0));
        Assert.Equal(0, Settings.Get("b", 0));
    }

    // ===== Type coercion / fallback =====

    [Fact]
    public void Get_with_wrong_type_falls_back_to_default()
    {
        Settings.Set("port", "not-a-number");
        // Asking for an int when the stored value can't be coerced → default
        Assert.Equal(8080, Settings.Get("port", 8080));
    }

    [Fact]
    public void Get_after_Save_then_Reload_round_trips_int_via_JsonElement()
    {
        // After Save+Reload, values come back as JsonElement and need Deserialize.
        // This exercises the JsonElement branch of Get<T>.
        Settings.Set("count", 42);
        Settings.Save();
        Settings.OverrideSettingsDirectory(_tempDir);   // forces a re-load

        Assert.Equal(42, Settings.Get("count", 0));
    }

    [Fact]
    public void Get_after_reload_round_trips_bool_via_JsonElement()
    {
        Settings.Set("flag", true);
        Settings.Save();
        Settings.OverrideSettingsDirectory(_tempDir);

        Assert.True(Settings.Get("flag", false));
    }

    [Fact]
    public void Get_after_reload_round_trips_string_via_JsonElement()
    {
        Settings.Set("name", "FOXPOD");
        Settings.Save();
        Settings.OverrideSettingsDirectory(_tempDir);

        Assert.Equal("FOXPOD", Settings.Get("name", ""));
    }

    // ===== Malformed JSON / missing file =====

    [Fact]
    public void Loading_with_no_file_present_starts_empty()
    {
        // _tempDir exists but contains no settings.json — Get should return defaults
        Settings.OverrideSettingsDirectory(_tempDir);
        Assert.Equal("default", Settings.Get("anything", "default"));
    }

    [Fact]
    public void Loading_with_malformed_JSON_falls_back_to_empty()
    {
        // Plant garbage in the settings file — EnsureLoaded should swallow + start empty
        File.WriteAllText(Path.Combine(_tempDir, "settings.json"), "this is not json {[}");
        Settings.OverrideSettingsDirectory(_tempDir);

        Assert.Equal("default", Settings.Get("any-key", "default"));
        // And we should still be able to write
        Settings.Set("recovered", true);
        Assert.True(Settings.Get("recovered", false));
    }

    [Fact]
    public void Save_creates_directory_if_missing()
    {
        // Override to a non-existent directory; Save should create it
        var nestedDir = Path.Combine(_tempDir, "nested", "twice");
        Settings.OverrideSettingsDirectory(nestedDir);

        Settings.Set("k", "v");
        Settings.Save();

        Assert.True(File.Exists(Path.Combine(nestedDir, "settings.json")));
    }
}
