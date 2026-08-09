// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrgZ;

internal static class Settings
{
    private const string SettingsFileName = "settings.json";
    private static readonly string DefaultSettingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrgZ");

    private static string SettingsDirectory { get; set; } = DefaultSettingsDirectory;
    private static string SettingsFilePath { get; set; } = Path.Combine(DefaultSettingsDirectory, SettingsFileName);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static Dictionary<string, object>? _settings;
    private static readonly Lock _lock = new();

    /// <summary>
    /// Test hook: redirect the settings file to a custom directory. Pass null to restore
    /// the default %APPDATA%/OrgZ location. Resets the in-memory cache so the next Get
    /// re-loads from the new path.
    /// </summary>
    internal static void OverrideSettingsDirectory(string? directory)
    {
        lock (_lock)
        {
            SettingsDirectory = directory ?? DefaultSettingsDirectory;
            SettingsFilePath = Path.Combine(SettingsDirectory, SettingsFileName);
            _settings = null;
        }
    }

    /// <summary>
    /// Loads settings from the JSON file or creates empty settings.  Caller must
    /// already hold <see cref="_lock"/> - this method is not self-synchronizing
    /// because the race between "check → load" and
    /// <see cref="OverrideSettingsDirectory"/> (which nulls <c>_settings</c>
    /// under the same lock) can otherwise leave Get/Set observing a null map
    /// in between.  See the test-suite regression that surfaced under xUnit
    /// parallel execution in 2026-04.
    /// </summary>
    private static void EnsureLoadedLocked()
    {
        if (_settings != null)
        {
            return;
        }

        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                _settings = JsonSerializer.Deserialize<Dictionary<string, object>>(json, JsonOptions) ?? [];
                return;
            }
        }
        catch (Exception ex)
        {
            // Falling back to empty settings resets every preference - that MUST leave a trail.
            // Logging.For is resolved lazily: if this runs before Logging.Initialize, Serilog's
            // default silent logger absorbs it rather than throwing.
            Services.Logging.For("Settings").Error(ex, "Failed to load settings from {Path} - starting with empty settings", SettingsFilePath);
        }

        _settings = [];
    }

    /// <summary>
    /// Gets a setting value by key, or returns the default value if not found
    /// </summary>
    public static T Get<T>(string key, T defaultValue = default!)
    {
        lock (_lock)
        {
            EnsureLoadedLocked();
            if (_settings!.TryGetValue(key, out object? value))
            {
                try
                {
                    return value is JsonElement element ? element.Deserialize<T>(JsonOptions)! : (T)Convert.ChangeType(value, typeof(T));
                }
                catch
                {
                    return defaultValue;
                }
            }

            return defaultValue;
        }
    }

    /// <summary>
    /// Sets a setting value by key
    /// </summary>
    public static void Set<T>(string key, T value)
    {
        lock (_lock)
        {
            EnsureLoadedLocked();
            _settings![key] = value!;
        }
    }

    /// <summary>
    /// Saves the current settings to the JSON file
    /// </summary>
    public static void Save()
    {
        try
        {
            lock (_lock)
            {
                // A direct Save supersedes any pending deferred one - everything it
                // would have written is going out right now.
                _deferredSave?.Change(Timeout.Infinite, Timeout.Infinite);

                EnsureLoadedLocked();
                Directory.CreateDirectory(SettingsDirectory);
                string json = JsonSerializer.Serialize(_settings, JsonOptions);

                // Atomic: SaveDeferred fires this on a 500ms timer during volume drags, so a
                // crash mid-write would otherwise truncate settings.json and reset everything.
                Helpers.AtomicFile.WriteAllBytes(SettingsFilePath, System.Text.Encoding.UTF8.GetBytes(json));

                // Anything caching a settings-derived value re-reads after a save.
                Models.MediaItem.BadFormatCriteria.Invalidate();
            }
        }
        catch (Exception ex)
        {
            Services.Logging.For("Settings").Error(ex, "Failed to save settings to {Path}", SettingsFilePath);
            throw;
        }
    }

    // ── Debounced persistence ─────────────────────────────────
    // Save() is a synchronous whole-file JSON write. Hot paths (a volume drag fires per
    // slider tick, a sidebar switch per click) must update the in-memory value and let
    // the DISK write trail behind; anything that saves at a natural boundary (dialog
    // close, shutdown) keeps calling Save(), which also supersedes any pending deferral.
    private static Timer? _deferredSave;

    /// <summary>Schedules a Save ~500ms after the last call - one write per burst.</summary>
    public static void SaveDeferred()
    {
        lock (_lock)
        {
            _deferredSave ??= new Timer(_ =>
            {
                try
                {
                    Save();
                }
                catch
                {
                    // Save() already reported it; a deferred failure has no caller to throw to.
                }
            }, null, Timeout.Infinite, Timeout.Infinite);
            _deferredSave.Change(TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Clears all settings
    /// </summary>
    public static void Clear()
    {
        lock (_lock)
        {
            _settings = [];
        }
    }

    /// <summary>
    /// Gets the path to the settings file
    /// </summary>
    public static string GetSettingsFilePath()
    {
        return SettingsFilePath;
    }
}
