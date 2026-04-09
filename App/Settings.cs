// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrgZ;

internal static class Settings
{
    private static readonly string SettingsFileName = "settings.json";
    private static readonly string SettingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrgZ");
    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, SettingsFileName);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static Dictionary<string, object>? _settings;
    private static readonly Lock _lock = new();

    /// <summary>
    /// Loads settings from the JSON file or creates empty settings if file doesn't exist
    /// </summary>
    private static void EnsureLoaded()
    {
        if (_settings != null)
        {
            return;
        }

        lock (_lock)
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
                Console.WriteLine($"Error loading settings from {SettingsFilePath}: {ex.Message}");
            }

            _settings = [];
        }
    }

    /// <summary>
    /// Gets a setting value by key, or returns the default value if not found
    /// </summary>
    public static T Get<T>(string key, T defaultValue = default!)
    {
        EnsureLoaded();

        lock (_lock)
        {
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
        EnsureLoaded();

        lock (_lock)
        {
            _settings![key] = value!;
        }
    }

    /// <summary>
    /// Saves the current settings to the JSON file
    /// </summary>
    public static void Save()
    {
        EnsureLoaded();

        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(SettingsDirectory);
                string json = JsonSerializer.Serialize(_settings, JsonOptions);
                File.WriteAllText(SettingsFilePath, json);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving settings to {SettingsFilePath}: {ex.Message}");
            throw;
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
