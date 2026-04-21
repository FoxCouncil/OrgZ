// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;

namespace OrgZ.Helpers;

/// <summary>
/// Persists per-view column visibility + order. Settings are keyed by view config
/// ("Music", "Radio", "Device:L:\\", etc.) so toggling "Year" off in Music doesn't
/// affect the Radio view.
///
/// Storage shape: each view gets a list of <see cref="ColumnState"/> rows. Serialized
/// as JSON into a single Settings value. On load, entries for unknown columns are
/// silently dropped, so adding a new column to a view config doesn't crash older
/// saved state.
/// </summary>
public static class ColumnStateStore
{
    public record ColumnState
    {
        public required string Key { get; init; }
        public bool IsVisible { get; init; } = true;
    }

    /// <summary>
    /// Loads saved column state for the given view. Returns empty list when no state
    /// has been saved yet (caller should fall back to <c>ColumnDef.IsDefaultVisible</c>
    /// and the definition's own order). Malformed JSON is treated as "no state".
    /// </summary>
    internal static List<ColumnState> Load(string viewKey)
    {
        var raw = Settings.Get<string>(BuildSettingsKey(viewKey), string.Empty);
        if (string.IsNullOrEmpty(raw))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ColumnState>>(raw) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Saves column state for the given view. Persists ALL columns — callers should pass
    /// the full ordered list they want remembered. Empty input removes the setting so
    /// the next load falls back to defaults.
    /// </summary>
    internal static void Save(string viewKey, IReadOnlyList<ColumnState> states)
    {
        var settingsKey = BuildSettingsKey(viewKey);

        if (states.Count == 0)
        {
            Settings.Set(settingsKey, string.Empty);
        }
        else
        {
            var json = JsonSerializer.Serialize(states);
            Settings.Set(settingsKey, json);
        }
        Settings.Save();
    }

    /// <summary>
    /// Returns the ordered list of column keys from the saved state. Callers using this
    /// to reorder the DataGrid columns should treat unknown keys (not in the live
    /// ColumnDef list) as ignorable and append unknown-to-user-but-known-to-config
    /// columns at the end. Empty list = use the ColumnDef order as-authored.
    /// </summary>
    internal static List<string> LoadOrder(string viewKey)
        => Load(viewKey).Select(s => s.Key).ToList();

    /// <summary>
    /// Returns the visibility override for a single column key, or null when no saved
    /// state mentions it. Null means "use ColumnDef.IsDefaultVisible".
    /// </summary>
    internal static bool? GetVisibility(string viewKey, string columnKey)
    {
        foreach (var state in Load(viewKey))
        {
            if (state.Key == columnKey)
            {
                return state.IsVisible;
            }
        }
        return null;
    }

    private static string BuildSettingsKey(string viewKey)
        => $"OrgZ.Columns.{viewKey}";
}
